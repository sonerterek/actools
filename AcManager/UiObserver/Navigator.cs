using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Windows.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AcManager.UiObserver
{
	internal enum NavDirection { Up, Down, Left, Right }

	/// <summary>
	/// Slider adjustment operations (independent of navigation directions).
	/// Used for adjusting slider values and ranges without directional coupling.
	/// </summary>
	internal enum SliderAdjustment
	{
		SmallIncrement,
		SmallDecrement
		// Future extensions:
		// LargeIncrement,
		// LargeDecrement
	}

	/// <summary>
	/// Describes the source and behavior of a navigation context.
	/// Different context types have different scope resolution rules.
	/// </summary>
	internal enum NavContextType
	{
		/// <summary>
		/// Root window context (MainWindow).
		/// Scope: All descendants of the window.
		/// Lifecycle: Managed by Observer (window lifecycle).
		/// </summary>
		RootWindow,
		
		/// <summary>
		/// Modal dialog or popup context (SelectCarDialog, ContextMenu, PopupRoot, etc.).
		/// Scope: All descendants of the popup/dialog.
		/// Lifecycle: Managed by Observer (ModalGroupOpened/Closed events).
		/// </summary>
		ModalDialog,
		
		/// <summary>
		/// Interactive control context (Slider, DoubleSlider, RoundSlider, etc.).
		/// Scope: Only the control itself (single element).
		/// Lifecycle: Managed by Navigator (EnterInteractionMode/ExitInteractionMode).
		/// Future: Could be extended for ComboBox dropdowns, menu submenus, etc
		/// </summary>
		InteractiveControl,
		
		/// <summary>
		/// PageSelector context (tab content, Frame navigation, etc.).
		/// Scope: Reuses parent context's scope (typically MainWindow or modal dialog).
		/// Lifecycle: Managed by Observer (PageSelector node added/removed events).
		/// Purpose: Switches StreamDeck page when content changes, doesn't create new navigation scope.
		/// </summary>
		PageSelector
	}

	/// <summary>
	/// Represents a navigation context within the context hierarchy.
	/// Each context bundles a scope (the root node defining the context)
	/// with the currently focused node within that scope.
	/// </summary>
	internal class NavContext
	{
		/// <summary>
		/// The scope node that defines this context's boundaries.
		/// For modal contexts, this is the Window/Popup/PopupRoot.
		/// For interaction mode contexts, this is a single control (Slider, etc.).
		/// For page selector contexts, this reuses the parent context's scope.
		/// Never null.
		/// </summary>
		public NavNode ScopeNode { get; }
		
		/// <summary>
		/// The type of this context, which determines scope resolution behavior.
		/// </summary>
		public NavContextType ContextType { get; }
		
		/// <summary>
		/// Currently focused node within this context.
		/// Null if no focus has been established yet in this context.
		/// </summary>
		public NavNode FocusedNode { get; set; }
		
		/// <summary>
		/// Original value of the control when entering interaction mode.
		/// Used for Cancel (revert) functionality.
		/// Only populated for InteractiveControl contexts.
		/// </summary>
		public object OriginalValue { get; set; }
		
		/// <summary>
		/// The PageSelector node that triggered this context (WHAT content is being displayed).
		/// Only populated for PageSelector contexts.
		/// Example: QuickDrive:UserControl, KunosCareer:Frame
		/// </summary>
		public NavNode PageSelectorNode { get; set; }
		
		/// <summary>
		/// The StreamDeck page name associated with this context.
		/// Only populated for PageSelector contexts.
		/// Example: "QuickDrive", "KunosCareer"
		/// </summary>
		public string PageName { get; set; }
		
		public NavContext(NavNode scopeNode, NavContextType contextType, NavNode focusedNode = null)
		{
			ScopeNode = scopeNode ?? throw new ArgumentNullException(nameof(scopeNode));
			ContextType = contextType;
			FocusedNode = focusedNode;
		}
	}

	/// <summary>
	/// Navigator - Navigation coordinator with event-driven architecture.
	/// 
	/// Responsibilities:
	/// - Subscribe to Observer modal lifecycle events (ModalGroupOpened, ModalGroupClosed)
	/// - Manage modal context stack (each context = modal scope + focused node)
	/// - Handle keyboard input (Ctrl+Shift+Arrow keys for navigation)
	/// - Filter navigable candidates by modal scope
	/// - Find best candidate in direction using spatial algorithm
	/// - Manage focus highlighting overlay
	/// 
	/// Architecture:
	/// - NavNode: Data + type-specific behaviors (CreateNavNode, Activate, Close)
	/// - Observer: Discovery engine (scans visual trees silently, emits modal lifecycle events)
	/// - Navigator: Navigation logic (subscribes to modal events only, manages modal stack, handles input)
	/// 
	/// Note: Individual node discovery is SILENT. Navigator only reacts to modal lifecycle changes,
	/// which provides complete information about all nodes in the modal scope at once.
	/// </summary>
	internal static partial class Navigator
	{
		#region Fields and Properties

		private static bool _initialized = false;

		// Context stack - each entry bundles scope + focused node
		// Invariant: _contextStack.Count >= 1 after initialization (root context always present)
		internal static readonly List<NavContext> _contextStack = new List<NavContext>();
		
		// Helper property for current context (never null after initialization)
		internal static NavContext CurrentContext => _contextStack.Count > 0 
			? _contextStack[_contextStack.Count - 1] 
			: null;

		// Events
		internal static event Action<NavNode, NavNode> FocusChanged; // (oldNode, newNode)

		// Highlighting overlay (used by both production focus and debug visualization)
		internal static HighlightOverlay _overlay;

		// Tooltip management - saved state for restoration
		private static int _originalTooltipDelay = 500;
		private static bool _tooltipsDisabled = false;

		// Mouse tracking control - moves mouse once on focus change, then allows free movement
		// Mouse only moves on explicit focus changes (arrow keys), not on layout updates (resize)
		private static bool _enableMouseTracking = true;

		// ✓ NEW: Track ignored modals (empty popups like tooltips) to prevent close warnings
		// When a modal has no navigable children, we ignore it (don't push to stack).
		// But WPF still fires Unloaded event when it closes, so we need to track these
		// to avoid "WARNING: Closed modal not at top" messages.
		private static readonly HashSet<NavNode> _ignoredModals = new HashSet<NavNode>();
		
		// Track when we need to re-initialize focus after focused node unloads
		private static bool _needsFocusReinit = false;

		/// <summary>
		/// Gets whether verbose navigation debug output is enabled.
		/// Controlled by Ctrl+Shift+F9 hotkey in debug builds.
		/// Field defined in Navigator.Debug.cs partial class.
		/// </summary>
		internal static bool VerboseNavigationDebug =>
#if DEBUG
			_verboseNavigationDebug;
#else
			false;
#endif

		#endregion

		#region Initialization

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			// Step 1: Load NavConfig from file FIRST
			_navConfig = NavConfigParser.Load();
			
			// Step 2: Add built-in rules to NavConfig
			AddBuiltInRules();

			// Step 3: Initialize StreamDeck (uses _navConfig)
			InitializeStreamDeck();

			// Step 4: Disable tooltips globally during navigation
			DisableTooltips();

			// Step 5: Create overlay BEFORE starting Observer to avoid race condition
			EnsureOverlay();

			// Step 6: Pass NavConfig to Observer during initialization
			Observer.Initialize(_navConfig);

			// Step 7: Subscribe to Observer events
			Observer.ModalGroupOpened += OnModalGroupOpened;
			Observer.ModalGroupClosed += OnModalGroupClosed;
			Observer.WindowLayoutChanged += OnWindowLayoutChanged;
			Observer.NodesUpdated += Observer_NodesUpdated;
			
			// Step 8: Install focus guard to prevent WPF from stealing focus to non-navigable elements
			InstallFocusGuard();

			// Step 9: Register keyboard handler
			try {
				EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, 
					new KeyEventHandler(OnPreviewKeyDown), true);
			} catch { }
		}

		/// <summary>
		/// Adds built-in navigation rules to NavConfig.
		/// These rules are added AFTER loading the config file, so config file rules can override them.
		/// </summary>
		private static void AddBuiltInRules()
		{
			// Built-in exclusion rules
			var exclusions = new[] {
				// Exclude scrollbars
				"** > *:ScrollBar",
				"** > *:BetterScrollBar",

				// Exclude text or fancy menu items
				"** > *:HistoricalTextBox > **",
				"** > *:LazyMenuItem > **",
				"** > *:ModernTabSplitter",
				"*:PopupRoot > ** > *:MenuItem",

				// CRITICAL: Exclude debug overlay from navigation tracking to prevent feedback loop
				"*:HighlightOverlay > **",

				// Exclude main menu and content frame from navigation
				"Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu",
				"Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > ContentFrame:ModernFrame",
				"Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > ContentFrame:ModernFrame > (unnamed):Border > (unnamed):Cell > (unnamed):TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > This:QuickDrive > (unnamed):Border > (unnamed):ContentPresenter > MainGrid:Grid > (unnamed):Grid > ModeTab:ModernTab > (unnamed):DockPanel > PART_Frame:ModernFrame",
				"(unnamed):SelectTrackDialog > (unnamed):Border > (unnamed):Cell > (unnamed):AdornerDecorator > PART_Border:Border > (unnamed):Cell > (unnamed):DockPanel > (unnamed):Border > PART_Content:TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > (unnamed):Grid > (unnamed):DockPanel > (unnamed):AdornerDecorator > Tabs:ModernTab",
				"(unnamed):SelectTrackDialog > (unnamed):Border > (unnamed):Cell > (unnamed):AdornerDecorator > PART_Border:Border > (unnamed):Cell > (unnamed):DockPanel > (unnamed):Border > PART_Content:TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > (unnamed):Grid > (unnamed):DockPanel > (unnamed):AdornerDecorator > Tabs:ModernTab > (unnamed):DockPanel > PART_Frame:ModernFrame",
			};

			foreach (var pattern in exclusions)
			{
				_navConfig.AddExclusionPattern(pattern);
			}
			
			Debug.WriteLine($"[Navigator] Added {exclusions.Length} built-in exclusion rules");
			
			// Built-in classification rules (as NavClassifier objects)
			var classifications = new[] {
				new NavClassifier {
					PathFilter = "** > *:SelectCarDialog",
					Role = "group",
					IsModal = true
				},
				new NavClassifier {
					PathFilter = "** > *:SelectTrackDialog",
					Role = "group",
					IsModal = true
				},
				new NavClassifier {
					PathFilter = "** > PART_SystemButtonsPanel:StackPanel",
					Role = "group",
					IsModal = false
				},
				new NavClassifier {
					PathFilter = "** > PART_TitleBar:DockPanel > *:ItemsControl",
					Role = "group",
					IsModal = false
				},
			};
			
			foreach (var classification in classifications)
			{
				_navConfig.Classifications.Add(classification);
			}
			
			Debug.WriteLine($"[Navigator] Added {classifications.Length} built-in classification rules");
		}

		internal static void EnsureOverlay()
		{
#if DEBUG
			if (_overlay == null) {
				try {
					_overlay = new HighlightOverlay();
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] Failed to create overlay: {ex.Message}");
				}
			}
#endif
		}
		
		/// <summary>
		/// Disables tooltips globally by setting an extremely high InitialShowDelay.
		/// Saves the original delay for potential restoration.
		/// 
		/// This prevents tooltips from appearing when the mouse is programmatically moved
		/// to track the focused NavNode during keyboard navigation.
		/// </summary>
		private static void DisableTooltips()
		{
			try {
				// Get current default delay
				var metadata = System.Windows.Controls.ToolTipService.InitialShowDelayProperty.GetMetadata(typeof(DependencyObject)) 
					as FrameworkPropertyMetadata;
				if (metadata != null) {
					_originalTooltipDelay = (int)metadata.DefaultValue;
				}

				// Set to very high value to effectively disable tooltips
				const int disabledDelay = 999999;
				System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
					typeof(DependencyObject), 
					new FrameworkPropertyMetadata(disabledDelay));

				_tooltipsDisabled = true;
				Debug.WriteLine($"[Navigator] Tooltips disabled (original delay: {_originalTooltipDelay}ms, new: {disabledDelay}ms)");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to disable tooltips: {ex.Message}");
			}
		}

		/// <summary>
		/// Restores the original tooltip delay.
		/// Currently not called, but available for cleanup/shutdown if needed.
		/// </summary>
		private static void RestoreTooltips()
		{
			if (!_tooltipsDisabled) return;

			try {
				System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
					typeof(DependencyObject), 
					new FrameworkPropertyMetadata(_originalTooltipDelay));

				_tooltipsDisabled = false;
				Debug.WriteLine($"[Navigator] Tooltips restored (delay: {_originalTooltipDelay}ms)");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to restore tooltips: {ex.Message}");
			}
		}

		#endregion

		#region Event Handlers

		/// <summary>
		/// Called by Observer when nodes are added or removed during a sync.
		/// Handles:
		/// - PageSelector context management (removal BEFORE addition for clean transitions)
		/// - Focus re-initialization when focused node is removed
		/// - Shortcut binding updates (Phase 4)
		/// </summary>
		private static void Observer_NodesUpdated(NavNode[] addedNodes, NavNode[] removedNodes)
		{
			if (CurrentContext == null) return;
			
			// ✅ STEP 1: Process REMOVALS FIRST (pop old PageSelector contexts)
			// This ensures old context is gone before new one is pushed (clean transitions)
			if (removedNodes != null && removedNodes.Length > 0)
			{
				foreach (var removedNode in removedNodes)
				{
					// Check if this was a PageSelector that had a context
					if (removedNode.IsPageSelector)
					{
						OnPageSelectorDeactivated(removedNode);
					}
					
					// Check if our currently focused node was removed
					if (CurrentContext.FocusedNode != null && ReferenceEquals(CurrentContext.FocusedNode, removedNode))
					{
						Debug.WriteLine($"[Navigator] ⚠ Focused node was removed: {removedNode.SimpleName}");
						removedNode.HasFocus = false;
						CurrentContext.FocusedNode = null;
						_needsFocusReinit = true;
					}
				}
			}
			
			// ✅ STEP 2: Process ADDITIONS SECOND (push new PageSelector contexts)
			// At this point, old PageSelector contexts have been popped (if any)
			if (addedNodes != null && addedNodes.Length > 0)
			{
				// Find the first PageSelector that's in our active scope
				// We only switch for the FIRST one found (most specific wins if multiple)
				foreach (var addedNode in addedNodes)
				{
					if (addedNode.IsPageSelector && IsInActiveModalScope(addedNode))
					{
						OnPageSelectorActivated(addedNode);
						// Only process the FIRST PageSelector found
						break;
					}
				}
			}
			
			// ✅ STEP 3: Handle focus re-initialization if needed
			// If focused node was removed and new navigable nodes were added in our scope, try to focus one
			if (_needsFocusReinit && addedNodes != null && addedNodes.Length > 0)
			{
				// Find first added node that's navigable and in our scope
				foreach (var addedNode in addedNodes)
				{
					if (!addedNode.IsGroup && addedNode.IsNavigable && IsInActiveModalScope(addedNode))
					{
						Debug.WriteLine($"[Navigator] ✅ Re-initializing focus to newly added node: {addedNode.SimpleName}");
						SetFocus(addedNode);
						_needsFocusReinit = false;
						return;
					}
				}
				
				Debug.WriteLine($"[Navigator] ⏳ No navigable nodes in added batch, will wait for next sync");
			}
			
			// ✅ STEP 4 (Phase 4): Update shortcut bindings
			// This rebinds ALL shortcuts to current nodes (handles additions AND removals)
			BindShortcutsToNodes();
			
			// ✅ STEP 5 (Phase 3): Switch to current context's page after all updates
			// This ensures the StreamDeck page reflects the final context state after:
			// - PageSelector contexts have been pushed/popped
			// - Focus has been re-initialized (if needed)
			// This is a batched update - happens once after all node changes are processed
			SwitchToCurrentContextPage();
		}

		/// <summary>
		/// Called when a modal group (popup) opens.
		/// Creates a new navigation context and initializes focus to the first navigable element.
		/// 
		/// ✓ CHANGED: Uses DispatcherPriority.ApplicationIdle + 50ms delay for nested modals (popups)
		/// to ensure WPF has completed layout AND positioning before calculating coordinates.
		/// MainWindow uses DispatcherPriority.Loaded since it's positioned at startup.
		/// 
		/// This fixes the bug where initial mouse position was wrong in popup menus because
		/// PointToScreen() was called before the popup reached its final screen position.
		/// 
		/// ✓ NEW: Selects appropriate StreamDeck page based on modal type.
		/// </summary>
		private static void OnModalGroupOpened(NavNode scopeNode)
		{
			Debug.WriteLine($"[Navigator] Context opened: {scopeNode.SimpleName} @ {scopeNode.HierarchicalPath}");

			// Check if this modal should be ignored (e.g., empty tooltip popups)
			var children = Observer.GetAllNavNodes()
				.Where(n => n.IsNavigable && !n.IsGroup && IsDescendantOf(n, scopeNode))
				.ToList();

			if (children.Count == 0) {
				Debug.WriteLine($"[Navigator] Context '{scopeNode.SimpleName}' has no navigable children - treating as non-modal overlay (ignoring)");
				_ignoredModals.Add(scopeNode);
				return;
			}

			Debug.WriteLine($"[Navigator] Context has {children.Count} navigable children - creating navigation context");

			// ✅ NEW: Determine context type based on scope node
			var contextType = DetermineModalContextType(scopeNode);
			
			// ✅ Determine page name at creation time
			var pageName = DeterminePageForNode(scopeNode, contextType);
			
			// ✅ Create context with PageName assigned
			var context = new NavContext(scopeNode, contextType, focusedNode: null)
			{
				PageName = pageName  // ← Store page in context
			};
			_contextStack.Add(context);
			
			var contextDepth = _contextStack.Count;
			Debug.WriteLine($"[Navigator] Context stack depth: {contextDepth}");
			Debug.WriteLine($"[Navigator] Context type: {contextType}");
			Debug.WriteLine($"[Navigator] Context page: {pageName ?? "(none)"}");

			// ✅ (Phase 3): Switch to current context's page immediately after creation
			SwitchToCurrentContextPage();

			// ✓ FIX: Use different dispatcher priorities based on modal type
			// MainWindow (depth 1): Loaded priority - window is already positioned at startup
			// Nested modals (depth 2+): ApplicationIdle + 50ms delay - wait for popup positioning to complete
			if (scopeNode.TryGetVisual(out var fe)) {
				var isNestedModal = contextDepth > 1;
				var priority = isNestedModal
					? DispatcherPriority.ApplicationIdle  // After ALL layout & positioning
					: DispatcherPriority.Loaded;          // After layout only

				var delayMs = isNestedModal ? 50 : 0;

				Debug.WriteLine($"[Navigator] Deferring focus init with priority: {priority} (depth={contextDepth}, delay={delayMs}ms)");

				fe.Dispatcher.BeginInvoke(
					priority,
					new Action(() => {
						if (delayMs > 0) {
							// For popups, add delay to ensure positioning completes
							var timer = new DispatcherTimer
							{
								Interval = TimeSpan.FromMilliseconds(delayMs)
							};
							timer.Tick += (s, e) => {
								timer.Stop();
								if (CurrentContext?.ScopeNode == scopeNode) {
									TryInitializeFocusIfNeeded();
								} else {
									Debug.WriteLine($"[Navigator] Skipped deferred focus init - context no longer current");
								}
							};
							timer.Start();
						} else {
							// MainWindow: initialize immediately after layout
							if (CurrentContext?.ScopeNode == scopeNode) {
								TryInitializeFocusIfNeeded();
							} else {
								Debug.WriteLine($"[Navigator] Skipped deferred focus init - context no longer current");
							}
						}
					})
				);
			} else {
				// Fallback if visual reference is dead (shouldn't happen for newly opened modals)
				Debug.WriteLine($"[Navigator] WARNING: Visual reference dead for newly opened context, initializing immediately");
				TryInitializeFocusIfNeeded();
			}
		}

		/// <summary>
		/// Determines the context type for an Observer-discovered modal.
		/// MainWindow is special - it's the root context.
		/// All other Observer-discovered modals are dialogs/popups.
		/// </summary>
		private static NavContextType DetermineModalContextType(NavNode scopeNode)
		{
			if (!scopeNode.TryGetVisual(out var element))
				return NavContextType.ModalDialog; // Safe default
		
			// MainWindow is special - it's the root context
			if (element is Window window && window == Application.Current.MainWindow)
				return NavContextType.RootWindow;
		
			// All other Observer-discovered modals are dialogs/popups
			return NavContextType.ModalDialog;
		}

		private static void OnModalGroupClosed(NavNode scopeNode)
		{
			if (scopeNode == null) return;
			
			Debug.WriteLine($"[Navigator] Context closed: {scopeNode.SimpleName} @ {scopeNode.HierarchicalPath}");
			
			// ✓ NEW: Check if this was an ignored modal (empty popup like tooltip)
			// These were never pushed to the stack, so we shouldn't try to pop them
			if (_ignoredModals.Remove(scopeNode)) {
				Debug.WriteLine($"[Navigator] Ignored context closed (no action needed): {scopeNode.SimpleName}");
				return;
			}
			
			// Validate that this modal is actually on the stack
			if (_contextStack.Count == 0) {
				Debug.WriteLine($"[Navigator] ERROR: Context stack is empty, cannot close context!");
				return;
			}
			
			// ✅ FIX: Compare by path instead of reference (Observer may create new NavNode instances)
			var currentTop = CurrentContext;
			if (currentTop.ScopeNode.HierarchicalPath != scopeNode.HierarchicalPath) {
				Debug.WriteLine($"[Navigator] WARNING: Closed context not at top (expected: {currentTop.ScopeNode.SimpleName}, got: {scopeNode.SimpleName})");
				// Try to find it in the stack by path and remove it
				for (int i = _contextStack.Count - 1; i >= 0; i--) {
					if (_contextStack[i].ScopeNode.HierarchicalPath == scopeNode.HierarchicalPath) {
						_contextStack.RemoveAt(i);
						Debug.WriteLine($"[Navigator] Removed context from position {i}");
						break;
					}
				}
			} else {
				// Normal case: pop from top
				_contextStack.RemoveAt(_contextStack.Count - 1);
				Debug.WriteLine($"[Navigator] Popped context from top");
			}
			
			Debug.WriteLine($"[Navigator] Context stack depth: {_contextStack.Count}");
			
			// ✅ STEP 1: Clear old focus visuals immediately (the closing modal's overlay)
#if DEBUG
			_overlay?.HideFocusRect();
#endif

			// ✅ STEP 2: Defer parent focus restoration until AFTER WPF completes unload
			// This gives WPF time to finish dismantling the closing modal's visual tree.
			// During unload, elements exist but have invalid bounds (GetCenterDip returns null),
			// which causes all candidates to score Double.MaxValue and focus initialization to fail.
			if (CurrentContext != null && CurrentContext.ScopeNode.TryGetVisual(out var parentScopeElement)) {
				parentScopeElement.Dispatcher.BeginInvoke(
					DispatcherPriority.Loaded,  // After unload completes
					new Action(() => {
						try {
							Debug.WriteLine($"[Navigator] Restoring parent focus after context closure");
							Debug.WriteLine($"[Navigator] Current context scope: {CurrentContext.ScopeNode.SimpleName} @ {CurrentContext.ScopeNode.HierarchicalPath}");
							
							// ✅ STEP 3: Now the closing modal is fully removed from visual tree
							// Parent context's elements have valid bounds for navigation
							ValidateAndRestoreParentFocus();
							
							// ✅ STEP 4 (Phase 3): Switch to current context's page after focus restoration
							// This is deferred until after WPF completes unload to ensure clean page transition
							SwitchToCurrentContextPage();
						} catch (Exception ex) {
							Debug.WriteLine($"[Navigator] Error restoring parent focus: {ex.Message}");
						}
					})
				);
			} else {
				Debug.WriteLine($"[Navigator] No parent context to restore");
			}
		}

		/// <summary>
		/// Validates and restores parent context focus after a modal closes.
		/// Called AFTER WPF completes unload of the closing modal, so elements have valid bounds.
		/// 
		/// This is separated from OnModalGroupClosed to make the deferred execution pattern clear.
		/// </summary>
		private static void ValidateAndRestoreParentFocus()
		{
			if (CurrentContext == null) {
				Debug.WriteLine($"[Navigator] ValidateAndRestoreParentFocus: No current context");
				return;
			}
			
			var focusToRestore = CurrentContext.FocusedNode;
			
			// ✅ Validate that the focused node is still alive and in the parent context's scope
			// This prevents restoring focus to a node that was inside the closed dialog.
			if (focusToRestore != null) {
				bool isValid = false;
				
				// Check if visual is still alive
				if (focusToRestore.TryGetVisual(out var fe)) {
					// Check if element is still in visual tree
					if (PresentationSource.FromVisual(fe) != null) {
						// Check if element is in the parent context's scope (not the closed modal's scope)
						if (IsDescendantOf(focusToRestore, CurrentContext.ScopeNode)) {
						 isValid = true;
						} else {
							Debug.WriteLine($"[Navigator] Focused node '{focusToRestore.SimpleName}' is outside parent scope (was in closed modal) - clearing focus");
						}
					} else {
						Debug.WriteLine($"[Navigator] Focused node '{focusToRestore.SimpleName}' is no longer in visual tree - clearing focus");
					}
				} else {
					Debug.WriteLine($"[Navigator] Focused node '{focusToRestore.SimpleName}' visual reference is dead - clearing focus");
				}
				
				if (!isValid) {
					// Clear the invalid focused node
					focusToRestore.HasFocus = false;
					CurrentContext.FocusedNode = null;
					focusToRestore = null;
				}
			}
			
			// Now restore focus if we have a valid node, otherwise initialize
			if (focusToRestore != null) {
				Debug.WriteLine($"[Navigator] Restored focus to '{focusToRestore.SimpleName}'");
				SetFocusVisuals(focusToRestore);
			} else {
				Debug.WriteLine($"[Navigator] Parent context has no valid focus, initializing...");
				TryInitializeFocusIfNeeded();
			}
		}

		private static void OnWindowLayoutChanged(object sender, EventArgs e)
		{
#if DEBUG
			if (CurrentContext?.FocusedNode != null) {
				UpdateFocusRect(CurrentContext.FocusedNode);
			}
#endif
		}

		#endregion

		#region Navigation Rules


		#endregion

		#region Public API

		public static bool MoveInDirection(NavDirection dir)
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			var best = FindBestCandidateInDirection(CurrentContext.FocusedNode, dir);
			if (best == null) return false;

			return SetFocus(best);
		}

		public static bool ActivateFocusedNode()
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			return CurrentContext.FocusedNode.Activate();
		}

		/// <summary>
		/// Exits the current group/modal or interaction mode.
		/// Priority: Interaction mode > Modal dialog.
		/// Called when user presses Back/Escape key.
		/// </summary>
		public static bool ExitGroup()
		{
			if (CurrentContext == null) return false;

			// We're in interaction mode, exit the interaction but don't revert changes
			if (CurrentContext.ContextType == NavContextType.InteractiveControl)
			{
				return ExitInteractionMode(revertChanges: false);
			}

			// Otherwise Close the current Scope (modal dialog, DropDown menu, etc)
			return CurrentContext.ScopeNode.Close();
		}

		#endregion

		// ✅ Navigation Algorithm moved to Navigator.Navigation.cs

		#region Helper Methods

		/// <summary>
		/// Checks if a child node is a descendant of an ancestor node.
		/// Uses HierarchicalPath string comparison for efficiency and consistency.
		/// </summary>
		internal static bool IsDescendantOf(NavNode child, NavNode ancestor)
		{
			if (child == null || ancestor == null) return false;

			var ancestorPath = ancestor.HierarchicalPath;
			var childPath = child.HierarchicalPath;

			// Exact match (child IS the ancestor)
			if (childPath == ancestorPath) return true;

			// Descendant match (child is inside ancestor's scope)
			return childPath.StartsWith(ancestorPath + " > ");
		}

		/// <summary>
		/// Checks if a node is within the current active context scope.
		/// Scope resolution depends on the context type:
		/// - RootWindow: All descendants
		/// - ModalDialog: All descendants
		/// - InteractiveControl: Only the control itself (single element)
		/// </summary>
		internal static bool IsInActiveModalScope(NavNode node)
		{
			if (CurrentContext == null) return true;
			if (node == null) return false;

			// ✅ Type-specific scope resolution
			switch (CurrentContext.ContextType)
			{
				case NavContextType.RootWindow:
					// Root window - everything is in scope (descendants)
					return IsDescendantOf(node, CurrentContext.ScopeNode);
				
				case NavContextType.ModalDialog:
					// Modal dialog - descendants are in scope
					return IsDescendantOf(node, CurrentContext.ScopeNode);
				
				case NavContextType.PageSelector:
					// PageSelector - reuses parent's scope, descendants are in scope
					return IsDescendantOf(node, CurrentContext.ScopeNode);
				
				case NavContextType.InteractiveControl:
					// Interactive control - ONLY the control itself is in scope
					// This restricts navigation to just the slider/control being interacted with
					return ReferenceEquals(node, CurrentContext.ScopeNode);
				
				default:
					// Unknown type - fail safe to descendant check
					Debug.WriteLine($"[Navigator] WARNING: Unknown context type: {CurrentContext.ContextType}");
					return IsDescendantOf(node, CurrentContext.ScopeNode);
			}
		}

		internal static List<NavNode> GetCandidatesInScope()
		{
			return Observer.GetAllNavNodes()
				.Where(n => IsNavigableForSelection(n) && IsInActiveModalScope(n))
				.ToList();
		}

		private static bool IsNavigableForSelection(NavNode node)
		{
			if (!node.IsNavigable) return false;
			return !node.IsGroup;
		}

		private static bool AreInSameNonModalGroup(NavNode a, NavNode b)
		{
			var groupA = FindClosestNonModalGroup(a);
			var groupB = FindClosestNonModalGroup(b);
			if (groupA == null || groupB == null) return false;
			return groupA.HierarchicalPath == groupB.HierarchicalPath;
		}

		private static NavNode FindClosestNonModalGroup(NavNode node)
		{
			if (node == null) return null;

			var current = node.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (parentNode.IsGroup)
				{
					if (parentNode.IsModal) return null;
					return parentNode;
				}
				current = parentNode.Parent;
			}
			
			return null;
		}

		static void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				OnDebugHotkey(e);
			}
		}

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a == null || b == null) return false;
			if (a.Parent == null || b.Parent == null) return false;
			
			if (!a.Parent.TryGetTarget(out var aParent)) return false;
			if (!b.Parent.TryGetTarget(out var bParent)) return false;
			
			return ReferenceEquals(aParent, bParent);
		}

		// ✅ Focus Guard moved to Navigator.FocusGuard.cs (experimental, currently disabled)
		
		// ✅ Interaction Mode (Slider logic) moved to Navigator.InteractionMode.cs

		/// <summary>
		/// Determines the StreamDeck page name for a given node.
		/// Used when creating contexts to assign the appropriate page.
		/// 
		/// Priority:
		/// 1. Node's PageName property (from classification rules)
		/// 2. Type-based fallback for interactive controls (Slider, DoubleSlider, etc.)
		/// 3. Context-type-based default
		/// </summary>
		/// <param name="node">The node to determine the page for</param>
		/// <param name="contextType">The type of context being created</param>
		/// <returns>The page name, or null if no page should be assigned</returns>
		private static string DeterminePageForNode(NavNode node, NavContextType contextType)
		{
			if (node == null) return null;
			
			// Priority 1: Check if node has explicit PageName (from classification)
			if (!string.IsNullOrEmpty(node.PageName))
			{
				Debug.WriteLine($"[Navigator] Using node's PageName: '{node.PageName}' for {node.SimpleName}");
				return node.PageName;
			}
			
			// Priority 2: Type-based assignment
			if (node.TryGetVisual(out var element))
			{
				var typeName = element.GetType().Name;
				
				// Interactive controls get type-specific pages
				if (typeName == "Slider" || typeName == "FormattedSlider")
					return "Slider";
				else if (typeName == "DoubleSlider")
					return "DoubleSlider";
				else if (typeName == "RoundSlider")
					return "RoundSlider";
				// PopupRoot gets UpDown page (vertical navigation for menus)
				else if (typeName == "PopupRoot")
					return "UpDown";
				
				if (VerboseNavigationDebug)
				{
					Debug.WriteLine($"[Navigator] Type-based page check for {typeName}: no specific page");
				}
			}
			
			// Priority 3: Context-type-based defaults
			switch (contextType)
			{
				case NavContextType.RootWindow:
					return "Navigation";  // Root always gets Navigation page
				
				case NavContextType.ModalDialog:
				case NavContextType.PageSelector:
				case NavContextType.InteractiveControl:
					return "Navigation";  // Default fallback
				
				default:
					Debug.WriteLine($"[Navigator] WARNING: Unknown context type: {contextType}");
					return "Navigation";
			}
		}

		/// <summary>
		/// Switches StreamDeck to the page associated with the current context.
		/// This is the ONLY method that should perform page switches based on context stack state.
		/// 
		/// Called after:
		/// - Context push/pop operations in NodesUpdated (batched)
		/// - Modal close + focus restoration (deferred)
		/// - Interactive mode exit (immediate)
		/// 
		/// Does nothing if:
		/// - No current context exists
		/// - Current context has no PageName assigned
		/// - StreamDeck client is not connected
		/// </summary>
		private static void SwitchToCurrentContextPage()
		{
			if (CurrentContext == null)
			{
				Debug.WriteLine("[Navigator] No current context - cannot switch page");
				return;
			}
			
			if (string.IsNullOrEmpty(CurrentContext.PageName))
			{
				Debug.WriteLine($"[Navigator] Current context ({CurrentContext.ContextType}) has no PageName - skipping page switch");
				return;
			}
			
			if (_streamDeckClient == null)
			{
				return;  // Silent fail if StreamDeck not connected
			}
			
			Debug.WriteLine($"[Navigator] Switching to page: '{CurrentContext.PageName}' (context: {CurrentContext.ContextType}, scope: {CurrentContext.ScopeNode.SimpleName})");
			_streamDeckClient.SwitchPage(CurrentContext.PageName);
		}

		/// <summary>
		/// Restores the previous StreamDeck page before confirmation was requested.
		/// Uses SDPClient's page history tracking for reliable restoration.
		/// </summary>
		private static void RestorePreviousPage()
		{
			if (_streamDeckClient == null) return;
			
			// ✅ Use SDPClient's built-in page history tracking
			if (!_streamDeckClient.RestorePreviousPage())
			{
				// Fallback: If history is empty, restore to current context's page
				SwitchToCurrentContextPage();
			}
		}

		/// <summary>
		/// Called when a PageSelector node becomes active (visible in the current context).
		/// Creates a new PageSelector context and switches StreamDeck to the appropriate page.
		/// 
		/// PageSelectors are non-modal elements (like tab content, Frame content) that have a PageName property.
		/// They reuse the parent context's navigation scope but switch the StreamDeck page.
		/// </summary>
		private static void OnPageSelectorActivated(NavNode pageSelectorNode)
		{
			if (pageSelectorNode == null || string.IsNullOrEmpty(pageSelectorNode.PageName)) 
				return;
			
			Debug.WriteLine($"[Navigator] PageSelector activated: {pageSelectorNode.SimpleName} → page '{pageSelectorNode.PageName}'");
			
			// Find the actual navigation scope by walking up the context stack
			var scopeNode = FindNavigationScope();
			if (scopeNode == null)
			{
				Debug.WriteLine($"[Navigator] WARNING: Could not find navigation scope for PageSelector (context stack might be empty)");
				return;
			}
			
			// Create PageSelector context (reuses parent's scope, but different page)
			var context = new NavContext(scopeNode, NavContextType.PageSelector)
			{
				PageSelectorNode = pageSelectorNode,
				PageName = pageSelectorNode.PageName,
				FocusedNode = CurrentContext?.FocusedNode  // Inherit focus from parent
			};
			
			_contextStack.Add(context);
			Debug.WriteLine($"[Navigator] Pushed PageSelector context: page='{context.PageName}', scope={scopeNode.SimpleName}, stack depth={_contextStack.Count}");
			
			// Switch StreamDeck page
			_streamDeckClient?.SwitchPage(pageSelectorNode.PageName);
		}

		/// <summary>
		/// Called when a PageSelector node is removed from the visual tree.
		/// Pops the associated PageSelector context and restores the previous context's page.
		/// </summary>
		private static void OnPageSelectorDeactivated(NavNode pageSelectorNode)
		{
			if (pageSelectorNode == null) return;
			
			Debug.WriteLine($"[Navigator] PageSelector deactivated: {pageSelectorNode.SimpleName}");
			
			// Find the context for this PageSelector in the stack (walk backwards to find most recent)
			for (int i = _contextStack.Count - 1; i >= 0; i--)
			{
				var context = _contextStack[i];
				if (context.ContextType == NavContextType.PageSelector &&
					ReferenceEquals(context.PageSelectorNode, pageSelectorNode))
				{
					_contextStack.RemoveAt(i);
					Debug.WriteLine($"[Navigator] Popped PageSelector context from position {i}, stack depth now: {_contextStack.Count}");
					
					// Restore previous context's page
					if (CurrentContext != null)
					{
						SwitchToContextPage(CurrentContext);
					}
					
					return;
				}
			}
			
			Debug.WriteLine($"[Navigator] WARNING: No context found for deactivated PageSelector '{pageSelectorNode.SimpleName}'");
		}

		/// <summary>
		/// Finds the navigation scope for a new PageSelector context by walking up the context stack.
		/// Returns the scope of the first non-PageSelector context found (RootWindow or ModalDialog).
		/// PageSelector contexts reuse their parent's navigation scope - they don't create new navigation boundaries.
		/// </summary>
		private static NavNode FindNavigationScope()
		{
			// Walk backwards through context stack to find the first actual navigation scope
			// Skip PageSelector contexts (they don't have their own scope)
			for (int i = _contextStack.Count - 1; i >= 0; i--)
			{
				var context = _contextStack[i];
				
				// Found a context that defines a navigation scope
				if (context.ContextType == NavContextType.RootWindow ||
					context.ContextType == NavContextType.ModalDialog)
				{
					return context.ScopeNode;
				}
				
				// InteractiveControl has a scope but it's too narrow for PageSelectors
				// Keep walking up to find the actual navigation context
				// PageSelector contexts are transparent - keep walking
			}
			
			// Should never reach here if stack is properly initialized with RootWindow
			Debug.WriteLine($"[Navigator] WARNING: FindNavigationScope reached end of stack without finding RootWindow or ModalDialog");
			return null;
		}

		/// <summary>
		/// Switches StreamDeck to the appropriate page for the given context.
		/// Used when contexts are activated/deactivated.
		/// </summary>
		private static void SwitchToContextPage(NavContext context)
		{
			if (context == null) return;
			
			// ✅ Use context's PageName directly (assigned at creation time)
			if (!string.IsNullOrEmpty(context.PageName))
			{
				Debug.WriteLine($"[Navigator] Switching to page: {context.PageName} (context type: {context.ContextType})");
				_streamDeckClient?.SwitchPage(context.PageName);
			}
			else
			{
				// Fallback: Context has no PageName, skip page switch
				Debug.WriteLine($"[Navigator] Context {context.ContextType} has no PageName - skipping page switch");
			}
		}

		#endregion
	}
}
