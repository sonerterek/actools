using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using FirstFloor.ModernUI.Windows.Controls;
using FirstFloor.ModernUI.Helpers;
using System.IO.Pipes;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Documents;

namespace AcManager.UiObserver
{
    public static class UiObserver
    {
        private static bool _initialized;
        private static string _pipeName;
        private static NamedPipeClientStream _pipeStream;
        private static StreamWriter _pipeWriter;
        private static readonly object PipeLock = new object();

        // Map: parent type -> control type -> count (kept for compatibility)
        private static readonly object TypeMapLock = new object();
        private static readonly Dictionary<string, Dictionary<string, int>> ParentControlCountMap =
                new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        // Per-instance tracking: instance id -> info
        private class InstanceInfo {
            public WeakReference<FrameworkElement> ElementRef;
            public int ChildCount;
        }
        private static readonly Dictionary<string, InstanceInfo> Instances = new Dictionary<string, InstanceInfo>(StringComparer.Ordinal);
        private static readonly object InstancesLock = new object();
        private static readonly DependencyProperty InstanceIdProperty = DependencyProperty.RegisterAttached("UiObserverInstanceId", typeof(string), typeof(UiObserver), new PropertyMetadata(null));

        // track ItemContainerGenerator owners
        private static readonly object GeneratorOwnersLock = new object();
        private static readonly Dictionary<ItemContainerGenerator, ItemsControl> GeneratorOwners = new Dictionary<ItemContainerGenerator, ItemsControl>();

        // Adorners for highlighting
        private static readonly List<Adorner> CurrentAdorners = new List<Adorner>();
        private static bool _highlightingShown;

        // Debounced traversal scheduling
        private static readonly object ScheduleLock = new object();
        private static readonly HashSet<FrameworkElement> PendingRoots = new HashSet<FrameworkElement>();
        private static bool _scheduled;

        // Helper to decide whether a visual node is worth traversing
        private static bool ShouldTraverseVisualNode(DependencyObject node) {
            if (node == null) return false;

            // Only visuals are relevant
            if (!(node is Visual) && !(node is Visual3D)) return false;

            // Skip adorners (overlays)
            if (node is Adorner) return false;

            var fe = node as FrameworkElement;
            if (fe != null) {
                // Named or automation id — interesting
                if (!string.IsNullOrEmpty(fe.Name) || !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetAutomationId(fe))) return true;

                // If it holds data, caller may want it
                if (fe.DataContext != null) return true;

                // Container-like elements should be traversed
                if (fe is Panel || fe is ItemsControl || fe is ContentControl || fe is Decorator) return true;
            }

            // Otherwise traverse if there are visual children
            try { return VisualTreeHelper.GetChildrenCount(node) > 0; } catch { return false; }
        }

        // Schedule a debounced traversal for a root element (coalesces multiple requests)
        private static void ScheduleTraversal(FrameworkElement root) {
            if (root == null) return;
            lock (ScheduleLock) {
                PendingRoots.Add(root);
                if (_scheduled) return;
                _scheduled = true;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(ProcessPendingTraversals), DispatcherPriority.ApplicationIdle);
            }
        }

        private static void ProcessPendingTraversals() {
            FrameworkElement[] roots;
            lock (ScheduleLock) {
                roots = PendingRoots.ToArray();
                PendingRoots.Clear();
                _scheduled = false;
            }

            foreach (var r in roots) {
                try { ForceRegisterSubtreeVisualOnly(r); } catch { }
            }
        }

        // Visual-only subtree registration (efficient)
        // Augmented: also consider logical children and ItemsControl logical items/containers.
        private static void ForceRegisterSubtreeVisualOnly(object rootObj) {
            if (rootObj == null) return;
            var rootFe = rootObj as FrameworkElement;
            if (rootFe == null && rootObj is ContextMenu cm) {
                // try to traverse visual child if available
                var popup = cm.Parent as Popup;
                if (popup?.Child is FrameworkElement popupChild) {
                    rootFe = popupChild;
                } else {
                    // fallback to items (non-visual) as before
                    foreach (var item in cm.Items) {
                        if (item is FrameworkElement feItem) ForceRegisterSubtreeVisualOnly(feItem);
                    }
                    return;
                }
            }

            if (rootFe == null) return;

            // visited set to avoid cycles when mixing logical and visual traversal
            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(rootFe);
            visited.Add(rootFe);

            // safety cap to avoid very expensive traversals
            const int MaxProcessedNodes = 2000;
            int processed = 0;

            while (stack.Count > 0) {
                if (++processed > MaxProcessedNodes) {
                    // stop early to avoid UI stalls; scheduling another traversal later is preferable
                    try { FirstFloor.ModernUI.Helpers.Logging.Warning("UiObserver: traversal aborted — too many nodes"); } catch { }
                    break;
                }

                var node = stack.Pop();

                if (node is FrameworkElement fe) {
                    var id = GetOrCreateInstanceId(fe);
                    try {
                        lock (InstancesLock) {
                            if (!Instances.ContainsKey(id)) Instances[id] = new InstanceInfo { ElementRef = new WeakReference<FrameworkElement>(fe), ChildCount = 0 };
                            var parent = VisualTreeHelper.GetParent(fe) as FrameworkElement;
                            if (parent != null) {
                                var pid = GetOrCreateInstanceId(parent);
                                if (Instances.TryGetValue(pid, out var pinfo)) pinfo.ChildCount++;
                            }
                        }
                    } catch { }

                    PushEvent(new { Type = "ControlDiscovered", Control = DescribeElement(fe), ParentId = GetIdForElement(VisualTreeHelper.GetParent(fe) as FrameworkElement), ParentType = (VisualTreeHelper.GetParent(fe) as FrameworkElement)?.GetType().FullName });
                }

                // traverse visual children but only those worth traversing
                var visualCount = 0;
                try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { visualCount = 0; }
                for (int i = 0; i < visualCount; i++) {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child == null) continue;
                    if (!visited.Contains(child) && ShouldTraverseVisualNode(child)) {
                        visited.Add(child);
                        stack.Push(child);
                    }
                }

                // If ItemsControl, try realized containers and also push logical items only when they are FrameworkElement
                if (node is ItemsControl itemsControl) {
                    try {
                        var gen = itemsControl.ItemContainerGenerator;
                        for (int i = 0; i < itemsControl.Items.Count; i++) {
                            // realized container (visual)
                            var container = gen.ContainerFromIndex(i) as DependencyObject;
                            if (container != null) {
                                if (!visited.Contains(container) && ShouldTraverseVisualNode(container)) {
                                    visited.Add(container);
                                    stack.Push(container);
                                }
                            } else {
                                // fallback: push logical item only if it's a FrameworkElement (avoid pushing data objects)
                                var item = itemsControl.Items[i];
                                if (item is FrameworkElement itemFe && !visited.Contains(itemFe)) {
                                    visited.Add(itemFe);
                                    stack.Push(itemFe);
                                }
                            }
                        }
                    } catch { }
                }

                // ContentControl.Content (logical content) — only traverse if it's a FrameworkElement or visual
                try {
                    if (node is ContentControl cc) {
                        var content = cc.Content as DependencyObject;
                        if (content != null && !visited.Contains(content) && (content is FrameworkElement || content is Visual || content is Visual3D)) {
                            visited.Add(content);
                            stack.Push(content);
                        }
                    }
                } catch { }

                // Generic logical children — restrict to FrameworkElement/Visual/Visual3D only to avoid enumerating data objects
                try {
                    foreach (var logicalChild in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) {
                        if (logicalChild == null || visited.Contains(logicalChild)) continue;
                        if (logicalChild is FrameworkElement || logicalChild is Visual || logicalChild is Visual3D) {
                            visited.Add(logicalChild);
                            stack.Push(logicalChild);
                        }
                    }
                } catch { }
            }
        }

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

            EventManager.RegisterClassHandler(typeof(CheckBox), System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
                    new RoutedEventHandler(OnCheckBoxToggled), true);
            EventManager.RegisterClassHandler(typeof(CheckBox), System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
                    new RoutedEventHandler(OnCheckBoxToggled), true);

            // Additional handlers for dynamic menus/popups
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnMenuItemSubmenuOpened), true);
            // combo box DropDownOpened is a CLR event, attach per-instance in OnElementLoaded
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening), true);

            // Key handler for toggling highlighting (Ctrl+Shift+F12)
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);

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
                    // Also kick off discovery of templates/children for already-open windows' content
                    foreach (Window w in Application.Current.Windows) {
                        try {
                            var content = (w.Content as FrameworkElement);
                            if (content != null) {
                                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(content)), DispatcherPriority.ApplicationIdle);
                            }
                        } catch { }
                    }
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
            try {
                var content = w.Content as FrameworkElement;
                if (content != null) ScheduleTraversal(content);
            } catch { }
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

            try {
                // Ensure templates and visual tree of window content are discovered shortly after load
                var content = w?.Content as FrameworkElement;
                if (content != null) {
				Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(content)), DispatcherPriority.ApplicationIdle);
                    ScheduleTraversal(content);
                }
            } catch { /* ignore */ }
        }

        // Control creation/destruction handlers
        private static void OnElementLoaded(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            // windows handled separately
            if (fe is Window) return;

            // attach visibility change handler to keep visibility state up-to-date
            try {
                fe.IsVisibleChanged -= OnIsVisibleChanged;
                fe.IsVisibleChanged += OnIsVisibleChanged;
            } catch { }

            var parent = FindParentFrameworkElement(fe);

            // assign instance id
            var id = GetOrCreateInstanceId(fe);

            // update per-instance map and parent-child counts
            try {
                lock (InstancesLock) {
                    if (!Instances.ContainsKey(id)) Instances[id] = new InstanceInfo { ElementRef = new WeakReference<FrameworkElement>(fe), ChildCount = 0 };
                    if (parent != null) {
                        var pid = GetOrCreateInstanceId(parent);
                        if (Instances.TryGetValue(pid, out var pinfo)) pinfo.ChildCount++;
                    }
                }
            } catch { /* ignore */ }

            // update parent->control-type aggregated map as before
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
		// Kick off a deferred visual/logical subtree discovery for this element.
		// This ensures template parts (Slider thumb, ContextMenuButton PART_MoreButton, etc.)
		// are discovered after the template is applied.
            // Kick off a deferred visual subtree discovery for this element.
            try {
			Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(fe)), DispatcherPriority.ApplicationIdle);
                ScheduleTraversal(fe);
            } catch { /* ignore */ }

            // In OnElementLoaded(...) after other per-instance wiring

            // Time slider: attach per-instance when TimeSliderService.IsTimeSlider == true
            try {
                var slider = fe as Slider;
                if (slider != null && AcManager.Controls.Services.TimeSliderService.GetIsTimeSlider(slider)) {
                    slider.ValueChanged -= OnTimeSliderValueChanged;
                    slider.ValueChanged += OnTimeSliderValueChanged;
                    slider.PreviewMouseLeftButtonUp -= OnTimeSliderMouseUp;
                    slider.PreviewMouseLeftButtonUp += OnTimeSliderMouseUp;
                    // optional: find Thumb and subscribe to DragCompleted for final value event
                    var thumb = FindVisualChildByName(slider, "PART_Track") /* or search for Thumb in visual tree */;
                    // attach DragCompleted if found
                }
            } catch { /* ignore */ }

            // ContextMenuButton: attach per-instance and register menu opened handler
            try {
                var cmb = fe as FirstFloor.ModernUI.Windows.Controls.ContextMenuButton;
                if (cmb != null) {
                    // assume it exposes Click event taking ContextMenuButtonEventArgs
                    cmb.Click -= OnContextMenuButtonClick;
                    cmb.Click += OnContextMenuButtonClick;

                    if (cmb.Menu is FrameworkElement menuFe) {
                        // if menu is already set, prepare to inspect when opened
                        ForceRegisterSubtreeVisualOnly(menuFe);
                        // if it's a ContextMenu instance, attach Opened to discover items when it opens
                        var cm = cmb.Menu as ContextMenu;
                        if (cm != null) {
                            cm.Opened -= ContextMenu_Opened;
                            cm.Opened += ContextMenu_Opened;
                        }
                    }
                }
            } catch { /* ignore */ }

            // If this is a Popup, attach to CLR Opened/Closed
            try {
                if (fe is Popup p) {
                    p.Opened -= Popup_Opened;
                    p.Closed -= Popup_Closed;
                    p.Opened += Popup_Opened;
                    p.Closed += Popup_Closed;
                }

                // If this element has a ContextMenu instance, attach Opened/Closed
                if (fe.ContextMenu != null) {
                    fe.ContextMenu.Opened -= ContextMenu_Opened;
                    fe.ContextMenu.Closed -= ContextMenu_Closed;
                    fe.ContextMenu.Opened += ContextMenu_Opened;
                    fe.ContextMenu.Closed += ContextMenu_Closed;
                }

                // Watch item container generation for ItemsControl (including ComboBox)
                if (fe is ItemsControl ic) {
                    var gen = ic.ItemContainerGenerator;
                    lock (GeneratorOwnersLock) {
                        GeneratorOwners[gen] = ic;
                    }
                    gen.StatusChanged -= ItemContainerGenerator_StatusChanged;
                    gen.StatusChanged += ItemContainerGenerator_StatusChanged;
                }

                // Attach ComboBox.DropDownOpened per-instance (CLR event)
                if (fe is ComboBox cb) {
                    cb.DropDownOpened -= ComboBox_DropDownOpened;
                    cb.DropDownOpened += ComboBox_DropDownOpened;
                }
            } catch { /* ignore */ }
        }

        private static void OnElementUnloaded(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            if (fe is Window) return;

            // detach visibility handler
            try { fe.IsVisibleChanged -= OnIsVisibleChanged; } catch { }

            var parent = FindParentFrameworkElement(fe);

            // update per-instance map: decrement parent child count and remove instance
            try {
                lock (InstancesLock) {
                    var id = GetInstanceId(fe);
                    if (id != null) {
                        Instances.Remove(id);
                    }
                    if (parent != null) {
                        var pid = GetInstanceId(parent);
                        if (pid != null && Instances.TryGetValue(pid, out var pinfo)) {
                            pinfo.ChildCount = Math.Max(0, pinfo.ChildCount - 1);
                        }
                    }
                }
            } catch { /* ignore */ }

            PushEvent(new {
                Type = "ControlDestroyed",
                Control = DescribeElement(fe),
                ParentId = GetIdForElement(parent),
                ParentType = parent?.GetType().FullName
            });

            // detach CLR handlers we may have attached earlier
            try {
                if (fe is Popup p) {
                    p.Opened -= Popup_Opened;
                    p.Closed -= Popup_Closed;
                }

                if (fe.ContextMenu != null) {
                    fe.ContextMenu.Opened -= ContextMenu_Opened;
                    fe.ContextMenu.Closed -= ContextMenu_Closed;
                }

                if (fe is ItemsControl ic) {
                    var gen = ic.ItemContainerGenerator;
                    gen.StatusChanged -= ItemContainerGenerator_StatusChanged;
                    lock (GeneratorOwnersLock) {
                        GeneratorOwners.Remove(gen);
                    }
                }

                if (fe is ComboBox cb) {
                    cb.DropDownOpened -= ComboBox_DropDownOpened;
                }
            } catch { /* ignore */ }
        }

        // New handlers to add (examples)
        private static void OnCheckBoxToggled(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            if (cb == null) return;
            PushEvent(new { Type = "CheckBoxToggled", Id = GetIdForElement(cb), Name = cb.Name, Checked = cb.IsChecked });
        }

        private static void OnTimeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var s = sender as Slider;
            if (s == null) return;
            // optional: throttle or ignore intermediate values if you only want final value
            PushEvent(new { Type = "TimeSliderValueChanged", Id = GetIdForElement(s), Value = e.NewValue });
        }

        private static void OnTimeSliderMouseUp(object sender, MouseButtonEventArgs e)
        {
            var s = sender as Slider;
            if (s == null) return;
            PushEvent(new { Type = "TimeSliderDragCompleted", Id = GetIdForElement(s), Value = s.Value });
        }

        private static void OnContextMenuButtonClick(object sender, ContextMenuButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            PushEvent(new { Type = "ContextMenuButtonClick", Id = GetIdForElement(fe) });
            // inspect e.Menu / fe.Menu and ForceRegisterSubtree when appropriate
        }

        private static string GetOrCreateInstanceId(FrameworkElement fe) {
            if (fe == null) return null;
            var v = fe.GetValue(InstanceIdProperty) as string;
            if (!string.IsNullOrEmpty(v)) return v;
            v = "I:" + Guid.NewGuid().ToString("N");
            fe.SetValue(InstanceIdProperty, v);
            return v;
        }

        private static string GetInstanceId(FrameworkElement fe) {
            return fe?.GetValue(InstanceIdProperty) as string;
        }

        // Key handler: toggle highlighting of leaf controls (no children)
        private static void OnPreviewKeyDown(object sender, KeyEventArgs e) {
            if (e == null) return;
            if (e.Key == Key.F12 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift)) {
                e.Handled = true;
                ToggleHighlighting();
            }
        }

        private static void ToggleHighlighting() {
            if (_highlightingShown) {
                ClearHighlighting();
                _highlightingShown = false;
                return;
            }

            var toHighlight = new List<FrameworkElement>();
            lock (InstancesLock) {
                foreach (var kv in Instances.ToList()) {
                    if (kv.Value.ElementRef.TryGetTarget(out var fe)) {
                        if (kv.Value.ChildCount == 0 && fe.IsVisible && fe.IsLoaded) toHighlight.Add(fe);
                    }
                }
            }

            foreach (var fe in toHighlight) {
                try {
                    var layer = AdornerLayer.GetAdornerLayer(fe) ?? AdornerLayer.GetAdornerLayer(Window.GetWindow(fe)?.Content as Visual);
                    if (layer == null) continue;
                    var ad = new OutlineAdorner(fe);
                    layer.Add(ad);
                    CurrentAdorners.Add(ad);
                } catch { /* ignore */ }
            }

            _highlightingShown = true;
        }

        private static void ClearHighlighting() {
            foreach (var ad in CurrentAdorners.ToList()) {
                try {
                    var layer = AdornerLayer.GetAdornerLayer(ad.AdornedElement);
                    layer?.Remove(ad);
                } catch { /* ignore */ }
            }
            CurrentAdorners.Clear();
        }

        // Adorner to draw orange rectangle around element
        private class OutlineAdorner : Adorner {
            public OutlineAdorner(UIElement adornedElement) : base(adornedElement) {
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext) {
                var fe = AdornedElement as FrameworkElement;
                if (fe == null) return;
                var r = new Rect(new Point(0, 0), new Size(fe.ActualWidth, fe.ActualHeight));
                var pen = new Pen(Brushes.Orange, 2);
                drawingContext.DrawRectangle(Brushes.Transparent, pen, r);
            }
        }

        // Remaining methods (menu/popup handlers, generator status, ForceRegisterSubtree, etc.) are left unchanged
        // to keep this patch minimal. The ForceRegisterSubtreeVisualOnly augmentation should help discover logical/template parts.

        private static void OnMenuItemSubmenuOpened(object sender, RoutedEventArgs e) {
            try {
                var mi = sender as MenuItem;
                if (mi == null) return;
                PushEvent(new { Type = "MenuItem.SubmenuOpened", Header = mi.Header?.ToString(), Id = GetIdForElement(mi) });
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(mi)), DispatcherPriority.ApplicationIdle);
                ScheduleTraversal(mi);
            } catch { }
        }

        private static void ComboBox_DropDownOpened(object sender, EventArgs e) {
            try {
                var cb = sender as ComboBox;
                if (cb == null) return;
                PushEvent(new { Type = "ComboBox.DropDownOpened", Name = cb.Name, Id = GetIdForElement(cb) });
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(cb)), DispatcherPriority.ApplicationIdle);
                ScheduleTraversal(cb);
            } catch { }
        }

        private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                if (fe == null) return;
                var cm = fe.ContextMenu;
                if (cm != null) {
                    PushEvent(new { Type = "ContextMenuOpening", Owner = GetIdForElement(fe) });
                    cm.Opened -= ContextMenu_Opened;
                    cm.Opened += ContextMenu_Opened;
                }
            } catch { }
        }

        private static void Popup_Opened(object sender, EventArgs e) {
            try {
                var p = sender as Popup;
                if (p == null) return;
                PushEvent(new { Type = "PopupOpened", Popup = DescribePopup(p) });
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(p.Child as FrameworkElement)), DispatcherPriority.ApplicationIdle);
                ScheduleTraversal(p.Child as FrameworkElement);
            } catch { }
        }

        private static void Popup_Closed(object sender, EventArgs e) {
            try {
                var p = sender as Popup;
                if (p == null) return;
                PushEvent(new { Type = "PopupClosed", Popup = DescribePopup(p) });
            } catch { }
        }

        private static void ContextMenu_Opened(object sender, EventArgs e) {
            try {
                var cm = sender as ContextMenu;
                if (cm == null) return;
                PushEvent(new { Type = "ContextMenuOpened", Owner = GetIdForElement(cm.PlacementTarget as FrameworkElement) });
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => ForceRegisterSubtreeVisualOnly(cm)), DispatcherPriority.ApplicationIdle);
                ScheduleTraversal(cm);
            } catch { }
        }

        private static void ContextMenu_Closed(object sender, EventArgs e) {
            try {
                var cm = sender as ContextMenu;
                if (cm == null) return;
                PushEvent(new { Type = "ContextMenuClosed", Owner = GetIdForElement(cm.PlacementTarget as FrameworkElement) });
            } catch { }
        }

        private static void ItemContainerGenerator_StatusChanged(object sender, EventArgs e) {
            try {
                var gen = sender as ItemContainerGenerator;
                if (gen == null) return;
                if (gen.Status == GeneratorStatus.ContainersGenerated) {
                    ItemsControl owner = null;
                    lock (GeneratorOwnersLock) {
                        GeneratorOwners.TryGetValue(gen, out owner);
                    }
                    if (owner != null) {
                        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => {
                            try {
                                // schedule traversal for realized containers
                                for (int i = 0; i < owner.Items.Count; i++) {
                                    var container = owner.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                                    if (container != null) ForceRegisterSubtree(container);
                                    if (container != null) ScheduleTraversal(container);
                                }
                            } catch { }
                        }), DispatcherPriority.ApplicationIdle);
                    }
                }
            } catch { }
        }

        private static void ForceRegisterSubtree(object rootObj) {
            try {
                if (rootObj == null) return;
                var rootFe = rootObj as FrameworkElement;
                if (rootFe == null && rootObj is ContextMenu cm) {
                    foreach (var item in cm.Items) {
                        if (item is FrameworkElement feItem) ForceRegisterSubtree(feItem);
                    }
                    return;
                }

                var stack = new Stack<DependencyObject>();
                stack.Push(rootFe);
                while (stack.Count > 0) {
                    var node = stack.Pop();
                    if (node is FrameworkElement fe) {
                        var id = GetOrCreateInstanceId(fe);
                        try {
                            lock (InstancesLock) {
                                if (!Instances.ContainsKey(id)) Instances[id] = new InstanceInfo { ElementRef = new WeakReference<FrameworkElement>(fe), ChildCount = 0 };
                                var parent = FindParentFrameworkElement(fe);
                                if (parent != null) {
                                    var pid = GetOrCreateInstanceId(parent);
                                    if (Instances.TryGetValue(pid, out var pinfo)) pinfo.ChildCount++;
                                }
                            }
                        } catch { }

                        PushEvent(new { Type = "ControlDiscovered", Control = DescribeElement(fe), ParentId = GetIdForElement(FindParentFrameworkElement(fe)), ParentType = FindParentFrameworkElement(fe)?.GetType().FullName });
                    }

                    var count = 0;
                    try { count = VisualTreeHelper.GetChildrenCount(node); } catch { count = 0; }
                    for (int i = 0; i < count; i++) {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child != null) stack.Push(child);
                    }

                    if (node is ItemsControl itemsControl) {
                        foreach (var item in itemsControl.Items) {
                            if (item is DependencyObject dobj) stack.Push(dobj);
                        }
                    }
                }
            } catch { /* ignore */ }
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                if (fe == null) return;
                PushEvent(new { Type = "VisibilityChanged", Id = GetIdForElement(fe), Visible = fe.IsVisible });
            } catch { }
        }

        private static FrameworkElement FindVisualChildByName(DependencyObject parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name)) return null;
            try {
                var stack = new Stack<DependencyObject>();
                stack.Push(parent);
                while (stack.Count > 0) {
                    var node = stack.Pop();
                    var count = 0;
                    try { count = VisualTreeHelper.GetChildrenCount(node); } catch { count = 0; }
                    for (int i = 0; i < count; i++) {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child is FrameworkElement fe) {
                            if (fe.Name == name) return fe;
                        }
                        if (child != null) stack.Push(child);
                    }
                }
            } catch { /* ignore */ }
            return null;
        }

        private static FrameworkElement FindParentFrameworkElement(FrameworkElement fe)
        {
            if (fe == null) return null;
            DependencyObject current = fe;
            while (true) {
                DependencyObject next = null;
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

        private static void OnGotFocus(object sender, RoutedEventArgs e) { return; }
        private static void OnLostFocus(object sender, RoutedEventArgs e) { return; }

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

        private static object DescribeWindow(Window w) {
            if (w == null) return null;
            return new { Id = GetIdForElement(w), Type = w.GetType().FullName, Title = w.Title, Bounds = GetScreenBounds(w) };
        }

        private static object DescribePopup(Popup p) {
            if (p == null) return null;
            return new { Id = GetIdForElement(p.Child as FrameworkElement), Type = p.GetType().FullName, IsOpen = p.IsOpen };
        }

        private static object DescribeElement(FrameworkElement fe) {
            if (fe == null) return null;
            var w = Window.GetWindow(fe);
            var bounds = GetScreenBounds(fe, w);
            return new { Id = GetIdForElement(fe), Type = fe.GetType().FullName, Name = fe.Name, AutomationId = System.Windows.Automation.AutomationProperties.GetAutomationId(fe), Bounds = bounds, Visible = fe.IsVisible, Enabled = (fe as Control)?.IsEnabled };
        }

        private static string GetIdForElement(FrameworkElement fe) {
            if (fe == null) return null;
            var auto = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrEmpty(auto)) return "A:" + auto;
            if (!string.IsNullOrEmpty(fe.Name)) return "N:" + fe.Name;
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
                var source = PresentationSource.FromVisual(w);
                if (source != null) {
                    var m = source.CompositionTarget.TransformToDevice;
                    p1 = new Point(p1.X * m.M11, p1.Y * m.M22);
                    p2 = new Point(p2.X * m.M11, p2.Y * m.M22);
                }
                return new Rect(p1, p2);
            } catch { return null; }
        }

        private static void PushEvent(object payload) { return; }
    }
}