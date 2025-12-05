using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

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
        private static Func<FrameworkElement, object> _getLogicalKey = DefaultGetLogicalKey;
        private static Func<FrameworkElement, bool> _isTrulyVisible = DefaultIsTrulyVisible;
        private static Func<FrameworkElement, string> _computeStableId = DefaultComputeStableId;

        public static event Action<FrameworkElement> RootChanged;

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
                            // trigger an immediate sync
                            SyncRoot(fe);
                            return;
                        }
                    } catch { }

                    // fallback: if element has no visual parent and no logical parent, treat as root
                    try {
                        var hasVisualParent = false;
                        try { hasVisualParent = VisualTreeHelper.GetParent(fe) != null; } catch { hasVisualParent = false; }
                        if (!hasVisualParent && fe.Parent == null) {
                            RegisterRoot(fe);
                            SyncRoot(fe);
                            return;
                        }
                    } catch { }
                } else {
                    // if it's the window content, ensure the window's content is registered
                    var content = win.Content as FrameworkElement;

                    // If the loaded element IS the window content (common case), register that content.
                    if (content != null && ReferenceEquals(content, fe)) {
                        RegisterRoot(content);
                        SyncRoot(content);
                        return;
                    }

                    // If the loaded element is the Window itself (Window.Loaded fired), content may not yet be set.
                    // Register the content if available, otherwise register the Window object as a root (Window is a FrameworkElement).
                    if (ReferenceEquals(win, fe)) {
                        if (content != null) {
                            RegisterRoot(content);
                            SyncRoot(content);
                        } else {
                            // Register the Window itself as a scanning root. This ensures the main window is scanned
                            // even when Content is not yet assigned at the time of this Loaded event.
                            RegisterRoot(win);
                            SyncRoot(win);
                        }
                        return;
                    }

                    // Otherwise, if some other element loaded that equals the content, we handled it above.
                }
            } catch { }
        }

        public static void RegisterRoot(FrameworkElement root) {
            if (root == null) return;
            _rootIndex.GetOrAdd(root, _ => new HashSet<CompositeKey>());
            // schedule an initial sync on UI thread
            try { root.Dispatcher.BeginInvoke(new Action(() => SyncRoot(root)), DispatcherPriority.ApplicationIdle); } catch { }
        }

        public static void UnregisterRoot(FrameworkElement root) {
            if (root == null) return;
            if (_rootIndex.TryRemove(root, out var set)) {
                foreach (var k in set) {
                    _byComposite.TryRemove(k, out var _);
                    _parent.TryRemove(k, out var _);
                }
            }
        }

        public static void SyncRoot(FrameworkElement root) {
            if (root == null) return;
            if (!root.IsLoaded) return; // only scan after layout applied

            // Ensure we're on UI thread
            if (!root.Dispatcher.CheckAccess()) {
                try { root.Dispatcher.BeginInvoke(new Action(() => SyncRoot(root)), DispatcherPriority.ApplicationIdle); } catch { }
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