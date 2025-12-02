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

namespace AcManager.Tools.Helpers {
    public static class UiObserver {
        private static bool _initialized;

        public static void Initialize() {
            if (_initialized) return;
            _initialized = true;

            // Window lifecycle: DpiAwareWindow.NewWindowCreated + class handler for Loaded
            DpiAwareWindow.NewWindowCreated += (s, e) => OnWindowCreated(s as Window);
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded), true);

            // Focus
            EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.GotFocusEvent, new RoutedEventHandler(OnGotFocus), true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), UIElement.LostFocusEvent, new RoutedEventHandler(OnLostFocus), true);

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

        private static void OnWindowCreated(Window w) {
            if (w == null) return;
            AttachWindowHandlers(w);
            PushEvent(new { Type = "WindowCreated", Window = DescribeWindow(w) });
        }

        private static void AttachWindowHandlers(Window w) {
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

        private static void WindowActivatedHandler(object s, EventArgs e) {
            PushEvent(new { Type = "WindowActivated", Window = DescribeWindow((Window)s) });
        }

        private static void WindowDeactivatedHandler(object s, EventArgs e) {
            PushEvent(new { Type = "WindowDeactivated", Window = DescribeWindow((Window)s) });
        }

        private static void WindowLoadedHandler(object s, RoutedEventArgs e) {
            PushEvent(new { Type = "WindowLoaded", Window = DescribeWindow((Window)s) });
        }

        private static void WindowClosedHandler(object s, EventArgs e) {
            PushEvent(new { Type = "WindowClosed", Window = DescribeWindow((Window)s) });
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e) {
            var w = sender as Window;
            PushEvent(new { Type = "WindowLoaded", Window = DescribeWindow(w) });
        }

        // Note: Popup.Opened/Closed are CLR events (not routed). We can't RegisterClassHandler for them.
        // If detection for popups is required, consider scanning PresentationSource.CurrentSources periodically
        // or attach handlers when you create/populate popups.

        private static void OnGotFocus(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "ControlGotFocus", Control = DescribeElement(fe) });
        }

        private static void OnLostFocus(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "ControlLostFocus", Control = DescribeElement(fe) });
        }

        private static void OnButtonClick(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "ButtonClick", Control = DescribeElement(fe) });
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "SelectionChanged", Control = DescribeElement(fe) });
        }

        private static void OnRangeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "ValueChanged", Control = DescribeElement(fe), Value = e.NewValue });
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e) {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "TextChanged", Control = DescribeElement(fe) });
        }

        // --- helpers to describe elements/windows/popups

        private static object DescribeWindow(Window w) {
            if (w == null) return null;
            return new {
                Id = GetIdForElement(w),
                Type = w.GetType().FullName,
                Title = w.Title,
                Bounds = GetScreenBounds(w)
            };
        }

        private static object DescribePopup(Popup p) {
            if (p == null) return null;
            return new {
                Id = GetIdForElement(p.Child as FrameworkElement),
                Type = p.GetType().FullName,
                IsOpen = p.IsOpen
            };
        }

        private static object DescribeElement(FrameworkElement fe) {
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

        private static string GetIdForElement(FrameworkElement fe) {
            if (fe == null) return null;
            var auto = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrEmpty(auto)) return "A:" + auto;
            if (!string.IsNullOrEmpty(fe.Name)) return "N:" + fe.Name;
            // fallback: type + hash
            return $"G:{fe.GetType().Name}:{fe.GetHashCode():X8}";
        }

        private static Rect? GetScreenBounds(FrameworkElement fe, Window w = null) {
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
        private static void PushEvent(object payload) {
            // TODO: replace with named pipe / socket / shared memory / whatever your process listens to.
            // For debug: log to app logging
            try {
                var s = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                Logging.Debug("UiObserver: " + (s?.Length > 1000 ? s.Substring(0, 1000) + "…" : s));
                // Example: ExternalMessenger.Send(s);
            } catch { /* ignore */ }
        }
    }
}