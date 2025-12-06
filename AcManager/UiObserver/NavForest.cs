using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Reflection;

namespace AcManager.UiObserver {
    // NavForest keeps per-root trees and allows incremental sync
    internal static class NavForest {
        // Track NavNodes by their FrameworkElement (direct reference instead of CompositeKey)
        private static readonly ConcurrentDictionary<FrameworkElement, NavNode> _nodesByElement = new ConcurrentDictionary<FrameworkElement, NavNode>();
        private static readonly ConcurrentDictionary<string, NavNode> _nodesById = new ConcurrentDictionary<string, NavNode>();
        private static readonly ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>> _rootIndex = new ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>>();
        
        // Debouncing: track pending sync operations per root
        private static readonly ConcurrentDictionary<FrameworkElement, DispatcherOperation> _pendingSyncs = new ConcurrentDictionary<FrameworkElement, DispatcherOperation>();
        
        private static Func<FrameworkElement, bool> _isTrulyVisible = null;

        public static event Action<FrameworkElement> RootChanged;

        // Attached flag to avoid subscribing handlers multiple times on the same element
        private static readonly DependencyProperty AttachedHandlersProperty = DependencyProperty.RegisterAttached(
                "AttachedHandlers", typeof(bool), typeof(NavForest), new PropertyMetadata(false));

        private static bool GetAttachedHandlers(DependencyObject o) {
            try { return (bool)o.GetValue(AttachedHandlersProperty); } catch { return false; }
        }

        private static void SetAttachedHandlers(DependencyObject o, bool value) {
            try { o.SetValue(AttachedHandlersProperty, value); } catch { }
        }

        public static void Configure(Func<FrameworkElement, bool> isTrulyVisible) {
            if (isTrulyVisible != null) _isTrulyVisible = isTrulyVisible;
        }

        // Enable automatic discovery of visual roots by listening to Loaded events.
        // This helps detect popups/context menus/other separate visuals when they appear.
        public static void EnableAutoRootTracking() {
            try {
                EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                        new RoutedEventHandler(OnAnyElementLoaded), true);
                // Listen for common popup open events (ContextMenu/MenuItem). These are routed events and help discover popup roots.
                try {
                    EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
                            new RoutedEventHandler(OnAnyPopupOpened), true);
                } catch { }
                try {
                    EventManager.RegisterClassHandler(typeof(System.Windows.Controls.MenuItem), System.Windows.Controls.MenuItem.SubmenuOpenedEvent,
                            new RoutedEventHandler(OnAnyPopupOpened), true);
                } catch { }
            } catch { }
        }

        private static void OnAnyElementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) {
            try {
                if (!(e.NewValue is bool b) || !b) return; // only care when element becomes visible
                var fe = sender as FrameworkElement;
                if (fe == null) return;

                // If this visual has a PresentationSource (popup/overlay), register and sync it
                try {
                    var ps = PresentationSource.FromVisual(fe);
                    if (ps != null) {
                        RegisterRoot(fe);
                        SyncRoot(fe);
                    }
                } catch { }
            } catch { }
        }

        private static void OnAnyElementLoaded(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            try {
                // Heuristics: register as root when element is not hosted in a Window or when it's a top-level content
                // PresentationSource.FromVisual non-null while Window.GetWindow returns null usually means popup-like visual
                var win = Window.GetWindow(fe);
                if (win == null) {
                    try {
                        var ps = PresentationSource.FromVisual(fe);
                        if (ps != null) {
                            RegisterRoot(fe);
                            return;
                        }
                    } catch { }

                    // fallback: if element has no visual parent and no logical parent, treat as root
                    try {
                        var hasVisualParent = false;
                        try { hasVisualParent = VisualTreeHelper.GetParent(fe) != null; } catch { hasVisualParent = false; }
                        if (!hasVisualParent && fe.Parent == null) {
                            RegisterRoot(fe);
                            return;
                        }
                    } catch { }
                } else {
                    // if it's the window content, ensure the window's content is registered
                    var content = win.Content as FrameworkElement;

                    // If the loaded element IS the window content (common case), register that content.
                    if (content != null && ReferenceEquals(content, fe)) {
                        RegisterRoot(content);
                        return;
                    }

                    // If the loaded element is the Window itself (Window.Loaded fired), content may not yet be set.
                    // Register the content if available, otherwise register the Window object as a root (Window is a FrameworkElement).
                    if (ReferenceEquals(win, fe)) {
                        if (content != null) {
                            RegisterRoot(content);
                        } else {
                            // Register the Window itself as a scanning root. This ensures the main window is scanned
                            // even when Content is not yet assigned at the time of this Loaded event.
                            RegisterRoot(win);
                        }
                        return;
                    }

                    // Otherwise, if some other element loaded that equals the content, we handled it above.
                }

                // Attach per-instance handlers for common popup-like controls when elements are loaded
                if (!GetAttachedHandlers(fe)) {
                    SetAttachedHandlers(fe, true);

                    // Clear the flag when element unloads so handlers can re-attach if element is re-added
                    RoutedEventHandler unloadedHandler = null;
                    unloadedHandler = (s, args) => {
                        try {
                            SetAttachedHandlers(fe, false);
                            fe.Unloaded -= unloadedHandler;
                        } catch { }
                    };
                    fe.Unloaded += unloadedHandler;

                    try {
                        if (fe is ComboBox cb) {
                            cb.DropDownOpened += (s2, e2) => {
                                try {
                                    // ComboBox popup is typically PART_Popup in template
                                    var popup = cb.Template?.FindName("PART_Popup", cb) as Popup;
                                    if (popup?.Child is FrameworkElement child) {
                                        RegisterRoot(child);
                                    }
                                } catch { }
                            };
                        }
                    } catch { }

                    try {
                        if (fe is Popup p) {
                            p.Opened += (s2, e2) => {
                                try { if (p.Child is FrameworkElement child) { RegisterRoot(child); } } catch { }
                            };
                        }
                    } catch { }

                    try {
                        if (fe is ContextMenu cm) {
                            cm.Opened += (s2, e2) => { try { RegisterRoot(cm); } catch { } };
                        }
                    } catch { }

                    try {
                        if (fe is MenuItem mi) {
                            mi.SubmenuOpened += (s2, e2) => { try { RegisterRoot(mi); } catch { } };
                        }
                    } catch { }

                    // TabControl: when selection changes, scan the TabControl subtree to discover newly realized tab content
                    try {
                        if (fe is TabControl tc) {
                            tc.SelectionChanged += (s2, e2) => {
                                try {
                                    RegisterRoot(tc);
                                } catch { }
                            };

                            // Also listen for the ItemContainerGenerator status — when containers are generated,
                            // the TabItem visuals are realized (useful for virtualized/delayed templates).
                            try {
                                var gen = tc.ItemContainerGenerator;
                                gen.StatusChanged += (s2, e2) => {
                                    try {
                                        if (gen.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) {
                                            RegisterRoot(tc);
                                        }
                                    } catch { }
                                };
                            } catch { }
                        }
                    } catch { }

                    // Special-case: ModernTab from FirstFloor.ModernUI is not a TabControl but behaves similarly.
                    // Attach to its selection/navigation change to rescan its subtree.
                    try {
                        if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernTab") {
                            try {
                                var et = fe.GetType();
                                var ev = et.GetEvent("SelectedSourceChanged");
                                if (ev != null) {
                                    var handler = Delegate.CreateDelegate(ev.EventHandlerType,
                                            typeof(NavForest).GetMethod(nameof(ModernTab_OnSelectedSourceChanged), BindingFlags.NonPublic | BindingFlags.Static));
                                    ev.AddEventHandler(fe, handler);
                                }
                            } catch { }
                        }

                        // Special-case: ModernMenu (FirstFloor.ModernUI) fires SelectedChange when menu selection changes.
                        try {
                            if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernMenu") {
                                try {
                                    var et = fe.GetType();
                                    var ev = et.GetEvent("SelectedChange");
                                    if (ev != null) {
                                        var handler = Delegate.CreateDelegate(ev.EventHandlerType,
                                                typeof(NavForest).GetMethod(nameof(ModernMenu_OnSelectedChange), BindingFlags.NonPublic | BindingFlags.Static));
                                        ev.AddEventHandler(fe, handler);
                                    }
                                } catch { }
                            }
                        } catch { }

                        // Special-case: ModernFrame (FirstFloor.ModernUI) hosts navigable content — when it navigates, its Content is updated.
                        try {
                            if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernFrame") {
                                try {
                                    // Use direct type reference if available
                                    var mf = fe as FirstFloor.ModernUI.Windows.Controls.ModernFrame;
                                    if (mf != null) {
                                        mf.Navigated += (s2, e2) => {
                                            try {
                                                // Register the new content (usually a Page or UserControl) as a root for scanning
                                                var content = mf.Content as FrameworkElement;
                                                if (content != null) {
                                                    RegisterRoot(content);
                                                } else {
                                                    // fallback to register the frame itself
                                                    RegisterRoot(mf);
                                                }
                                            } catch { }
                                        };

                                        // Also when the frame itself is loaded, attempt to register current content
                                        mf.Loaded += (s2, e2) => {
                                            try {
                                                var content = mf.Content as FrameworkElement;
                                                if (content != null) {
                                                    RegisterRoot(content);
                                                } else {
                                                    RegisterRoot(mf);
                                                }
                                            } catch { }
                                        };
                                    }
                                } catch { }
                            }
                        } catch { }
                    } catch { }
                }
            } catch { }
        }

        private static void ModernTab_OnSelectedSourceChanged(object sender, EventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                if (fe == null) return;
                RegisterRoot(fe);
            } catch { }
        }

        private static void ModernMenu_OnSelectedChange(object sender, EventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                if (fe == null) return;
                RegisterRoot(fe);
            } catch { }
        }

        public static void RegisterRoot(FrameworkElement root) {
            if (root == null) return;
            
            var isNew = !_rootIndex.ContainsKey(root);
            _rootIndex.GetOrAdd(root, _ => new HashSet<FrameworkElement>());
            
            // Debug output for root registration
            try {
                var typeName = root.GetType().Name;
                var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                var status = isNew ? "NEW" : "existing";
                System.Diagnostics.Debug.WriteLine($"[NavForest] RegisterRoot: {typeName} '{elementName}' ({status})");
                
                // Show what triggered this (call stack info)
                if (isNew) {
                    var ps = PresentationSource.FromVisual(root);
                    System.Diagnostics.Debug.WriteLine($"[NavForest]   -> PresentationSource: {ps?.GetType().Name ?? "null"}");
                    System.Diagnostics.Debug.WriteLine($"[NavForest]   -> IsLoaded: {root.IsLoaded}");
                    System.Diagnostics.Debug.WriteLine($"[NavForest]   -> IsVisible: {root.IsVisible}");
                }
            } catch { }
            
            // Attach cleanup handlers once per root
            if (isNew) {
                AttachCleanupHandlers(root);
            }
            
            // Schedule debounced sync: cancel any pending sync and schedule a new one
            ScheduleDebouncedSync(root);
        }

        private static void AttachCleanupHandlers(FrameworkElement root) {
            if (root == null) return;
            
            // Unloaded event for general cleanup
            root.Unloaded += OnRootUnloaded;
            
            // Closed event for Window, Popup, ContextMenu
            try {
                if (root is Window win) {
                    win.Closed += OnWindowClosed;
                }
            } catch { }
            
            try {
                if (root is Popup popup) {
                    popup.Closed += OnPopupClosed;
                }
            } catch { }
            
            try {
                if (root is ContextMenu cm) {
                    cm.Closed += OnContextMenuClosed;
                }
            } catch { }
        }

        private static void OnRootUnloaded(object sender, RoutedEventArgs e) {
            var root = sender as FrameworkElement;
            if (root != null) {
                UnregisterRoot(root);
            }
        }

        private static void OnWindowClosed(object sender, EventArgs e) {
            var root = sender as FrameworkElement;
            if (root != null) {
                UnregisterRoot(root);
            }
        }

        private static void OnPopupClosed(object sender, EventArgs e) {
            var root = sender as FrameworkElement;
            if (root != null) {
                UnregisterRoot(root);
            }
        }

        private static void OnContextMenuClosed(object sender, RoutedEventArgs e) {
            var root = sender as FrameworkElement;
            if (root != null) {
                UnregisterRoot(root);
            }
        }

        private static void ScheduleDebouncedSync(FrameworkElement root) {
            if (root == null) return;
            
            try {
                // Cancel any pending sync operation for this root
                if (_pendingSyncs.TryGetValue(root, out var pendingOp)) {
                    try {
                        if (pendingOp != null && pendingOp.Status == DispatcherOperationStatus.Pending) {
                            pendingOp.Abort();
                        }
                    } catch { }
                }
                
                // Schedule new sync at ApplicationIdle priority
                var newOp = root.Dispatcher.BeginInvoke(new Action(() => {
                    try {
                        _pendingSyncs.TryRemove(root, out var _);
                        SyncRoot(root);
                    } catch { }
                }), DispatcherPriority.ApplicationIdle);
                
                _pendingSyncs[root] = newOp;
            } catch { }
        }

        public static void UnregisterRoot(FrameworkElement root) {
            if (root == null) return;
            
            // Cancel any pending sync operation
            if (_pendingSyncs.TryRemove(root, out var pendingOp)) {
                try {
                    if (pendingOp != null && pendingOp.Status == DispatcherOperationStatus.Pending) {
                        pendingOp.Abort();
                    }
                } catch { }
            }
            
            // Detach cleanup handlers
            DetachCleanupHandlers(root);
            
            // Remove all NavNodes associated with this root
            if (_rootIndex.TryRemove(root, out var set)) {
                foreach (var fe in set) {
                    if (_nodesByElement.TryRemove(fe, out var node)) {
                        _nodesById.TryRemove(node.Id, out var _);
                    }
                }
            }
        }

        private static void DetachCleanupHandlers(FrameworkElement root) {
            if (root == null) return;
            
            try {
                root.Unloaded -= OnRootUnloaded;
            } catch { }
            
            try {
                if (root is Window win) {
                    win.Closed -= OnWindowClosed;
                }
            } catch { }
            
            try {
                if (root is Popup popup) {
                    popup.Closed -= OnPopupClosed;
                }
            } catch { }
            
            try {
                if (root is ContextMenu cm) {
                    cm.Closed -= OnContextMenuClosed;
                }
            } catch { }
        }

        public static void SyncRoot(FrameworkElement root) {
            if (root == null) return;
            if (!root.IsLoaded) return; // only scan after layout applied

            // Ensure we're on UI thread
            if (!root.Dispatcher.CheckAccess()) {
                ScheduleDebouncedSync(root);
                return;
            }

            try {
                var typeName = root.GetType().Name;
                var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                System.Diagnostics.Debug.WriteLine($"[NavForest] SyncRoot START: {typeName} '{elementName}'");
                
                var newElements = new HashSet<FrameworkElement>();
                
                // Scan visual tree and create NavNodes using the factory
                ScanVisualTree(root, newElements);

                System.Diagnostics.Debug.WriteLine($"[NavForest] SyncRoot END: {typeName} '{elementName}' - found {newElements.Count} elements");

                _rootIndex.AddOrUpdate(root, newElements, (k, old) => {
                    // Remove old nodes that are no longer in the tree
                    foreach (var oldFe in old) {
                        if (!newElements.Contains(oldFe)) {
                            if (_nodesByElement.TryRemove(oldFe, out var oldNode)) {
                                _nodesById.TryRemove(oldNode.Id, out var _);
                            }
                        }
                    }
                    return newElements;
                });

                try { RootChanged?.Invoke(root); } catch { }
            } catch { }
        }
        
        private static void ScanVisualTree(FrameworkElement root, HashSet<FrameworkElement> discoveredElements) {
            if (root == null) return;

            // Capture root's PresentationSource once to avoid mixing visuals from other roots.
            PresentationSource psRoot = null;
            try { psRoot = PresentationSource.FromVisual(root); } catch { psRoot = null; }

            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            visited.Add(root);

            while (stack.Count > 0) {
                var node = stack.Pop();

                if (node is FrameworkElement fe) {
                    // Skip if not visible
                    if (_isTrulyVisible != null && !_isTrulyVisible(fe)) {
                        continue;
                    }

                    // Ensure the visual is connected to a PresentationSource
                    PresentationSource psFe = null;
                    try {
                        psFe = PresentationSource.FromVisual(fe);
                        if (psFe == null) continue; // disconnected visual
                        
                        // IMPORTANT: Any element with its own PresentationSource is a navigation group boundary.
                        // This includes:
                        // - Popup content (has separate HwndSource)
                        // - Dialog overlays with separate HwndSource (Grid-based dialogs)
                        // - Any other element that serves as a PresentationSource root
                        //
                        // Check if this element IS the root visual of its PresentationSource.
                        // If so, treat it as a navigation group (unless it's the root we're currently scanning).
                        bool isOwnPresentationSourceRoot = false;
                        try {
                            isOwnPresentationSourceRoot = object.ReferenceEquals(psFe.RootVisual, fe);
                        } catch { }
                        
                        // If element has different PresentationSource than root, register it as a separate root
                        if (psRoot != null && !object.ReferenceEquals(psFe, psRoot)) {
                            RegisterRoot(fe);
                            continue; // Don't scan this subtree now - it will be scanned as its own root
                        }
                        
                        // If element IS a PresentationSource root (and not the root we're scanning),
                        // also register it as a group boundary
                        if (isOwnPresentationSourceRoot && !object.ReferenceEquals(fe, root)) {
                            RegisterRoot(fe);
                            continue; // Don't scan this subtree now - it will be scanned as its own root
                        }
                    } catch { continue; }

                    // Try to create a NavNode - returns null if element type should be ignored
                    var navNode = NavNode.CreateNavNode(fe);
                    if (navNode != null) {
                        _nodesByElement.AddOrUpdate(fe, navNode, (k, old) => navNode);
                        _nodesById.AddOrUpdate(navNode.Id, navNode, (k, old) => navNode);
                        discoveredElements.Add(fe);
                    }
                }

                // Continue traversing visual tree
                int visualCount = 0;
                try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { visualCount = 0; }
                for (int i = 0; i < visualCount; i++) {
                    try {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child == null) continue;
                        if (!visited.Contains(child)) {
                          visited.Add(child);
                          stack.Push(child);
                        }
                    } catch { }
                }
            }
        }

        // Handler invoked when common popup-like controls open (ContextMenu, MenuItem submenu).
        private static void OnAnyPopupOpened(object sender, RoutedEventArgs e) {
            try {
                if (sender == null) return;
                var dispatcher = (sender as DispatcherObject)?.Dispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher != null) {
                    dispatcher.BeginInvoke(new Action(() => {
                        try { RegisterNewPresentationSourceRoots(); } catch { }
                    }), DispatcherPriority.ApplicationIdle);
                } else {
                    RegisterNewPresentationSourceRoots();
                }
            } catch { }
        }

        // Enumerate PresentationSource.CurrentSources and register any root visuals not yet tracked.
        private static void RegisterNewPresentationSourceRoots() {
            try {
                var sources = PresentationSource.CurrentSources;
                foreach (PresentationSource ps in sources) {
                    try {
                        var root = ps?.RootVisual as FrameworkElement;
                        if (root == null) continue;
                        if (!_rootIndex.ContainsKey(root)) {
                            RegisterRoot(root);
                        }
                    } catch { }
                }
            } catch { }
        }

        private static bool DefaultIsTrulyVisible(FrameworkElement fe) {
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

        public static IReadOnlyCollection<NavNode> GetNavNodesForRoot(FrameworkElement root) {
            if (root == null) return new NavNode[0];
            if (!_rootIndex.TryGetValue(root, out var set)) return new NavNode[0];
            var list = new List<NavNode>();
            foreach (var fe in set.ToArray()) {
                if (_nodesByElement.TryGetValue(fe, out var nav)) list.Add(nav);
            }
            return list;
        }
        
        public static IEnumerable<NavNode> GetAllNavNodes() {
            return _nodesByElement.Values.ToArray();
        }
    }
}