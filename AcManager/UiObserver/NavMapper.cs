using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Windows.Input;
using System.Diagnostics; // added for diagnostics

namespace AcManager.UiObserver
{
	internal enum NavDirection { Up, Down, Left, Right }

	// Top-level coordinator: subscribes to NavForest events and exposes global navigation API
	internal static class NavMapper
	{
		private static bool _initialized = false;
		private static readonly ConcurrentDictionary<string, NavNode> _navById = new ConcurrentDictionary<string, NavNode>();

		// Focus and group stack management
		private static string _focusedNodeId;
		private static readonly Stack<string> _activeGroupStack = new Stack<string>();
		private static readonly Dictionary<string, List<IDisposable>> _groupMonitors = new Dictionary<string, List<IDisposable>>();
		
		// Focus lock to prevent feedback loops
		private static bool _suppressFocusTracking = false;

		// Events
		public static event Action NavMapUpdated;
		public static event Action<string, string> FocusChanged; // (oldId, newId)
		public static event Action<string> GroupEntered; // groupId
		public static event Action<string> GroupExited; // groupId

		// Highlighting overlay
		private static HighlightOverlay _overlay;
		private static bool _debugMode;

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			NavNode.PathFilter.AddExcludeRule("Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu");

			// configure NavForest with helpers
			NavForest.Configure(IsTrulyVisible);
			NavForest.RootChanged += OnRootChanged;
			NavForest.FocusedNodeChanged += OnWpfFocusChanged;
			
			// enable automatic tracking of newly created visual roots (popups, menus, etc.)
			NavForest.EnableAutoRootTracking();

			// Register class handler so PreviewKeyDown is caught for Window only
			try {
				EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
			} catch { }

			if (Application.Current != null) {
				Application.Current.Dispatcher.BeginInvoke(new Action(() => {
					foreach (Window w in Application.Current.Windows) {
						HookWindow(w);
						var content = w.Content as FrameworkElement;
						if (content != null) {
							NavForest.RegisterRoot(content);
						}
					}
					
					// Initialize overlay after main window is ready
					EnsureOverlay();
				}), DispatcherPriority.ApplicationIdle);
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

		// === PUBLIC NAVIGATION API ===

		/// <summary>
		/// Move focus in the specified direction within the current active group context.
		/// Returns false if no valid candidate found (focus unchanged).
		/// </summary>
		public static bool MoveInDirection(NavDirection dir)
		{
			// Auto-enter any newly opened modal groups
			CheckForOpenModalGroups();

			if (string.IsNullOrEmpty(_focusedNodeId)) return false;
			if (!_navById.TryGetValue(_focusedNodeId, out var current)) return false;

			var best = FindBestCandidateInDirection(current, dir);
			if (best == null) return false;

			// Lock focus tracking during navigation to prevent feedback loops
			_suppressFocusTracking = true;
			try {
				return SetFocus(best.Id);
			} finally {
				// Release lock after a short delay to allow WPF focus events to settle
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		/// <summary>
		/// Activate the currently focused node using its type-specific activation behavior.
		/// Returns false if no focused node or activation failed.
		/// </summary>
		public static bool ActivateFocusedNode()
		{
			if (string.IsNullOrEmpty(_focusedNodeId)) return false;
			if (!_navById.TryGetValue(_focusedNodeId, out var node)) return false;

			// Lock focus tracking during activation to prevent feedback loops
			// (some controls set WPF focus when clicked/activated)
			_suppressFocusTracking = true;
			try {
				// Delegate to the node's polymorphic Activate method
				return node.Activate();
			} finally {
				// Release lock after a short delay to allow WPF focus events to settle
				Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
					_suppressFocusTracking = false;
				}), DispatcherPriority.Input);
			}
		}

		/// <summary>
		/// Exit the current active modal group (pop stack).
		/// Sets focus back to the group node itself (now closed).
		/// Returns false if already at root context.
		/// </summary>
		public static bool ExitGroup()
		{
			if (_activeGroupStack.Count == 0) return false;

			var groupId = _activeGroupStack.Peek();
			if (!_navById.TryGetValue(groupId, out var node)) return false;

			// Try to close the group using its polymorphic Close method
			if (node is INavGroup group) {
				if (group.Close()) {
					// Auto-exit will be triggered by the IsOpen change detection
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Directly set focus to a specific node by ID.
		/// Adjusts active group stack to match the node's context.
		/// Returns false if node not found or not navigable.
		/// </summary>
		public static bool FocusNodeById(string id)
		{
			if (string.IsNullOrEmpty(id)) return false;
			if (!_navById.TryGetValue(id, out var node)) return false;
			if (!IsNavigableForSelection(node)) return false;

			// TODO: Adjust group stack to match node's context
			// For now, just set focus
			return SetFocus(id);
		}

		// === STATE QUERY API ===

		/// <summary>
		/// Get the ID of the currently focused node.
		/// Returns null if no focus established.
		/// </summary>
		public static string GetFocusedNodeId() => _focusedNodeId;

		/// <summary>
		/// Get the active modal group (top of stack), or null if at root context.
		/// </summary>
		public static INavGroup GetActiveGroup()
		{
			if (_activeGroupStack.Count == 0) return null;
			var groupId = _activeGroupStack.Peek();
			if (_navById.TryGetValue(groupId, out var node) && node is INavGroup group) {
				return group;
			}
			return null;
		}

		/// <summary>
		/// Get read-only copy of the current group stack (bottom to top).
		/// </summary>
		public static IReadOnlyList<string> GetGroupStackIds() => _activeGroupStack.Reverse().ToList();

		// === INTERNAL FOCUS MANAGEMENT ===

		private static void OnWpfFocusChanged(string nodeId)
		{
			if (string.IsNullOrEmpty(nodeId)) return;
			
			// Ignore focus changes when we're in the middle of a navigation operation
			if (_suppressFocusTracking) {
				Debug.WriteLine($"[NavMapper] WPF focus change suppressed (locked): {nodeId}");
				return;
			}
			
			// Verify this is a valid navigation target (leaf or closed dual-role group)
			if (_navById.TryGetValue(nodeId, out var node)) {
				if (!IsNavigableForSelection(node)) {
					Debug.WriteLine($"[NavMapper] Ignoring focus on non-selectable node: {nodeId} (IsGroup={node.IsGroup}, IsDualRole={node.IsDualRoleGroup})");
					return;
				}
			}
			
			// Only update our focus if we don't already have focus or it's different
			if (_focusedNodeId != nodeId) {
				Debug.WriteLine($"[NavMapper] WPF focus changed to: {nodeId}");
				SetFocus(nodeId);
			}
		}

		private static bool SetFocus(string nodeId)
		{
			if (_focusedNodeId == nodeId) return true; // Already focused

			var oldId = _focusedNodeId;
			
			// Clear old focus
			if (!string.IsNullOrEmpty(oldId) && _navById.TryGetValue(oldId, out var oldNode)) {
				oldNode.HasFocus = false;
			}

			// Set new focus
			if (_navById.TryGetValue(nodeId, out var newNode)) {
				newNode.HasFocus = true;
				_focusedNodeId = nodeId;

				// Update focus rectangle on overlay
				UpdateFocusRect(newNode);

				try { FocusChanged?.Invoke(oldId, nodeId); } catch { }
				
				Debug.WriteLine($"[NavMapper] Focus: {oldId} -> {nodeId}");
				return true;
			}

			return false;
		}

		private static void UpdateFocusRect(NavNode node)
		{
			if (_overlay == null) {
				EnsureOverlay();
				if (_overlay == null) return;
			}

			if (node == null) {
				_overlay.HideFocusRect();
				return;
			}

			if (!node.TryGetVisual(out var fe)) {
				_overlay.HideFocusRect();
				return;
			}

			try {
				if (!fe.IsLoaded || !fe.IsVisible) {
					_overlay.HideFocusRect();
					return;
				}

				// Get screen bounds in device pixels
				Point tlDevice, brDevice;
				try {
					tlDevice = fe.PointToScreen(new Point(0, 0));
					brDevice = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
				} catch {
					_overlay.HideFocusRect();
					return;
				}

				// Transform from device pixels to DIP
				Point tlDip, brDip;
				var ps = PresentationSource.FromVisual(fe);
				if (ps?.CompositionTarget != null) {
					var transform = ps.CompositionTarget.TransformFromDevice;
					tlDip = transform.Transform(tlDevice);
					brDip = transform.Transform(brDevice);
				} else {
					tlDip = tlDevice;
					brDip = brDevice;
				}

				var x1 = Math.Min(tlDip.X, brDip.X);
				var y1 = Math.Min(tlDip.Y, brDip.Y);
				var x2 = Math.Max(tlDip.X, brDip.X);
				var y2 = Math.Max(tlDip.Y, brDip.Y);
				var rectDip = new Rect(new Point(x1, y1), new Point(x2, y2));

				if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height) ||
					rectDip.Width < 1.0 || rectDip.Height < 1.0) {
					_overlay.HideFocusRect();
					return;
				}

				_overlay.ShowFocusRect(rectDip);
			} catch (Exception ex) {
				Debug.WriteLine($"[NavMapper] Failed to update focus rect: {ex.Message}");
				_overlay.HideFocusRect();
			}
		}

		// === GROUP STACK MANAGEMENT ===

		private static void CheckForOpenModalGroups()
		{
			// Scan all dual-role groups to detect newly opened ones
			foreach (var node in _navById.Values.Where(n => n.IsDualRoleGroup && n.IsGroup)) {
				var group = node as INavGroup;
				if (group != null && group.IsOpen) {
					// Check if it's not already on the stack
					if (!_activeGroupStack.Contains(node.Id)) {
						AutoEnterGroup(node.Id);
					}
				} else if (group != null && !group.IsOpen) {
					// Check if it's on the stack (should be auto-exited)
					if (_activeGroupStack.Contains(node.Id)) {
						AutoExitGroup(node.Id);
					}
				}
			}
		}

		private static void AutoEnterGroup(string groupId)
		{
			if (!_navById.TryGetValue(groupId, out var node)) return;
			if (!node.TryGetVisual(out var fe)) return;

			Debug.WriteLine($"[NavMapper] Auto-entering group: {groupId}");

			// Push to stack
			_activeGroupStack.Push(groupId);

			// Setup monitoring for IsOpen changes
			SetupGroupMonitoring(node, fe);

			try { GroupEntered?.Invoke(groupId); } catch { }

			// Move focus to first navigable child
			var group = node as INavGroup;
			if (group != null) {
				var firstChild = group.GetNavigableChildren().FirstOrDefault();
				if (firstChild != null) {
					// Lock focus tracking when auto-entering groups
					_suppressFocusTracking = true;
					try {
						SetFocus(firstChild.Id);
					} finally {
						Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
							_suppressFocusTracking = false;
						}), DispatcherPriority.Input);
					}
				}
			}
		}

		private static void AutoExitGroup(string groupId)
		{
			if (_activeGroupStack.Count == 0) return;
			if (_activeGroupStack.Peek() != groupId) {
				// Group is not at top of stack, need to unwind
				Debug.WriteLine($"[NavMapper] Group {groupId} not at top of stack, unwinding...");
				while (_activeGroupStack.Count > 0 && _activeGroupStack.Peek() != groupId) {
					var toRemove = _activeGroupStack.Pop();
					CleanupGroupMonitoring(toRemove);
					try { GroupExited?.Invoke(toRemove); } catch { }
				}
			}

			if (_activeGroupStack.Count > 0 && _activeGroupStack.Peek() == groupId) {
				_activeGroupStack.Pop();
				CleanupGroupMonitoring(groupId);

				Debug.WriteLine($"[NavMapper] Auto-exiting group: {groupId}");
				try { GroupExited?.Invoke(groupId); } catch { }

				// Return focus to the group itself (if it's now a valid navigation target)
				if (_navById.TryGetValue(groupId, out var node) && IsNavigableForSelection(node)) {
					// Lock focus tracking when auto-exiting groups
					_suppressFocusTracking = true;
					try {
						SetFocus(groupId);
					} finally {
						Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
							_suppressFocusTracking = false;
						}), DispatcherPriority.Input);
					}
				}
			}
		}

		private static void SetupGroupMonitoring(NavNode node, FrameworkElement fe)
		{
			if (_groupMonitors.ContainsKey(node.Id)) return;

			var monitors = new List<IDisposable>();

			// Monitor IsOpen property for ComboBox
			if (fe is ComboBox comboBox) {
				var descriptor = DependencyPropertyDescriptor.FromProperty(ComboBox.IsDropDownOpenProperty, typeof(ComboBox));
				if (descriptor != null) {
					EventHandler handler = (s, e) => OnGroupOpenChanged(node.Id, comboBox.IsDropDownOpen);
					descriptor.AddValueChanged(comboBox, handler);
					monitors.Add(new DelegateDisposable(() => descriptor.RemoveValueChanged(comboBox, handler)));
				}
			}
			// Monitor IsOpen property for ContextMenu
			else if (fe is ContextMenu contextMenu) {
				var descriptor = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
				if (descriptor != null) {
					EventHandler handler = (s, e) => OnGroupOpenChanged(node.Id, contextMenu.IsOpen);
					descriptor.AddValueChanged(contextMenu, handler);
					monitors.Add(new DelegateDisposable(() => descriptor.RemoveValueChanged(contextMenu, handler)));
				}
			}

			if (monitors.Count > 0) {
				_groupMonitors[node.Id] = monitors;
			}
		}

		private static void CleanupGroupMonitoring(string groupId)
		{
			if (_groupMonitors.TryGetValue(groupId, out var monitors)) {
				foreach (var monitor in monitors) {
					try { monitor.Dispose(); } catch { }
				}
				_groupMonitors.Remove(groupId);
			}
		}

		private static void OnGroupOpenChanged(string groupId, bool isOpen)
		{
			if (isOpen) {
				// Auto-enter if not already in stack
				if (!_activeGroupStack.Contains(groupId)) {
					AutoEnterGroup(groupId);
				}
			} else {
				// Auto-exit if in stack
				if (_activeGroupStack.Contains(groupId)) {
					AutoExitGroup(groupId);
				}
			}
		}

		// Helper class for disposable event handlers
		private class DelegateDisposable : IDisposable
		{
			private readonly Action _disposeAction;
			public DelegateDisposable(Action disposeAction) { _disposeAction = disposeAction; }
			public void Dispose() { _disposeAction?.Invoke(); }
		}

		// === DIRECTION-FINDING ALGORITHM ===

		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir)
		{
			var curCenter = current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			// Determine the search scope (respects modal group boundaries)
			var allCandidates = GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;

			// Get direction vector
			var dirVector = GetDirectionVector(dir);

			// PASS 1: Try to find best candidate within the same non-modal group
			// This keeps navigation contained within logical groupings (TabControl, ListBox, etc.)
			var sameGroupBest = FindBestInCandidates(
				current, 
				curCenter.Value, 
				dir, 
				dirVector, 
				allCandidates.Where(c => AreInSameNonModalGroup(current, c)).ToList()
			);

			if (sameGroupBest != null)
			{
				Debug.WriteLine($"[NavMapper] Found candidate in same group: {sameGroupBest.Id}");
				return sameGroupBest;
			}

			// PASS 2: No candidates in same group - search across all groups
			// This allows navigation to jump between different containers
			var crossGroupBest = FindBestInCandidates(current, curCenter.Value, dir, dirVector, allCandidates);

			if (crossGroupBest != null)
			{
				Debug.WriteLine($"[NavMapper] Found candidate across groups: {crossGroupBest.Id}");
			}
			else
			{
				Debug.WriteLine($"[NavMapper] No candidates found in direction {dir}");
			}

			return crossGroupBest;
		}

		/// <summary>
		/// Checks if two nodes are in the same non-modal group.
		/// Returns true if they share a common non-modal group ancestor.
		/// Returns false if they're separated by a modal group or have no common non-modal group.
		/// </summary>
		private static bool AreInSameNonModalGroup(NavNode a, NavNode b)
		{
			// Find the closest non-modal group ancestor for each node
			var groupA = FindClosestNonModalGroup(a);
			var groupB = FindClosestNonModalGroup(b);

			// If either has no non-modal group, they're not in the same group
			if (groupA == null || groupB == null) return false;

			// They're in the same group if they share the same non-modal group ancestor
			return groupA.Id == groupB.Id;
		}

		/// <summary>
		/// Finds the closest non-modal group ancestor of a node.
		/// Stops searching when encountering a modal group (which acts as a boundary).
		/// Returns null if no non-modal group is found.
		/// </summary>
		private static NavNode FindClosestNonModalGroup(NavNode node)
		{
			var current = node.Parent;
			while (current != null)
			{
				if (current is NavNode parentNode && parentNode.IsGroup)
				{
					// Stop at modal groups - they act as boundaries
					if (parentNode.IsModal) return null;
					
					// Found a non-modal group
					return parentNode;
				}
				current = current.Parent;
			}
			return null;
		}

		/// <summary>
		/// Finds the best navigation candidate from a list of candidates in a given direction.
		/// Uses distance and alignment scoring to determine the best match.
		/// </summary>
		private static NavNode FindBestInCandidates(
			NavNode current,
			Point currentCenter,
			NavDirection dir,
			Point dirVector,
			List<NavNode> candidates)
		{
			if (candidates.Count == 0) return null;

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				if (candidate.Id == current.Id) continue;
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) continue;

				var c = candidateCenter.Value;
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) continue;

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				// Must be in the general direction (dot > 0)
				if (dot <= 0) continue;

				// Compute base cost: distance / alignment
				var baseCost = len / Math.Max(1e-7, dot);

				// Apply bonuses
				var cost = baseCost;

				// Bonus: same immediate parent group (-30%)
				if (HaveSameImmediateParent(current, candidate))
				{
					cost *= 0.7;
				}

				// Bonus: well-aligned on primary axis (-20%)
				if (IsWellAligned(currentCenter, c, dir))
				{
					cost *= 0.8;
				}

				validCandidates.Add(new ScoredCandidate { Node = candidate, Cost = cost });
			}

			if (validCandidates.Count == 0) return null;

			// Return best candidate (lowest cost)
			return validCandidates.OrderBy(sc => sc.Cost).First().Node;
		}

		private class ScoredCandidate
		{
			public NavNode Node { get; set; }
			public double Cost { get; set; }
		}

		/// <summary>
		/// Determines if a node is navigable for selection purposes.
		/// 
		/// Basic rule: Leaves are always navigable (if IsNavigable passes)
		/// Special rule for groups:
		/// - Pure groups (ListBox, TabControl): NEVER navigable for selection
		/// - Dual-role groups (ComboBox, ContextMenu): Only navigable when CLOSED
		/// 
		/// This keeps the navigation layer (NavMapper) responsible for dual-role logic,
		/// while the node layer (NavNode) only handles basic visibility/validity.
		/// </summary>
		private static bool IsNavigableForSelection(NavNode node)
		{
			// Basic validity check (alive, visible, has bounds, etc.)
			if (!node.IsNavigable) return false;
			
			if (node.IsGroup)
			{
				// Pure groups: never navigable for selection
				// (their children are navigable, not the group itself)
				if (!node.IsDualRoleGroup) return false;
				
				// Dual-role groups: only navigable when closed
				// When closed: act as leaves (user navigates TO them)
				// When open: act as groups (user navigates WITHIN them)
				var group = node as INavGroup;
				return group != null && !group.IsOpen;
			}
			
			// Leaves: always navigable
			return true;
		}

		private static List<NavNode> GetCandidatesInScope()
		{
			if (_activeGroupStack.Count == 0) {
				// Root context: all navigable nodes not inside modal groups
				return _navById.Values
					.Where(n => IsNavigableForSelection(n) && !IsInsideModalGroup(n))
					.ToList();
			} else {
				// Modal group context: only descendants of active group
				var groupId = _activeGroupStack.Peek();
				if (_navById.TryGetValue(groupId, out var groupNode) && groupNode is INavGroup group) {
					return GetAllDescendants(group)
						.OfType<NavNode>()
						.Where(n => IsNavigableForSelection(n))
						.ToList();
				}
				return new List<NavNode>();
			}
		}

		private static bool IsInsideModalGroup(NavNode node)
		{
			// Check if node is a descendant of any dual-role group that's currently open
			var current = node.Parent;
			while (current != null) {
				if (current is NavNode parentNode && parentNode.IsDualRoleGroup && parentNode.IsGroup) {
					var group = parentNode as INavGroup;
					if (group != null && group.IsOpen) {
						return true;
					}
				}
				current = current.Parent;
			}
			return false;
		}

		private static List<INavNode> GetAllDescendants(INavGroup group)
		{
			var result = new List<INavNode>();
			var queue = new Queue<INavNode>(group.GetNavigableChildren());

			while (queue.Count > 0) {
				var node = queue.Dequeue();
				result.Add(node);

				if (node is INavGroup childGroup) {
					foreach (var child in childGroup.GetNavigableChildren()) {
						queue.Enqueue(child);
					}
				}
			}

			return result;
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

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a.Parent == null || b.Parent == null) return false;
			return a.Parent.Id == b.Parent.Id;
		}

		private static bool IsWellAligned(Point from, Point to, NavDirection dir)
		{
			const double alignmentThreshold = 20.0; // pixels

			switch (dir) {
				case NavDirection.Up:
				case NavDirection.Down:
					// Check horizontal alignment
					return Math.Abs(from.X - to.X) < alignmentThreshold;
				case NavDirection.Left:
				case NavDirection.Right:
					// Check vertical alignment
					return Math.Abs(from.Y - to.Y) < alignmentThreshold;
				default:
					return false;
			}
		}

		// === DEBUG AND VISUALIZATION ===

		public static void DebugDumpOverlayInfo()
		{
			try {
				Debug.WriteLine($"SystemParameters.VirtualScreenLeft={SystemParameters.VirtualScreenLeft}");
				Debug.WriteLine($"SystemParameters.VirtualScreenTop={SystemParameters.VirtualScreenTop}");
				Debug.WriteLine($"SystemParameters.VirtualScreenWidth={SystemParameters.VirtualScreenWidth}");
				Debug.WriteLine($"SystemParameters.VirtualScreenHeight={SystemParameters.VirtualScreenHeight}");

				if (_overlay != null) {
					Debug.WriteLine($"Overlay Left={_overlay.Left} Top={_overlay.Top} Width={_overlay.Width} Height={_overlay.Height} IsVisible={_overlay.IsVisible}");
				} else Debug.WriteLine("Overlay is null");

				var main = Application.Current?.MainWindow;
				if (main != null) {
					Debug.WriteLine("Main window present");
				} else Debug.WriteLine("Application.Current.MainWindow is null");

				// Dump first 10 NavNodes
				int i = 0;
				foreach (var node in NavForest.GetAllNavNodes()) {
					if (i++ > 10) break;
					if (!node.TryGetVisual(out var fe)) { Debug.WriteLine($"Nav {node.Id}: Visual=GONE"); continue; }
					var center = node.GetCenterDip();
					Debug.WriteLine($"Nav {node.Id}: Center={center} Fe.PointToScreen(0,0)={(TryPointToScreen(fe))} Fe.Actual={fe.ActualWidth}x{fe.ActualHeight}");
				}
			} catch (Exception ex) {
				Debug.WriteLine($"DebugDumpOverlayInfo failed: {ex.Message}");
			}
		}

		private static Point TryPointToScreen(FrameworkElement fe)
		{
			try { return fe.PointToScreen(new Point(0, 0)); } catch { return new Point(double.NaN, double.NaN); }
		}

		private static void HookWindow(Window w)
		{
			if (w == null) return;
			w.Loaded -= OnRootLayoutChanged;
			w.Loaded += OnRootLayoutChanged;
			w.LayoutUpdated -= OnRootLayoutChanged;
			w.LayoutUpdated += OnRootLayoutChanged;
			w.LocationChanged -= OnRootLayoutChanged;
			w.LocationChanged += OnRootLayoutChanged;
			w.SizeChanged -= OnRootLayoutChanged;
			w.SizeChanged += OnRootLayoutChanged;

			w.PreviewKeyDown -= OnPreviewKeyDown;
			w.PreviewKeyDown += OnPreviewKeyDown;
		}

		private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e == null) return;
			
			// Ctrl+Shift+F12: Toggle highlighting overlay
			if (e.Key == Key.F12 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting();
				return;
			}

			// Alt+Arrow keys: Directional navigation
			if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) {
				bool handled = false;
				
				// When Alt is pressed, arrow keys come through as e.SystemKey, not e.Key
				var key = e.Key == Key.System ? e.SystemKey : e.Key;
				
				switch (key) {
					case Key.Up:
						handled = MoveInDirection(NavDirection.Up);
						Debug.WriteLine($"[NavMapper] Alt+Up pressed, result: {handled}");
						break;
					case Key.Down:
						handled = MoveInDirection(NavDirection.Down);
						Debug.WriteLine($"[NavMapper] Alt+Down pressed, result: {handled}");
						break;
					case Key.Left:
						handled = MoveInDirection(NavDirection.Left);
						Debug.WriteLine($"[NavMapper] Alt+Left pressed, result: {handled}");
						break;
					case Key.Right:
						handled = MoveInDirection(NavDirection.Right);
						Debug.WriteLine($"[NavMapper] Alt+Right pressed, result: {handled}");
						break;
					case Key.Return: // Alt+Enter: Activate focused node
						handled = ActivateFocusedNode();
						Debug.WriteLine($"[NavMapper] Alt+Enter pressed, result: {handled}");
						break;
					case Key.Escape: // Alt+Escape: Exit current group
						handled = ExitGroup();
						Debug.WriteLine($"[NavMapper] Alt+Escape pressed, result: {handled}");
						break;
				}
				
				if (handled) {
					e.Handled = true;
				}
			}
		}

		private class DebugRectInfo
		{
			public Rect Rect { get; set; }
			public string DebugLine { get; set; }
			public bool IsGroup { get; set; }
		}

		private static void ToggleHighlighting()
		{
			if (_debugMode) {
				ClearHighlighting();
				_debugMode = false;
				return;
			}

			Debug.WriteLine("\n========== NavMapper: Highlight Rectangles ==========");

			var leafRects = new List<Rect>();
			var groupRects = new List<Rect>();
			var allDebugInfo = new List<DebugRectInfo>();
			int skippedCount = 0;
			
			foreach (var node in NavForest.GetAllNavNodes()) {
				// Use IsNavigableForSelection to determine if this node is actually a navigation target
				if (!IsNavigableForSelection(node)) {
					skippedCount++;
					continue;
				}
				
				var center = node.GetCenterDip();
				if (!center.HasValue) {
					skippedCount++;
					continue;
				}
				
				if (!node.TryGetVisual(out var fe)) {
					skippedCount++;
					continue;
				}
				
				try {
					if (!fe.IsLoaded || !fe.IsVisible) {
						skippedCount++;
						continue;
					}
					
					// PointToScreen returns DEVICE PIXELS, need to transform to DIP
					Point tlDevice, brDevice;
					try {
						tlDevice = fe.PointToScreen(new Point(0, 0));
						brDevice = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
					} catch {
						skippedCount++;
						continue; // cannot compute screen points for this element now
					}

					// Transform from device pixels to DIP
					Point tlDip, brDip;
					var ps = PresentationSource.FromVisual(fe);
					if (ps?.CompositionTarget != null) {
						var transform = ps.CompositionTarget.TransformFromDevice;
						tlDip = transform.Transform(tlDevice);
						brDip = transform.Transform(brDevice);
					} else {
						// Fallback: use device pixels as-is (shouldn't happen for visible elements)
						tlDip = tlDevice;
						brDip = brDevice;
					}

					var x1 = Math.Min(tlDip.X, brDip.X);
					var y1 = Math.Min(tlDip.Y, brDip.Y);
					var x2 = Math.Max(tlDip.X, brDip.X);
					var y2 = Math.Max(tlDip.Y, brDip.Y);
					var rectDip = new Rect(new Point(x1, y1), new Point(x2, y2));

					if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height)) {
						skippedCount++;
						continue;
					}
					if (rectDip.Width < 1.0 || rectDip.Height < 1.0) {
						skippedCount++;
						continue;
					}
					
					// Get hierarchical path for this element
					string hierarchicalPath = NavNode.GetHierarchicalPath(fe);
					
					// Determine role and color
					bool shouldBeGray = false;
					string roleDescription = "";
					
					if (node.IsGroup) {
						if (node.IsDualRoleGroup) {
							var isOpen = (node as INavGroup)?.IsOpen == true;
							shouldBeGray = isOpen;
							roleDescription = isOpen ? "DualGroup(OPEN)" : "DualGroup(CLOSED)";
						} else {
							shouldBeGray = true;
							roleDescription = "PureGroup";
						}
					} else {
						roleDescription = "Leaf";
					}
					
					var typeName = fe.GetType().Name;
					var elementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
					var navId = node.Id;
					
					// Detect modal window for this node
					var modalTag = node.IsModal ? " [MODAL]" : "";
					
					// Build debug line (will be numbered after sorting)
					var colorTag = shouldBeGray ? "GRAY" : "LEAF";
					var debugLine = $"{typeName,-20} | {elementName,-20} | {roleDescription,-18} | {navId,-30} | ({rectDip.Left,7:F1}, {rectDip.Top,7:F1}) {rectDip.Width,6:F1}x{rectDip.Height,6:F1}{modalTag} | {hierarchicalPath}";

					allDebugInfo.Add(new DebugRectInfo { 
						Rect = rectDip, 
						DebugLine = debugLine,
						IsGroup = shouldBeGray
					});
					
					if (shouldBeGray) {
						groupRects.Add(rectDip);
					} else {
						leafRects.Add(rectDip);
					}
				} catch (Exception ex) { 
					Debug.WriteLine($"[ERROR] Processing node {node.Id}: {ex.Message}");
					skippedCount++;
				}
			}

			// Sort all rectangles by hierarchical path (last column)
			allDebugInfo.Sort((a, b) => {
				var pathA = a.DebugLine.Substring(a.DebugLine.LastIndexOf('|') + 1).Trim();
				var pathB = b.DebugLine.Substring(b.DebugLine.LastIndexOf('|') + 1).Trim();
				return string.Compare(pathA, pathB, StringComparison.Ordinal);
			});

			// Output sorted rectangles (mixed leaves and groups)
			Debug.WriteLine("");
			int leafCount = 0;
			int groupCount = 0;
			
			for (int i = 0; i < allDebugInfo.Count; i++) {
				var info = allDebugInfo[i];
				if (info.IsGroup) {
					groupCount++;
					Debug.WriteLine($"[GRAY] #{i + 1,-3} | {info.DebugLine}");
				} else {
					leafCount++;
					Debug.WriteLine($"[LEAF] #{i + 1,-3} | {info.DebugLine}");
				}
			}

			Debug.WriteLine($"\n========== Summary: {leafCount} leaves, {groupCount} groups, {skippedCount} skipped ==========\n");

			try {
				EnsureOverlay();
				if (_overlay != null) {
					_overlay.ShowDebugRects(leafRects, groupRects);
					_debugMode = true;
				}
			} catch (Exception ex) {
				Debug.WriteLine($"[ERROR] Showing overlay: {ex.Message}");
			}
		}

		private static void ClearHighlighting()
		{
			try {
				_overlay?.ClearDebugRects();
			} catch { }
		}

		// Remaining code unchanged (OnRootChanged, helpers, navigation)
		private static void OnRootLayoutChanged(object sender, EventArgs e)
		{
			var w = sender as Window;
			if (w == null) return;
			var content = w.Content as FrameworkElement;
			if (content != null) NavForest.RegisterRoot(content);
			
			// Update focus rectangle position if focus is set
			if (!string.IsNullOrEmpty(_focusedNodeId) && _navById.TryGetValue(_focusedNodeId, out var focusedNode)) {
				UpdateFocusRect(focusedNode);
			}
		}

		private static void OnRootChanged(FrameworkElement root)
		{
			if (root == null) return;
			try {
				var nodes = NavForest.GetNavNodesForRoot(root);

				// Update our local cache
				foreach (var nav in nodes) {
					_navById.AddOrUpdate(nav.Id, nav, (k, old) => nav);
					
					// Setup monitoring for new dual-role groups
					if (nav.IsDualRoleGroup && nav.IsGroup && nav.TryGetVisual(out var fe)) {
						SetupGroupMonitoring(nav, fe);
					}
				}

				try { NavMapUpdated?.Invoke(); } catch { }
			} catch { }
		}

		private static bool IsTrulyVisible(FrameworkElement fe)
		{
			if (fe == null) return false;
			DependencyObject cur = fe;
			while (cur != null) {
				if (cur is UIElement ui) {
					if (ui.Visibility != Visibility.Visible) return false;
				}
				try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
			}
			return fe.IsVisible && fe.IsLoaded;
		}

		// Public API
		public static IEnumerable<NavNode> GetAllNavNodes() => _navById.Values.ToArray();

		public static bool TryGetById(string id, out NavNode nav) => _navById.TryGetValue(id, out nav);

		// DEPRECATED: Use MoveInDirection instead
		[Obsolete("Use MoveInDirection instead")]
		public static string Navigate(string currentId, NavDirection dir)
		{
			if (string.IsNullOrEmpty(currentId)) return null;
			if (!_navById.TryGetValue(currentId, out var current)) return null;
			var curCenter = current.GetCenterDip();
			if (curCenter == null) return null;

			var candidates = _navById.Values.Where(n => n.Id != currentId && n.GetCenterDip() != null)
					.Select(n => new { Nav = n, C = n.GetCenterDip().Value }).ToArray();

			Point dirVec;
			switch (dir) {
				case NavDirection.Up: dirVec = new Point(0, -1); break;
				case NavDirection.Down: dirVec = new Point(0, 1); break;
				case NavDirection.Left: dirVec = new Point(-1, 0); break;
				case NavDirection.Right: dirVec = new Point(1, 0); break;
				default: dirVec = new Point(0, 0); break;
			}

			double bestCost = double.MaxValue; NavNode best = null;
			var cur = curCenter.Value;
			foreach (var c in candidates) {
				var v = new Point(c.C.X - cur.X, c.C.Y - cur.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < Double.Epsilon) continue;
				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVec.X + vNorm.Y * dirVec.Y;
				if (dot <= 0) continue;
				var cost = len / Math.Max(1e-7, dot);
				if (cost < bestCost) { bestCost = cost; best = c.Nav; }
			}

			return best?.Id;
		}
	}
}