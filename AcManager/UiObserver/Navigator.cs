using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace AcManager.UiObserver
{
	internal enum NavDirection { Up, Down, Left, Right }

	/// <summary>
	/// Represents a navigation context within the modal hierarchy.
	/// Each context bundles a modal scope (the root node defining the context)
	/// with the currently focused node within that scope.
	/// </summary>
	internal class NavContext
	{
		/// <summary>
		/// The modal node that defines this context's scope.
		/// For root context, this is the MainWindow.
		/// For modal contexts, this is the Popup/Window/PopupRoot node.
		/// Never null.
		/// </summary>
		public NavNode ModalNode { get; }
		
		/// <summary>
		/// Currently focused node within this modal context.
		/// Null if no focus has been established yet in this context.
		/// </summary>
		public NavNode FocusedNode { get; set; }
		
		public NavContext(NavNode modalNode, NavNode focusedNode = null)
		{
			ModalNode = modalNode ?? throw new ArgumentNullException(nameof(modalNode));
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
	internal static class Navigator
	{
		private static bool _initialized = false;

		// Modal context stack - each entry bundles modal scope + focused node
		// Invariant: _modalContextStack.Count >= 1 after initialization (root context always present)
		private static readonly List<NavContext> _modalContextStack = new List<NavContext>();
		
		// Helper property for current context (never null after initialization)
		private static NavContext CurrentContext => _modalContextStack.Count > 0 
			? _modalContextStack[_modalContextStack.Count - 1] 
			: null;
		
		// Focus lock to prevent feedback loops
		private static bool _suppressFocusTracking = false;

		// Events
		public static event Action NavMapUpdated;
		public static event Action<NavNode, NavNode> FocusChanged; // (oldNode, newNode)

		// Highlighting overlay
		private static HighlightOverlay _overlay;
		private static bool _debugMode;

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			// Initialize navigation rules
			InitializeNavigationRules();

			// Configure Observer
			Observer.Configure(IsTrulyVisible);
			
			// Subscribe to Observer modal lifecycle events (simplified event model!)
			Observer.ModalGroupOpened += OnModalGroupOpened;
			Observer.ModalGroupClosed += OnModalGroupClosed;
			
			// Enable automatic root tracking
			Observer.EnableAutoRootTracking();

			// Register keyboard handler
			try {
				EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, 
					new KeyEventHandler(OnPreviewKeyDown), true);
			} catch { }

			// Initialize windows
			if (Application.Current != null) {
				Application.Current.Dispatcher.BeginInvoke(new Action(() => {
					foreach (Window w in Application.Current.Windows) {
						HookWindow(w);
						var content = w.Content as FrameworkElement;
						if (content != null) {
							Observer.RegisterRoot(content);
						}
					}
					
					EnsureOverlay();
				}), DispatcherPriority.ApplicationIdle);
			}
		}
		
		private static void InitializeNavigationRules()
		{
			var rules = new[] {
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu",
				"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true",
				"EXCLUDE: ** > *:HistoricalTextBox > **",
				"EXCLUDE: ** > *:LazyMenuItem > **",

				"EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
				"EXCLUDE: *:PopupRoot > ** > *:BetterScrollBar",

				// CRITICAL: Exclude debug overlay from navigation tracking to prevent feedback loop
				"EXCLUDE: *:HighlightOverlay > **",
			};

			try {
				NavNode.PathFilter.ParseRules(rules);
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to initialize navigation rules: {ex}");
			}
		}

		private static void EnsureOverlay()
		{
			if (_overlay == null) {
				try {
					_overlay = new HighlightOverlay();
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] Failed to create overlay: {ex.Message}");
				}
			}
		}

		// === EVENT HANDLERS (Reactive Architecture) ===

		private static void OnModalGroupOpened(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[Navigator] Modal opened: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			// Validate linear chain (except for root context AND nodes with no parent yet)
			if (_modalContextStack.Count > 0) {
				var currentTop = CurrentContext.ModalNode;
				
				// Optimistic validation: Only reject if HAS parent but WRONG parent
				// Accept modals with no parent yet (will be linked by Observer after this event)
				if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
					Debug.WriteLine($"[Navigator] ERROR: Modal {modalNode.SimpleName} not descendant of {currentTop.SimpleName}!");
					return;
				}
			}
			
			// Push new context onto stack
			var newContext = new NavContext(modalNode, focusedNode: null);
			_modalContextStack.Add(newContext);
			
			Debug.WriteLine($"[Navigator] Modal stack depth: {_modalContextStack.Count}");
			
			// Clear old focus visuals (new context has no focus yet)
			_overlay?.HideFocusRect();
			
			// Initialize focus in new context (all nodes within scope are already discovered!)
			TryInitializeFocusIfNeeded();
			
			// Notify external subscribers
			try { NavMapUpdated?.Invoke(); } catch { }
		}

		private static void OnModalGroupClosed(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[Navigator] Modal closed: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			// Pop context from stack
			if (_modalContextStack.Count > 0 
				&& CurrentContext.ModalNode.HierarchicalPath == modalNode.HierarchicalPath) {
				_modalContextStack.RemoveAt(_modalContextStack.Count - 1);
			} else {
				Debug.WriteLine($"[Navigator] WARNING: Closed modal not at top");
				_modalContextStack.RemoveAll(ctx => ctx.ModalNode.HierarchicalPath == modalNode.HierarchicalPath);
			}
			
			// Restore focus from previous context
			if (CurrentContext != null && CurrentContext.FocusedNode != null) {
				SetFocusVisuals(CurrentContext.FocusedNode);
				Debug.WriteLine($"[Navigator] Restored focus to '{CurrentContext.FocusedNode.SimpleName}'");
			} else {
				// No previous focus - try to initialize
				TryInitializeFocusIfNeeded();
			}
			
			// Notify external subscribers
			try { NavMapUpdated?.Invoke(); } catch { }
		}

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
			if (CurrentContext == null) return;
			if (CurrentContext.FocusedNode != null) return;
			
			Debug.WriteLine($"[Navigator] Finding first navigable in scope '{CurrentContext.ModalNode.SimpleName}'...");
			
			var candidates = GetCandidatesInScope()
				.Where(n => CurrentContext.ModalNode == null || IsDescendantOf(n, CurrentContext.ModalNode))
				.Select(n => {
					var center = n.GetCenterDip();
					var score = center.HasValue ? center.Value.X + center.Value.Y * 10000.0 : double.MaxValue;
					
					Debug.WriteLine($"  Candidate: {n.SimpleName} @ {n.HierarchicalPath}");
					Debug.WriteLine($"    Center: {center?.X:F1},{center?.Y:F1} | Score: {score:F1}");
					
					return new { Node = n, Score = score };
				})
				.OrderBy(x => x.Score)
				.ToList();
			
			var firstNode = candidates.FirstOrDefault()?.Node;
			if (firstNode != null) {
				Debug.WriteLine($"  ? WINNER: {firstNode.SimpleName} (score: {candidates[0].Score:F1})");
				CurrentContext.FocusedNode = firstNode;
				SetFocusVisuals(firstNode);
				
				Debug.WriteLine($"[Navigator] Initialized focus in '{CurrentContext.ModalNode.SimpleName}' -> '{firstNode.SimpleName}'");
				try { FocusChanged?.Invoke(null, firstNode); } catch { }
			}
		}

		/// <summary>
		/// Find the first navigable element in the given scope, preferring top-left position.
		/// </summary>
		private static NavNode FindFirstNavigableInScope(NavNode scopeNode)
		{
			var candidates = GetCandidatesInScope()
				.Where(n => scopeNode == null || IsDescendantOf(n, scopeNode))
				.OrderBy(n => {
					// Prefer top-left position (Y has higher weight than X)
					var center = n.GetCenterDip();
					if (!center.HasValue) return double.MaxValue;
					return center.Value.X + center.Value.Y * 10000.0;
				})
				.ToList();
			
			return candidates.FirstOrDefault();
		}

		/// <summary>
		/// Updates visual feedback (overlay) for focused node.
		/// Separated from focus state management for clarity.
		/// </summary>
		private static void SetFocusVisuals(NavNode node)
		{
			if (node != null)
			{
				node.HasFocus = true;
				UpdateFocusRect(node);
			}
			else
			{
				_overlay?.HideFocusRect();
			}
		}

		// === PUBLIC API ===

		public static bool MoveInDirection(NavDirection dir)
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			var best = FindBestCandidateInDirection(CurrentContext.FocusedNode, dir);
			if (best == null) return false;

			_suppressFocusTracking = true;
			try {
				return SetFocus(best);
			} finally {
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		public static bool ActivateFocusedNode()
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			_suppressFocusTracking = true;
			try {
				return CurrentContext.FocusedNode.Activate();
			} finally {
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		public static bool ExitGroup()
		{
			if (CurrentContext == null) return false;
			return CurrentContext.ModalNode.Close();
		}

		public static bool FocusNodeByPath(string hierarchicalPath)
		{
			if (string.IsNullOrEmpty(hierarchicalPath)) return false;
			
			var node = Observer.GetAllNavNodes().FirstOrDefault(n => n.HierarchicalPath == hierarchicalPath);
			if (node == null) return false;
			if (!IsNavigableForSelection(node)) return false;
			
			return SetFocus(node);
		}

		public static string GetFocusedNodePath() => CurrentContext?.FocusedNode?.HierarchicalPath;
		
		public static NavNode GetFocusedNode() => CurrentContext?.FocusedNode;
		
		public static NavNode GetActiveModal() => CurrentContext?.ModalNode;
		
		public static IReadOnlyList<string> GetModalStackPaths() => 
			_modalContextStack.Select(ctx => ctx.ModalNode.HierarchicalPath).ToList();

		// === FOCUS MANAGEMENT ===

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
				UpdateFocusRect(newNode);
				try { FocusChanged?.Invoke(oldNode, newNode); } catch { }
				return true;
			}

			CurrentContext.FocusedNode = null;
			return false;
		}

		private static void UpdateFocusRect(NavNode node)
		{
			if (_overlay == null || node == null) return;
			if (!node.TryGetVisual(out var fe)) { _overlay.HideFocusRect(); return; }
			if (!fe.IsLoaded || !fe.IsVisible) { _overlay.HideFocusRect(); return; }

			try {
				var topLeft = fe.PointToScreen(new Point(0, 0));
				var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

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
				} else {
					_overlay.HideFocusRect();
				}
			} catch {
				_overlay.HideFocusRect();
			}
		}

		// === NAVIGATION ALGORITHM ===

		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir)
		{
			var curCenter = current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			var allCandidates = GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;

			var dirVector = GetDirectionVector(dir);

			// Try same group first
			var sameGroupBest = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector,
				allCandidates.Where(c => AreInSameNonModalGroup(current, c)).ToList()
			);

			if (sameGroupBest != null) return sameGroupBest;

			// Try across groups
			return FindBestInCandidates(current, curCenter.Value, dir, dirVector, allCandidates);
		}

		private static NavNode FindBestInCandidates(
			NavNode current, Point currentCenter, NavDirection dir, Point dirVector, List<NavNode> candidates)
		{
			if (candidates.Count == 0) return null;

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				if (candidate.HierarchicalPath == current.HierarchicalPath) continue;
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) continue;

				var c = candidateCenter.Value;
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) continue;

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				if (dot <= 0) continue;

				var cost = len / Math.Max(1e-7, dot);

				if (HaveSameImmediateParent(current, candidate)) cost *= 0.7;
				if (IsWellAligned(currentCenter, c, dir)) cost *= 0.8;

				validCandidates.Add(new ScoredCandidate { Node = candidate, Cost = cost });
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

		// === HELPER METHODS ===

		private static bool IsDescendantOf(NavNode child, NavNode ancestor)
		{
			if (child == null || ancestor == null) return false;
			
			// Walk up the Parent chain (which includes PlacementTarget bridges!)
			var current = child.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (ReferenceEquals(parentNode, ancestor)) return true;
				current = parentNode.Parent;
			}
			
			return false;
		}

		private static bool IsInActiveModalScope(NavNode node)
		{
			if (CurrentContext == null) return true;
			return IsDescendantOf(node, CurrentContext.ModalNode);
		}

		private static List<NavNode> GetCandidatesInScope()
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

		private static bool IsTrulyVisible(FrameworkElement fe)
		{
			if (fe == null) return false;
			DependencyObject cur = fe;
			while (cur != null) {
				if (cur is UIElement ui && ui.Visibility != Visibility.Visible) return false;
				try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
			}
			return fe.IsVisible && fe.IsLoaded;
		}

		// === KEYBOARD INPUT ===

		private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e == null) return;
			
			// Ctrl+Shift+F12: Toggle debug overlay (filtered by active modal scope)
			if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting(filterByModalScope: true);
				return;
			}

			// Ctrl+Shift+F11: Toggle debug overlay (show ALL nodes, unfiltered)
			if (e.Key == Key.F11 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting(filterByModalScope: false);
				return;
			}

			// Ctrl+Shift+Arrow keys: Navigation (ensure ONLY Ctrl+Shift, no other modifiers)
			if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				bool handled = false;
				
				switch (e.Key) {
					case Key.Up: handled = MoveInDirection(NavDirection.Up); break;
					case Key.Down: handled = MoveInDirection(NavDirection.Down); break;
					case Key.Left: handled = MoveInDirection(NavDirection.Left); break;
					case Key.Right: handled = MoveInDirection(NavDirection.Right); break;
					case Key.Return: handled = ActivateFocusedNode(); break;
					case Key.Escape: handled = ExitGroup(); break;
				}
				
				if (handled) e.Handled = true;
			}
		}

		private static void HookWindow(Window w)
		{
			if (w == null) return;
			w.Loaded += OnRootLayoutChanged;
			w.LayoutUpdated += OnRootLayoutChanged;
			w.LocationChanged += OnRootLayoutChanged;
			w.SizeChanged += OnRootLayoutChanged;
		}

		private static void OnRootLayoutChanged(object sender, EventArgs e)
		{
			if (sender is Window w) {
				var content = w.Content as FrameworkElement;
				if (content != null) Observer.RegisterRoot(content);
			}
			
			if (CurrentContext?.FocusedNode != null) {
				UpdateFocusRect(CurrentContext.FocusedNode);
			}
		}

		// === DEBUG VISUALIZATION ===

		private const bool ENABLE_CONSISTENCY_VALIDATION = false;

		private static void ValidateNodeConsistency()
		{
			if (!ENABLE_CONSISTENCY_VALIDATION) return;
			
			Debug.WriteLine("\n========== NavNode Consistency Check ==========");

			var allNodes = Observer.GetAllNavNodes().ToList();
			Debug.WriteLine($"Total nodes to validate: {allNodes.Count}");
			
			int deadVisuals = 0;
			int parentMismatches = 0;
			int childConsistencyErrors = 0;
			int orphanedNodes = 0;
			int circularRefs = 0;
			
			foreach (var node in allNodes)
			{
				// Check 1: Visual element is alive
				if (!node.TryGetVisual(out var fe))
				{
					deadVisuals++;
					Debug.WriteLine($"  ? DEAD VISUAL: {node.SimpleName}");
					Debug.WriteLine($"     Path: {node.HierarchicalPath}");
					continue;
				}
				
				// Get rectangle bounds for debug output
				Rect? bounds = null;
				try
				{
					var topLeft = fe.PointToScreen(new Point(0, 0));
					var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
					
					var ps = PresentationSource.FromVisual(fe);
					if (ps?.CompositionTarget != null)
					{
						var transform = ps.CompositionTarget.TransformFromDevice;
						topLeft = transform.Transform(topLeft);
						bottomRight = transform.Transform(bottomRight);
					}
					
					bounds = new Rect(
						new Point(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y)),
						new Point(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y))
					);
				}
				catch { }
				
				// Check 2: Parent reference matches visual tree
				if (node.Parent != null && node.Parent.TryGetTarget(out var parentNode))
				{
					// Walk up visual tree to find actual parent NavNode
					NavNode actualParent = null;
					DependencyObject current = fe;
					
					try
					{
						while (current != null)
						{
							current = VisualTreeHelper.GetParent(current);
							if (current is FrameworkElement parentFe && Observer.TryGetNavNode(parentFe, out var candidateParent))
							{
								actualParent = candidateParent;
								break;
							}
						}
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"  ??  ERROR walking visual tree for {node.SimpleName}: {ex.Message}");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
					}
					
					if (actualParent == null)
					{
						parentMismatches++;
						Debug.WriteLine($"  ? PARENT MISMATCH: {node.SimpleName}");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     Recorded Parent: {parentNode.SimpleName}");
						Debug.WriteLine($"     Recorded Parent Path: {parentNode.HierarchicalPath}");
						Debug.WriteLine($"     Actual Parent: NONE (visual tree has no NavNode parent)");
					}
					else if (!ReferenceEquals(actualParent, parentNode))
					{
						parentMismatches++;
						Debug.WriteLine($"  ? PARENT MISMATCH: {node.SimpleName}");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     Recorded Parent: {parentNode.SimpleName}");
						Debug.WriteLine($"     Recorded Parent Path: {parentNode.HierarchicalPath}");
						Debug.WriteLine($"     Actual Parent: {actualParent.SimpleName}");
						Debug.WriteLine($"     Actual Parent Path: {actualParent.HierarchicalPath}");
					}
				}
				
				// Check 3: Children list internal consistency
				var recordedChildren = new List<NavNode>();
				var deadChildRefs = 0;
				var seenChildren = new HashSet<NavNode>();
				
				foreach (var childRef in node.Children)
				{
					if (!childRef.TryGetTarget(out var child))
					{
						deadChildRefs++;
						continue;
					}
					
					// Check for duplicates
					if (seenChildren.Contains(child))
					{
						childConsistencyErrors++;
						Debug.WriteLine($"  ? DUPLICATE CHILD: {node.SimpleName}");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     Duplicate child: {child.SimpleName}");
						Debug.WriteLine($"     Duplicate child path: {child.HierarchicalPath}");
						continue;
					}
					
					seenChildren.Add(child);
					recordedChildren.Add(child);
					
					// Check if child's Parent points back to this node
					if (child.Parent == null || !child.Parent.TryGetTarget(out var childParent) || !ReferenceEquals(childParent, node))
					{
						childConsistencyErrors++;
						Debug.WriteLine($"  ? CHILD PARENT MISMATCH: {node.SimpleName}");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     Child: {child.SimpleName}");
						Debug.WriteLine($"     Child path: {child.HierarchicalPath}");
						if (child.Parent == null)
						{
							Debug.WriteLine($"     Child's Parent: NULL");
						}
						else if (!child.Parent.TryGetTarget(out var cp))
						{
							Debug.WriteLine($"     Child's Parent: DEAD REFERENCE");
						}
						else
						{
							Debug.WriteLine($"     Child's Parent: {cp.SimpleName}");
							Debug.WriteLine($"     Child's Parent path: {cp.HierarchicalPath}");
						}
					}
				}
				
				if (deadChildRefs > 0)
				{
					childConsistencyErrors++;
					Debug.WriteLine($"  ? DEAD CHILD REFERENCES: {node.SimpleName}");
					Debug.WriteLine($"     Path: {node.HierarchicalPath}");
					if (bounds.HasValue)
					{
						Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
					}
					Debug.WriteLine($"     Dead references: {deadChildRefs}");
				}
				
				// Check 4: Cross-check with visual tree - find all NavNode descendants
				// Walk down visual tree recursively and verify all found NavNodes are reachable through tree structure
				var visualTreeNavNodes = new HashSet<NavNode>();
				var stack = new Stack<DependencyObject>();
				stack.Push(fe);
				
				try
				{
					while (stack.Count > 0)
					{
						var current = stack.Pop();
						
						// Skip the node itself
						if (!ReferenceEquals(current, fe) && current is FrameworkElement childFe && Observer.TryGetNavNode(childFe, out var foundNode))
						{
							visualTreeNavNodes.Add(foundNode);
						}
						
						// Add children to stack
						try
						{
							int childCount = VisualTreeHelper.GetChildrenCount(current);
							for (int i = 0; i < childCount; i++)
							{
								var visualChild = VisualTreeHelper.GetChild(current, i);
								if (visualChild != null)
								{
									stack.Push(visualChild);
								}
							}
						}
						catch { }
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"  ??  ERROR walking visual tree descendants for {node.SimpleName}: {ex.Message}");
					Debug.WriteLine($"     Path: {node.HierarchicalPath}");
					if (bounds.HasValue)
					{
						Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
					}
				}
				
				// Now check if all visual tree NavNodes are reachable through tree structure
				foreach (var visualNode in visualTreeNavNodes)
				{
					// Walk up Parent chain from visualNode - should eventually reach this node
					bool reachable = false;
					var ancestor = visualNode.Parent;
					while (ancestor != null && ancestor.TryGetTarget(out var ancestorNode))
					{
						if (ReferenceEquals(ancestorNode, node))
						{
							reachable = true;
							break;
						}
						ancestor = ancestorNode.Parent;
					}
					
					if (!reachable)
					{
						orphanedNodes++;
						Debug.WriteLine($"  ? ORPHANED NODE IN VISUAL TREE: {visualNode.SimpleName}");
						Debug.WriteLine($"     Path: {visualNode.HierarchicalPath}");
						Debug.WriteLine($"     Found in visual subtree of: {node.SimpleName}");
						Debug.WriteLine($"     Parent node path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Parent node bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     But NOT reachable through Parent chain from this node!");
					}
				}
				
				// Check 5: No circular references
				var visited = new HashSet<NavNode>();
				var current2 = node.Parent;
				while (current2 != null && current2.TryGetTarget(out var ancestor))
				{
					if (visited.Contains(ancestor))
					{
						circularRefs++;
						Debug.WriteLine($"  ? CIRCULAR REFERENCE: {node.SimpleName} has circular parent chain!");
						Debug.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							Debug.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						Debug.WriteLine($"     Circular ancestor: {ancestor.SimpleName}");
						Debug.WriteLine($"     Circular ancestor path: {ancestor.HierarchicalPath}");
						break;
					}
					visited.Add(ancestor);
					current2 = ancestor.Parent;
				}
			}
			
			Debug.WriteLine($"\n========== Consistency Check Results ==========");
			Debug.WriteLine($"Total nodes: {allNodes.Count}");
			Debug.WriteLine($"Dead visuals: {deadVisuals}");
			Debug.WriteLine($"Parent mismatches: {parentMismatches}");
			Debug.WriteLine($"Child consistency errors: {childConsistencyErrors} (duplicates, dead refs, back-link errors)");
			Debug.WriteLine($"Orphaned nodes: {orphanedNodes} (in visual tree but not reachable through Parent chain)");
			Debug.WriteLine($"Circular references: {circularRefs}");
			
			if (deadVisuals == 0 && parentMismatches == 0 && childConsistencyErrors == 0 && orphanedNodes == 0 && circularRefs == 0)
			{
				Debug.WriteLine("? All nodes are CONSISTENT with visual tree!");
			}
			else
			{
				Debug.WriteLine("??  INCONSISTENCIES DETECTED - see details above");
			}
			
			Debug.WriteLine("================================================\n");
		}

		private static void ToggleHighlighting(bool filterByModalScope)
		{
			if (_debugMode) {
				ClearHighlighting();
				_debugMode = false;
				
				long memBefore = GC.GetTotalMemory(forceFullCollection: false);
				
				try {
					if (_overlay != null && _overlay.IsVisible) {
						_overlay.Hide();
					}
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] Hide error: {ex.Message}");
				}

				long memAfter = GC.GetTotalMemory(forceFullCollection: false);
				Debug.WriteLine($"[Navigator] Memory: Before={memBefore:N0}, After={memAfter:N0}, Delta={memAfter - memBefore:N0} bytes");

				return;
			}

			ValidateNodeConsistency();

			if (filterByModalScope) {
				Debug.WriteLine("\n========== NavMapper: Highlight Rectangles (Active Modal Scope ONLY) ==========");
			} else {
				Debug.WriteLine("\n========== NavMapper: Highlight Rectangles (ALL NODES - Unfiltered) ==========");
			}
			
			// Show modal stack
			if (_modalContextStack.Count > 0) {
				Debug.WriteLine($"Modal Stack ({_modalContextStack.Count} contexts):");
				for (int i = 0; i < _modalContextStack.Count; i++) {
					var ctx = _modalContextStack[i];
					var focusInfo = ctx.FocusedNode != null 
						? $" [focused: {ctx.FocusedNode.SimpleName}]" 
						: " [no focus]";
					Debug.WriteLine($"  [{i}] {ctx.ModalNode.HierarchicalPath}{focusInfo}");
				}
			} else {
				Debug.WriteLine("Modal Stack: (empty - waiting for root context)");
			}
			Debug.WriteLine("");

			// ? Pre-allocate with capacity hints
			var leafRects = new List<Rect>(256);
			var groupRects = new List<Rect>(128);

			// Get nodes from Observer (authoritative source)
			List<NavNode> nodesToShow;
			if (filterByModalScope) {
				// Filtered: Only show nodes in active modal scope
				nodesToShow = Observer.GetAllNavNodes()
					.Where(n => IsInActiveModalScope(n))
					.ToList();
				Debug.WriteLine($"Nodes in current modal scope: {nodesToShow.Count}");
			} else {
				// Unfiltered: Show ALL discovered nodes (including groups!)
				nodesToShow = Observer.GetAllNavNodes().ToList();
				Debug.WriteLine($"All discovered nodes: {nodesToShow.Count}");
			}
			
			// ? Limit processing to prevent OOM
			const int MAX_NODES_TO_PROCESS = 1000;
			if (nodesToShow.Count > MAX_NODES_TO_PROCESS) {
				Debug.WriteLine($"[NavMapper] WARNING: {nodesToShow.Count} nodes exceeds limit {MAX_NODES_TO_PROCESS}. Truncating.");
				nodesToShow = nodesToShow.Take(MAX_NODES_TO_PROCESS).ToList();
			}
			
			// ? Use initial capacity based on actual count
			var allDebugInfo = new List<DebugRectInfo>(nodesToShow.Count);
			
			foreach (var node in nodesToShow) {
				var center = node.GetCenterDip();
				if (!center.HasValue) continue;
				
				if (!node.TryGetVisual(out var fe)) continue;
				if (!fe.IsLoaded || !fe.IsVisible) continue;
				
				try {
					var topLeft = fe.PointToScreen(new Point(0, 0));
					var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

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
                        // Get node type description
                        var nodeType = node.IsGroup ? "PureGroup" : "Leaf";
                        var navigable = node.IsGroup ? "[NOT navigable - pure group]" : "[NAVIGABLE]";
                        var scopeInfo = "";
                        
                        // Add scope information if we're showing all nodes
                        if (!filterByModalScope && _modalContextStack.Count > 0) {
                            var inScope = IsInActiveModalScope(node);
                            scopeInfo = inScope ? " {IN SCOPE}" : " {OUT OF SCOPE}";
                        }
                        
                        var typeName = fe.GetType().Name;
                        var elementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
                        var modalTag = node.IsModal ? "MODAL" : "";
                        var navId = node.HierarchicalPath;
                        
                        // Get hierarchical path for sorting
                        var hierarchicalPath = NavNode.GetHierarchicalPath(fe);
                        
                        // Format bounds as: (X, Y) WxH
                        var boundsStr = $"({rect.Left,7:F1}, {rect.Top,7:F1}) {rect.Width,6:F1}x{rect.Height,6:F1}";

                        // Build formatted debug line
                        var debugLine = $"{typeName,-20} | {elementName,-20} | {nodeType,-18} | {modalTag,-6} | {navigable,-35}{scopeInfo,-15} | {boundsStr,-30} | {navId,-30} | {hierarchicalPath}";

                        allDebugInfo.Add(new DebugRectInfo { 
                            Rect = rect, 
                            DebugLine = debugLine,
                            IsGroup = node.IsGroup,
                            HierarchicalPath = hierarchicalPath
                        });
                        
                        if (node.IsGroup) {
                            groupRects.Add(rect);
                        } else {
                            leafRects.Add(rect);
                        }
                    }

				} catch (Exception ex) {
					Debug.WriteLine($"  ERROR processing {node.HierarchicalPath}: {ex.Message}");
				}
			}

			// Sort by hierarchical path for easy reading
			allDebugInfo.Sort((a, b) => string.Compare(a.HierarchicalPath, b.HierarchicalPath, StringComparison.Ordinal));

			// Output sorted rectangles
			Debug.WriteLine("");
			int leafCount = 0;
			int groupCount = 0;
			
			for (int i = 0; i < allDebugInfo.Count; i++) {
				var info = allDebugInfo[i];
				var prefix = info.IsGroup ? "[GRAY]" : "[LEAF]";
				if (info.IsGroup) {
					groupCount++;
				} else {
					leafCount++;
				}
				Debug.WriteLine($"{prefix} #{i + 1,-3} | {info.DebugLine}");
			}

			Debug.WriteLine($"\n========== Summary: {leafCount} leaves, {groupCount} groups ==========");
			if (filterByModalScope) {
				Debug.WriteLine("Mode: FILTERED (Ctrl+Shift+F12) - showing only nodes in active modal scope");
			} else {
				Debug.WriteLine("Mode: UNFILTERED (Ctrl+Shift+F11) - showing ALL discovered nodes");
			}
			Debug.WriteLine("=============================================================\n");

			try {
				EnsureOverlay();  // ? Reuses existing overlay if it exists
				_overlay?.ShowDebugRects(leafRects, groupRects);
				_debugMode = true;
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Overlay error: {ex.Message}");
			 }
			
			// ? Clear local collections to hint GC
			allDebugInfo.Clear();
			allDebugInfo = null;
			nodesToShow.Clear();
			nodesToShow = null;
		}

		private class DebugRectInfo
		{
			public Rect Rect { get; set; }
			public string DebugLine { get; set; }
			public bool IsGroup { get; set; }
			public string HierarchicalPath { get; set; }
		}

		private static void ClearHighlighting()
		{
			try { _overlay?.ClearDebugRects(); } catch { }
		}

		// === PUBLIC QUERY API ===

		public static IEnumerable<NavNode> GetAllNavNodes() => Observer.GetAllNavNodes();
		
		public static bool TryGetById(string id, out NavNode nav)
		{
			nav = Observer.GetAllNavNodes().FirstOrDefault(n => n.HierarchicalPath == id);
			return nav != null;
		}
	}
}
