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
		static void OnDebugHotkey(KeyEventArgs e)
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
		/// Also toggles Observer's verbose discovery logging and NavNode's creation logging.
		/// </summary>
		private static void ToggleVerboseNavigationDebug()
		{
			_verboseNavigationDebug = !_verboseNavigationDebug;
			
			// Toggle Observer's verbose debug (which cascades to NavNode)
			Observer.VerboseDebug = _verboseNavigationDebug;
			
			DebugLog.WriteLine($"\n========== Verbose Debug Mode: {(_verboseNavigationDebug ? "ENABLED" : "DISABLED")} ==========");
			DebugLog.WriteLine("Enabled for: Navigator (algorithm), Observer (discovery), NavNode (creation)");
			DebugLog.WriteLine("Press Ctrl+Shift+F9 to toggle");
			DebugLog.WriteLine("=============================================================\n");
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
			DebugLog.WriteLine("\n========== NavNode Consistency Check ==========");

			var allNodes = Observer.GetAllNavNodes().ToList();
			DebugLog.WriteLine($"Total nodes to validate: {allNodes.Count}");
			
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
					DebugLog.WriteLine($"  ⚠ DEAD VISUAL: {node.SimpleName}");
					DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
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
						DebugLog.WriteLine($"  ⚠⚠  ERROR walking visual tree for {node.SimpleName}: {ex.Message}");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
					}
					
					if (actualParent == null)
					{
						parentMismatches++;
						DebugLog.WriteLine($"  ⚠ PARENT MISMATCH: {node.SimpleName}");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     Recorded Parent: {parentNode.SimpleName}");
						DebugLog.WriteLine($"     Recorded Parent Path: {parentNode.HierarchicalPath}");
						DebugLog.WriteLine($"     Actual Parent: NONE (visual tree has no NavNode parent)");

						// ✓ NEW: Parallel walk of captured path and current visual tree
						if (node.VisualTreePath != null && node.VisualTreePath.Count > 0)
						{
							DebugLog.WriteLine($"     Intermediate Elements ({node.VisualTreePath.Count} captured during discovery):");
							
							DependencyObject visualDO = fe;
							int interIndex = 0;
							bool reachedRecordedParent = false;
							
							while (interIndex < node.VisualTreePath.Count)
							{
								try {
									// Get the captured parent at this index
									FrameworkElement capturedParentFE = null;
									if (!node.VisualTreePath[interIndex].TryGetTarget(out capturedParentFE)) {
										DebugLog.WriteLine($"       [{interIndex}] Captured element is DEAD (garbage collected)");
										break;
									}
									
									// Get the current visual parent
									var visualParentDO = VisualTreeHelper.GetParent(visualDO);
									
									if (visualParentDO == null) {
										// Visual tree breaks here!
										DebugLog.WriteLine($"       [{interIndex}] BREAK: GetParent() returned NULL");
										DebugLog.WriteLine($"       Disconnected child: {visualDO.GetType().Name}");
										DebugLog.WriteLine($"       Expected parent: {capturedParentFE.GetType().Name}");
										DebugLog.WriteLine($"       Checking if parent still has child in VisualChildren...");
										
										// Check if capturedParentFE still has visualDO as a visual child
										int childCount = VisualTreeHelper.GetChildrenCount(capturedParentFE);
										bool foundAsChild = false;
										
										for (int childNo = 0; childNo < childCount; childNo++) {
											var visualChild = VisualTreeHelper.GetChild(capturedParentFE, childNo);
											if (ReferenceEquals(visualChild, visualDO)) {
												foundAsChild = true;
												break;
											}
											// Also check descendants
											if (visualChild != null && IsVisualDescendant(visualDO, visualChild)) {
												foundAsChild = true;
												break;
											}
										}
										
										if (foundAsChild) {
											DebugLog.WriteLine($"       ⚠⚠  INCONSISTENCY DETECTED!");
											DebugLog.WriteLine($"       Parent ({capturedParentFE.GetType().Name}) STILL has child in VisualChildren");
											DebugLog.WriteLine($"       But GetParent(child) returns NULL!");
											DebugLog.WriteLine($"       This is a WPF visual tree corruption!");
										} else {
											DebugLog.WriteLine($"       Parent ({capturedParentFE.GetType().Name}) does NOT have child in VisualChildren");
											DebugLog.WriteLine($"       Visual tree disconnect is consistent on both sides.");
										}
										break;
									}
									
									// Check if we reached the recorded parent NavNode
									if (visualParentDO is FrameworkElement visualParentFE && 
										Observer.TryGetNavNode(visualParentFE, out var visualParentNode) &&
										ReferenceEquals(visualParentNode, parentNode)) {
										DebugLog.WriteLine($"       [{interIndex}] ✓ Reached recorded parent NavNode: {parentNode.SimpleName}");
										reachedRecordedParent = true;
										break;
									}
									
									// Compare current visual parent with captured parent
									if (!ReferenceEquals(visualParentDO, capturedParentFE)) {
										DebugLog.WriteLine($"       [{interIndex}] MISMATCH:");
										DebugLog.WriteLine($"         Captured: {capturedParentFE.GetType().Name}");
										DebugLog.WriteLine($"         Current:  {visualParentDO.GetType().Name}");
										DebugLog.WriteLine($"         Visual tree structure has changed!");
										break;
									}
						
									// Match! Continue to next level
									DebugLog.WriteLine($"       [{interIndex}] ✓ Match: {capturedParentFE.GetType().Name}");
									visualDO = visualParentDO;
									interIndex++;
	
								} catch (Exception ex) {
									DebugLog.WriteLine($"       Exception at index {interIndex}: {ex.Message}");
									break;
								}
							}
							
							if (reachedRecordedParent) {
								DebugLog.WriteLine($"     ✓ Visual tree path is consistent - reached recorded parent successfully");
							}
						}
						else
						{
							DebugLog.WriteLine($"     (No intermediate elements were captured - direct parent or root node)");
						}
					}
					else if (!ReferenceEquals(actualParent, parentNode))
					{
						parentMismatches++;
						DebugLog.WriteLine($"  ⚠ PARENT MISMATCH: {node.SimpleName}");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     Recorded Parent: {parentNode.SimpleName}");
						DebugLog.WriteLine($"     Recorded Parent Path: {parentNode.HierarchicalPath}");
						DebugLog.WriteLine($"     Actual Parent: {actualParent.SimpleName}");
						DebugLog.WriteLine($"     Actual Parent Path: {actualParent.HierarchicalPath}");
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
						DebugLog.WriteLine($"  ⚠ DUPLICATE CHILD: {node.SimpleName}");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     Duplicate child: {child.SimpleName}");
						DebugLog.WriteLine($"     Duplicate child path: {child.HierarchicalPath}");
						continue;
					}
					
					seenChildren.Add(child);
					recordedChildren.Add(child);
					
					// Check if child's Parent points back to this node
					if (child.Parent == null || !child.Parent.TryGetTarget(out var childParent) || !ReferenceEquals(childParent, node))
					{
						childConsistencyErrors++;
						DebugLog.WriteLine($"  ⚠ CHILD PARENT MISMATCH: {node.SimpleName}");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     Child: {child.SimpleName}");
						DebugLog.WriteLine($"     Child path: {child.HierarchicalPath}");
						if (child.Parent == null)
						{
							DebugLog.WriteLine($"     Child's Parent: NULL");
						}
						else if (!child.Parent.TryGetTarget(out var cp))
						{
							DebugLog.WriteLine($"     Child's Parent: DEAD REFERENCE");
						}
						else
						{
							DebugLog.WriteLine($"     Child's Parent: {cp.SimpleName}");
							DebugLog.WriteLine($"     Child's Parent path: {cp.HierarchicalPath}");
						}
					}
				}
				
				if (deadChildRefs > 0)
				{
					childConsistencyErrors++;
					DebugLog.WriteLine($"  ⚠ DEAD CHILD REFERENCES: {node.SimpleName}");
					DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
					if (bounds.HasValue)
					{
						DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
					}
					DebugLog.WriteLine($"     Dead references: {deadChildRefs}");
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
					DebugLog.WriteLine($"  ⚠⚠  ERROR walking visual tree descendants for {node.SimpleName}: {ex.Message}");
					DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
					if (bounds.HasValue)
					{
						DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
					}
				}
				
				// Now check if all visual tree NavNodes are reachable through tree structure
				foreach (var visualNode in visualTreeNavNodes)
				{
					// Walk up Parent chain from visualParentNode - should eventually reach this node
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
						DebugLog.WriteLine($"  ⚠ ORPHANED NODE IN VISUAL TREE: {visualNode.SimpleName}");
						DebugLog.WriteLine($"     Path: {visualNode.HierarchicalPath}");
						DebugLog.WriteLine($"     Found in visual subtree of: {node.SimpleName}");
						DebugLog.WriteLine($"     Parent node path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Parent node bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     But NOT reachable through Parent chain from this node!");
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
						DebugLog.WriteLine($"  ⚠ CIRCULAR REFERENCE: {node.SimpleName} has circular parent chain!");
						DebugLog.WriteLine($"     Path: {node.HierarchicalPath}");
						if (bounds.HasValue)
						{
							DebugLog.WriteLine($"     Bounds: ({bounds.Value.Left:F1}, {bounds.Value.Top:F1}) {bounds.Value.Width:F1}x{bounds.Value.Height:F1}");
						}
						DebugLog.WriteLine($"     Circular ancestor: {ancestor.SimpleName}");
						DebugLog.WriteLine($"     Circular ancestor path: {ancestor.HierarchicalPath}");
						break;
					}
					visited.Add(ancestor);
					current2 = ancestor.Parent;
				}
			}
			
			DebugLog.WriteLine($"\n========== Consistency Check Results ==========");
			DebugLog.WriteLine($"Total nodes: {allNodes.Count}");
			DebugLog.WriteLine($"Dead visuals: {deadVisuals}");
			DebugLog.WriteLine($"Parent mismatches: {parentMismatches}");
			DebugLog.WriteLine($"Child consistency errors: {childConsistencyErrors} (duplicates, dead refs, back-link errors)");
			DebugLog.WriteLine($"Orphaned nodes: {orphanedNodes} (in visual tree but not reachable through Parent chain)");
			DebugLog.WriteLine($"Circular references: {circularRefs}");
			
			if (deadVisuals == 0 && parentMismatches == 0 && childConsistencyErrors == 0 && orphanedNodes == 0 && circularRefs == 0)
			{
				DebugLog.WriteLine("✓ All nodes are CONSISTENT with visual tree!");
			}
			else
			{
				DebugLog.WriteLine("⚠⚠  INCONSISTENCIES DETECTED - see details above");
			}
			
			DebugLog.WriteLine("================================================\n");
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
		/// ✓ FIX: Never hides the overlay window, only clears/shows debug rectangles.
		/// </summary>
		/// <param name="filterByModalScope">If true, only show nodes in active modal scope. If false, show all discovered nodes.</param>
		private static void ToggleHighlighting(bool filterByModalScope)
		{
			if (_debugMode) {
				ClearHighlighting();
				_debugMode = false;
				
				long memBefore = GC.GetTotalMemory(forceFullCollection: false);
				
				// ✓ FIX: DO NOT hide the overlay window!
				// Hiding triggers Unloaded events that Observer's global handlers catch,
				// causing circular reference (overlay observes UI, Observer tracks overlay).
				// Just clear the rectangles - empty transparent window is invisible anyway.
				
				// REMOVED:
				// try {
				//     if (_overlay != null && _overlay.IsVisible) {
				//         _overlay.Hide();
				//     }
				// } catch (Exception ex) {
				//     DebugLog.WriteLine($"[Navigator] Hide error: {ex.Message}");
				// }

				long memAfter = GC.GetTotalMemory(forceFullCollection: false);
				DebugLog.WriteLine($"[Navigator] Memory: Before={memBefore:N0}, After={memAfter:N0}, Delta={memAfter - memBefore:N0} bytes");

				return;
			}

#if DEBUG
			ValidateNodeConsistency();
#endif

			if (filterByModalScope) {
				DebugLog.WriteLine("\n========== NavMapper: Highlight Rectangles (Active Modal Scope ONLY) ==========");
			} else {
				DebugLog.WriteLine("\n========== NavMapper: Highlight Rectangles (ALL NODES - Unfiltered) ==========");
			}
			
			// Show context stack
			if (_contextStack.Count > 0) {
				DebugLog.WriteLine($"Context Stack ({_contextStack.Count} contexts):");
				for (int i = 0; i < _contextStack.Count; i++) {
					var ctx = _contextStack[i];
					var focusInfo = ctx.FocusedNode != null 
						? $" [focused: {ctx.FocusedNode.SimpleName}]" 
						: " [no focus]";
					DebugLog.WriteLine($"  [{i}] {ctx.ScopeNode.HierarchicalPath}{focusInfo}");
				}
			} else {
				DebugLog.WriteLine("Context Stack: (empty - waiting for root context)");
			}
			DebugLog.WriteLine("");

			// ✓ Pre-allocate with capacity hints
			var debugRects = new List<IDebugRect>(256);

			// Get nodes from Observer (authoritative source)
			List<NavNode> nodesToShow;
			if (filterByModalScope) {
				// Filtered: Only show nodes in active modal scope
				nodesToShow = Observer.GetAllNavNodes()
					.Where(n => IsInActiveModalScope(n))
					.ToList();
				DebugLog.WriteLine($"Nodes in current modal scope: {nodesToShow.Count}");
			} else {
				// Unfiltered: Show ALL discovered nodes (including groups!)
				nodesToShow = Observer.GetAllNavNodes().ToList();
			 DebugLog.WriteLine($"All discovered nodes: {nodesToShow.Count}");
			}
			
			// ✓ Limit processing to prevent OOM
			const int MAX_NODES_TO_PROCESS = 1000;
			if (nodesToShow.Count > MAX_NODES_TO_PROCESS) {
				DebugLog.WriteLine($"[NavMapper] WARNING: {nodesToShow.Count} nodes exceeds limit {MAX_NODES_TO_PROCESS}. Truncating.");
				nodesToShow = nodesToShow.Take(MAX_NODES_TO_PROCESS).ToList();
			}
			
			// ✓ Use initial capacity based on actual count
			var allDebugInfo = new List<DebugRectInfo>(nodesToShow.Count);
			
			foreach (var node in nodesToShow) {
				var center = node.GetCenterDip();
				if (!center.HasValue) continue;
				
				if (!node.TryGetVisual(out var fe)) continue;
				// ✓ CHANGED: Removed IsVisible check - allow elements in non-active tabs
				// GetCenterDip() already validates element is usable
				if (!fe.IsLoaded) continue;
				
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
						if (!filterByModalScope && _contextStack.Count > 0) {
							var inScope = IsInActiveModalScope(node);
							scopeInfo = inScope ? " {IN SCOPE}" : " {OUT OF SCOPE}";
						}
						
						var typeName = fe.GetType().Name;
						var elementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
						var modalTag = node.IsModal ? "MODAL" : "";
						var hierarchicalPath = node.HierarchicalPath;  // Use stored path for both display and sorting
						
						// Format bounds as: (X, Y) WxH
						var boundsStr = $"({rect.Left,7:F1}, {rect.Top,7:F1}) {rect.Width,6:F1}x{rect.Height,6:F1}";

						// Build formatted debug line (hierarchicalPath is last column)
						var debugLine = $"{typeName,-20} | {elementName,-20} | {nodeType,-18} | {modalTag,-6} | {navigable,-35}{scopeInfo,-15} | {boundsStr,-30} | {hierarchicalPath}";

						allDebugInfo.Add(new DebugRectInfo { 
							DebugLine = debugLine,
							IsGroup = node.IsGroup,
							HierarchicalPath = hierarchicalPath
						});
					}

				} catch (Exception ex) {
					DebugLog.WriteLine($"  ERROR processing {node.HierarchicalPath}: {ex.Message}");
				}
			}

			// Sort by hierarchical path for easy reading
			allDebugInfo.Sort((a, b) => string.Compare(a.HierarchicalPath, b.HierarchicalPath, StringComparison.Ordinal));

			// Output sorted rectangles
			DebugLog.WriteLine("");
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
				DebugLog.WriteLine($"{prefix} #{i + 1,-3} | {info.DebugLine}");
			}

			DebugLog.WriteLine($"\n========== Summary: {leafCount} leaves, {groupCount} groups ==========");
			if (filterByModalScope) {
				DebugLog.WriteLine("Mode: FILTERED (Ctrl+Shift+F12) - showing only nodes in active modal scope");
			} else {
				DebugLog.WriteLine("Mode: UNFILTERED (Ctrl+Shift+F11) - showing ALL discovered nodes");
			}
			DebugLog.WriteLine("Color Legend: Orange = Navigable leaves | Dark Red = Non-navigable leaves | Gray = Groups");
			DebugLog.WriteLine("=============================================================\n");

			try {
				EnsureOverlay();  // ✓ Reuses existing overlay if it exists
				_overlay?.ShowDebugRects(debugRects);
				_debugMode = true;
			} catch (Exception ex) {
				DebugLog.WriteLine($"[Navigator] Overlay error: {ex.Message}");
			}
			
			// ✓ Clear local collections to hint GC
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

		/// <summary>
		/// Checks if a given DependencyObject is a visual descendant of a parent.
		/// </summary>
		private static bool IsVisualDescendant(DependencyObject descendant, DependencyObject parent)
		{
			if (descendant == null || parent == null) return false;
			
			try
			{
				var stack = new Stack<DependencyObject>();
				stack.Push(parent);
				
				while (stack.Count > 0)
				{
					var current = stack.Pop();
					
					if (ReferenceEquals(current, descendant))
					{
						return true;
					}
					
					int childCount = VisualTreeHelper.GetChildrenCount(current);
					for (int i = 0; i < childCount; i++)
					{
						var child = VisualTreeHelper.GetChild(current, i);
						if (child != null)
						{
							stack.Push(child);
						}
					}
				}
			}
			catch { }
			
			return false;
		}

		#endregion
	}
}
