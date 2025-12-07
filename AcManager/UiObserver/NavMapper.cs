using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

		public static event Action NavMapUpdated;

		// Highlighting overlay
		private static HighlightOverlay _overlay;
		private static bool _highlightingShown;

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			NavNode.PathFilter.AddExcludeRule("Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu");

			// configure NavForest with helpers
			NavForest.Configure(IsTrulyVisible);
			NavForest.RootChanged += OnRootChanged;
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
				}), DispatcherPriority.ApplicationIdle);
			}
		}

		public static void DebugDumpNavNodes()
		{
			return;
			System.Diagnostics.Debug.WriteLine($"NavMapper.DebugDumpNavNodes: count={_navById.Count}");
			foreach (var kv in _navById.ToArray()) {
				var nav = kv.Value;
				try {
					if (!nav.TryGetVisual(out var fe)) {
						System.Diagnostics.Debug.WriteLine($"NavNode Id={nav.Id} Visual=GONE");
						continue;
					}

					var type = nav.IsGroup ? "Group" : "Leaf";
					var navigable = nav.IsNavigable ? "Navigable" : "NotNavigable";
					System.Diagnostics.Debug.WriteLine($"NavNode Id={nav.Id} Type={type} {navigable} Element={fe.GetType().Name}");
				} catch (Exception ex) {
					System.Diagnostics.Debug.WriteLine($"DebugDumpNavNodes: exception for id={kv.Key}: {ex.Message}");
				}
			}
		}

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
			if (e.Key == Key.F12 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)) {
				e.Handled = true;
				ToggleHighlighting();
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
			if (_highlightingShown) {
				ClearHighlighting();
				_highlightingShown = false;
				return;
			}

			DebugDumpNavNodes();

			Debug.WriteLine("\n========== NavMapper: Highlight Rectangles ==========");

			var leafRects = new List<Rect>();
			var groupRects = new List<Rect>();
			var allDebugInfo = new List<DebugRectInfo>();
			int skippedCount = 0;
			
			foreach (var node in NavForest.GetAllNavNodes()) {
				if (!node.IsNavigable) {
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
					
					// Build debug line (will be numbered after sorting)
					var colorTag = shouldBeGray ? "GRAY" : "LEAF";
					var debugLine = $"{typeName,-20} | {elementName,-20} | {roleDescription,-18} | {navId,-30} | ({rectDip.Left,7:F1}, {rectDip.Top,7:F1}) {rectDip.Width,6:F1}x{rectDip.Height,6:F1} | {hierarchicalPath}";
					
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
				if (_overlay == null) _overlay = new HighlightOverlay();

				try { if (!_overlay.IsVisible) _overlay.Show(); } catch { }
				
				// Show both leaf and group rects with different colors
				_overlay.ShowRects(leafRects, groupRects);
				_highlightingShown = true;
			} catch (Exception ex) {
				Debug.WriteLine($"[ERROR] Showing overlay: {ex.Message}");
			}
		}

		private static void ClearHighlighting()
		{
			try {
				_overlay?.HideOverlay();
			} catch { }
		}

		// Remaining code unchanged (OnRootChanged, helpers, navigation)
		private static void OnRootLayoutChanged(object sender, EventArgs e)
		{
			var w = sender as Window;
			if (w == null) return;
			var content = w.Content as FrameworkElement;
			if (content != null) NavForest.RegisterRoot(content);
		}

		private static void OnRootChanged(FrameworkElement root)
		{
			if (root == null) return;
			try {
				var nodes = NavForest.GetNavNodesForRoot(root);

				// Update our local cache
				foreach (var nav in nodes) {
					_navById.AddOrUpdate(nav.Id, nav, (k, old) => nav);
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