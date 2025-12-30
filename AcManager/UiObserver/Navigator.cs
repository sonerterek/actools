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
		/// Future: Could be extended for ComboBox dropdowns, menu submenus, etc.
		/// </summary>
		InteractiveControl
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

			// Initialize StreamDeck early, since it is expensive but async
			InitializeStreamDeck();

			// Initialize navigation rules
			InitializeNavigationRules();

			// Disable tooltips globally during navigation
			DisableTooltips();

			// Subscribe to Observer events
			Observer.ModalGroupOpened += OnModalGroupOpened;
			Observer.ModalGroupClosed += OnModalGroupClosed;
			Observer.WindowLayoutChanged += OnWindowLayoutChanged;
			Observer.NodeUnloaded += OnNodeUnloaded;  // ✅ NEW: Subscribe to node unload events
			
			// ✅ FIX: Create overlay BEFORE starting Observer to avoid race condition
			// When Observer discovers MainWindow and fires ModalGroupOpened, the overlay
			// must already exist so focus initialization can show the blue rectangle
			EnsureOverlay();
			
			// Startup Observer (it will hook windows itself and fire ModalGroupOpened)
			Observer.Initialize();

			// ✅ NEW: Install focus guard to prevent WPF from stealing focus to non-navigable elements
			InstallFocusGuard();

			// Register keyboard handler
			try {
				EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, 
					new KeyEventHandler(OnPreviewKeyDown), true);
			} catch { }
		}
		
		private static void InitializeNavigationRules()
		{
			var rules = new[] {
				// Exclude scrollbars in popups/menus
				"EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
				"EXCLUDE: *:PopupRoot > ** > *:BetterScrollBar",

				// Exclude text or fancy menu items
				"EXCLUDE: ** > *:HistoricalTextBox > **",
				"EXCLUDE: ** > *:LazyMenuItem > **",
				"EXCLUDE: ** > *:ModernTabSplitter",
				"EXCLUDE: *:PopupRoot > ** > *:MenuItem",

				// CRITICAL: Exclude debug overlay from navigation tracking to prevent feedback loop
				"EXCLUDE: *:HighlightOverlay > **",

				// Exclude main menu and content frame from navigation
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu",
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > ContentFrame:ModernFrame",
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > ContentFrame:ModernFrame > (unnamed):Border > (unnamed):Cell > (unnamed):TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > This:QuickDrive > (unnamed):Border > (unnamed):ContentPresenter > MainGrid:Grid > (unnamed):Grid > ModeTab:ModernTab > (unnamed):DockPanel > PART_Frame:ModernFrame",
				"EXCLUDE: (unnamed):SelectTrackDialog > (unnamed):Border > (unnamed):Cell > (unnamed):AdornerDecorator > PART_Border:Border > (unnamed):Cell > (unnamed):DockPanel > (unnamed):Border > PART_Content:TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > (unnamed):Grid > (unnamed):DockPanel > (unnamed):AdornerDecorator > Tabs:ModernTab",
				"EXCLUDE: (unnamed):SelectTrackDialog > (unnamed):Border > (unnamed):Cell > (unnamed):AdornerDecorator > PART_Border:Border > (unnamed):Cell > (unnamed):DockPanel > (unnamed):Border > PART_Content:TransitioningContentControl > (unnamed):Cell > CurrentWrapper:Border > CurrentContentPresentationSite:ContentPresenter > (unnamed):Grid > (unnamed):DockPanel > (unnamed):AdornerDecorator > Tabs:ModernTab > (unnamed):DockPanel > PART_Frame:ModernFrame",

				"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true",
				"CLASSIFY: ** > *:SelectTrackDialog => role=group; modal=true",
				"CLASSIFY: ** > PART_SystemButtonsPanel:StackPanel => role=group; modal=false",
				"CLASSIFY: ** > PART_TitleBar:DockPanel > *:ItemsControl => role=group; modal=false",
			};

			try {
				NavNode.PathFilter.ParseRules(rules);
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to initialize navigation rules: {ex}");
			}
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

		internal static void EnsureOverlay()
		{
			if (_overlay == null) {
				try {
					_overlay = new HighlightOverlay();
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] Failed to create overlay: {ex.Message}");
				}
			}
		}

		#endregion

		#region Event Handlers

		/// <summary>
		/// Called by Observer when any NavNode's element is being unloaded from the visual tree.
		/// We check if this is our currently focused node and handle the in-modal navigation case.
		/// </summary>
		private static void OnNodeUnloaded(NavNode unloadingNode)
		{
			if (CurrentContext == null) return;

			// Check if the unloading node is our currently focused node
			if (!ReferenceEquals(CurrentContext.FocusedNode, unloadingNode)) {
				// Not our focused node - ignore
				return;
			}

			// Our focused node is being unloaded!
			Debug.WriteLine($"[Navigator] ⚠ Focused node is unloading: {unloadingNode.SimpleName} - will re-initialize focus when new content loads");

			// Clear the focused node (it's about to be dead)
			unloadingNode.HasFocus = false;
			CurrentContext.FocusedNode = null;

			// Schedule focus re-initialization for after the new content loads
			// Use a short delay to allow the new content to be discovered by Observer
			Application.Current?.Dispatcher.BeginInvoke(
				DispatcherPriority.Loaded,
				new Action(() => {
					try {
						// Re-check that we're still in the same modal context and still have no focus
						if (CurrentContext == null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus re-init skipped - no current context");
							}
							return;
						}

						if (CurrentContext.FocusedNode != null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus re-init skipped - focus already set to '{CurrentContext.FocusedNode.SimpleName}'");
							}
							return;
						}

						// Try to initialize focus to the new content
						Debug.WriteLine($"[Navigator] Re-initializing focus after in-modal navigation");
						TryInitializeFocusIfNeeded();
					} catch (Exception ex) {
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Error re-initializing focus: {ex.Message}");
						}
					}
				})
			);
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
			
			// Create context FIRST (so GetCandidatesInScope works correctly)
			_contextStack.Add(new NavContext(scopeNode, contextType, focusedNode: null));
			var contextDepth = _contextStack.Count;
			Debug.WriteLine($"[Navigator] Context stack depth: {contextDepth}");
			Debug.WriteLine($"[Navigator] Context type: {contextType}");

			// ✓ NEW: Switch StreamDeck page based on modal type
			SwitchStreamDeckPageForModal(scopeNode);

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
			_overlay?.HideFocusRect();
			
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
							
							// ✅ STEP 4: Switch StreamDeck page for the now-current context
							// When a modal closes, we need to switch back to the parent context's page
							SwitchStreamDeckPageForModal(CurrentContext.ScopeNode);
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
			if (CurrentContext?.FocusedNode != null) {
				UpdateFocusRect(CurrentContext.FocusedNode);
			}
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
		/// For interaction mode: Reverts changes (Cancel behavior).
		/// </summary>
		public static bool ExitGroup()
		{
			if (CurrentContext == null) return false;
			
			// Priority 1: Exit interaction mode if active (with Cancel - revert changes)
			if (CurrentContext.ContextType == NavContextType.InteractiveControl)
			{
				return ExitInteractionMode(revertChanges: true);
			}
			
			// Priority 2: Close modal (original behavior)
			return CurrentContext.ScopeNode.Close();
		}

		#endregion

		#region Navigation Rules


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

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a == null || b == null) return false;
			if (a.Parent == null || b.Parent == null) return false;
			
			if (!a.Parent.TryGetTarget(out var aParent)) return false;
			if (!b.Parent.TryGetTarget(out var bParent)) return false;
			
			return ReferenceEquals(aParent, bParent);
		}

		#endregion

		#region Keyboard Input

		private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
#if DEBUG
			// Keyboard navigation only available in DEBUG builds for development/testing
			// StreamDeck is the primary input method in release builds
			if (Keyboard.Modifiers != ModifierKeys.None) return;

			switch (e.Key)
			{
				case Key.Up:
					MoveInDirection(NavDirection.Up);
					e.Handled = true;
					break;
				case Key.Down:
					MoveInDirection(NavDirection.Down);
					e.Handled = true;
					break;
				case Key.Left:
					MoveInDirection(NavDirection.Left);
					e.Handled = true;
					break;
				case Key.Right:
					MoveInDirection(NavDirection.Right);
					e.Handled = true;
					break;
				case Key.Enter:
				case Key.Space:
					ActivateFocusedNode();
					e.Handled = true;
					break;
				case Key.Escape:
					ExitGroup();
					e.Handled = true;
					break;
			}
#endif
		}

		// Partial method declaration - implemented in Navigator.Debug.cs
		static partial void OnDebugHotkey(KeyEventArgs e);

		#endregion

		#region Focus Guard

		/// <summary>
		/// Installs a global focus monitor to prevent WPF from stealing focus to non-navigable elements.
		/// 
		/// Problem:
		/// WPF automatically moves keyboard focus to text inputs (TextBox, etc.) when they load or become visible.
		/// These elements are excluded from our navigation system (not NavNodes), so we lose track of focus.
		/// 
		/// Solution:
		/// Monitor WPF's focus changes globally. If focus moves to a non-tracked element, restore it
		/// to our last known focused NavNode.
		/// 
		/// This acts as a "focus guard" that keeps keyboard focus aligned with our navigation system.
		/// 
		/// ✓ DISABLED: Focus guard is currently disabled as it was causing issues with menu navigation.
		/// The original issue (SelectCarDialog selection problems) was due to Shift+Ctrl keys being held down,
		/// not focus stealing. Keeping code for potential future use.
		/// </summary>
		private static void InstallFocusGuard()
		{
			// ✓ DISABLED: Focus guard functionality disabled
			// Uncomment below to re-enable
			/*
			try {
				// Subscribe to global keyboard focus changes (tunneling event - fires before Loaded/GotFocus)
				EventManager.RegisterClassHandler(
					typeof(UIElement),
					Keyboard.GotKeyboardFocusEvent,
					new KeyboardFocusChangedEventHandler(OnGlobalKeyboardFocusChanged),
					handledEventsToo: true  // ← Monitor even if event is marked as handled
				);

				Debug.WriteLine("[Navigator] Focus guard installed");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to install focus guard: {ex.Message}");
			}
			*/
			
			Debug.WriteLine("[Navigator] Focus guard DISABLED (keeping code for future use)");
		}

		/// <summary>
		/// Global keyboard focus change handler.
		/// Restores focus to our tracked NavNode if WPF tries to focus a non-navigable element.
		/// 
		/// ✓ DISABLED: This handler is not currently hooked up (see InstallFocusGuard).
		/// </summary>
		private static void OnGlobalKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
		{
			// ✓ DISABLED: Focus guard functionality disabled
			// This method is kept for future use but not currently subscribed to events
			return;
			
			#pragma warning disable CS0162 // Unreachable code detected
			
			// Get the newly focused element
			if (!(e.NewFocus is FrameworkElement newFocusedElement)) {
				// Focus moved to non-FrameworkElement (or null) - ignore
				return;
			}

			// Check if this element is a tracked NavNode
			if (Observer.TryGetNavNode(newFocusedElement, out var navNode)) {
				// Focus moved to a tracked NavNode - this is expected behavior
				// Update our focus tracking to match WPF's focus
				if (CurrentContext != null && !ReferenceEquals(CurrentContext.FocusedNode, navNode)) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Focus moved to tracked NavNode: {navNode.SimpleName}");
					}

					// Sync our tracking with WPF's focus (don't trigger visual update - already focused)
					var oldNode = CurrentContext.FocusedNode;
					if (oldNode != null) oldNode.HasFocus = false;

					navNode.HasFocus = true;
					CurrentContext.FocusedNode = navNode;

					try { FocusChanged?.Invoke(oldNode, navNode); } catch { }
				}
				return;
			}

			// ❌ Focus moved to a NON-tracked element (e.g., excluded TextBox)

			if (CurrentContext?.FocusedNode == null) {
				// No previous focus to restore - let WPF do its thing
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Focus moved to non-tracked element '{newFocusedElement.GetType().Name}' (no previous focus to restore)");
				}
				return;
			}

			// CHECK IF OUR FOCUSED NAVNODE IS STILL VALID
			if (!CurrentContext.FocusedNode.TryGetVisual(out var ourFocusedElement)) {
				// Our focused element is DEAD - OnNodeUnloaded should have already handled this
				Debug.WriteLine($"[Navigator] Focus stolen but our focused NavNode is dead (element unloaded) - allowing focus to move");

				CurrentContext.FocusedNode.HasFocus = false;
				CurrentContext.FocusedNode = null;
				return;
			}

			// CHECK IF ELEMENT IS STILL IN VISUAL TREE
			if (PresentationSource.FromVisual(ourFocusedElement) == null) {
				Debug.WriteLine($"[Navigator] Focus stolen but our focused element is no longer in visual tree - allowing focus to move");

				CurrentContext.FocusedNode.HasFocus = false;
				CurrentContext.FocusedNode = null;
				return;
			}

			// Element is alive and in visual tree - restore focus to it
			Debug.WriteLine($"[Navigator] ⚠ Focus stolen by non-tracked '{newFocusedElement.GetType().Name}' - restoring to '{CurrentContext.FocusedNode.SimpleName}'");

			// Use Dispatcher to avoid re-entrancy issues (focus change during focus change)
			ourFocusedElement.Dispatcher.BeginInvoke(
				DispatcherPriority.Input,  // High priority - restore focus ASAP
				new Action(() => {
					try {
						// ✅ UPDATED: Re-check that our focused node is STILL valid
						if (CurrentContext?.FocusedNode == null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - focused node cleared");
							}
							return;
						}

						if (!CurrentContext.FocusedNode.TryGetVisual(out var elementToFocus)) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - element died");
							}
							CurrentContext.FocusedNode.HasFocus = false;
							CurrentContext.FocusedNode = null;
							return;
						}

						if (PresentationSource.FromVisual(elementToFocus) == null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - element removed from tree");
							}
							CurrentContext.FocusedNode.HasFocus = false;
							CurrentContext.FocusedNode = null;
							return;
						}

						// Restore keyboard focus
						Keyboard.Focus(elementToFocus);

						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Focus restored to '{CurrentContext.FocusedNode.SimpleName}'");
						}
					} catch (Exception ex) {
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Failed to restore focus: {ex.Message}");
						}
					}
				})
			);
			
			#pragma warning restore CS0162 // Unreachable code detected
		}

		#endregion

		#region Interaction Mode

		/// <summary>
		/// Enters interaction mode for a non-modal control (Slider, DoubleSlider, etc.).
		/// Creates a new navigation context scoped to the single control.
		/// 
		/// This allows controls to create focused "modes" without requiring a popup/dialog.
		/// Example: Slider activation switches to Slider page, locks navigation to the slider.
		/// </summary>
		/// <param name="control">The control to enter interaction mode for</param>
		/// <param name="pageName">Optional StreamDeck page name (auto-detected if null)</param>
		/// <returns>True if interaction mode was entered successfully</returns>
		public static bool EnterInteractionMode(NavNode control, string pageName = null)
		{
			if (control == null)
			{
				Debug.WriteLine("[Navigator] EnterInteractionMode failed: control is null");
				return false;
			}
			
			if (!control.IsNavigable)
			{
				Debug.WriteLine($"[Navigator] EnterInteractionMode failed: {control.SimpleName} is not navigable");
				return false;
			}
			
			// ✅ Check if already in interaction mode for this control
			if (CurrentContext != null && 
		    CurrentContext.ContextType == NavContextType.InteractiveControl &&
		    ReferenceEquals(CurrentContext.ScopeNode, control))
			{
				Debug.WriteLine($"[Navigator] Already in interaction mode for {control.SimpleName} - ignoring duplicate activation");
				return true;  // Return true since we're already in the desired state
			}
			
			// ✅ Capture original value for Cancel functionality
			object originalValue = null;
			if (control.TryGetVisual(out var fe))
			{
				if (fe is Slider slider)
				{
					originalValue = slider.Value;
				}
				else
				{
					var typeName = fe.GetType().Name;
					if (typeName == "DoubleSlider" || typeName == "RoundSlider")
					{
						var valueProperty = fe.GetType().GetProperty("Value");
						if (valueProperty != null)
						{
							originalValue = valueProperty.GetValue(fe);
						}
					}
				}
				
				Debug.WriteLine($"[Navigator] Captured original value: {originalValue}");
			}
			
			// Create interaction context (focus set to control itself)
			var context = new NavContext(control, NavContextType.InteractiveControl, focusedNode: control)
			{
				OriginalValue = originalValue
			};
			
			_contextStack.Add(context);
			var depth = _contextStack.Count;
			
			Debug.WriteLine($"[Navigator] Entered interaction mode: {control.SimpleName}");
			Debug.WriteLine($"[Navigator] Context stack depth: {depth}");
			Debug.WriteLine($"[Navigator] Context type: {NavContextType.InteractiveControl}");
			
			// Switch StreamDeck page
			if (string.IsNullOrEmpty(pageName) && control.TryGetVisual(out var element))
			{
				pageName = GetBuiltInPageForControl(element);
			}
			
			if (!string.IsNullOrEmpty(pageName) && _streamDeckClient != null)
			{
				Debug.WriteLine($"[Navigator] Switching to page: {pageName}");
				_streamDeckClient.SwitchPage(pageName);
			}
			
			// Show focus visuals (blue highlight on the control)
			SetFocusVisuals(control);
			
			return true;
		}

		/// <summary>
		/// Exits interaction mode and restores previous navigation context.
		/// Called when user presses Back/Escape (cancel) or MouseLeft (confirm).
		/// </summary>
		/// <param name="revertChanges">If true, reverts control to original value (Cancel). If false, keeps current value (Confirm).</param>
		/// <returns>True if interaction mode was exited successfully</returns>
		public static bool ExitInteractionMode(bool revertChanges = false)
		{
			if (CurrentContext == null)
			{
				Debug.WriteLine("[Navigator] ExitInteractionMode failed: no current context");
				return false;
			}
			
			if (CurrentContext.ContextType != NavContextType.InteractiveControl)
			{
				Debug.WriteLine($"[Navigator] ExitInteractionMode failed: current context is {CurrentContext.ContextType}, not InteractiveControl");
				return false;
			}
			
			var control = CurrentContext.ScopeNode;
			
			// ✅ Revert value if requested (Cancel)
			if (revertChanges && CurrentContext.OriginalValue != null)
			{
				if (control.TryGetVisual(out var fe))
				{
					try
					{
						if (fe is Slider slider)
						{
							var oldValue = slider.Value;
							slider.Value = (double)CurrentContext.OriginalValue;
							Debug.WriteLine($"[Navigator] Reverted slider value: {oldValue:F2} → {slider.Value:F2}");
						}
						else
						{
							var typeName = fe.GetType().Name;
							if (typeName == "DoubleSlider" || typeName == "RoundSlider")
							{
								var valueProperty = fe.GetType().GetProperty("Value");
								if (valueProperty != null)
								{
									var oldValue = valueProperty.GetValue(fe);
									valueProperty.SetValue(fe, CurrentContext.OriginalValue);
									Debug.WriteLine($"[Navigator] Reverted {typeName} value: {oldValue} → {CurrentContext.OriginalValue}");
								}
							}
						}
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[Navigator] Failed to revert value: {ex.Message}");
					}
				}
			}
			else if (!revertChanges)
			{
				Debug.WriteLine($"[Navigator] Confirmed changes (keeping current value)");
			}
			
			// Pop the interaction context
			_contextStack.RemoveAt(_contextStack.Count - 1);
			var depth = _contextStack.Count;
			
			Debug.WriteLine($"[Navigator] Exited interaction mode: {control.SimpleName} (revert={revertChanges})");
			Debug.WriteLine($"[Navigator] Context stack depth: {depth}");
			
			// Restore parent context focus
			if (CurrentContext != null)
			{
				// Try to restore focus to the control we just exited
				// (user is still "at" that location, just not in interaction mode)
				if (CurrentContext.FocusedNode == null || !ReferenceEquals(CurrentContext.FocusedNode, control))
				{
					SetFocus(control);
				}
				else
				{
					// Focus already on control, just update visuals/page
					SetFocusVisuals(control);
				}
				
				// Switch StreamDeck page for parent context
				SwitchStreamDeckPageForModal(CurrentContext.ScopeNode);
			}
			else
			{
				// No parent context - clear visuals
				_overlay?.HideFocusRect();
			}
			
			return true;
		}

		/// <summary>
		/// Determines the optimal built-in StreamDeck page for a given control type.
		/// Returns null if no special page is needed (use default Navigation page).
		/// 
		/// This is separate from config-based page mapping (which has higher priority).
		/// </summary>
		private static string GetBuiltInPageForControl(FrameworkElement element)
		{
			if (element == null) return null;
			
			var typeName = element.GetType().Name;
			
			// ✅ Slider family - optimize for value adjustment
			if (element is Slider)
				return "Slider";  // Left/Right only
			
			if (typeName == "DoubleSlider")
				return "DoubleSlider";  // Vertical coarse + Horizontal fine
			
			if (typeName == "RoundSlider")
				return "RoundSlider";  // All 4 directions (circular)
			
			// ✅ Menu items - optimize for vertical navigation
			if (element is MenuItem)
				return "UpDown";  // Up/Down only
			
			// ✅ List items - context-dependent
			// (You might want to use UpDown for long lists, Navigation for grids)
			// For now, return null and use default Navigation page
			if (element is ListBoxItem)
				return null;
			
			// ✅ Future control types:
			// if (element is ComboBox comboBox && comboBox.IsDropDownOpen)
			//     return "UpDown";  // Dropdown is vertical
			//
			// if (element is TreeViewItem)
			//     return "TreeNav";  // Custom tree navigation page
			//
			// if ( element is DataGridCell)
			//     return "GridNav";  // Custom grid navigation page
			
			return null;  // Use default Navigation page
		}

		/// <summary>
		/// Adjusts the value of the currently focused slider control.
		/// Only works when in InteractiveControl context (slider interaction mode).
		/// Supports Slider, DoubleSlider, and RoundSlider types.
		/// </summary>
		/// <param name="direction">Direction to adjust (Left/Right for horizontal, Up/Down for vertical)</param>
		private static void AdjustSliderValue(NavDirection direction)
		{
			if (CurrentContext?.ContextType != NavContextType.InteractiveControl)
			{
				Debug.WriteLine("[Navigator] AdjustSliderValue: Not in interaction mode");
				return;
			}
			
			var control = CurrentContext.ScopeNode;
			if (!control.TryGetVisual(out var fe))
			{
				Debug.WriteLine("[Navigator] AdjustSliderValue: Visual reference dead");
				return;
			}
			
			Debug.WriteLine($"[Navigator] AdjustSliderValue: Adjusting {fe.GetType().Name} in direction {direction}");
			
			try
			{
				// Handle standard WPF Slider
				if (fe is Slider slider)
				{
					var step = slider.LargeChange > 0 ? slider.LargeChange : slider.SmallChange;
					var oldValue = slider.Value;
					
					if (direction == NavDirection.Left)
						slider.Value = Math.Max(slider.Minimum, slider.Value - step);
					else if (direction == NavDirection.Right)
						slider.Value = Math.Min(slider.Maximum, slider.Value + step);
					
					Debug.WriteLine($"[Navigator] Slider adjusted: {oldValue:F2} → {slider.Value:F2} (step={step:F2})");
					return;
				}
				
				// Handle custom slider types via reflection (DoubleSlider, RoundSlider)
				var typeName = fe.GetType().Name;
				
				// DoubleSlider: Up/Down for coarse adjustment, Left/Right for fine adjustment
				if (typeName == "DoubleSlider")
				{
					var valueProperty = fe.GetType().GetProperty("Value");
					var minProperty = fe.GetType().GetProperty("Minimum");
					var maxProperty = fe.GetType().GetProperty("Maximum");
					var largeChangeProperty = fe.GetType().GetProperty("LargeChange");
					var smallChangeProperty = fe.GetType().GetProperty("SmallChange");
				
					if (valueProperty != null && minProperty != null && maxProperty != null)
					{
						var currentValue = (double)valueProperty.GetValue(fe);
						var min = (double)minProperty.GetValue(fe);
						var max = (double)maxProperty.GetValue(fe);
						
						// Use LargeChange for Up/Down (coarse), SmallChange for Left/Right (fine)
						double step;
						if (direction == NavDirection.Up || direction == NavDirection.Down)
						{
							// Coarse adjustment
							step = largeChangeProperty != null ? (double)largeChangeProperty.GetValue(fe) : 1.0;
						}
						else
						{
							// Fine adjustment
							step = smallChangeProperty != null ? (double)smallChangeProperty.GetValue(fe) : 0.1;
						}
						
						// Calculate new value
						double newValue = currentValue;
						if (direction == NavDirection.Left || direction == NavDirection.Down)
							newValue = Math.Max(min, currentValue - step);
						else if (direction == NavDirection.Right || direction == NavDirection.Up)
							newValue = Math.Min(max, currentValue + step);
						
						valueProperty.SetValue(fe, newValue);
						Debug.WriteLine($"[Navigator] DoubleSlider adjusted: {currentValue:F2} → {newValue:F2} (step={step:F2}, mode={((direction == NavDirection.Up || direction == NavDirection.Down) ? "coarse" : "fine")})");
					}
					return;
				}
				
				// RoundSlider: All 4 directions supported (circular control)
				if (typeName == "RoundSlider")
				{
					var valueProperty = fe.GetType().GetProperty("Value");
					var minProperty = fe.GetType().GetProperty("Minimum");
					var maxProperty = fe.GetType().GetProperty("Maximum");
					var largeChangeProperty = fe.GetType().GetProperty("LargeChange");
					var smallChangeProperty = fe.GetType().GetProperty("SmallChange");
				
					if (valueProperty != null && minProperty != null && maxProperty != null)
					{
						var currentValue = (double)valueProperty.GetValue(fe);
						var min = (double)minProperty.GetValue(fe);
						var max = (double)maxProperty.GetValue(fe);
						
						// Use LargeChange for Up/Down, SmallChange for Left/Right
						double step;
						if (direction == NavDirection.Up || direction == NavDirection.Down)
						{
							step = largeChangeProperty != null ? (double)largeChangeProperty.GetValue(fe) : 1.0;
						}
						else
						{
							step = smallChangeProperty != null ? (double)smallChangeProperty.GetValue(fe) : 0.1;
						}
						
						// RoundSlider semantics: Right/Up = increase, Left/Down = decrease
						double newValue = currentValue;
						if (direction == NavDirection.Left || direction == NavDirection.Down)
							newValue = Math.Max(min, currentValue - step);
						else if (direction == NavDirection.Right || direction == NavDirection.Up)
							newValue = Math.Min(max, currentValue + step);
						
						valueProperty.SetValue(fe, newValue);
						Debug.WriteLine($"[Navigator] RoundSlider adjusted: {currentValue:F2} → {newValue:F2} (step={step:F2})");
					}
					return;
				}
				
				Debug.WriteLine($"[Navigator] AdjustSliderValue: Unsupported control type '{typeName}'");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] AdjustSliderValue ERROR: {ex.Message}");
			}
		}

		#endregion
	}
}
