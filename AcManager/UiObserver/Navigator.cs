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

		#region Focus Initialization

		/// <summary>
		/// Initialize focus in the current context if:
		/// 1. Current context exists
		/// 2. Current context has no focus
		/// 3. There are navigable candidates available in scope
		/// 
		/// This is called when modal lifecycle events fire (ModalGroupOpened/Closed).
		/// All nodes within the modal scope are already discovered when this executes,
		/// ensuring complete information for optimal focus selection.
		/// </summary>
		private static void TryInitializeFocusIfNeeded()
		{
			if (CurrentContext == null) {
				Debug.WriteLine($"[Navigator] TryInitializeFocusIfNeeded: CurrentContext is null!");
				return;
			}
			
			if (CurrentContext.FocusedNode != null) {
				Debug.WriteLine($"[Navigator] TryInitializeFocusIfNeeded: Already has focus: {CurrentContext.FocusedNode.SimpleName}");
				return;
			}
			
			Debug.WriteLine($"[Navigator] Finding first navigable in scope '{CurrentContext.ScopeNode.SimpleName}'...");
			
			// Log scope node details
			if (CurrentContext.ScopeNode.TryGetVisual(out var scopeVisual)) {
				Debug.WriteLine($"[Navigator] ScopeNode type: {scopeVisual.GetType().Name}");
			} else {
				Debug.WriteLine($"[Navigator] ScopeNode type: (dead reference)");
			}
			Debug.WriteLine($"[Navigator] ScopeNode path: {CurrentContext.ScopeNode.HierarchicalPath}");
			
			// Get all candidates and log details
			var allNodes = Observer.GetAllNavNodes().ToList();
			Debug.WriteLine($"[Navigator] Total nodes from Observer: {allNodes.Count}");
			
			var navigableNodes = allNodes.Where(n => IsNavigableForSelection(n)).ToList();
			Debug.WriteLine($"[Navigator] Navigable nodes (IsGroup=false, IsNavigable=true): {navigableNodes.Count}");
			
			var allCandidates = GetCandidatesInScope();
			Debug.WriteLine($"[Navigator] GetCandidatesInScope() returned {allCandidates.Count} candidates");
			
			if (allCandidates.Count == 0) {
				// Enhanced diagnostics
				Debug.WriteLine($"[Navigator] ❌ NO CANDIDATES FOUND - Detailed Analysis:");
				
				var inScopeNodes = navigableNodes.Where(n => IsInActiveModalScope(n)).ToList();
				Debug.WriteLine($"[Navigator]   Nodes passing IsInActiveModalScope: {inScopeNodes.Count}");
				
				if (navigableNodes.Count > 0 && inScopeNodes.Count == 0) {
					Debug.WriteLine($"[Navigator]   ⚠ Scope filtering removed ALL candidates!");
					
					// Check first few navigable nodes
					var samplesToCheck = Math.Min(5, navigableNodes.Count);
					for (int i = 0; i < samplesToCheck; i++) {
						var sample = navigableNodes[i];
						Debug.WriteLine($"[Navigator]   Sample #{i}: {sample.SimpleName}");
						
						if (sample.TryGetVisual(out var sampleVisual)) {
							Debug.WriteLine($"[Navigator]     Type: {sampleVisual.GetType().Name}");
						} else {
							Debug.WriteLine($"[Navigator]     Type: (dead reference)");
						}
						
						Debug.WriteLine($"[Navigator]     Path: {sample.HierarchicalPath}");
						Debug.WriteLine($"[Navigator]     IsDescendantOf(ScopeNode): {IsDescendantOf(sample, CurrentContext.ScopeNode)}");
						
						// Walk parent chain
						var current = sample.Parent;
						int depth = 0;
						Debug.WriteLine($"[Navigator]     Parent chain:");
						while (current != null && current.TryGetTarget(out var parentNode) && depth < 8) {
							var isScope = ReferenceEquals(parentNode, CurrentContext.ScopeNode);
							Debug.WriteLine($"[Navigator]       [{depth}] {parentNode.SimpleName} {(isScope ? "← SCOPE ROOT" : "")}");
							current = parentNode.Parent;
							depth++;
							if (isScope) break;
						}
						if (depth >= 8) Debug.WriteLine($"[Navigator]       [...] (chain continues)");
					}
				}
				
				return;
			}
			
			var candidates = allCandidates
				.Select(n => {
					var center = n.GetCenterDip();
					var score = center.HasValue ? center.Value.X + center.Value.Y * 10000.0 : double.MaxValue;
					
					Debug.WriteLine($"  Candidate: {n.SimpleName} @ {n.HierarchicalPath}");
					Debug.WriteLine($"    Center: {center?.X:F1},{center?.Y:F1} | Score: {score:F1}");
					
					return new { Node = n, Score = score };
				})
				.OrderBy(x => x.Score)
				.ToList();
	
			// ✅ FIX: Skip if all candidates have invalid positions
			if (candidates.Count == 0 || candidates[0].Score == double.MaxValue) {
				Debug.WriteLine($"[Navigator] No valid candidates found (all have invalid positions)");
				return; // Don't try to set focus if nothing is positioned yet
			}
			
			var firstNode = candidates.FirstOrDefault()?.Node;
			if (firstNode != null) {
				Debug.WriteLine($"  ✅ WINNER: {firstNode.SimpleName} (score: {candidates[0].Score:F1})");
				CurrentContext.FocusedNode = firstNode;
				
				// ✓ FIX: Defer visual update until after layout is complete
				// SetFocusVisuals() calls UpdateFocusRect() which needs final bounds.
				// During ModalGroupOpened, visual tree may not be fully rendered yet,
				// so we schedule the visual update on Dispatcher with Loaded priority.
				if (Application.Current != null) {
					Application.Current.Dispatcher.BeginInvoke(new Action(() => {
						// Re-check that focus hasn't changed while waiting
						if (CurrentContext?.FocusedNode == firstNode) {
							SetFocusVisuals(firstNode);
						}
					}), DispatcherPriority.Loaded);
				} else {
					// Fallback if no dispatcher available
					SetFocusVisuals(firstNode);
				}
				
				Debug.WriteLine($"[Navigator] Initialized focus in '{CurrentContext.ScopeNode.SimpleName}' -> '{firstNode.SimpleName}'");
				try { FocusChanged?.Invoke(null, firstNode); } catch { }
			} else {
				Debug.WriteLine($"[Navigator] No valid candidate found after filtering!");
			}
		}

		/// <summary>
		/// Updates visual feedback (overlay) for focused node.
		/// Separated from focus state management for clarity.
		/// Also moves mouse to the focused node AND sets WPF keyboard focus.
		/// 
		/// ✓ FIXED: Now always uses MoveMouseToFocusedNode() which correctly transforms
		/// DIP → device pixels. Previously used SetCursorPos() directly with DIP coordinates,
		/// causing mouse position errors at non-100% DPI scales (e.g., 150% DPI).
		/// 
		/// ✅ NEW: Also sets WPF keyboard focus to sync with Navigator's focus tracking.
		/// This prevents WPF from stealing focus to excluded elements like TextBox.
		/// </summary>
		private static void SetFocusVisuals(NavNode node)
		{
			if (node == null) {
				_overlay?.HideFocusRect();
				return;
			}

			UpdateFocusRect(node);

			// ✅ NEW: Only set WPF keyboard focus for interactive controls, not list items
			// ListBoxItems should not receive keyboard focus during navigation because
			// WPF's ListBox automatically selects items when they receive keyboard focus.
			// We track Navigator focus separately from WPF keyboard focus.
			if (node.TryGetVisual(out var element)) {
				try {
					// Check if this is a list item (ListBoxItem, ComboBoxItem, etc.)
					var isListItem = element is System.Windows.Controls.ListBoxItem ||
									 element is System.Windows.Controls.ComboBoxItem ||
									 element.GetType().Name.Contains("ListBoxItem");
					
					if (!isListItem) {
						// Only set keyboard focus for non-list items (buttons, textboxes, etc.)
						Keyboard.Focus(element);
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Set keyboard focus to '{node.SimpleName}'");
						}
					} else {
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Skipped keyboard focus for list item '{node.SimpleName}' (would trigger selection)");
						}
					}
				} catch (Exception ex) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Failed to set keyboard focus: {ex.Message}");
					}
				}
			}

			// ✓ FIXED: Always use MoveMouseToFocusedNode() which handles DIP → device pixel conversion
			// Removed modal stack depth check - mouse tracking should work consistently everywhere
			if (_enableMouseTracking) {
				MoveMouseToFocusedNode(node);
			}
		}

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

		public static bool ExitGroup()
		{
			if (CurrentContext == null) return false;
			return CurrentContext.ScopeNode.Close();
		}

		#endregion

		#region Focus Management

		private static bool SetFocus(NavNode newNode)
		{
			if (CurrentContext == null) return false;
			if (ReferenceEquals(CurrentContext.FocusedNode, newNode)) return true;

			var oldNode = CurrentContext.FocusedNode;
			
			if (oldNode != null) {
				oldNode.HasFocus = false;
			}

			if (newNode != null) {
				newNode.HasFocus = true;
				CurrentContext.FocusedNode = newNode;
				
				SetFocusVisuals(newNode);
				
				// ✅ NEW: Check if this element has a custom page mapping
				if (_navConfig != null && _streamDeckClient != null)
				{
					var elementPath = GetPathWithoutHwnd(newNode);
					var pageName = _navConfig.FindPageForElement(elementPath);
					
					if (!string.IsNullOrEmpty(pageName))
					{
						Debug.WriteLine($"[Navigator] Switching to custom page '{pageName}' for focused element '{newNode.SimpleName}'");
						_streamDeckClient.SwitchPage(pageName);
					}
				}
				
				// ✅ NEW: Ensure the item is scrolled into view if in a virtualized container
				EnsureScrolledIntoView(newNode);
				
				// ✅ FIX: Defer mouse movement until after layout completes
				// EnsureScrolledIntoView() triggers a layout pass, but returns immediately.
				// If we move the mouse now, PointToScreen() returns the OLD position (before scroll).
				// Schedule mouse movement on Dispatcher with Render priority (after layout, before render).
				if (_enableMouseTracking) {
					if (newNode.TryGetVisual(out var element)) {
						element.Dispatcher.BeginInvoke(
							DispatcherPriority.Render,  // After layout updates element position
							new Action(() => {
								// Re-check that focus hasn't changed while waiting
								if (CurrentContext?.FocusedNode == newNode) {
									MoveMouseToFocusedNode(newNode);
								}
							})
						);
					} else {
						// Fallback if visual reference is dead (shouldn't happen for newly focused node)
						MoveMouseToFocusedNode(newNode);
					}
				}
				
				try { FocusChanged?.Invoke(oldNode, newNode); } catch { }
				return true;
			}

			CurrentContext.FocusedNode = null;
			return false;
		}

		/// <summary>
		/// Ensures that the given node's container is scrolled into view if it's inside a virtualized list.
		/// Uses ListBox.ScrollIntoView() which works correctly with virtualization - it will generate
		/// the container if needed and scroll it into view.
		/// 
		/// This is called after setting focus to ensure off-screen items become visible.
		/// </summary>
		private static void EnsureScrolledIntoView(NavNode node)
		{
			if (node == null) return;
			if (!node.TryGetVisual(out var element)) return;

			try {
				// Find parent ListBox (most common virtualized container)
				var listBox = element.GetParent<ListBox>();
				if (listBox != null) {
					// Get the data item from the container
					var item = listBox.ItemContainerGenerator.ItemFromContainer(element);
					if (item != null && item != DependencyProperty.UnsetValue) {
						// Scroll the item into view
						listBox.ScrollIntoView(item);

						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Scrolled '{node.SimpleName}' into view in ListBox");
						}
						return;
					}
				}

				// Fallback: Try ItemsControl (base class for ListBox, ListView, etc.)
				var itemsControl = element.GetParent<ItemsControl>();
				if (itemsControl != null) {
					// ItemsControl doesn't have ScrollIntoView, but we can try BringIntoView on the element
					element.BringIntoView();

					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Called BringIntoView on '{node.SimpleName}' in ItemsControl");
					}
					return;
				}

				// Last resort: Try BringIntoView on any scrollable parent
				var scrollViewer = element.GetParent<ScrollViewer>();
				if (scrollViewer != null) {
					element.BringIntoView();

					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Called BringIntoView on '{node.SimpleName}' in ScrollViewer");
					}
				}
			} catch (Exception ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Failed to scroll '{node.SimpleName}' into view: {ex.Message}");
				}
			}
		}

		private static void UpdateFocusRect(NavNode node)
		{
			if (_overlay == null || node == null) {
				if (VerboseNavigationDebug && _overlay == null) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: overlay is null");
				}
				return;
			}
			
			if (!node.TryGetVisual(out var fe)) { 
				Debug.WriteLine($"[Navigator] UpdateFocusRect: Visual DEAD for {node.SimpleName}");
				_overlay.HideFocusRect(); 
				return; 
			}

			// Check if element is in visual tree (connected to window)
			if (PresentationSource.FromVisual(fe) == null) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: NO PRESENTATION SOURCE - {node.SimpleName}");
				}
				return;
			}

			// Try to get bounds - handles async image loading gracefully
			Point? centerDip = null;
			try {
				centerDip = node.GetCenterDip();
			} catch (InvalidOperationException ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: BOUNDS ERROR - {node.SimpleName}: {ex.Message}");
				}
				return;
			}

			if (!centerDip.HasValue) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: NO BOUNDS - {node.SimpleName}");
				}
				return;
			}

			if (!fe.IsVisible) {
				Debug.WriteLine($"[Navigator] UpdateFocusRect: NOT VISIBLE - {node.SimpleName}");
				_overlay.HideFocusRect(); 
				return;
			}

			try {
				var topLeft = fe.PointToScreen(new Point(0, 0));
				var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: {node.SimpleName} @ screen({topLeft.X:F1}, {topLeft.Y:F1})");
				}

				var ps = PresentationSource.FromVisual(fe);
				if (ps?.CompositionTarget != null) {
					var transform = ps.CompositionTarget.TransformFromDevice;
					topLeft = transform.Transform(topLeft);
					bottomRight = transform.Transform(bottomRight);
				}

				var rect = new Rect(
					new Point(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y)),
					new Point(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y))
				);

				if (rect.Width >= 1.0 && rect.Height >= 1.0) {
					_overlay.ShowFocusRect(rect);
					
					// DO NOT move mouse here - this gets called on resize/layout changes
					// Mouse should only move on explicit focus changes (in SetFocus)
				} else {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: Rectangle too small ({rect.Width:F1}x{rect.Height:F1}) - {node.SimpleName}");
					_overlay.HideFocusRect();
				}
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] UpdateFocusRect EXCEPTION for {node.SimpleName}: {ex.Message}");
				_overlay.HideFocusRect();
			}
		}

		/// <summary>
		/// Moves the mouse cursor to the center of the focused NavNode.
		/// This provides visual feedback during keyboard navigation and prepares
		/// the mouse position for potential click-based activation.
		/// 
		/// Tooltips are already disabled globally, so no popup interference.
		/// </summary>
		private static void MoveMouseToFocusedNode(NavNode node)
		{
			if (node == null) return;

			try {
				var centerDip = node.GetCenterDip();
				if (!centerDip.HasValue) return;

				// Get the visual element to access PresentationSource
				if (!node.TryGetVisual(out var fe)) return;

				// Convert DIP to screen coordinates (device pixels)
				var ps = PresentationSource.FromVisual(fe);
				if (ps?.CompositionTarget == null) return;

				var transformToDevice = ps.CompositionTarget.TransformToDevice;
				var centerDevice = transformToDevice.Transform(centerDip.Value);

				// Get screen size for absolute positioning (SendInput uses 0-65535 range)
				var screenWidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
				var screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

				// Calculate absolute position in SendInput's coordinate space (0-65536)
				var absoluteX = (int)(centerDevice.X * 65536.0 / screenWidth);
				var absoluteY = (int)(centerDevice.Y * 65536.0 / screenHeight);

				// Move mouse to focused element
				var mouse = new AcTools.Windows.Input.MouseSimulator();
				mouse.MoveMouseTo(absoluteX, absoluteY);

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Mouse moved to '{node.SimpleName}' @ DIP({centerDip.Value.X:F0},{centerDip.Value.Y:F0}) Device({centerDevice.X:F0},{centerDevice.Y:F0})");
				}
			} catch (Exception ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Failed to move mouse to '{node?.SimpleName}': {ex.Message}");
				}
			}
		}

		#endregion

		#region Navigation Algorithm

		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir)
		{
			var curCenter = current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			var allCandidates = GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;

			var dirVector = GetDirectionVector(dir);

			if (VerboseNavigationDebug) {
				Debug.WriteLine($"\n[NAV] ========== From '{current.SimpleName}' → {dir} @ ({curCenter.Value.X:F0},{curCenter.Value.Y:F0}) | Candidates: {allCandidates.Count} ==========");
			}

			// Try same group first
			var sameGroupCandidates = allCandidates.Where(c => AreInSameNonModalGroup(current, c)).ToList();
			
			var sameGroupBest = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector,
				sameGroupCandidates,
				"SAME GROUP"
			);

			if (sameGroupBest != null) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[NAV] ✅ FOUND in same group: '{sameGroupBest.SimpleName}'");
					Debug.WriteLine($"[NAV] ============================================================\n");
				}
				return sameGroupBest;
			}

			if (VerboseNavigationDebug) {
				Debug.WriteLine($"[NAV] No match in same group, trying across groups...");
			}

			// Try across groups
			var acrossGroupsBest = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector, 
				allCandidates,
				"ACROSS GROUPS"
			);

			if (VerboseNavigationDebug) {
				if (acrossGroupsBest != null) {
					Debug.WriteLine($"[NAV] ✅ FOUND across groups: '{acrossGroupsBest.SimpleName}'");
				} else {
				 Debug.WriteLine($"[NAV] ❌ NO CANDIDATE FOUND");
				}
				Debug.WriteLine($"[NAV] ============================================================\n");
			}

			return acrossGroupsBest;
		}

		private static NavNode FindBestInCandidates(
			NavNode current, Point currentCenter, NavDirection dir, Point dirVector, List<NavNode> candidates,
			String phase = "")
		{
			if (candidates.Count == 0) return null;

			if (VerboseNavigationDebug && !string.IsNullOrEmpty(phase)) {
				Debug.WriteLine($"[NAV] --- {phase}: {candidates.Count} candidates ---");
			}

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				// Compare by object reference, not HierarchicalPath (which may not be unique)
				if (ReferenceEquals(candidate, current)) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' (skipped: same as current node)");
					}
					continue;
				}
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' (skipped: no center point)");
					}
					continue;
				}

				var c = candidateCenter.Value;
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (skipped: zero distance)");
					}
					continue;
				}

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				if (dot <= 0) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ❌ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dot={dot:F2} (wrong direction)");
					}
					continue;
				}

				var cost = len / Math.Max(1e-7, dot);
				var bonuses = "";

				if (HaveSameImmediateParent(current, candidate)) {
					cost *= 0.7;
					bonuses += " parent×0.7";
				}
				
				if (IsWellAligned(currentCenter, c, dir)) {
					cost *= 0.8;
					bonuses += " align×0.8";
				}

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[NAV]   ✅ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dist={len:F0} dot={dot:F2} cost={cost:F0}{bonuses}");
				}

				validCandidates.Add(new ScoredCandidate { Node = candidate, Cost = cost });
			}

			if (VerboseNavigationDebug && validCandidates.Count > 0) {
				var sorted = validCandidates.OrderBy(sc => sc.Cost).ToList();
				Debug.WriteLine($"[NAV]   🥇 WINNER: '{sorted[0].Node.SimpleName}' (cost={sorted[0].Cost:F0})");
				
				// Show runner-ups if available
				if (sorted.Count > 1) {
					Debug.WriteLine($"[NAV]   🥈 Runner-up: '{sorted[1].Node.SimpleName}' (cost={sorted[1].Cost:F0})");
				}
				if (sorted.Count > 2) {
					Debug.WriteLine($"[NAV]   🥉 3rd place: '{sorted[2].Node.SimpleName}' (cost={sorted[2].Cost:F0})");
				}
			}

			return validCandidates.OrderBy(sc => sc.Cost).FirstOrDefault()?.Node;
		}

		private class ScoredCandidate
		{
			public NavNode Node { get; set; }
			public double Cost { get; set; }
		}

		private static Point GetDirectionVector(NavDirection dir)
		{
			switch (dir) {
				case NavDirection.Up: return new Point(0, -1);
				case NavDirection.Down: return new Point(0, 1);
				case NavDirection.Left: return new Point(-1, 0);
				case NavDirection.Right: return new Point(1, 0);
				default: return new Point(0, 0);
			}
		}

		private static bool IsWellAligned(Point from, Point to, NavDirection dir)
		{
			const double threshold = 20.0;
			switch (dir) {
				case NavDirection.Up:
				case NavDirection.Down:
					return Math.Abs(from.X - to.X) < threshold;
				case NavDirection.Left:
				case NavDirection.Right:
					return Math.Abs(from.Y - to.Y) < threshold;
				default:
					return false;
			}
		}

		#endregion

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
	}
}
