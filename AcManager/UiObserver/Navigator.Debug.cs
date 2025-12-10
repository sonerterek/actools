using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator debug visualization - partial class containing diagnostic tools.
	/// 
	/// This file contains debug-only functionality for visualizing and validating
	/// the navigation system. All code here is separate from production navigation logic.
	/// </summary>
	internal static partial class Navigator
	{
		#region Debug Fields

		private static bool _debugMode;

		/// <summary>
		/// Controls verbose navigation algorithm debug output.
		/// When true, logs detailed distance calculations, scoring, and candidate evaluation.
		/// Toggle with Ctrl+Shift+F9.
		/// </summary>
		internal static bool _verboseNavigationDebug = false;

		#endregion

		#region Debug Keyboard Handlers

		/// <summary>
		/// Handles debug hotkeys (F9/F11/F12) for toggling visualization overlay and verbose output.
		/// Called from OnPreviewKeyDown in Navigator.cs.
		/// </summary>
		static partial void OnDebugHotkey(KeyEventArgs e)
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

			// Ctrl+Shift+F9: Toggle verbose navigation debug output
			if (e.Key == Key.F9 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleVerboseNavigationDebug();
				return;
			}
		}

		/// <summary>
		/// Toggles verbose navigation algorithm debug output.
		/// </summary>
		private static void ToggleVerboseNavigationDebug()
		{
			_verboseNavigationDebug = !_verboseNavigationDebug;
			Debug.WriteLine($"\n========== Verbose Navigation Debug: {(_verboseNavigationDebug ? "ENABLED" : "DISABLED")} ==========");
			Debug.WriteLine($"Press Ctrl+Shift+F9 to toggle");
			Debug.WriteLine("=============================================================\n");
		}

		#endregion

		#region Debug Visualization

#if DEBUG
		/// <summary>
		/// Validates consistency between NavNode hierarchy and visual tree.
		/// Checks for dead references, parent/child mismatches, circular references, etc.
		/// </summary>
		private static void ValidateNodeConsistency()
		{
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
#endif

		/// <summary>
		/// Debug rectangle information for visualization.
		/// Encapsulates styling logic based on NavNode properties.
		/// </summary>
		private class NavDebugRect : IDebugRect
		{
			private readonly Rect _bounds;
			private readonly NavNode _node;
			
			public NavDebugRect(Rect bounds, NavNode node)
			{
				_bounds = bounds;
				_node = node;
			}
			
			public Rect Bounds => _bounds;
			
			public Point? CenterPoint => _node.GetCenterDip();
			
			public Brush StrokeBrush
			{
				get
				{
					// Color coding based on node properties
					if (_node.IsGroup)
					{
						return Brushes.Gray;  // Groups = Gray
					}
					else if (!_node.IsNavigable)
					{
						return Brushes.DarkRed;  // Non-navigable leaves = Dark Red
					}
					else
					{
						return Brushes.Orange;  // Navigable leaves = Orange
					}
				}
			}
			
			public double StrokeThickness => 2.0;
			
			public Brush FillBrush => Brushes.Transparent;
			
			public double Inset => _node.IsGroup ? 0.0 : 2.0;  // Inset leaves to show inside groups
		}

		/// <summary>
		/// Toggles debug rectangle visualization overlay.
		/// Shows colored rectangles for all navigable elements (leaves = orange, groups = gray).
		/// </summary>
		/// <param name="filterByModalScope">If true, only show nodes in active modal scope. If false, show all discovered nodes.</param>
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

#if DEBUG
			ValidateNodeConsistency();
#endif

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
			var debugRects = new List<IDebugRect>(256);

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
						// Create debug rect with node reference for smart styling
						debugRects.Add(new NavDebugRect(rect, node));
						
						// Get node type description
						var nodeType = node.IsGroup ? "PureGroup" : "Leaf";
						var navigable = node.IsNavigable 
							? (node.IsGroup ? "[NOT navigable - pure group]" : "[NAVIGABLE]")
							: "[NON-NAVIGABLE]";
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
							DebugLine = debugLine,
							IsGroup = node.IsGroup,
							HierarchicalPath = hierarchicalPath
						});
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
			Debug.WriteLine("Color Legend: Orange = Navigable leaves | Dark Red = Non-navigable leaves | Gray = Groups");
			Debug.WriteLine("=============================================================\n");

			try {
				EnsureOverlay();  // ? Reuses existing overlay if it exists
				_overlay?.ShowDebugRects(debugRects);
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

		/// <summary>
		/// Helper class for sorting debug output by hierarchical path.
		/// </summary>
		private class DebugRectInfo
		{
			public string DebugLine { get; set; }
			public bool IsGroup { get; set; }
			public string HierarchicalPath { get; set; }
		}

		/// <summary>
		/// Clears all debug rectangle overlays (keeps focus rectangle visible).
		/// </summary>
		private static void ClearHighlighting()
		{
			try { _overlay?.ClearDebugRects(); } catch { }
		}

		#endregion
	}
}
