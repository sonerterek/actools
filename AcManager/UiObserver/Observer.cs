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
    /// Observer discovers and tracks NavNodes across multiple PresentationSources.
    /// 
    /// Architecture:
    /// - NavNode: Pure data structure with type-specific behaviors (CreateNavNode factory)
    /// - Observer: Discovery engine - scans visual trees silently, builds hierarchy, emits modal events only
    /// - Navigator: Navigation logic - subscribes to modal lifecycle events, manages modal stack, handles input
    /// 
    /// Events (modal lifecycle only):
    /// - ModalGroupOpened: Modal scope opened (Window, PopupRoot)
    /// - ModalGroupClosed: Modal scope closed
    /// 
    /// Note: Individual node discovery is SILENT (no events fired per node).
    /// Navigator reacts only to modal lifecycle changes, which provides complete
    /// information about all nodes in the modal scope.
    /// </summary>
    internal static class Observer {
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

        #endregion

        #region Events

        /// <summary>
        /// Fired when a modal scope opens (Window, PopupRoot).
        /// Navigator should push to modal context stack and initialize focus.
        /// All nodes within the modal scope are already discovered when this event fires.
        /// </summary>
        public static event Action<NavNode> ModalGroupOpened;

        /// <summary>
        /// Fired when a modal scope closes.
        /// Navigator should pop from modal context stack and restore previous focus.
        /// </summary>
        public static event Action<NavNode> ModalGroupClosed;

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
                "AttachedHandlers", typeof(bool), typeof(Observer), new PropertyMetadata(false));

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
                        // Build Parent/Children relationships (visual tree with PlacementTarget bridging)
                        LinkToParent(navNode, fe);
                        
                        if (_rootIndex.TryGetValue(navTreeRoot, out var elementSet)) {
                            lock (elementSet) {
                                elementSet.Add(fe);
                            }
                            
                            // Enhanced debug output for Popup elements
                            if (fe is Popup popupElement) {
                                var placementTargetInfo = popupElement.PlacementTarget != null
                                    ? $"{popupElement.PlacementTarget.GetType().Name} '{(string.IsNullOrEmpty((popupElement.PlacementTarget as FrameworkElement)?.Name) ? "(unnamed)" : (popupElement.PlacementTarget as FrameworkElement)?.Name)}'"
                                    : "NULL";
                                Debug.WriteLine($"[Observer] Dynamic NavNode added: Popup '{navNode.SimpleName}' with PlacementTarget={placementTargetInfo}");
                            } else {
                                Debug.WriteLine($"[Observer] Dynamic NavNode added: {fe.GetType().Name} '{navNode.SimpleName}'");
                            }

                            // Fire modal event AFTER all setup is complete (consistent with bulk scan)
                            if (navNode.IsModal && navNode.IsGroup) {
                                Debug.WriteLine($"[Observer] Pure modal activated (dynamic): {navNode.SimpleName}");
                                try { ModalGroupOpened?.Invoke(navNode); } catch { }
                            }
                        }
                    }
                }
            } catch { }
        }

        /// <summary>
        /// Placeholder for attaching Unloaded/LayoutUpdated handlers for dynamic element tracking.
        /// Currently not needed as full tree rescans handle dynamic changes.
        /// </summary>
        private static void AttachSubtreeChangeHandlers(FrameworkElement fe) {
            // Placeholder - dynamic changes are handled by periodic rescans via LayoutUpdated
            // Could be extended in future to track specific element unload events
        }

        /// <summary>
        /// Links a newly-created NavNode to its parent in the navigation tree.
        /// Walks up the visual tree to find the first parent NavNode and establishes bidirectional link.
        /// Also handles Popup boundaries by using PlacementTarget to bridge the visual tree gap.
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
                        
                        Debug.WriteLine($"[Observer] Linked {childNode.SimpleName} -> parent: {parentNode.SimpleName}");
                        return;
                    }
                    
                    // ? NEW: If we hit a Popup, jump across the boundary using PlacementTarget
                    if (current is Popup popup && popup.PlacementTarget is FrameworkElement placementTarget) {
                        if (_nodesByElement.TryGetValue(placementTarget, out var ownerNode)) {
                            // Found owner across Popup boundary - establish visual tree link
                            childNode.Parent = new WeakReference<NavNode>(ownerNode);
                            ownerNode.Children.Add(new WeakReference<NavNode>(childNode));
                            
                            Debug.WriteLine($"[Observer] Linked {childNode.SimpleName} -> parent (via Popup): {ownerNode.SimpleName}");
                            return;
                        } else {
                            // Owner not yet discovered, continue walking up from PlacementTarget
                            current = placementTarget;
                            Debug.WriteLine($"[Observer] Bridged Popup boundary for {childNode.SimpleName}, continuing from {placementTarget.GetType().Name}");
                            continue;
                        }
                    }
                }
                
                // No parent found - this is a root node
                Debug.WriteLine($"[Observer] Root node: {childNode.SimpleName} (no parent)");
            } catch (Exception ex) {
                Debug.WriteLine($"[Observer] Error linking parent for {childNode.SimpleName}: {ex.Message}");
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
                Debug.WriteLine($"[Observer] Error unlinking {node.SimpleName}: {ex.Message}");
            }
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
                    Debug.WriteLine($"[Observer] RegisterRoot: {typeName} '{elementName}' ({status})");
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
                        // Clean up Parent/Children links
                        UnlinkNode(node);
                        
                        // Handle modal tracking for removed node (fires ModalGroupClosed if modal)
                        HandleRemovedNodeModalTracking(node);
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

        public static void SyncRoot(FrameworkElement root = null) {
            if (root == null) return;

            if (!root.Dispatcher.CheckAccess()) {
                ScheduleDebouncedSync(root);
                return;
            }

            try {
                var typeName = root.GetType().Name;
                var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
                Debug.WriteLine($"[Observer] SyncRoot START: {typeName} '{elementName}'");
                
                var newElements = new HashSet<FrameworkElement>();
                
                ScanVisualTree(root, newElements);

                Debug.WriteLine($"[Observer] SyncRoot END: {typeName} '{elementName}' - found {newElements.Count} elements");

                _rootIndex.AddOrUpdate(root, newElements, (k, old) => {
                    foreach (var oldFe in old) {
                        if (!newElements.Contains(oldFe)) {
                            if (_nodesByElement.TryRemove(oldFe, out var oldNode)) {
                                // Clean up Parent/Children links
                                UnlinkNode(oldNode);
                                
                                // Handle modal tracking for removed node (fires ModalGroupClosed if modal)
                                HandleRemovedNodeModalTracking(oldNode);
                            }
                        }
                    }
                    return newElements;
                });
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
            
            // Collect modals discovered during scan - fire events AFTER scan completes
            var discoveredModals = new List<NavNode>();

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
                        
                        // Build Parent/Children relationships (visual tree with PlacementTarget bridging)
                        LinkToParent(navNode, fe);
                        
                        if (isNewNode) {
                            // Enhanced debug output for Popup elements
                            if (fe is Popup popupElement) {
                                var placementTargetInfo = popupElement.PlacementTarget != null
                                    ? $"{popupElement.PlacementTarget.GetType().Name} '{(string.IsNullOrEmpty((popupElement.PlacementTarget as FrameworkElement)?.Name) ? "(unnamed)" : (popupElement.PlacementTarget as FrameworkElement)?.Name)}'"
                                    : "NULL";
                                Debug.WriteLine($"[Observer] NavNode discovered: Popup '{navNode.SimpleName}' with PlacementTarget={placementTargetInfo}");
                            }

                            // Collect modals for later event firing (after scan completes)
                            if (navNode.IsModal && navNode.IsGroup) {
                                discoveredModals.Add(navNode);
                            }
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
            
            // Scan complete - now fire modal events with complete information available
            foreach (var modalNode in discoveredModals) {
                Debug.WriteLine($"[Observer] Pure modal activated (after scan): {modalNode.SimpleName}");
                try { ModalGroupOpened?.Invoke(modalNode); } catch { }
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
        /// Handles modal tracking when a node is removed.
        /// For pure modals, emit ModalGroupClosed event.
        /// </summary>
        private static void HandleRemovedNodeModalTracking(NavNode node) {
            if (node == null) return;

            try {
                // If this is a pure modal being removed, emit closed event
                if (node.IsModal && node.IsGroup) {
                    Debug.WriteLine($"[Observer] Pure modal CLOSED (removed): {node.SimpleName}");
                    try { ModalGroupClosed?.Invoke(node); } catch { }
                }

            } catch (Exception ex) {
                Debug.WriteLine($"[Observer] Error handling removal of {node.SimpleName}: {ex.Message}");
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