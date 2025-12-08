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
    /// <summary>
    /// NavForest maintains one NavTree per PresentationSource.
    /// Each NavTree is an isolated navigation domain for keyboard navigation.
    /// Supports incremental subtree rescanning for dynamic content changes.
    /// 
    /// Uses dual-path NavNode discovery:
    /// 1. Visual Tree scanning - catches XAML-declared elements and template internals
    /// 2. Per-element Loaded events - catches dynamically loaded/lazy-loaded content
    /// </summary>
    internal static class NavForest {
        // Track NavNodes by their FrameworkElement
        private static readonly ConcurrentDictionary<FrameworkElement, NavNode> _nodesByElement = new ConcurrentDictionary<FrameworkElement, NavNode>();
        private static readonly ConcurrentDictionary<string, NavNode> _nodesById = new ConcurrentDictionary<string, NavNode>();
        
        // Map each PresentationSource to its NavTree root element (enforces 1:1 mapping)
        private static readonly ConcurrentDictionary<PresentationSource, FrameworkElement> _presentationSourceRoots = 
            new ConcurrentDictionary<PresentationSource, FrameworkElement>();
        
        // Root index: NavTree root element ? all elements in that tree
        private static readonly ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>> _rootIndex = 
            new ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>>();
        
        // Debouncing: track pending sync operations per root
        private static readonly ConcurrentDictionary<FrameworkElement, DispatcherOperation> _pendingSyncs = 
            new ConcurrentDictionary<FrameworkElement, DispatcherOperation>();
        
        private static Func<FrameworkElement, bool> _isTrulyVisible = null;

        public static event Action<FrameworkElement> RootChanged;
        public static event Action<string> FocusedNodeChanged; // nodeId

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

        /// <summary>
        /// Enable automatic discovery of PresentationSource roots by listening to Loaded events.
        /// </summary>
        public static void EnableAutoRootTracking() {
            try {
                EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                        new RoutedEventHandler(OnAnyElementLoaded), true);
                
                // Listen for focus changes to synchronize with NavMapper
                EventManager.RegisterClassHandler(typeof(UIElement), UIElement.GotFocusEvent,
                        new RoutedEventHandler(OnAnyElementGotFocus), true);
                
                // Listen for popup open events to ensure new PresentationSources are registered
                try {
                    EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                            new RoutedEventHandler(OnAnyPopupOpened), true);
                } catch { }
                try {
                    EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
                            new RoutedEventHandler(OnAnyPopupOpened), true);
                } catch { }
            } catch { }
        }

        private static void OnAnyElementGotFocus(object sender, RoutedEventArgs e) {
            try {
                var fe = sender as FrameworkElement;
                if (fe == null) return;

                // Try to find the appropriate NavNode for this focused element
                // The focused element might be:
                // 1. The exact element we have a NavNode for
                // 2. A child of the element we have a NavNode for (e.g., TextBox inside Button template)
                // 3. A parent of the element we have a NavNode for (less common but possible)
                
                NavNode navNode = FindNavNodeForFocusedElement(fe);
                
                if (navNode != null && navNode.IsNavigable) {
                    // IMPORTANT: Only focus leaf nodes, never groups
                    if (!navNode.IsGroup || !navNode.IsNavigable) {
                        try {
                            FocusedNodeChanged?.Invoke(navNode.Id);
                        } catch { }
                    }
                }
            } catch { }
        }

        /// <summary>
        /// Finds the appropriate NavNode for a focused element by searching:
        /// 1. The element itself
        /// 2. Up the visual tree (focused element is a child of our NavNode element)
        /// 3. Down the visual tree (focused element is a parent, we need to find the leaf NavNode)
        /// </summary>
        private static NavNode FindNavNodeForFocusedElement(FrameworkElement focusedElement) {
            if (focusedElement == null) return null;

            // 1. Check if we have a direct NavNode for this element
            if (_nodesByElement.TryGetValue(focusedElement, out var directNode)) {
                // Make sure it's a leaf node (not a group)
                if (!directNode.IsGroup) {
                    return directNode;
                }
            }

            // 2. Walk UP the visual tree to find a NavNode
            // This handles cases where WPF focuses a child element (e.g., TextBox in Button template)
            var current = focusedElement;
            while (current != null) {
                if (_nodesByElement.TryGetValue(current, out var ancestorNode)) {
                    // Found a NavNode - make sure it's a leaf
                    if (!ancestorNode.IsGroup) {
                        return ancestorNode;
                    }
                }

                try {
                    current = VisualTreeHelper.GetParent(current) as FrameworkElement;
                } catch {
                    break;
                }
            }

            // 3. Walk DOWN the visual tree to find a leaf NavNode
            // This handles cases where we focused a container but should focus its leaf child
            var leafNode = FindLeafNavNodeInSubtree(focusedElement);
            if (leafNode != null) {
                return leafNode;
            }

            // 4. If still not found, try to create a NavNode for the focused element
            // This catches dynamically created elements that weren't in the initial scan
            var newNode = NavNode.CreateNavNode(focusedElement);
            if (newNode != null && !newNode.IsGroup) {
                if (_nodesByElement.TryAdd(focusedElement, newNode)) {
                    _nodesById.TryAdd(newNode.Id, newNode);
                    
                    // Try to add to appropriate root's element set
                    try {
                        var ps = PresentationSource.FromVisual(focusedElement);
                        if (ps != null && _presentationSourceRoots.TryGetValue(ps, out var navTreeRoot)) {
                            if (_rootIndex.TryGetValue(navTreeRoot, out var elementSet)) {
                                lock (elementSet) {
                                    elementSet.Add(focusedElement);
                                }
                            }
                        }
                    } catch { }
                    
                    return newNode;
                }
            }

            return null;
        }

        /// <summary>
        /// Searches the visual subtree for the first leaf NavNode.
        /// Returns null if no leaf NavNode is found.
        /// </summary>
        private static NavNode FindLeafNavNodeInSubtree(DependencyObject root) {
            if (root == null) return null;

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);

            while (queue.Count > 0) {
                var current = queue.Dequeue();

                if (current is FrameworkElement fe) {
                    if (_nodesByElement.TryGetValue(fe, out var node)) {
                        // Found a NavNode - check if it's a leaf
                        if (!node.IsGroup && node.IsNavigable) {
                            return node;
                        }
                        // If it's a group, don't search its children (they should already be NavNodes)
                        if (node.IsGroup) {
                            continue;
                        }
                    }
                }

                // Continue searching children
                try {
                    var childCount = VisualTreeHelper.GetChildrenCount(current);
                    for (int i = 0; i < childCount; i++) {
                        var child = VisualTreeHelper.GetChild(current, i);
                        if (child != null) {
                            queue.Enqueue(child);
                        }
                    }
                } catch { }
            }

            return null;
        }

        private static void OnAnyElementLoaded(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            
            try {
                var win = Window.GetWindow(fe);
                
                // Handle Window-based content
                if (win != null) {
                    // Only register Window.Content or Window itself as roots
                    if (ReferenceEquals(win, fe)) {
                        // Window.Loaded fired
                        var content = win.Content as FrameworkElement;
                        RegisterRoot(content ?? win);
                        return;
                    }
                    
                    if (win.Content != null && ReferenceEquals(win.Content, fe)) {
                        // Window.Content loaded
                        RegisterRoot(fe);
                        return;
                    }
                    
                    // For other elements in window: attach handlers AND try creating NavNode (dual-path)
                    AttachSubtreeChangeHandlers(fe);
                    TryCreateNavNodeForElement(fe);
                    return;
                }
                
                // Handle non-Window elements (Popups, ContextMenus, dialog overlays)
                var ps = PresentationSource.FromVisual(fe);
                if (ps != null) {
                    var psRoot = ps.RootVisual as FrameworkElement;
                    
                    // Check if this element IS the PresentationSource.RootVisual
                    if (psRoot != null && object.ReferenceEquals(psRoot, fe)) {
                        // This IS a PS root - register it
                        RegisterRoot(fe);
                        return;
                    }
                    
                    // Not a PS root - check if PS is already tracked
                    if (_presentationSourceRoots.ContainsKey(ps)) {
                        // PS already tracked: attach handlers AND try creating NavNode (dual-path)
                        AttachSubtreeChangeHandlers(fe);
                        TryCreateNavNodeForElement(fe);
                        return;
                    }
                    
                    // PS not tracked - register its root
                    if (psRoot != null) {
                        RegisterRoot(psRoot);
                        return;
                    }
                }
                
                // Fallback: disconnected elements with no parent
                try {
                    var hasVisualParent = VisualTreeHelper.GetParent(fe) != null;
                    if (!hasVisualParent && fe.Parent == null) {
                        RegisterRoot(fe);
                    }
                } catch { }
            } catch { }
        }

        /// <summary>
        /// Dual-path discovery: Try to create a NavNode for a dynamically loaded element.
        /// This catches elements that are added after the initial Visual Tree scan.
        /// If element is NavNode-worthy and its PresentationSource root is tracked, add it to that tree.
        /// </summary>
        private static void TryCreateNavNodeForElement(FrameworkElement fe) {
            if (fe == null) return;
            
            try {
                // Skip if already tracked
                if (_nodesByElement.ContainsKey(fe)) return;
                
                // Check if element is visible
                if (_isTrulyVisible != null && !_isTrulyVisible(fe)) return;
                
                // Get PresentationSource
                var ps = PresentationSource.FromVisual(fe);
                if (ps == null) return; // Not connected
                
                // Find the NavTree root for this PresentationSource
                if (!_presentationSourceRoots.TryGetValue(ps, out var navTreeRoot)) {
                    // PS not tracked - this shouldn't happen, but be defensive
                    return;
                }
                
                // Check if we're in the same PresentationSource as the root
                var psRoot = PresentationSource.FromVisual(navTreeRoot);
                if (!object.ReferenceEquals(ps, psRoot)) {
                    // Different PresentationSource - element belongs to a different tree
                    return;
                }
                
                // Try to create NavNode for this element
                var navNode = NavNode.CreateNavNode(fe);
                if (navNode != null) {
                    // Add to tracking dictionaries
                    if (_nodesByElement.TryAdd(fe, navNode)) {
                        _nodesById.TryAdd(navNode.Id, navNode);
                        
                        // Add to root's element set (thread-safe)
                        if (_rootIndex.TryGetValue(navTreeRoot, out var elementSet)) {
                            lock (elementSet) {
                                elementSet.Add(fe);
                            }
                            
                            // Notify that tree changed
                            try { RootChanged?.Invoke(navTreeRoot); } catch { }
                            
                            System.Diagnostics.Debug.WriteLine($"[NavForest] Dynamic NavNode added: {fe.GetType().Name} '{navNode.Id}'");
                        }
                    }
                }
            } catch { }
        }

        /// <summary>
        /// Attaches handlers for subtree content changes (TabControl, ComboBox, ModernFrame, etc.)
        /// Does NOT create new NavTree roots - just triggers rescans.
        /// </summary>
        private static void AttachSubtreeChangeHandlers(FrameworkElement fe) {
            if (GetAttachedHandlers(fe)) return;
            SetAttachedHandlers(fe, true);
            
            // Clear flag on unload for potential reattachment
            fe.Unloaded += (s, e) => SetAttachedHandlers(fe, false);
            
            try {
                // ComboBox: dropdown has its own PresentationSource - register as new root
                if (fe is ComboBox cb) {
                    cb.DropDownOpened += (s, e) => {
                        try {
                            var popup = cb.Template?.FindName("PART_Popup", cb) as Popup;
                            if (popup?.Child is FrameworkElement child) {
                                RegisterRoot(child); // Popup.Child has its own PS
                            }
                        } catch { }
                    };
                }
            } catch { }
            
            try {
                // Popup: has its own PresentationSource - register as new root
                if (fe is Popup p) {
                    p.Opened += (s, e) => {
                        try {
                            if (p.Child is FrameworkElement child) {
                                RegisterRoot(child);
                            }
                        } catch { }
                    };
                }
            } catch { }
            
            try {
                // ContextMenu: has its own PresentationSource - register as new root
                if (fe is ContextMenu cm) {
                    cm.Opened += (s, e) => {
                        try { RegisterRoot(cm); } catch { }
                    };
                }
            } catch { }
            
            try {
                // MenuItem: submenu is a separate PresentationSource - register as new root
                if (fe is MenuItem mi) {
                    mi.SubmenuOpened += (s, e) => {
                        try { RegisterRoot(mi); } catch { }
                    };
                }
            } catch { }
            
            try {
                // TabControl: content changes within same PS - rescan subtree
                if (fe is TabControl tc) {
                    tc.SelectionChanged += (s, e) => {
                        try { RescanSubtree(tc); } catch { }
                    };
                    
                    // Note: No ItemContainerGenerator.StatusChanged handler needed
                    // Dual-path discovery via Loaded events catches virtualized items automatically
                }
            } catch { }
            
            try {
                // ModernTab: content changes - rescan subtree
                if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernTab") {
                    try {
                        var et = fe.GetType();
                        var ev = et.GetEvent("SelectedSourceChanged");
                        if (ev != null) {
                            var handler = Delegate.CreateDelegate(ev.EventHandlerType,
                                    typeof(NavForest).GetMethod(nameof(OnModernControlChanged), BindingFlags.NonPublic | BindingFlags.Static));
                            ev.AddEventHandler(fe, handler);
                        }
                    } catch { }
                }
            } catch { }
            
            try {
                // ModernMenu: selection changes - rescan subtree
                if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernMenu") {
                    try {
                        var et = fe.GetType();
                        var ev = et.GetEvent("SelectedChange");
                        if (ev != null) {
                            var handler = Delegate.CreateDelegate(ev.EventHandlerType,
                                    typeof(NavForest).GetMethod(nameof(OnModernControlChanged), BindingFlags.NonPublic | BindingFlags.Static));
                            ev.AddEventHandler(fe, handler);
                        }
                    } catch { }
                }
            } catch { }
            
            try {
                // ModernFrame: navigation changes content - rescan subtree
                if (fe.GetType().FullName == "FirstFloor.ModernUI.Windows.Controls.ModernFrame") {
                    try {
                        var mf = fe as FirstFloor.ModernUI.Windows.Controls.ModernFrame;
                        if (mf != null) {
                            mf.Navigated += (s, e) => {
                                try { RescanSubtree(mf); } catch { }
                            };
                        }
                    } catch { }
                }
            } catch { }
        }

        private static void OnModernControlChanged(object sender, EventArgs e) {
            try {
                if (sender is FrameworkElement fe) {
                    RescanSubtree(fe);
                }
            } catch { }
        }

        /// <summary>
        /// Registers a NavTree root for a PresentationSource.
        /// Enforces 1:1 mapping - only PresentationSource.RootVisual elements are registered as roots.
        /// If a child element is passed, the actual PS root is registered instead.
        /// </summary>
        public static void RegisterRoot(FrameworkElement root) {
            if (root == null) return;
            
            try {
                var ps = PresentationSource.FromVisual(root);
                if (ps == null) return; // Not connected to a presentation source
                
                // Get the actual PresentationSource.RootVisual
                var psRootVisual = ps.RootVisual as FrameworkElement;
                if (psRootVisual == null) return;
                
                // If we're trying to register a non-root element, redirect to actual root
                if (!object.ReferenceEquals(root, psRootVisual)) {
                    // Check if the actual PS root is already registered
                    if (_presentationSourceRoots.TryGetValue(ps, out var existingRoot)) {
                        // PS root already registered - schedule a rescan instead
                        ScheduleDebouncedSync(existingRoot);
                        return;
                    }
                    
                    // Register the actual PS root, not the child element
                    root = psRootVisual;
                }
                
                // Register this PresentationSource ? Root mapping
                var isNew = _presentationSourceRoots.TryAdd(ps, root);
                _rootIndex.GetOrAdd(root, _ => new HashSet<FrameworkElement>());
                
                // Debug output
                try {
                    var typeName = root.GetType().Name;
                    var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                    var status = isNew ? "NEW" : "existing";
                    System.Diagnostics.Debug.WriteLine($"[NavForest] RegisterRoot: {typeName} '{elementName}' ({status})");
                    
                    if (isNew) {
                        System.Diagnostics.Debug.WriteLine($"[NavForest]   -> PresentationSource: {ps.GetType().Name}");
                        System.Diagnostics.Debug.WriteLine($"[NavForest]   -> IsLoaded: {root.IsLoaded}");
                        System.Diagnostics.Debug.WriteLine($"[NavForest]   -> IsVisible: {root.IsVisible}");
                    }
                } catch { }
                
                // Attach cleanup handlers once per root
                if (isNew) {
                    AttachCleanupHandlers(root);
                }
                
                // Schedule debounced sync
                ScheduleDebouncedSync(root);
            } catch { }
        }

        /// <summary>
        /// Rescans a specific subtree within an existing NavTree.
        /// Used when content changes within the same PresentationSource (TabControl selection, ModernFrame navigation, etc.)
        /// </summary>
        public static void RescanSubtree(FrameworkElement subtreeRoot) {
            if (subtreeRoot == null) return;
            
            try {
                var ps = PresentationSource.FromVisual(subtreeRoot);
                if (ps == null) return;
                
                // Find the NavTree root for this PresentationSource
                if (!_presentationSourceRoots.TryGetValue(ps, out var navTreeRoot)) {
                    // PS not tracked yet - register it
                    RegisterRoot(subtreeRoot);
                    return;
                }
                
                // Schedule rescan of the entire NavTree (which includes this subtree)
                // The scan will be debounced, so multiple rapid calls coalesce
                ScheduleDebouncedSync(navTreeRoot);
            } catch { }
        }

        private static void AttachCleanupHandlers(FrameworkElement root) {
            if (root == null) return;
            
            root.Unloaded += OnRootUnloaded;
            
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
            if (sender is FrameworkElement root) {
                UnregisterRoot(root);
            }
        }

        private static void OnWindowClosed(object sender, EventArgs e) {
            if (sender is FrameworkElement root) {
                UnregisterRoot(root);
            }
        }

        private static void OnPopupClosed(object sender, EventArgs e) {
            if (sender is FrameworkElement root) {
                UnregisterRoot(root);
            }
        }

        private static void OnContextMenuClosed(object sender, RoutedEventArgs e) {
            if (sender is FrameworkElement root) {
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
            
            // Remove PS ? Root mapping
            try {
                var ps = PresentationSource.FromVisual(root);
                if (ps != null) {
                    _presentationSourceRoots.TryRemove(ps, out var _);
                }
            } catch { }
            
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
                
                // Scan visual tree and create NavNodes
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

            // Capture root's PresentationSource once to enforce boundary
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
                        
                        // Check if this element IS the root visual of its PresentationSource
                        bool isOwnPresentationSourceRoot = false;
                        try {
                            isOwnPresentationSourceRoot = object.ReferenceEquals(psFe.RootVisual, fe);
                        } catch { }
                        
                        // If element has different PresentationSource than root, register it as a separate root
                        if (psRoot != null && !object.ReferenceEquals(psFe, psRoot)) {
                            RegisterRoot(fe);
                            continue; // Don't scan this subtree - it will be scanned as its own root
                        }
                        
                        // If element IS a PresentationSource root (and not the root we're scanning),
                        // also register it as a group boundary
                        if (isOwnPresentationSourceRoot && !object.ReferenceEquals(fe, root)) {
                            RegisterRoot(fe);
                            continue; // Don't scan this subtree - it will be scanned as its own root
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
                        if (!_presentationSourceRoots.ContainsKey(ps)) {
                            var root = ps?.RootVisual as FrameworkElement;
                            if (root != null) {
                                RegisterRoot(root);
                            }
                        }
                    } catch { }
                }
            } catch { }
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