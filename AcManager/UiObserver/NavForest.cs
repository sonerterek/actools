using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Reflection;

namespace AcManager.UiObserver {
    /// <summary>
    /// NavForest discovers and tracks NavNodes across multiple PresentationSources.
    /// 
    /// Architecture:
    /// - NavNode: Pure data structure with type-specific behaviors (CreateNavNode factory)
    /// - NavForest: Discovery engine - scans visual trees, builds HierarchicalId, emits events
    /// - NavMapper: Navigation logic - subscribes to events, manages modal stack, handles input
    /// 
    /// Events:
    /// - NavNodeAdded: New node discovered
    /// - NavNodeRemoved: Node removed from tree
    /// - ModalGroupOpened: Dual-role modal opened (ComboBox, ContextMenu)
    /// - ModalGroupClosed: Dual-role modal closed
    /// </summary>
    internal static class NavForest {
        #region Indexes

        // Track NavNodes by their FrameworkElement (TRUE source of truth)
        private static readonly ConcurrentDictionary<FrameworkElement, NavNode> _nodesByElement = new ConcurrentDictionary<FrameworkElement, NavNode>();
        
        // Map each PresentationSource to its NavTree root element
        private static readonly ConcurrentDictionary<PresentationSource, FrameworkElement> _presentationSourceRoots = 
            new ConcurrentDictionary<PresentationSource, FrameworkElement>();
        
        // Root index: NavTree root element ? all elements in that tree
        private static readonly ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>> _rootIndex = 
            new ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>>();
        
        // Debouncing: track pending sync operations per root
        private static readonly ConcurrentDictionary<FrameworkElement, DispatcherOperation> _pendingSyncs = 
            new ConcurrentDictionary<FrameworkElement, DispatcherOperation>();

        // Track which elements already have modal event handlers attached to prevent duplicate subscriptions
        private static readonly HashSet<FrameworkElement> _elementsWithModalHandlers = new HashSet<FrameworkElement>();

        #endregion

        #region Events

        /// <summary>
        /// Fired when a new NavNode is added to the forest.
        /// NavMapper can subscribe to track nodes.
        /// </summary>
        public static event Action<NavNode> NavNodeAdded;

        /// <summary>
        /// Fired when a NavNode is removed from the forest.
        /// NavMapper should clean up references (focused node, modal stack).
        /// </summary>
        public static event Action<NavNode> NavNodeRemoved;

        /// <summary>
        /// Fired when a dual-role modal group opens (ComboBox dropdown, ContextMenu, etc.).
        /// NavMapper should push to modal stack.
        /// </summary>
        public static event Action<NavNode> ModalGroupOpened;

        /// <summary>
        /// Fired when a dual-role modal group closes.
        /// NavMapper should pop from modal stack.
        /// </summary>
        public static event Action<NavNode> ModalGroupClosed;

        /// <summary>
        /// Fired when a NavTree root changes (nodes added/removed).
        /// </summary>
        public static event Action<FrameworkElement> RootChanged;

        #endregion

        #region Configuration

        private static Func<FrameworkElement, bool> _isTrulyVisible = null;

        public static void Configure(Func<FrameworkElement, bool> isTrulyVisible) {
            if (isTrulyVisible != null) _isTrulyVisible = isTrulyVisible;
        }

        #endregion

        #region Auto Root Tracking

        // Attached flag to avoid subscribing handlers multiple times
        private static readonly DependencyProperty AttachedHandlersProperty = DependencyProperty.RegisterAttached(
                "AttachedHandlers", typeof(bool), typeof(NavForest), new PropertyMetadata(false));

        private static bool GetAttachedHandlers(DependencyObject o) {
            try { return (bool)o.GetValue(AttachedHandlersProperty); } catch { return false; }
        }

        private static void SetAttachedHandlers(DependencyObject o, bool value) {
            try { o.SetValue(AttachedHandlersProperty, value); } catch { }
        }

        /// <summary>
        /// Enable automatic discovery of PresentationSource roots by listening to Loaded events.
        /// </summary>
        public static void EnableAutoRootTracking() {
            try {
                EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                        new RoutedEventHandler(OnAnyElementLoaded), true);
                
                // Listen for popup open events
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

        #endregion

        #region Discovery

        private static void OnAnyElementLoaded(object sender, RoutedEventArgs e) {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            
            try {
                var win = Window.GetWindow(fe);
                
                if (win != null) {
                    if (ReferenceEquals(win, fe)) {
                        var content = win.Content as FrameworkElement;
                        RegisterRoot(content ?? win);
                        return;
                    }
                    
                    if (win.Content != null && ReferenceEquals(win.Content, fe)) {
                        RegisterRoot(fe);
                        return;
                    }
                    
                    AttachSubtreeChangeHandlers(fe);
                    TryCreateNavNodeForElement(fe);
                    return;
                }
                
                var ps = PresentationSource.FromVisual(fe);
                if (ps != null) {
                    var psRoot = ps.RootVisual as FrameworkElement;
                    
                    if (psRoot != null && object.ReferenceEquals(psRoot, fe)) {
                        RegisterRoot(fe);
                        return;
                    }
                    
                    if (_presentationSourceRoots.ContainsKey(ps)) {
                        AttachSubtreeChangeHandlers(fe);
                        TryCreateNavNodeForElement(fe);
                        return;
                    }
                    
                    if (psRoot != null) {
                        RegisterRoot(psRoot);
                        return;
                    }
                }
                
                try {
                    var hasVisualParent = VisualTreeHelper.GetParent(fe) != null;
                    if (!hasVisualParent && fe.Parent == null) {
                        RegisterRoot(fe);
                    }
                } catch { }
            } catch { }
        }

        private static void TryCreateNavNodeForElement(FrameworkElement fe) {
            if (fe == null) return;
            
            try {
                if (_nodesByElement.ContainsKey(fe)) return;
                
                if (_isTrulyVisible != null && !_isTrulyVisible(fe)) return;
                
                var ps = PresentationSource.FromVisual(fe);
                if (ps == null) return;
                
                if (!_presentationSourceRoots.TryGetValue(ps, out var navTreeRoot)) {
                    return;
                }
                
                var psRoot = PresentationSource.FromVisual(navTreeRoot);
                if (!object.ReferenceEquals(ps, psRoot)) {
                    return;
                }
                
                var navNode = NavNode.CreateNavNode(fe);
                if (navNode != null) {
                    if (_nodesByElement.TryAdd(fe, navNode)) {
                        // Compute HierarchicalPath from full visual tree (for PathFilter)
                        navNode.HierarchicalPath = ComputeHierarchicalPath(fe);
                        
                        // Build Parent/Children relationships
                        LinkToParent(navNode, fe);
                        
                        if (_rootIndex.TryGetValue(navTreeRoot, out var elementSet)) {
                            lock (elementSet) {
                                elementSet.Add(fe);
                            }
                            
                            try { RootChanged?.Invoke(navTreeRoot); } catch { }
                            try { NavNodeAdded?.Invoke(navNode); } catch { }
                            
                            Debug.WriteLine($"[NavForest] Dynamic NavNode added: {fe.GetType().Name} '{navNode.SimpleName}'");

                            // Track modal state
                            TrackModalStateForNewNode(navNode);
                        }
                    }
                }
            } catch { }
        }

        /// <summary>
        /// Links a newly-created NavNode to its parent in the navigation tree.
        /// Walks up the visual tree to find the first parent NavNode and establishes bidirectional link.
        /// </summary>
        /// <param name="childNode">The newly-created NavNode to link</param>
        /// <param name="childFe">The FrameworkElement for the child node</param>
        private static void LinkToParent(NavNode childNode, FrameworkElement childFe) {
            if (childNode == null || childFe == null) return;

            try {
                DependencyObject current = childFe;
                
                // Walk up visual tree to find first parent NavNode
                while (current != null) {
                    try {
                        current = VisualTreeHelper.GetParent(current);
                    } catch {
                        break;
                    }

                    if (current is FrameworkElement parentFe && _nodesByElement.TryGetValue(parentFe, out var parentNode)) {
                        // Found parent NavNode - establish bidirectional link
                        childNode.Parent = new WeakReference<NavNode>(parentNode);
                        parentNode.Children.Add(new WeakReference<NavNode>(childNode));
                        
                        Debug.WriteLine($"[NavForest] Linked {childNode.SimpleName} -> parent: {parentNode.SimpleName}");
                        return;
                    }
                }
                
                // No parent found - this is a root node
                Debug.WriteLine($"[NavForest] Root node: {childNode.SimpleName} (no parent)");
            } catch (Exception ex) {
                Debug.WriteLine($"[NavForest] Error linking parent for {childNode.SimpleName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Unlinks a NavNode from its parent and children before removal.
        /// Cleans up bidirectional references to prevent memory leaks.
        /// </summary>
        /// <param name="node">The NavNode to unlink</param>
        private static void UnlinkNode(NavNode node) {
            if (node == null) return;

            try {
                // Remove from parent's children list
                if (node.Parent != null && node.Parent.TryGetTarget(out var parent)) {
                    parent.Children.RemoveAll(wr => {
                        if (!wr.TryGetTarget(out var child)) return true; // Dead reference
                        return ReferenceEquals(child, node);
                    });
                }

                // Clear node's parent reference
                node.Parent = null;

                // Clear node's children list (they'll update their own parents as needed)
                node.Children.Clear();
            } catch (Exception ex) {
                Debug.WriteLine($"[NavForest] Error unlinking {node.SimpleName}: {ex.Message}");
            }
        }

        private static void AttachSubtreeChangeHandlers(FrameworkElement fe) {
            if (GetAttachedHandlers(fe)) return;
            SetAttachedHandlers(fe, true);
            
            fe.Unloaded += (s, e) => SetAttachedHandlers(fe, false);
            
            try {
                if (fe is ComboBox cb) {
                    // Register popup child as separate root when dropdown opens
                    cb.DropDownOpened += (s, e) => {
                        try {
                            var popup = cb.Template?.FindName("PART_Popup", cb) as Popup;
                            if (popup?.Child is FrameworkElement child) {
                                RegisterRoot(child);
                            }
                        } catch { }
                    };
                }
            } catch { }
        }

        #endregion

        #region Root Management

        public static void RegisterRoot(FrameworkElement root) {
            if (root == null) return;
            
            try {
                var ps = PresentationSource.FromVisual(root);
                if (ps == null) return;
                
                var psRootVisual = ps.RootVisual as FrameworkElement;
                if (psRootVisual == null) return;
                
                if (!object.ReferenceEquals(root, psRootVisual)) {
                    if (_presentationSourceRoots.TryGetValue(ps, out var existingRoot)) {
                        ScheduleDebouncedSync(existingRoot);
                        return;
                    }
                    
                    root = psRootVisual;
                }
                
                var isNew = _presentationSourceRoots.TryAdd(ps, root);
                _rootIndex.GetOrAdd(root, _ => new HashSet<FrameworkElement>());
                
                try {
                    var typeName = root.GetType().Name;
                    var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                    var status = isNew ? "NEW" : "existing";
                    Debug.WriteLine($"[NavForest] RegisterRoot: {typeName} '{elementName}' ({status})");
                } catch { }
                
                if (isNew) {
                    AttachCleanupHandlers(root);
                }
                
                ScheduleDebouncedSync(root);
            } catch { }
        }

        public static void RescanSubtree(FrameworkElement subtreeRoot) {
            if (subtreeRoot == null) return;
            
            try {
                var ps = PresentationSource.FromVisual(subtreeRoot);
                if (ps == null) return;
                
                if (!_presentationSourceRoots.TryGetValue(ps, out var navTreeRoot)) {
                    RegisterRoot(subtreeRoot);
                    return;
                }
                
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
                if (_pendingSyncs.TryGetValue(root, out var pendingOp)) {
                    try {
                        if (pendingOp != null && pendingOp.Status == DispatcherOperationStatus.Pending) {
                            pendingOp.Abort();
                        }
                    } catch { }
                }
                
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
            
            if (_pendingSyncs.TryRemove(root, out var pendingOp)) {
                try {
                    if (pendingOp != null && pendingOp.Status == DispatcherOperationStatus.Pending) {
                        pendingOp.Abort();
                    }
                } catch { }
            }
            
            try {
                var ps = PresentationSource.FromVisual(root);
                if (ps != null) {
                    _presentationSourceRoots.TryRemove(ps, out var _);
                }
            } catch { }
            
            DetachCleanupHandlers(root);
            
            if (_rootIndex.TryRemove(root, out var set)) {
                foreach (var fe in set) {
                    if (_nodesByElement.TryRemove(fe, out var node)) {
                        // ? NEW: Clean up Parent/Children links
                        UnlinkNode(node);
                        
                        // Handle modal tracking for removed node
                        HandleRemovedNodeModalTracking(node);
                        
                        try { NavNodeRemoved?.Invoke(node); } catch { }
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

        #endregion

        #region Scanning

        public static void SyncRoot(FrameworkElement root) {
            if (root == null) return;
            if (!root.IsLoaded) return;

            if (!root.Dispatcher.CheckAccess()) {
                ScheduleDebouncedSync(root);
                return;
            }

            try {
                var typeName = root.GetType().Name;
                var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                Debug.WriteLine($"[NavForest] SyncRoot START: {typeName} '{elementName}'");
                
                var newElements = new HashSet<FrameworkElement>();
                
                ScanVisualTree(root, newElements);

                Debug.WriteLine($"[NavForest] SyncRoot END: {typeName} '{elementName}' - found {newElements.Count} elements");

                _rootIndex.AddOrUpdate(root, newElements, (k, old) => {
                    foreach (var oldFe in old) {
                        if (!newElements.Contains(oldFe)) {
                            if (_nodesByElement.TryRemove(oldFe, out var oldNode)) {
                                // Clean up Parent/Children links
                                UnlinkNode(oldNode);
                                
                                // Handle modal tracking for removed node
                                HandleRemovedNodeModalTracking(oldNode);
                                
                                try { NavNodeRemoved?.Invoke(oldNode); } catch { }
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

            PresentationSource psRoot = null;
            try { psRoot = PresentationSource.FromVisual(root); } catch { psRoot = null; }

            var visited = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            visited.Add(root);

            while (stack.Count > 0) {
                var node = stack.Pop();

                if (node is FrameworkElement fe) {
                    if (_isTrulyVisible != null && !_isTrulyVisible(fe)) {
                        continue;
                    }

                    PresentationSource psFe = null;
                    try {
                        psFe = PresentationSource.FromVisual(fe);
                        if (psFe == null) continue;
                        
                        bool isOwnPresentationSourceRoot = false;
                        try {
                          isOwnPresentationSourceRoot = object.ReferenceEquals(psFe.RootVisual, fe);
                        } catch { }
                        
                        if (psRoot != null && !object.ReferenceEquals(psFe, psRoot)) {
                          RegisterRoot(fe);
                          continue;
                        }
                        
                        if (isOwnPresentationSourceRoot && !object.ReferenceEquals(fe, root)) {
                          RegisterRoot(fe);
                          continue;
                        }
                    } catch { continue; }

                    var navNode = NavNode.CreateNavNode(fe);
                    if (navNode != null) {
                        var isNewNode = !_nodesByElement.ContainsKey(fe);
                        _nodesByElement.AddOrUpdate(fe, navNode, (k, old) => navNode);
                        discoveredElements.Add(fe);
                        
                        // Compute HierarchicalPath from full visual tree (for PathFilter)
                        navNode.HierarchicalPath = ComputeHierarchicalPath(fe);
                        
                        // Build Parent/Children relationships
                        LinkToParent(navNode, fe);
                        
                        if (isNewNode) {
                            try { NavNodeAdded?.Invoke(navNode); } catch { }

                            // Track modal state
                            TrackModalStateForNewNode(navNode);
                        }
                    }
                }

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

        #endregion

        #region Query API

        public static IReadOnlyCollection<NavNode> GetNavNodesForRoot(FrameworkElement root) {
            if (root == null) return new NavNode[0];
            if (!_rootIndex.TryGetValue(root, out var set)) return new NavNode[0];
            var list = new List<NavNode>();
            foreach (var fe in set) {
                if (_nodesByElement.TryGetValue(fe, out var node)) {
                    list.Add(node);
                }
            }
            return list.AsReadOnly();
        }

        public static IReadOnlyCollection<NavNode> GetAllNavNodes() {
            return _nodesByElement.Values.Distinct().ToArray();
        }

        /// <summary>
        /// Tries to get an already-discovered NavNode for a given FrameworkElement.
        /// Used by NavNode validation during creation to check for non-modal group nesting.
        /// </summary>
        /// <param name="fe">The FrameworkElement to look up</param>
        /// <param name="node">The discovered NavNode if found</param>
        /// <returns>True if a NavNode was found for this element, false otherwise</returns>
        public static bool TryGetNavNode(FrameworkElement fe, out NavNode node) {
            if (fe != null) {
                return _nodesByElement.TryGetValue(fe, out node);
            }
            node = null;
            return false;
        }

        #endregion

        #region Modal State Tracking

        /// <summary>
        /// Tracks modal state changes when a new NavNode is discovered.
        /// 
        /// For pure modal groups (Window, Popup):
        ///   - Existence = active ? emit ModalGroupOpened immediately
        /// 
        /// For dual-role modal groups (ComboBox, ContextMenu, Menu):
        ///   - Wire up WPF events (DropDownOpened/Closed, Opened/Closed, SubmenuOpened/Closed)
        ///   - Emit ModalGroupOpened/Closed based on actual WPF state changes
        /// 
        /// For leaf nodes or non-modal nodes:
        ///   - No modal tracking needed
        /// </summary>
        private static void TrackModalStateForNewNode(NavNode node) {
            if (node == null) return;

            try {
                // CASE 1: Pure modal group (Window, Popup) - existence = active
                if (node.IsModal && node.IsGroup && !node.IsDualRoleGroup) {
                    Debug.WriteLine($"[NavForest] Pure modal activated: {node.SimpleName}");
                    try { ModalGroupOpened?.Invoke(node); } catch { }
                    return;
                }

                // CASE 2: Dual-role modal group - wire up WPF events
                if (node.IsModal && node.IsGroup && node.IsDualRoleGroup) {
                    AttachDualModalEventListeners(node);
                    return;
                }

                // CASE 3: Non-modal nodes - no tracking needed

            } catch (Exception ex) {
                Debug.WriteLine($"[NavForest] Error tracking modal for {node.SimpleName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Attaches WPF event listeners to dual-role modal groups to detect open/close events.
        /// This is the ONLY reliable way to know when ComboBox/ContextMenu/Menu opens or closes.
        /// Only attaches handlers once per element to prevent memory leaks.
        /// </summary>
        private static void AttachDualModalEventListeners(NavNode node) {
            if (node == null || !node.TryGetVisual(out var fe)) return;

            try {
                // ? Only attach event handlers once per element!
                lock (_elementsWithModalHandlers) {
                    if (_elementsWithModalHandlers.Contains(fe)) {
                        Debug.WriteLine($"[NavForest] Skipping {node.SimpleName} - handlers already attached");
                        return;
                    }
                    _elementsWithModalHandlers.Add(fe);
                }

                if (fe is ComboBox comboBox) {
                    Debug.WriteLine($"[NavForest] Wiring ComboBox events: {node.SimpleName}");
                    
                    comboBox.DropDownOpened += (s, e) => {
                        Debug.WriteLine($"[NavForest] ComboBox OPENED: {node.SimpleName}");
                        try { ModalGroupOpened?.Invoke(node); } catch { }
                    };
                    
                    comboBox.DropDownClosed += (s, e) => {
                        Debug.WriteLine($"[NavForest] ComboBox CLOSED: {node.SimpleName}");
                        try { ModalGroupClosed?.Invoke(node); } catch { }
                    };
                    
                    return;
                }

                if (fe is ContextMenu contextMenu) {
                    Debug.WriteLine($"[NavForest] Wiring ContextMenu events: {node.SimpleName}");
                    
                    contextMenu.Opened += (s, e) => {
                        Debug.WriteLine($"[NavForest] ContextMenu OPENED: {node.SimpleName}");
                        try { ModalGroupOpened?.Invoke(node); } catch { }
                    };
                    
                    contextMenu.Closed += (s, e) => {
                        Debug.WriteLine($"[NavForest] ContextMenu CLOSED: {node.SimpleName}");
                        try { ModalGroupClosed?.Invoke(node); } catch { }
                    };
                    
                    return;
                }

                if (fe is Menu menu) {
                    Debug.WriteLine($"[NavForest] Wiring Menu events: {node.SimpleName}");
                    
                    // Menu uses MenuItem.SubmenuOpened/Closed events
                    foreach (MenuItem item in menu.Items.OfType<MenuItem>()) {
                        item.SubmenuOpened += (s, e) => {
                            if (ReferenceEquals(e.OriginalSource, item)) {
                                Debug.WriteLine($"[NavForest] Menu OPENED: {node.SimpleName}");
                                try { ModalGroupOpened?.Invoke(node); } catch { }
                            }
                        };
                        
                        item.SubmenuClosed += (s, e) => {
                            if (ReferenceEquals(e.OriginalSource, item)) {
                                Debug.WriteLine($"[NavForest] Menu CLOSED: {node.SimpleName}");
                                try { ModalGroupClosed?.Invoke(node); } catch { }
                            }
                        };
                    }
                    
                    return;
                }

            } catch (Exception ex) {
                Debug.WriteLine($"[NavForest] Error attaching dual-modal events for {node.SimpleName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles modal tracking when a node is removed.
        /// For pure modals, emit ModalGroupClosed event.
        /// For dual-role modals, WPF events already handle this.
        /// </summary>
        private static void HandleRemovedNodeModalTracking(NavNode node) {
            if (node == null) return;

            try {
                // If this is a pure modal being removed, emit closed event
                if (node.IsModal && node.IsGroup && !node.IsDualRoleGroup) {
                    Debug.WriteLine($"[NavForest] Pure modal CLOSED (removed): {node.SimpleName}");
                    try { ModalGroupClosed?.Invoke(node); } catch { }
                }

                // For dual-role modals:
                // - If they were open when removed, WPF Closed event will fire automatically
                // - If they were already closed, no event needed
                // - WPF event handlers are automatically cleaned up when object is GC'd

            } catch (Exception ex) {
                Debug.WriteLine($"[NavForest] Error handling removal of {node.SimpleName}: {ex.Message}");
            }
        }

        #endregion

        #region Computed HierarchicalPath

        /// <summary>
        /// Computes the HierarchicalPath by walking up the FULL visual tree to the root.
        /// Returns a string in format: "WindowName:Window > PanelName:StackPanel > ButtonName:Button"
        /// 
        /// IMPORTANT: This includes ALL FrameworkElements in the path, not just those with NavNodes.
        /// Used by PathFilter for pattern matching and as unique identifier for logging.
        /// </summary>
        private static string ComputeHierarchicalPath(FrameworkElement fe) {
            var segments = new List<string>();
            
            try {
                DependencyObject current = fe;
                
                // Walk up the ENTIRE visual tree
                while (current != null) {
                    if (current is FrameworkElement currentFe) {
                        var name = string.IsNullOrEmpty(currentFe.Name) ? "(unnamed)" : currentFe.Name;
                        var type = currentFe.GetType().Name;
                        segments.Insert(0, $"{name}:{type}");
                    }
                    
                    try {
                        current = VisualTreeHelper.GetParent(current);
                    } catch {
                        break;
                    }
                }
            } catch { }
            
            // If visual tree walk failed completely, use this element's Name:Type
            if (segments.Count == 0) {
                var name = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
                var type = fe.GetType().Name;
                segments.Add($"{name}:{type}");
            }
            
            return string.Join(" > ", segments);
        }

        #endregion
    }
}