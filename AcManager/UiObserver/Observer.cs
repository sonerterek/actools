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

        /// <summary>
        /// Fired when a window's layout changes (position, size, loaded state).
        /// Navigator subscribes to this to update the overlay position.
        /// Separates discovery concerns (Observer) from visual feedback (Navigator).
        /// </summary>
        public static event EventHandler WindowLayoutChanged;

        #endregion

        #region Initialize

        /// <summary>
        /// Initialize Observer and hook all existing application windows.
        /// This is the main external entry point called by Navigator.
        /// </summary>
        internal static void Initialize() {
			try {
				// Register global loaded event handler
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
				
				// Hook all existing windows (moved from Navigator!)
				if (Application.Current != null) {
					Application.Current.Dispatcher.BeginInvoke(new Action(() => {
						try {
							foreach (Window w in Application.Current.Windows) {
								HookExistingWindow(w);
							}
						} catch { }
					}), DispatcherPriority.ApplicationIdle);
				}
			} catch { }
		}

		/// <summary>
		/// Hook layout events for an existing window to keep discovery and overlay synchronized.
		/// Called during initialization for pre-existing windows.
		/// </summary>
		private static void HookExistingWindow(Window w) {
			if (w == null) return;
			
			try {
				// Register root for discovery
				var content = w.Content as FrameworkElement;
				if (content != null) {
					RegisterRoot(content);
				}
				
				// Hook layout events to fire WindowLayoutChanged for Navigator's overlay
				w.Loaded += OnWindowLayoutChanged;
				w.LayoutUpdated += OnWindowLayoutChanged;
				w.LocationChanged += OnWindowLayoutChanged;
				w.SizeChanged += OnWindowLayoutChanged;
			} catch { }
		}

		/// <summary>
		/// Window layout changed - notify subscribers (Navigator will update overlay).
		/// Also re-registers root to handle dynamic content changes.
		/// </summary>
		private static void OnWindowLayoutChanged(object sender, EventArgs e) {
			try {
				// Re-register root to handle dynamic content changes
				if (sender is Window w) {
					var content = w.Content as FrameworkElement;
					if (content != null) {
						RegisterRoot(content);
					}
				}
				
				// Notify subscribers (Navigator's overlay)
				WindowLayoutChanged?.Invoke(sender, e);
			} catch { }
		}

        #endregion

        #region Discovery

        private static void OnAnyElementLoaded(object sender, RoutedEventArgs e) {
            if (!(sender is FrameworkElement fe)) return;
            
            try {
                // 1. Check if element is a Window or Window.Content ? RegisterRoot
                var win = Window.GetWindow(fe);
                if (win != null && (ReferenceEquals(win, fe) || ReferenceEquals(win.Content, fe))) {
                    RegisterRoot(win.Content as FrameworkElement ?? win);
                    return;
                }
                
                // 2. Check if element is a PresentationSource root ? RegisterRoot
                var ps = PresentationSource.FromVisual(fe);
                if (ps != null) {
                    var psRoot = ps.RootVisual as FrameworkElement;
                    
                    if (psRoot != null && ReferenceEquals(psRoot, fe)) {
                        RegisterRoot(fe);
                        return;
                    }
                    
                    // 3. Element is inside a tracked root ? Try create node
                    if (_presentationSourceRoots.ContainsKey(ps)) {
                        TryCreateNavNodeForElement(fe);
                        return;
                    }
                    
                    // 4. Element's PresentationSource root not tracked yet ? Register it
                    if (psRoot != null) {
                        RegisterRoot(psRoot);
                        return;
                    }
                }
                
                // 5. Orphan element with no visual parent ? RegisterRoot as fallback
                try {
                    if (VisualTreeHelper.GetParent(fe) == null && fe.Parent == null) {
                        RegisterRoot(fe);
                    }
                } catch { }
            } catch { }
        }

        private static void TryCreateNavNodeForElement(FrameworkElement fe) {
            if (fe == null) return;
            
            try {
                if (_nodesByElement.ContainsKey(fe)) return;
                
                if (!IsTrulyVisible(fe)) return;
                
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

                            // ? REMOVED: Don't fire modal event for dynamically added nodes
                            // Modal events should only fire during bulk SyncRoot() after all children are linked
                            // If this is a problem for dynamically opened popups, we can schedule a re-sync instead
                        }
                    }
                }
            } catch { }
        }

        /// <summary>
        /// Fires ModalGroupOpened event if the node is a newly-discovered modal.
        /// Centralized logic to avoid duplication between dynamic and bulk discovery.
        /// </summary>
        private static void FireModalEventIfNeeded(NavNode node, bool isNewNode) {
            if (!isNewNode || !node.IsModal || !node.IsGroup) return;
            
            Debug.WriteLine($"[Observer] Modal opened: {node.SimpleName}");
            try { ModalGroupOpened?.Invoke(node); } catch { }
        }

        #endregion

        #region Hierarchy Management

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
                    
                    // If we hit a Popup, jump across the boundary using PlacementTarget
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

        private static void AttachCleanupHandlers(FrameworkElement root) {
            if (root == null) return;
            
            root.Unloaded += OnRootClosed;
            
            if (root is Window win) {
                win.Closed += OnRootClosed;
            } else if (root is Popup popup) {
                popup.Closed += OnRootClosed;
            } else if (root is ContextMenu cm) {
                cm.Closed += OnRootClosed;
            }
        }

        /// <summary>
        /// Generic handler for root closure events (Unloaded, Window.Closed, Popup.Closed, ContextMenu.Closed).
        /// Consolidates 4 identical event handlers into one.
        /// </summary>
        private static void OnRootClosed(object sender, EventArgs e) {
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
                root.Unloaded -= OnRootClosed;
            } catch { }
            
            try {
                if (root is Window win) {
                    win.Closed -= OnRootClosed;
                }
            } catch { }
            
            try {
                if (root is Popup popup) {
                    popup.Closed -= OnRootClosed;
                }
            } catch { }
            
            try {
                if (root is ContextMenu cm) {
                    cm.Closed -= OnRootClosed;
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
                var newModalNodes = new List<NavNode>(); // Track new modal nodes discovered during scan
                
                ScanVisualTree(root, newElements, newModalNodes);

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
                
                // ? FIX: Fire modal events AFTER all nodes are linked
                // This ensures Navigator sees complete Parent/Children relationships when TryInitializeFocusIfNeeded() runs
                foreach (var modalNode in newModalNodes) {
                    Debug.WriteLine($"[Observer] Modal opened: {modalNode.SimpleName}");
                    try { ModalGroupOpened?.Invoke(modalNode); } catch { }
                }
            } catch { }
        }
        
        private static void ScanVisualTree(FrameworkElement root, HashSet<FrameworkElement> discoveredElements, List<NavNode> newModalNodes) {
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
                    if (!IsTrulyVisible(fe)) {
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

                            // ? CHANGED: Don't fire modal event immediately - collect it for later
                            // This allows all child nodes to be discovered and linked first
                            if (navNode.IsModal && navNode.IsGroup) {
                                newModalNodes.Add(navNode);
                            }
                        }
                    }
                }

                int visualCount = 0;
                try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { visualCount = 0; }
                for (int i = 0; i < visualCount; i++) {
                    try {
                        var child = VisualTreeHelper.GetChild(node, i);
                        if (child != null && !visited.Contains(child)) {
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

		#region Helpers

		private static bool IsTrulyVisible(FrameworkElement fe)
		{
			if (fe == null) return false;
			DependencyObject cur = fe;
			while (cur != null) {
				if (cur is UIElement ui && ui.Visibility != Visibility.Visible) return false;
				try { cur = VisualTreeHelper.GetParent(cur); } catch { break; }
			}
			return fe.IsVisible && fe.IsLoaded;
		}

		#endregion

		#region Query API

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
    }
}