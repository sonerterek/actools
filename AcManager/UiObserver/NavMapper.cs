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
		
		// Focus management (store HierarchicalPath, not SimpleName!)
		private static string _focusedNodePath;
		
		// Focus lock to prevent feedback loops
		private static bool _suppressFocusTracking = false;

		// Events
		public static event Action NavMapUpdated;
		public static event Action<string, string> FocusChanged; // (oldPath, newPath)

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
			
			if (_focusedNodePath == node.HierarchicalPath) {
				_focusedNodePath = null;
				_overlay?.HideFocusRect();
			}
			
			_activeModalStack.RemoveAll(n => n.HierarchicalPath == node.HierarchicalPath);
			
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
			if (_focusedNodePath != null && NavForest.TryGetNavNodeByPath(_focusedNodePath, out var focusedNode)) {
				if (!IsInActiveModalScope(focusedNode)) {
					_focusedNodePath = null;
					_overlay?.HideFocusRect();
				}
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
			if (string.IsNullOrEmpty(_focusedNodePath)) return false;
			if (!NavForest.TryGetNavNodeByPath(_focusedNodePath, out var current)) return false;

			var best = FindBestCandidateInDirection(current, dir);
			if (best == null) return false;

			_suppressFocusTracking = true;
			try {
				return SetFocus(best.HierarchicalPath);
			} finally {
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		public static bool ActivateFocusedNode()
		{
			if (string.IsNullOrEmpty(_focusedNodePath)) return false;
			if (!NavForest.TryGetNavNodeByPath(_focusedNodePath, out var node)) return false;

			_suppressFocusTracking = true;
			try {
				return node.Activate();
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
			if (!NavForest.TryGetNavNodeByPath(hierarchicalPath, out var node)) return false;
			if (!IsNavigableForSelection(node)) return false;
			return SetFocus(hierarchicalPath);
		}

		public static string GetFocusedNodePath() => _focusedNodePath;
		
		public static NavNode GetActiveModal() => 
			_activeModalStack.Count > 0 ? _activeModalStack[_activeModalStack.Count - 1] : null;
		
		public static IReadOnlyList<string> GetModalStackPaths() => 
			_activeModalStack.Select(n => n.HierarchicalPath).ToList();
		// === FOCUS MANAGEMENT ===

		private static bool SetFocus(string nodePath)
		{
			if (_focusedNodePath == nodePath) return true;

			var oldPath = _focusedNodePath;
			
			if (!string.IsNullOrEmpty(oldPath) && NavForest.TryGetNavNodeByPath(oldPath, out var oldNode)) {
				oldNode.HasFocus = false;
			}

			if (NavForest.TryGetNavNodeByPath(nodePath, out var newNode)) {
				newNode.HasFocus = true;
				_focusedNodePath = nodePath;
				UpdateFocusRect(newNode);
				try { FocusChanged?.Invoke(oldPath, nodePath); } catch { }
				return true;
			}

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
			if (child.HierarchicalId == null || ancestor.HierarchicalId == null) return false;
			if (ancestor.HierarchicalId.Length >= child.HierarchicalId.Length) return false;
			
			for (int i = 0; i < ancestor.HierarchicalId.Length; i++) {
				if (child.HierarchicalId[i] != ancestor.HierarchicalId[i]) return false;
			}
			return true;
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
			if (node == null || node.HierarchicalId == null || node.HierarchicalId.Length <= 1)
				return null;

			for (int i = node.HierarchicalId.Length - 2; i >= 0; i--) {
				// Build the ancestor's HierarchicalPath from the HierarchicalId array
				var ancestorPath = string.Join(" > ", node.HierarchicalId.Take(i + 1));
				
				if (NavForest.TryGetNavNodeByPath(ancestorPath, out var ancestorNode) && ancestorNode.IsGroup) {
					if (ancestorNode.IsModal) return null;
					return ancestorNode;
				}
			}
			return null;
		}

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a == null || b == null) return false;
			if (a.HierarchicalId == null || b.HierarchicalId == null) return false;
			if (a.HierarchicalId.Length <= 1 || b.HierarchicalId.Length <= 1) return false;
			
			var aParentId = a.HierarchicalId[a.HierarchicalId.Length - 2];
			var bParentId = b.HierarchicalId[b.HierarchicalId.Length - 2];
			return aParentId == bParentId;
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
			
			if (!string.IsNullOrEmpty(_focusedNodePath) && NavForest.TryGetNavNodeByPath(_focusedNodePath, out var focusedNode)) {
				UpdateFocusRect(focusedNode);
			}
		}

		// === DEBUG VISUALIZATION ===

		private static void ToggleHighlighting(bool filterByModalScope)
		{
			if (_debugMode) {
				ClearHighlighting();
				_debugMode = false;
				return;
			}

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
						
						// Build formatted debug line
						var debugLine = $"{typeName,-20} | {elementName,-20} | {nodeType,-18} | {modalTag,-6} | {navigable,-35}{scopeInfo,-15} | {navId,-30} | ({rect.Left,7:F1}, {rect.Top,7:F1}) {rect.Width,6:F1}x{rect.Height,6:F1} | {hierarchicalPath}";
						
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
				Debug.WriteLine("Mode: UNFILTERED (Ctrl+Alt+F11) - showing ALL discovered nodes");
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
		public static bool TryGetById(string id, out NavNode nav) => NavForest.TryGetNavNodeByPath(id, out nav);
	}
}
