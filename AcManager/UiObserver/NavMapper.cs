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
		private static readonly ConcurrentDictionary<object, NavElem> _navByLogical = new ConcurrentDictionary<object, NavElem>();
		private static readonly ConcurrentDictionary<string, NavElem> _navById = new ConcurrentDictionary<string, NavElem>();

		public static event Action NavMapUpdated;

		// Highlighting overlay
		private static HighlightOverlay _overlay;
		private static bool _highlightingShown;

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			// configure NavForest with helpers
			NavForest.Configure(GetLogicalKeyForElement, IsTrulyVisible, ComputeStableId);
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

		public static void DebugDumpNavElems()
		{
			return;
			System.Diagnostics.Debug.WriteLine($"NavMapper.DebugDumpNavElems: _navByLogical.Count={_navByLogical.Count}");
			foreach (var kv in _navByLogical.ToArray()) {
				var logicalKey = kv.Key;
				var nav = kv.Value;
				try {
					if (!nav.TryGetVisual(out var fe)) {
						System.Diagnostics.Debug.WriteLine($"NavElem Id={nav.Id} LogicalKey={logicalKey ?? "<null>"} Visual=GONE");
						continue;
					}

					var ps = nav.PresentationSource;
					var psId = ps == null ? "<null>" : ps.GetHashCode().ToString();
					var rectDip = nav.BoundsDip;

					System.Diagnostics.Debug.WriteLine(
						$"NavElem Id={nav.Id} Type={fe.GetType().FullName} Name={fe.Name ?? "<noname>"} " +
						$"Loaded={fe.IsLoaded} Visible={fe.IsVisible} PresentationSource={psId} BoundsDIP={rectDip}");
					try {
						var tl = fe.PointToScreen(new Point(0, 0));
						var br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
						System.Diagnostics.Debug.WriteLine($"  PointToScreen TL={tl} BR={br} Actual={fe.ActualWidth}x{fe.ActualHeight}");
					} catch (Exception ex) {
						System.Diagnostics.Debug.WriteLine($"  PointToScreen failed: {ex.Message}");
					}
				} catch (Exception ex) {
					System.Diagnostics.Debug.WriteLine($"DebugDumpNavElems: exception for key={logicalKey}: {ex.Message}");
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

				// Dump first 10 NavElems
				int i = 0;
				foreach (var kv in _navByLogical.ToArray()) {
					if (i++ > 10) break;
					var nav = kv.Value;
					if (!nav.TryGetVisual(out var fe)) { Debug.WriteLine($"Nav {nav.Id}: Visual=GONE"); continue; }
					Debug.WriteLine($"Nav {nav.Id}: BoundsDIP={nav.BoundsDip} Fe.PointToScreen(0,0)={(TryPointToScreen(fe))} Fe.Actual={fe.ActualWidth}x{fe.ActualHeight}");
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

		private static void ToggleHighlighting()
		{
			if (_highlightingShown) {
				ClearHighlighting();
				_highlightingShown = false;
				return;
			}

			DebugDumpNavElems();

			var rectsInDip = new List<Rect>();
			foreach (var kv in _navByLogical.ToArray()) {
				var nav = kv.Value;
				if (!nav.TryGetVisual(out var fe)) continue;
				try {
					if (!fe.IsLoaded || !fe.IsVisible) continue;
					// Prefer computing rectangle in DIP using PointToScreen (returns DIP)
					Point tl, br;
					try {
						tl = fe.PointToScreen(new Point(0, 0));
						br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
					} catch {
						continue; // cannot compute screen points for this element now
					}

					// PointToScreen may return coordinates in device pixels depending on DPI-awareness context.
					// Normalize to DIP explicitly using the PresentationSource transform if available.
					try {
						var ps = PresentationSource.FromVisual(fe) ?? nav.PresentationSource;
						if (ps?.CompositionTarget != null) {
							var fromDevice = ps.CompositionTarget.TransformFromDevice;
							tl = fromDevice.Transform(tl);
							br = fromDevice.Transform(br);
						}
					} catch { }

					var x1 = Math.Min(tl.X, br.X);
					var y1 = Math.Min(tl.Y, br.Y);
					var x2 = Math.Max(tl.X, br.X);
					var y2 = Math.Max(tl.Y, br.Y);
					var rectDip = new Rect(new Point(x1, y1), new Point(x2, y2));

					if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height)) continue;
					if (rectDip.Width < 1.0 || rectDip.Height < 1.0) continue;
					rectsInDip.Add(rectDip);
				} catch { /* ignore per-element errors */ }
			}

			try {
				if (_overlay == null) _overlay = new HighlightOverlay();

				try { if (!_overlay.IsVisible) _overlay.Show(); } catch { }
				_overlay.ShowRects(rectsInDip);
				_highlightingShown = true;
			} catch { }
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
				var elems = NavForest.GetNavElemsForRoot(root);

				// remove previous entries belonging to this root
				var toRemove = new List<object>();
				foreach (var kv in _navByLogical.ToArray()) {
					if (kv.Value.TryGetVisual(out var fe)) {
						if (IsDescendantOf(fe, root)) toRemove.Add(kv.Key);
					}
				}
				foreach (var key in toRemove) _navByLogical.TryRemove(key, out var _);

				// add new ones
				foreach (var nav in elems) {
					_navByLogical.AddOrUpdate(nav.LogicalKey, nav, (k, old) => nav);
					_navById.AddOrUpdate(nav.Id, nav, (k, old) => nav);
				}

				try { NavMapUpdated?.Invoke(); } catch { }
			} catch { }
		}

		private static bool IsDescendantOf(FrameworkElement candidate, FrameworkElement root)
		{
			if (candidate == null || root == null) return false;
			DependencyObject cur = candidate;
			while (cur != null) {
				if (object.ReferenceEquals(cur, root)) return true;
				try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
			}
			return false;
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

		private static object GetLogicalKeyForElement(FrameworkElement fe)
		{
			if (fe == null) return null;
			if (fe.TemplatedParent != null) return fe.TemplatedParent;
			var dc = fe.DataContext;
			if (dc != null && !(dc is string) && !(dc.GetType().IsPrimitive)) return Tuple.Create((object)"DC", dc);
			try {
				DependencyObject current = fe;
				while (current != null) {
					if (current is FrameworkElement f && f.Parent != null) return f;
					current = LogicalTreeHelper.GetParent(current);
				}
			} catch { }
			return fe;
		}

		private static string ComputeStableId(FrameworkElement fe)
		{
			if (fe == null) return null;
			var auto = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
			if (!string.IsNullOrEmpty(auto)) return "A:" + auto;
			if (!string.IsNullOrEmpty(fe.Name)) return "N:" + fe.Name;
			return string.Format("G:{0}:{1:X8}", fe.GetType().Name, fe.GetHashCode());
		}

		// Public API
		public static IEnumerable<NavElem> GetAllNavElems() => _navByLogical.Values.ToArray();

		public static bool TryGetById(string id, out NavElem nav) => _navById.TryGetValue(id, out nav);

		public static string Navigate(string currentId, NavDirection dir)
		{
			if (string.IsNullOrEmpty(currentId)) return null;
			if (!_navById.TryGetValue(currentId, out var current)) return null;
			var curCenter = current.CenterDip;
			if (curCenter == null) return null;

			var candidates = _navById.Values.Where(n => n.Id != currentId && n.CenterDip != null)
					.Select(n => new { Nav = n, C = n.CenterDip.Value }).ToArray();

			Point dirVec;
			switch (dir) {
				case NavDirection.Up: dirVec = new Point(0, -1); break;
				case NavDirection.Down: dirVec = new Point(0, 1); break;
				case NavDirection.Left: dirVec = new Point(-1, 0); break;
				case NavDirection.Right: dirVec = new Point(1, 0); break;
				default: dirVec = new Point(0, 0); break;
			}

			double bestCost = double.MaxValue; NavElem best = null;
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