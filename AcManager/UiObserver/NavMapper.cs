using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace AcManager.UiObserver
{
	internal enum NavDirection { Up, Down, Left, Right }

	/// <summary>
	/// NavMapper - Navigation coordinator with event-driven architecture.
	/// 
	/// Responsibilities:
	/// - Subscribe to NavForest events (NavNodeAdded, NavNodeRemoved, ModalGroupOpened, ModalGroupClosed)
	/// - Manage modal stack (pushed/popped via events from NavForest)
	/// - Handle keyboard input (Alt+Arrow keys for navigation)
	/// - Filter navigable candidates by modal scope
	/// - Find best candidate in direction using spatial algorithm
	/// - Manage focus highlighting overlay
	/// 
	/// Architecture:
	/// - NavNode: Data + type-specific behaviors (CreateNavNode, Activate, Close)
	/// - NavForest: Discovery engine (scans visual trees, builds HierarchicalId, emits events)
	/// - NavMapper: Navigation logic (subscribes to events, manages modal stack, handles input)
	/// </summary>
	internal static class NavMapper
	{
		private static bool _initialized = false;

		// Modal stack - managed via NavForest events
		private static readonly List<NavNode> _activeModalStack = new List<NavNode>();
		
		// Focus management - store the actual NavNode, not the path!
		private static NavNode _focusedNode;
		
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

			// Configure NavForest
			NavForest.Configure(IsTrulyVisible);
			
			// ? Subscribe to NavForest events (event-driven architecture!)
			NavForest.NavNodeAdded += OnNavNodeAdded;
			NavForest.NavNodeRemoved += OnNavNodeRemoved;
			NavForest.ModalGroupOpened += OnModalGroupOpened;
			NavForest.ModalGroupClosed += OnModalGroupClosed;
			NavForest.RootChanged += OnRootChanged;
			
			// Enable automatic root tracking
			NavForest.EnableAutoRootTracking();

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
							NavForest.RegisterRoot(content);
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
			};

			try {
				NavNode.PathFilter.ParseRules(rules);
			} catch (Exception ex) {
				Debug.WriteLine($"[NavMapper] Failed to initialize navigation rules: {ex}");
			}
		}

		private static void EnsureOverlay()
		{
			if (_overlay == null) {
				try {
					_overlay = new HighlightOverlay();
				} catch (Exception ex) {
					Debug.WriteLine($"[NavMapper] Failed to create overlay: {ex.Message}");
				}
			}
		}

		// === EVENT HANDLERS (Reactive Architecture) ===

		private static void OnNavNodeAdded(NavNode node)
		{
			if (node == null) return;
			
			Debug.WriteLine($"[NavMapper] Node added: {node.SimpleName} @ {node.HierarchicalPath}");
			
			try { NavMapUpdated?.Invoke(); } catch { }
		}

		private static void OnNavNodeRemoved(NavNode node)
		{
			if (node == null) return;
			
			Debug.WriteLine($"[NavMapper] Node removed: {node.SimpleName} @ {node.HierarchicalPath}");
			
			if (ReferenceEquals(_focusedNode, node)) {
				_focusedNode = null;
				_overlay?.HideFocusRect();
			}
			
			_activeModalStack.RemoveAll(n => ReferenceEquals(n, node));
			
			try { NavMapUpdated?.Invoke(); } catch { }
		}

		private static void OnModalGroupOpened(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[NavMapper] Modal opened: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			// Validate linear chain
			if (_activeModalStack.Count > 0) {
				var currentTop = _activeModalStack[_activeModalStack.Count - 1];
				if (!IsDescendantOf(modalNode, currentTop)) {
					Debug.WriteLine($"[NavMapper] ERROR: Modal {modalNode.SimpleName} not descendant of {currentTop.SimpleName}!");
					return;
				}
			}
			
			_activeModalStack.Add(modalNode);
			Debug.WriteLine($"[NavMapper] Modal stack depth: {_activeModalStack.Count}");
			
			// Clear focus if outside modal scope
			if (_focusedNode != null && !IsInActiveModalScope(_focusedNode)) {
				_focusedNode = null;
				_overlay?.HideFocusRect();
			}
		}

		private static void OnModalGroupClosed(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[NavMapper] Modal closed: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			if (_activeModalStack.Count > 0 
				&& _activeModalStack[_activeModalStack.Count - 1].HierarchicalPath == modalNode.HierarchicalPath) {
				_activeModalStack.RemoveAt(_activeModalStack.Count - 1);
			} else {
				Debug.WriteLine($"[NavMapper] WARNING: Closed modal not at top");
				_activeModalStack.RemoveAll(m => m.HierarchicalPath == modalNode.HierarchicalPath);
			}
		}

		private static void OnRootChanged(FrameworkElement root)
		{
			try {
				NavMapUpdated?.Invoke();
			} catch { }
		}

		// === PUBLIC API ===

		public static bool MoveInDirection(NavDirection dir)
		{
			if (_focusedNode == null) return false;

			var best = FindBestCandidateInDirection(_focusedNode, dir);
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
			if (_focusedNode == null) return false;

			_suppressFocusTracking = true;
			try {
				return _focusedNode.Activate();
			} finally {
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		public static bool ExitGroup()
		{
			if (_activeModalStack.Count == 0) return false;

			var topModal = _activeModalStack[_activeModalStack.Count - 1];
			return topModal.Close();
		}

		public static bool FocusNodeByPath(string hierarchicalPath)
		{
			if (string.IsNullOrEmpty(hierarchicalPath)) return false;
			
			// Find node by iterating all nodes and checking HierarchicalPath
			var node = NavForest.GetAllNavNodes().FirstOrDefault(n => n.HierarchicalPath == hierarchicalPath);
			if (node == null) return false;
			if (!IsNavigableForSelection(node)) return false;
			
			return SetFocus(node);
		}

		public static string GetFocusedNodePath() => _focusedNode?.HierarchicalPath;
		
		public static NavNode GetFocusedNode() => _focusedNode;
		
		public static NavNode GetActiveModal() => 
			_activeModalStack.Count > 0 ? _activeModalStack[_activeModalStack.Count - 1] : null;
		
		public static IReadOnlyList<string> GetModalStackPaths() => 
			_activeModalStack.Select(n => n.HierarchicalPath).ToList();
		// === FOCUS MANAGEMENT ===

		private static bool SetFocus(NavNode newNode)
		{
			if (ReferenceEquals(_focusedNode, newNode)) return true;

			var oldNode = _focusedNode;
			
			if (oldNode != null) {
				oldNode.HasFocus = false;
			}

			if (newNode != null) {
				newNode.HasFocus = true;
				_focusedNode = newNode;
				UpdateFocusRect(newNode);
				try { FocusChanged?.Invoke(oldNode, newNode); } catch { }
				return true;
			}

			_focusedNode = null;
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
			
			// Walk up the Parent chain to see if we find the ancestor
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
			if (_activeModalStack.Count == 0) return true;
			var effectiveModal = _activeModalStack[_activeModalStack.Count - 1];
			return IsDescendantOf(node, effectiveModal);
		}

		private static List<NavNode> GetCandidatesInScope()
		{
			return NavForest.GetAllNavNodes()
				.Where(n => IsNavigableForSelection(n) && IsInActiveModalScope(n))
				.ToList();
		}

		private static bool IsNavigableForSelection(NavNode node)
		{
			if (!node.IsNavigable) return false;
			
			if (node.IsGroup) {
				if (!node.IsDualRoleGroup) return false;
				
				// For dual-role groups, check the actual WPF state
				// (ComboBox.IsDropDownOpen, ContextMenu.IsOpen, etc.)
				return !node.IsDualModalCurrentlyOpen();
			}
			
			return true;
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

			// Walk up the Parent chain to find first non-modal group
			var current = node.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (parentNode.IsGroup)
				{
					if (parentNode.IsModal) return null; // Hit modal boundary
					return parentNode; // Found non-modal group
				}
				current = parentNode.Parent;
			}
			
			return null;
		}

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a == null || b == null) return false;
			if (a.Parent == null || b.Parent == null) return false;
			
			// Check if both have same parent (reference equality)
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
			if (e.Key == Key.F12 && 
				(Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 
				(ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting(filterByModalScope: true);
				return;
			}

			// Ctrl+Shift+F11: Toggle debug overlay (show ALL nodes, unfiltered)
			if (e.Key == Key.F11 && 
				(Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 
				(ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting(filterByModalScope: false);
				return;
			}

			// Alt+Arrow keys: Navigation
			if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) {
				bool handled = false;
				var key = e.Key == Key.System ? e.SystemKey : e.Key;
				
				switch (key) {
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
				if (content != null) NavForest.RegisterRoot(content);
			}
			
			if (_focusedNode != null) {
				UpdateFocusRect(_focusedNode);
			}
		}

		// === DEBUG VISUALIZATION ===

		/// <summary>
		/// Validates the consistency of all NavNodes in the forest.
		/// Checks:
		/// 1. Visual element is still alive and accessible
		/// 2. Parent reference matches actual visual tree parent
		/// 3. Children list internal consistency (no duplicates, dead refs, parent back-links)
		/// 4. Cross-check: All NavNodes in visual subtree are reachable through tree structure
		/// </summary>
		private static void ValidateNodeConsistency()
		{
			Debug.WriteLine("\n========== NavNode Consistency Check ==========");
			
			var allNodes = NavForest.GetAllNavNodes().ToList();
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
							if (current is FrameworkElement parentFe && NavForest.TryGetNavNode(parentFe, out var candidateParent))
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
						if (!ReferenceEquals(current, fe) && current is FrameworkElement childFe && NavForest.TryGetNavNode(childFe, out var foundNode))
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
				return;
			}

			// ? CONSISTENCY CHECK BEFORE DISPLAYING
			ValidateNodeConsistency();

			if (filterByModalScope) {
				Debug.WriteLine("\n========== NavMapper: Highlight Rectangles (Active Modal Scope ONLY) ==========");
			} else {
				Debug.WriteLine("\n========== NavMapper: Highlight Rectangles (ALL NODES - Unfiltered) ==========");
			}
			
			// Show modal stack
			if (_activeModalStack.Count > 0) {
				Debug.WriteLine($"Modal Stack ({_activeModalStack.Count} active):");
				for (int i = 0; i < _activeModalStack.Count; i++) {
					var modal = _activeModalStack[i];
					Debug.WriteLine($"  [{i}] {modal.HierarchicalPath}");
				}
			} else {
				Debug.WriteLine("Modal Stack: (empty - root context)");
			}
			Debug.WriteLine("");

			var leafRects = new List<Rect>();
			var groupRects = new List<Rect>();
			var allDebugInfo = new List<DebugRectInfo>();
			
			// Get nodes from NavForest (authoritative source)
			List<NavNode> nodesToShow;
			if (filterByModalScope) {
				// Filtered: Only show nodes in active modal scope
				nodesToShow = NavForest.GetAllNavNodes()
					.Where(n => n.IsNavigable && IsInActiveModalScope(n))
					.ToList();
				Debug.WriteLine($"Nodes in current modal scope: {nodesToShow.Count}");
			} else {
				// Unfiltered: Show ALL discovered nodes
				nodesToShow = NavForest.GetAllNavNodes()
					.Where(n => n.IsNavigable)
					.ToList();
				Debug.WriteLine($"All discovered nodes: {nodesToShow.Count}");
			}
			
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
						var nodeType = "Leaf";
						var navigable = "[NAVIGABLE]";
						var scopeInfo = "";
						
						if (node.IsGroup) {
							if (node.IsDualRoleGroup) {
								// Check actual WPF state instead of modal stack
								var isOpen = node.IsDualModalCurrentlyOpen();
								nodeType = isOpen ? "DualGroup(OPEN)" : "DualGroup(closed)";
								navigable = isOpen ? "[NOT navigable - open]" : "[NAVIGABLE]";
							} else {
								nodeType = "PureGroup";
								navigable = "[NOT navigable - pure group]";
							}
						}
						
						// Add scope information if we're showing all nodes
						if (!filterByModalScope && _activeModalStack.Count > 0) {
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
						
						// Build formatted debug line with Bounds column
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
				EnsureOverlay();
				_overlay?.ShowDebugRects(leafRects, groupRects);
				_debugMode = true;
			} catch (Exception ex) {
				Debug.WriteLine($"[NavMapper] Overlay error: {ex.Message}");
			}
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

		public static IEnumerable<NavNode> GetAllNavNodes() => NavForest.GetAllNavNodes();
		
		public static bool TryGetById(string id, out NavNode nav)
		{
			// Lookup by HierarchicalPath
			nav = NavForest.GetAllNavNodes().FirstOrDefault(n => n.HierarchicalPath == id);
			return nav != null;
		}
	}
}
