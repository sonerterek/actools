using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Helpers;
using System.IO.Pipes;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AcManager.Tools.Helpers
{
	public static class UiObserver
	{
		private static bool _initialized;
		private static string _pipeName;
		private static NamedPipeClientStream _pipeStream;
		private static StreamWriter _pipeWriter;
		private static readonly object PipeLock = new object();

		// Map: parent type -> control type -> count
		private static readonly object TypeMapLock = new object();
		private static readonly Dictionary<string, Dictionary<string, int>> ParentControlCountMap =
				new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

		// Dumps the in-memory parent->control-type map to CSV file. Safe to call any time.
		public static void DumpControlParentTypeMapCsv(string filename)
		{
			try {
				if (string.IsNullOrEmpty(filename)) return;
				lock (TypeMapLock) {
					using (var writer = new StreamWriter(filename, false, System.Text.Encoding.UTF8)) {
						writer.WriteLine("ParentType,ControlType,Count");
						foreach (var parentType in ParentControlCountMap.Keys.OrderBy(x => x)) {
							var controls = ParentControlCountMap[parentType];
							foreach (var control in controls.OrderBy(x => x.Key)) {
								// simple CSV escaping
								string esc(string s) => '"' + (s?.Replace("\"", "\"\"") ?? string.Empty) + '"';
								writer.WriteLine($"{esc(parentType)},{esc(control.Key)},{control.Value}");
							}
						}
					}
				}
			} catch (Exception e) {
				try { FirstFloor.ModernUI.Helpers.Logging.Warning($"UiObserver: Dump failed: {e.Message}"); } catch { }
			}
		}

		public static void Initialize(string pipeName)
		{
#if !DEBUG
            if (string.IsNullOrEmpty(pipeName)) return;
#endif
			if (_initialized) return;
			_initialized = true;
			_pipeName = pipeName;

			// Try to connect in background
			Task.Run(() => TryConnectPipe());

			// Window lifecycle: DpiAwareWindow.NewWindowCreated + class handler for Loaded
			DpiAwareWindow.NewWindowCreated += (s, e) => OnWindowCreated(s as Window);
			EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), true);

			// Intercept creation / destruction of controls (Loaded / Unloaded of FrameworkElement)
			EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnElementLoaded), true);
			EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnElementUnloaded), true);

			// Focus (disabled for now)
			// EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.GotFocusEvent, new RoutedEventHandler(OnGotFocus), true);
			// EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.LostFocusEvent, new RoutedEventHandler(OnLostFocus), true);

			// Common control interactions
			EventManager.RegisterClassHandler(typeof(Button), Button.ClickEvent, new RoutedEventHandler(OnButtonClick), true);
			EventManager.RegisterClassHandler(typeof(Selector), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelectionChanged), true);
			EventManager.RegisterClassHandler(typeof(RangeBase), RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(OnRangeValueChanged), true);
			EventManager.RegisterClassHandler(typeof(TextBox), TextBox.TextChangedEvent, new TextChangedEventHandler(OnTextChanged), true);

			// Optionally hook Activated/Deactivated/Closed on existing Window instances:
			if (Application.Current?.Dispatcher != null) {
				Application.Current.Dispatcher.BeginInvoke(new Action(() => {
					foreach (Window w in Application.Current.Windows) AttachWindowHandlers(w);
				}), DispatcherPriority.ApplicationIdle);
			}
		}

		private static void TryConnectPipe()
		{
			try {
				lock (PipeLock) {
					if (string.IsNullOrEmpty(_pipeName) || _pipeWriter != null) return;

					try {
						_pipeStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
						// try to connect with short timeout
						_pipeStream.Connect(500);
						_pipeStream.ReadMode = PipeTransmissionMode.Message;
						_pipeWriter = new StreamWriter(_pipeStream) { AutoFlush = true };
					} catch (Exception) {
						// couldn't connect now; dispose
						try { _pipeStream?.Dispose(); } catch { }
						_pipeStream = null;
						_pipeWriter = null;
					}
				}
			} catch { /* ignore */ }
		}

		private static void OnWindowCreated(Window w)
		{
			if (w == null) return;
			AttachWindowHandlers(w);
			PushEvent(new { Type = "WindowCreated", Window = DescribeWindow(w) });
		}

		private static void AttachWindowHandlers(Window w)
		{
			if (w == null) return;
			// avoid multiple subscriptions
			w.Activated -= WindowActivatedHandler;
			w.Deactivated -= WindowDeactivatedHandler;
			w.Loaded -= WindowLoadedHandler;
			w.Closed -= WindowClosedHandler;

			w.Activated += WindowActivatedHandler;
			w.Deactivated += WindowDeactivatedHandler;
			w.Loaded += WindowLoadedHandler;
			w.Closed += WindowClosedHandler;
		}

		private static void WindowActivatedHandler(object s, EventArgs e)
		{
			PushEvent(new { Type = "WindowActivated", Window = DescribeWindow((Window)s) });
		}

		private static void WindowDeactivatedHandler(object s, EventArgs e)
		{
			PushEvent(new { Type = "WindowDeactivated", Window = DescribeWindow((Window)s) });
		}

		private static void WindowLoadedHandler(object s, RoutedEventArgs e)
		{
			PushEvent(new { Type = "WindowLoaded", Window = DescribeWindow((Window)s) });
		}

		private static void WindowClosedHandler(object s, EventArgs e)
		{
			PushEvent(new { Type = "WindowClosed", Window = DescribeWindow((Window)s) });
		}

		private static void OnWindowLoaded(object sender, RoutedEventArgs e)
		{
			var w = sender as Window;
			PushEvent(new { Type = "WindowLoaded", Window = DescribeWindow(w) });
		}

		// Control creation/destruction handlers
		private static void OnElementLoaded(object sender, RoutedEventArgs e)
		{
			var fe = sender as FrameworkElement;
			if (fe == null) return;
			// windows handled separately
			if (fe is Window) return;

			var parent = FindParentFrameworkElement(fe);

			// update in-memory map of parent types and control types (counting instances)
			try {
				var parentType = parent?.GetType().FullName ?? "<null>";
				var controlType = fe.GetType().FullName ?? fe.GetType().Name;
				lock (TypeMapLock) {
					if (!ParentControlCountMap.TryGetValue(parentType, out var inner)) {
						inner = new Dictionary<string, int>(StringComparer.Ordinal);
						ParentControlCountMap[parentType] = inner;
					}
					if (inner.ContainsKey(controlType)) inner[controlType]++; else inner[controlType] = 1;
				}
			} catch { /* ignore */ }

			PushEvent(new {
				Type = "ControlCreated",
				Control = DescribeElement(fe),
				ParentId = GetIdForElement(parent),
				ParentType = parent?.GetType().FullName
			});
		}

		private static void OnElementUnloaded(object sender, RoutedEventArgs e)
		{
			var fe = sender as FrameworkElement;
			if (fe == null) return;
			if (fe is Window) return;

			var parent = FindParentFrameworkElement(fe);
			PushEvent(new {
				Type = "ControlDestroyed",
				Control = DescribeElement(fe),
				ParentId = GetIdForElement(parent),
				ParentType = parent?.GetType().FullName
			});
		}

		// helper: walk up visual/logical tree to find nearest FrameworkElement parent
		private static FrameworkElement FindParentFrameworkElement(FrameworkElement fe)
		{
			if (fe == null) return null;
			DependencyObject current = fe;
			while (true) {
				DependencyObject next = null;

				// prefer logical Parent when available
				if (current is FrameworkElement f && f.Parent != null) {
					next = f.Parent;
				} else {
					try {
						next = VisualTreeHelper.GetParent(current);
					} catch {
						next = null;
					}
				}

				if (next == null) return null;
				if (next is FrameworkElement parentFe) return parentFe;
				current = next;
			}
		}

		// Note: Popup.Opened/Closed are CLR events (not routed). We can't RegisterClassHandler for them.
		// If detection for popups is required, consider scanning PresentationSource.CurrentSources periodically
		// or attach handlers when you create/populate popups.

		private static void OnGotFocus(object sender, RoutedEventArgs e)
		{
			// Focus events disabled — do nothing
			return;
		}

		private static void OnLostFocus(object sender, RoutedEventArgs e)
		{
			// Focus events disabled — do nothing
			return;
		}

		private static void OnButtonClick(object sender, RoutedEventArgs e)
		{
			var fe = sender as FrameworkElement;
			PushEvent(new { Type = "ButtonClick", Control = DescribeElement(fe) });
		}

		private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			var fe = sender as FrameworkElement;
			PushEvent(new { Type = "SelectionChanged", Control = DescribeElement(fe) });
		}

		private static void OnRangeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			var fe = sender as FrameworkElement;
			PushEvent(new { Type = "ValueChanged", Control = DescribeElement(fe), Value = e.NewValue });
		}

		private static void OnTextChanged(object sender, TextChangedEventArgs e)
		{
			var fe = sender as FrameworkElement;
			PushEvent(new { Type = "TextChanged", Control = DescribeElement(fe) });
		}

		// --- helpers to describe elements/windows/popups

		private static object DescribeWindow(Window w)
		{
			if (w == null) return null;
			return new {
				Id = GetIdForElement(w),
				Type = w.GetType().FullName,
				Title = w.Title,
				Bounds = GetScreenBounds(w)
			};
		}

		private static object DescribePopup(Popup p)
		{
			if (p == null) return null;
			return new {
				Id = GetIdForElement(p.Child as FrameworkElement),
				Type = p.GetType().FullName,
				IsOpen = p.IsOpen
			};
		}

		private static object DescribeElement(FrameworkElement fe)
		{
			if (fe == null) return null;
			var w = Window.GetWindow(fe);
			var bounds = GetScreenBounds(fe, w);
			return new {
				Id = GetIdForElement(fe),
				Type = fe.GetType().FullName,
				Name = fe.Name,
				AutomationId = System.Windows.Automation.AutomationProperties.GetAutomationId(fe),
				Bounds = bounds,
				Visible = fe.IsVisible,
				Enabled = (fe as Control)?.IsEnabled
			};
		}

		private static string GetIdForElement(FrameworkElement fe)
		{
			if (fe == null) return null;
			var auto = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
			if (!string.IsNullOrEmpty(auto)) return "A:" + auto;
			if (!string.IsNullOrEmpty(fe.Name)) return "N:" + fe.Name;
			// fallback: type + hash
			return $"G:{fe.GetType().Name}:{fe.GetHashCode():X8}";
		}

		private static Rect? GetScreenBounds(FrameworkElement fe, Window w = null)
		{
			try {
				if (fe == null) return null;
				if (w == null) w = Window.GetWindow(fe);
				if (w == null) return null;
				var transform = fe.TransformToAncestor(w) as GeneralTransform;
				var topLeft = transform.Transform(new Point(0, 0));
				var bottomRight = transform.Transform(new Point(fe.ActualWidth, fe.ActualHeight));
				var p1 = w.PointToScreen(topLeft);
				var p2 = w.PointToScreen(bottomRight);
				// convert to device pixels if necessary
				var source = PresentationSource.FromVisual(w);
				if (source != null) {
					var m = source.CompositionTarget.TransformToDevice;
					p1 = new Point(p1.X * m.M11, p1.Y * m.M22);
					p2 = new Point(p2.X * m.M11, p2.Y * m.M22);
				}
				return new Rect(p1, p2);
			} catch {
				return null;
			}
		}

		// Very small placeholder: serialize and push to external process here
		private static void PushEvent(object payload)
		{
			// For debug: log to app logging
			try {
				var s = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
				Logging.Debug("UiObserver: " + (s?.Length > 1000 ? s.Substring(0, 1000) + "…" : s));
				// send to pipe if available
				if (_pipeName != null) {
					try {
						lock (PipeLock) {
							if (_pipeWriter == null) {
								// try to connect once more
								TryConnectPipe();
							}

							if (_pipeWriter != null) {
								_pipeWriter.WriteLine(s);
								_pipeWriter.Flush();
							}
						}
					} catch { /* ignore */ }
				}
			} catch { /* ignore */ }
		}
	}
}