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
    // Helper composite key uniquely identifying a logical element within a particular root
    internal struct CompositeKey : IEquatable<CompositeKey> {
        public readonly FrameworkElement Root;
        public readonly object LogicalKey;

        public CompositeKey(FrameworkElement root, object logicalKey) {
            Root = root;
            LogicalKey = logicalKey;
        }

        public bool Equals(CompositeKey other) {
            return ReferenceEquals(Root, other.Root) && EqualityComparer<object>.Default.Equals(LogicalKey, other.LogicalKey);
        }

        public override bool Equals(object obj) {
            return obj is CompositeKey other && Equals(other);
        }

        public override int GetHashCode() {
            unchecked {
                var hash = Root != null ? RuntimeHelpersGetHashCode(Root) : 0;
                hash = (hash * 397) ^ (LogicalKey != null ? EqualityComparer<object>.Default.GetHashCode(LogicalKey) : 0);
                return hash;
            }
        }

        // Use RuntimeHelpers.GetHashCode to avoid depending on overridden GetHashCode on FrameworkElement
        private static int RuntimeHelpersGetHashCode(object o) {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }

    // NavForest keeps per-root trees and allows incremental sync
    internal static class NavForest {
        private static readonly ConcurrentDictionary<CompositeKey, NavElem> _byComposite = new ConcurrentDictionary<CompositeKey, NavElem>();
        private static readonly ConcurrentDictionary<FrameworkElement, HashSet<CompositeKey>> _rootIndex = new ConcurrentDictionary<FrameworkElement, HashSet<CompositeKey>>();
        private static readonly ConcurrentDictionary<CompositeKey, CompositeKey?> _parent = new ConcurrentDictionary<CompositeKey, CompositeKey?>();
        
        // Debouncing: track pending sync operations per root
        private static readonly ConcurrentDictionary<FrameworkElement, DispatcherOperation> _pendingSyncs = new ConcurrentDictionary<FrameworkElement, DispatcherOperation>();
        
        private static Func<FrameworkElement, object> _getLogicalKey = null;
        private static Func<FrameworkElement, bool> _isTrulyVisible = null;
        private static Func<FrameworkElement, string> _computeStableId = null;

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

        public static void Configure(Func<FrameworkElement, object> getLogicalKey,
                                     Func<FrameworkElement, bool> isTrulyVisible,
                                     Func<FrameworkElement, string> computeStableId) {
            if (getLogicalKey != null) _getLogicalKey = getLogicalKey;
            if (isTrulyVisible != null) _isTrulyVisible = isTrulyVisible;
            if (computeStableId != null) _computeStableId = computeStableId;
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
            _rootIndex.GetOrAdd(root, _ => new HashSet<CompositeKey>());
            
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
            
            // Remove all NavElems associated with this root
            if (_rootIndex.TryRemove(root, out var set)) {
                foreach (var k in set) {
                    _byComposite.TryRemove(k, out var _);
                    _parent.TryRemove(k, out var _);
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
                var candidateMap = new Dictionary<object, Tuple<FrameworkElement, int>>();
                BuildCandidateMap(root, candidateMap);

                var newKeys = new HashSet<CompositeKey>();

                foreach (var kv in candidateMap) {
                    var logicalKey = kv.Key;
                    var fe = kv.Value.Item1;
                    var id = _computeStableId != null ? _computeStableId(fe) : DefaultComputeStableId(fe);
                    var composite = new CompositeKey(root, logicalKey);
                    var nav = new NavElem(fe, logicalKey, id);
                    _byComposite.AddOrUpdate(composite, nav, (k, old) => nav);
                    newKeys.Add(composite);
                }

                _rootIndex.AddOrUpdate(root, newKeys, (k, old) => {
                    old.Clear();
                    foreach (var kk in newKeys) old.Add(kk);
                    return old;
                });

                // Update parent links
                foreach (var composite in newKeys) {
                    if (!_byComposite.TryGetValue(composite, out var nav)) continue;
                    if (!nav.TryGetVisual(out var fe)) continue;
                    var parentLogical = FindParentLogicalKeyWithinRoot(fe, root);
                    if (parentLogical == null || EqualityComparer<object>.Default.Equals(parentLogical, composite.LogicalKey)) {
                        _parent[composite] = null;
                    } else {
                        var parentComposite = new CompositeKey(root, parentLogical);
                        if (newKeys.Contains(parentComposite)) _parent[composite] = parentComposite; else _parent[composite] = null;
                    }
                }

                PruneAndUpdateBoundsForRoot(root);

                try { RootChanged?.Invoke(root); } catch { }
            } catch { }
        }

        // Handler invoked when common popup-like controls open (ContextMenu, MenuItem submenu).
        // Attempts to register the popup's visual root so it will be scanned.
        private static void OnAnyPopupOpened(object sender, RoutedEventArgs e) {
            try {
                if (sender == null) return;

                // The actual popup visual may be created after this event fires. Schedule a short delayed scan
                // of current PresentationSources to discover newly created popup roots and register them.
                try {
                    var dispatcher = (sender as DispatcherObject)?.Dispatcher ?? Application.Current?.Dispatcher;
                    if (dispatcher != null) {
                        dispatcher.BeginInvoke(new Action(() => {
                            try { RegisterNewPresentationSourceRoots(); } catch { }
                        }), DispatcherPriority.ApplicationIdle);
                    } else {
                        RegisterNewPresentationSourceRoots();
                    }
                } catch { }
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

        private static void BuildCandidateMap(FrameworkElement root, Dictionary<object, Tuple<FrameworkElement, int>> candidateMap) {
            if (root == null) return;

            // Capture root's PresentationSource once to avoid mixing visuals from other roots.
            PresentationSource psRoot = null;
            try { psRoot = PresentationSource.FromVisual(root); } catch { psRoot = null; }

            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<Tuple<DependencyObject, int>>();
            stack.Push(Tuple.Create((DependencyObject)root, 0));
            visited.Add(root);

            while (stack.Count > 0) {
                var tup = stack.Pop();
                var node = tup.Item1;
                var depth = tup.Item2;

                if (node is FrameworkElement fe) {
                    try {
                        // Skip visuals that are not actually visible according to provided predicate.
                        if (_isTrulyVisible != null && !_isTrulyVisible(fe)) goto skipAdd;
                    } catch { goto skipAdd; }

                    // Ensure the visual is connected to a PresentationSource and belongs to the same presentation root.
                    try {
                        var psFe = PresentationSource.FromVisual(fe);
                        if (psFe == null) goto skipAdd; // disconnected visual
                        if (psRoot != null && !object.ReferenceEquals(psFe, psRoot)) goto skipAdd; // different root (popup/tooltip/etc.)
                    } catch { goto skipAdd; }

                    object logicalKey = null;
                    try { logicalKey = _getLogicalKey != null ? _getLogicalKey(fe) : DefaultGetLogicalKey(fe); } catch { logicalKey = null; }

                    if (logicalKey != null) {
                        if (candidateMap.TryGetValue(logicalKey, out var existing)) {
                            if (depth < existing.Item2) candidateMap[logicalKey] = Tuple.Create(fe, depth);
                        } else {
                            candidateMap[logicalKey] = Tuple.Create(fe, depth);
                        }
                    }
                }

                skipAdd:;

                int visualCount = 0;
                try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { visualCount = 0; }
                for (int i = 0; i < visualCount; i++) {
                    try {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child == null) continue;
                        if (!visited.Contains(child)) {
                          visited.Add(child);
                          stack.Push(Tuple.Create(child, depth + 1));
                        }
                    } catch { }
                }

                // NOTE: intentionally do NOT traverse the logical tree. We only care about visuals connected to a PresentationSource.
                // LogicalTree traversal was producing disconnected/non-visual candidates that lack PresentationSource and produce
                // invalid screen coordinates. Removing logical traversal ensures we only discover elements that are actually in the visual tree.
            }
        }

        // Walk up parents to find nearest different logical key (within the same root)
        private static object FindParentLogicalKeyWithinRoot(FrameworkElement fe, FrameworkElement root) {
            if (fe == null || root == null) return null;
            object childKey = null;
            try { childKey = _getLogicalKey != null ? _getLogicalKey(fe) : DefaultGetLogicalKey(fe); } catch { childKey = null; }

            DependencyObject cur = fe;
            while (cur != null) {
                if (ReferenceEquals(cur, root)) break;

                DependencyObject next = null;
                if (cur is FrameworkElement f && f.Parent != null) next = f.Parent; else {
                    try { next = VisualTreeHelper.GetParent(cur); } catch { next = null; }
                }

                if (next == null) break;

                if (next is FrameworkElement pf) {
                    object pKey = null;
                    try { pKey = _getLogicalKey != null ? _getLogicalKey(pf) : DefaultGetLogicalKey(pf); } catch { pKey = null; }
                    if (pKey != null && !EqualityComparer<object>.Default.Equals(pKey, childKey)) return pKey;
                }

                cur = next;
            }

            return null;
        }

        private static bool IsDescendantOf(FrameworkElement candidate, FrameworkElement root) {
            if (candidate == null || root == null) return false;
            DependencyObject cur = candidate;
            while (cur != null) {
                if (object.ReferenceEquals(cur, root)) return true;
                try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
            }
            return false;
        }

        private static void PruneAndUpdateBoundsForRoot(FrameworkElement root) {
            if (root == null) return;
            if (!_rootIndex.TryGetValue(root, out var set)) return;

            var toRemove = new List<CompositeKey>();
            foreach (var composite in set.ToArray()) {
                if (!_byComposite.TryGetValue(composite, out var nav)) { toRemove.Add(composite); continue; }
                if (!nav.TryGetVisual(out var fe)) { toRemove.Add(composite); continue; }
                if (!IsDescendantOf(fe, root)) { toRemove.Add(composite); continue; }
                try {
                    if (_isTrulyVisible != null && !_isTrulyVisible(fe)) { toRemove.Add(composite); continue; }
                } catch { }

                try { nav.UpdateBounds(); } catch { }
            }

            foreach (var k in toRemove) {
                set.Remove(k);
                _byComposite.TryRemove(k, out var _);
                _parent.TryRemove(k, out var _);
            }
        }

        private static object DefaultGetLogicalKey(FrameworkElement fe) {
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

        private static string DefaultComputeStableId(FrameworkElement fe) {
            if (fe == null) return null;
            var auto = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrEmpty(auto)) return "A:" + auto;
            if (!string.IsNullOrEmpty(fe.Name)) return "N:" + fe.Name;
            return string.Format("G:{0}:{1:X8}", fe.GetType().Name, fe.GetHashCode());
        }

        public static IReadOnlyCollection<NavElem> GetNavElemsForRoot(FrameworkElement root) {
            if (root == null) return new NavElem[0];
            if (!_rootIndex.TryGetValue(root, out var set)) return new NavElem[0];
            var list = new List<NavElem>();
            foreach (var composite in set.ToArray()) {
                if (_byComposite.TryGetValue(composite, out var nav)) list.Add(nav);
            }
            return list;
        }
    }
}