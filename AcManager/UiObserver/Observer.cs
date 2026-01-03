using AcManager.UiObserver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Observer discovers and tracks NavNodes across multiple PresentationSources.
	/// 
	/// SIMPLIFIED ARCHITECTURE (LayoutUpdated-based discovery):
	/// - All discovery happens through ScanVisualTree bulk scanning triggered by:
	///   * Popup open events (new presentation sources)
	///   * RegisterRoot → SyncRoot (debounced)
	///   * Layout changes in windows → RegisterRoot (debounced)
	/// 
	/// Events (modal lifecycle only):
	/// - ModalGroupOpened: Modal scope opened (Window, PopupRoot)
	/// - ModalGroupClosed: Modal scope closed
	/// - NodesUpdated: Batch notification of added/removed nodes
	/// </summary>
	internal static class Observer
	{
		#region Debug Configuration

		private static bool _verboseDebug = false;  

		public static bool VerboseDebug {
			get => _verboseDebug;
			set {
				_verboseDebug = value;
				NavNode.VerboseDebug = value;
			}
		}

		public static void ToggleVerboseDebug()
		{
			VerboseDebug = !VerboseDebug;
			Debug.WriteLine($"\n========== Observer Verbose Debug: {(VerboseDebug ? "ENABLED" : "DISABLED")} ==========");
			Debug.WriteLine("Press Ctrl+Shift+F9 to toggle");
			Debug.WriteLine("=============================================================\n");
		}

		#endregion

		#region Indexes

		private static readonly ConcurrentDictionary<FrameworkElement, NavNode> _nodesByElement = new ConcurrentDictionary<FrameworkElement, NavNode>();
		private static readonly ConcurrentDictionary<PresentationSource, FrameworkElement> _presentationSourceRoots =
			new ConcurrentDictionary<PresentationSource, FrameworkElement>();
		private static readonly ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>> _rootIndex =
			new ConcurrentDictionary<FrameworkElement, HashSet<FrameworkElement>>();
		private static readonly ConcurrentDictionary<FrameworkElement, DispatcherOperation> _pendingSyncs =
			new ConcurrentDictionary<FrameworkElement, DispatcherOperation>();

		#endregion

		#region Configuration
		
		private static NavConfiguration _navConfig;
		
		#endregion

		#region Events

		public static event Action<NavNode> ModalGroupOpened;
		public static event Action<NavNode> ModalGroupClosed;
		public static event EventHandler WindowLayoutChanged;
		// Batch update event fired once at end of SyncRoot
		internal static event Action<NavNode[], NavNode[]> NodesUpdated;

		#endregion

		#region Initialize

		internal static void Initialize(NavConfiguration navConfig)
		{
			_navConfig = navConfig ?? throw new ArgumentNullException(nameof(navConfig));
			
			try {
				// Register handler for Window.Loaded ONLY (not all FrameworkElements)
				EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
						new RoutedEventHandler(OnWindowLoaded), true);

				// Hook all existing windows
				if (Application.Current != null) {
					Application.Current.Dispatcher.BeginInvoke(new Action(() => {
						try {
							foreach (Window w in Application.Current.Windows) {
								HookExistingWindow(w);
							}
						} catch { }
					}), DispatcherPriority.Background);
				}
			} catch { }
		}

		private static void OnWindowLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is Window w) {
				if (_verboseDebug) {
					Debug.WriteLine($"[Observer] Window loaded: {w.GetType().Name}");
				}

				HookExistingWindow(w);
			}
		}

		private static void HookExistingWindow(Window w)
		{
			if (w == null) return;

			try {
				// Register the Window itself as the root (not Content)
				RegisterRoot(w);

				// Hook layout events
				w.Loaded += OnWindowLayoutChanged;
				w.LayoutUpdated += OnWindowLayoutChanged;
				w.LocationChanged += OnWindowLayoutChanged;
				w.SizeChanged += OnWindowLayoutChanged;
			} catch { }

			// Periodically rescan to catch dynamic content changes (tab switches, etc.)
			var timer = new DispatcherTimer(
				TimeSpan.FromSeconds(1),
				DispatcherPriority.Background,
				(s, e) => {
					try {
						RegisterRoot(w);
					} catch { }
				},
				w.Dispatcher
			);
			timer.Start();

			w.Closed += (s, e) => {
				try {
					timer.Stop();
				} catch { }
			};
		}

		private static void OnWindowLayoutChanged(object sender, EventArgs e)
		{
			try {
				// Re-register the Window itself (not Content)
				if (sender is Window w) {
					RegisterRoot(w);
				}

				// Notify subscribers (Navigator's overlay)
				WindowLayoutChanged?.Invoke(sender, e);
			} catch { }
		}

		#endregion

		#region Parent/Child Linking

		private static void LinkToParentOptimized(NavNode childNode, FrameworkElement childFe, NavNode parentNavNode)
		{
			if (childNode == null || childFe == null) return;

			try {
				if (parentNavNode != null) {
					childNode.Parent = new WeakReference<NavNode>(parentNavNode);
					parentNavNode.Children.Add(new WeakReference<NavNode>(childNode));

					if (_verboseDebug) {
						Debug.WriteLine($"[Observer] Linked {childNode.SimpleName} -> parent: {parentNavNode.SimpleName}");
					}
					return;
				}

				// No parent from stack - this is a root node
				Debug.WriteLine($"[Observer] Root node: {childNode.SimpleName} (no parent)");
			} catch (Exception ex) {
				Debug.WriteLine($"[Observer] Error linking parent for {childNode.SimpleName}: {ex.Message}");
			}
		}

		private static void UnlinkNode(NavNode node)
		{
			if (node == null) return;

			try {
				if (node.Parent != null && node.Parent.TryGetTarget(out var parent)) {
					parent.Children.RemoveAll(wr => {
						if (!wr.TryGetTarget(out var child)) return true;
						return ReferenceEquals(child, node);
					});
				}

				node.Parent = null;
				node.Children.Clear();
			} catch (Exception ex) {
				Debug.WriteLine($"[Observer] Error unlinking {node.SimpleName}: {ex.Message}");
			}
		}

		#endregion

		#region Root Management

		public static void RegisterRoot(FrameworkElement root)
		{
			if (root == null) return;

			if (root is Window && ReferenceEquals(root, Navigator._overlay)) {
				Debug.WriteLine("[Observer] Skipping HighlightOverlay (static reference check)");
				return;
			}

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

		private static void AttachCleanupHandlers(FrameworkElement root)
		{
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

		private static void OnRootClosed(object sender, EventArgs e)
		{
			if (sender is FrameworkElement root) {
				UnregisterRoot(root);
			}
		}

		private static void ScheduleDebouncedSync(FrameworkElement root)
		{
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

		public static void UnregisterRoot(FrameworkElement root)
		{
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
						UnlinkNode(node);
						HandleRemovedNodeModalTracking(node);
					}
				}
			}
		}

		private static void DetachCleanupHandlers(FrameworkElement root)
		{
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

		public static void SyncRoot(FrameworkElement root = null)
		{
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
				var newModalNodes = new List<NavNode>();
				var addedNodes = new List<NavNode>();

				ScanVisualTree(root, newElements, newModalNodes, addedNodes);

				Debug.WriteLine($"[Observer] SyncRoot END: {typeName} '{elementName}' - found {newElements.Count} elements");

				var removedNodes = new List<NavNode>();

				_rootIndex.AddOrUpdate(root, newElements, (k, old) => {
					foreach (var oldFe in old) {
						if (!newElements.Contains(oldFe)) {
							if (_nodesByElement.TryRemove(oldFe, out var oldNode)) {
								removedNodes.Add(oldNode);
								UnlinkNode(oldNode);
								HandleRemovedNodeModalTracking(oldNode);
							}
						}
					}
					return newElements;
				});

				foreach (var modalNode in newModalNodes) {
					Debug.WriteLine($"[Observer] Modal opened: {modalNode.SimpleName}");
					try { ModalGroupOpened?.Invoke(modalNode); } catch { }
				}

				// Fire batch update event if there were any changes
				if (addedNodes.Count > 0 || removedNodes.Count > 0) {
					Debug.WriteLine($"[Observer] Firing NodesUpdated: {addedNodes.Count} added, {removedNodes.Count} removed");
					try {
						NodesUpdated?.Invoke(addedNodes.ToArray(), removedNodes.ToArray());
					} catch (Exception ex) {
						if (_verboseDebug) {
							Debug.WriteLine($"[Observer] NodesUpdated event handler threw exception: {ex.Message}");
						}
					}
				}
			} catch { }
		}

		// Struct to hold scanning context (.NET 4.5.2 compatible)
		struct ScanContext
		{
			public DependencyObject Element;
			public NavNode ParentNode;
			public bool ParentVisible;

			public ScanContext(DependencyObject element, NavNode parentNode, bool parentVisible)
			{
				Element = element;
				ParentNode = parentNode;
				ParentVisible = parentVisible;
			}
		}

		private static void ScanVisualTree(FrameworkElement root, HashSet<FrameworkElement> discoveredElements, List<NavNode> newModalNodes, List<NavNode> addedNodes)
		{
			if (root == null) return;

			// Get context ONCE for entire tree
			PresentationSource psRoot = null;
			try { psRoot = PresentationSource.FromVisual(root); } catch { }

			Window rootWindow = root as Window ?? Window.GetWindow(root);
			bool hasOverlay = Navigator._overlay != null;

			var visited = new HashSet<DependencyObject>();
			var stack = new Stack<ScanContext>();

			// Check root visibility once
			bool rootVisible = root.Visibility == Visibility.Visible && root.IsVisible && root.IsLoaded;
			stack.Push(new ScanContext(root, null, rootVisible));
			visited.Add(root);

			while (stack.Count > 0) {
				var context = stack.Pop();
				var node = context.Element;
				var parentNavNode = context.ParentNode;
				var parentVisible = context.ParentVisible;

				if (node is FrameworkElement fe) {
					// Cheap reference checks
					if (ReferenceEquals(fe, Navigator._overlay)) {
						continue;
					}

					if (hasOverlay && ReferenceEquals(rootWindow, Navigator._overlay)) {
						continue;
					}

					// Check if already tracked (skip expensive operations)
					bool alreadyTracked = _nodesByElement.TryGetValue(fe, out var existingNode);
					if (alreadyTracked) {
						discoveredElements.Add(fe);

						// Check THIS element's visibility (not walking up!)
						bool thisVisible = parentVisible && fe.Visibility == Visibility.Visible;

						// Push children with current visibility state
						int childCount = 0;
						try { childCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
						for (int i = 0; i < childCount; i++) {
							try {
								var child = VisualTreeHelper.GetChild(node, i);
								if (child != null && !visited.Contains(child)) {
									visited.Add(child);
									stack.Push(new ScanContext(child, existingNode, thisVisible));
								}
							} catch { }
						}
						continue;
					}

					// For NEW elements: cheap visibility check (no tree walk!)
					bool isVisible = parentVisible &&
								 fe.Visibility == Visibility.Visible &&
								 fe.IsVisible &&
								 fe.IsLoaded;

					if (!isVisible) {
						// Still traverse children (they might become visible later)
						int childCount = 0;
						try { childCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
						for (int i = 0; i < childCount; i++) {
							try {
								var child = VisualTreeHelper.GetChild(node, i);
								if (child != null && !visited.Contains(child)) {
									visited.Add(child);
									stack.Push(new ScanContext(child, parentNavNode, false));
								}
							} catch { }
						}
						continue;
					}

					// Check PresentationSource (might be different for Popups)
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

					// ✅ STEP 1: Build hierarchical path (always needed for checking)
					var hierarchicalPath = NavNode.GetHierarchicalPath(fe);
					var pathWithoutHwnd = StripHwndFromPath(hierarchicalPath);
					
					if (_verboseDebug)
					{
						Debug.WriteLine($"[Observer] Evaluating: {pathWithoutHwnd}");
					}
					
					// ✅ STEP 2: Check classification FIRST (highest priority)
					var classification = _navConfig?.GetClassification(pathWithoutHwnd);
				
					// ✅ STEP 3: Create node based on priority
					NavNode navNode = null;
					
					if (classification != null) {
						// PRIORITY 1: Classification exists → force create (ignore type/exclusions)
						navNode = NavNode.ForceCreateNavNode(fe, hierarchicalPath);
						
						Debug.WriteLine($"[Observer] ✅ Created via CLASSIFY rule: {navNode.SimpleName}");
					} else {
						// PRIORITY 2: No classification → use type-based rules
						navNode = NavNode.TryCreateNavNode(fe, hierarchicalPath, _navConfig);
						
						if (navNode != null) {
							Debug.WriteLine($"[Observer] ✅ Created via type rules: {navNode.SimpleName}");
						} else if (_verboseDebug) {
							Debug.WriteLine($"[Observer] ⊘ Skipped (type/exclusion)");
						}
					}
					
					// ✅ STEP 4: If node was created, apply classification properties & link
					if (navNode != null) {
						_nodesByElement.AddOrUpdate(fe, navNode, (k, old) => navNode);
						discoveredElements.Add(fe);
						
						// Apply classification properties (if found)
						if (classification != null) {
							if (!string.IsNullOrEmpty(classification.PageName)) {
								navNode.PageName = classification.PageName;
								if (_verboseDebug) {
									Debug.WriteLine($"[Observer]   → PageName: '{classification.PageName}'");
								}
							}
							if (!string.IsNullOrEmpty(classification.KeyName)) {
								navNode.KeyName = classification.KeyName;
								if (_verboseDebug) {
									Debug.WriteLine($"[Observer]   → KeyName: '{classification.KeyName}'");
								}
							}
							if (!string.IsNullOrEmpty(classification.KeyTitle)) {
								navNode.KeyTitle = classification.KeyTitle;
							}
							if (!string.IsNullOrEmpty(classification.KeyIcon)) {
								navNode.KeyIcon = classification.KeyIcon;
							}
							if (classification.NoAutoClick) {
								navNode.NoAutoClick = classification.NoAutoClick;
							}
							if (classification.TargetType != default(ShortcutTargetType)) {
								navNode.TargetType = classification.TargetType;
							}
							if (classification.RequireConfirmation) {
								navNode.RequireConfirmation = classification.RequireConfirmation;
							}
							if (!string.IsNullOrEmpty(classification.ConfirmationMessage)) {
								navNode.ConfirmationMessage = classification.ConfirmationMessage;
							}
						}

						// Link to parent
						LinkToParentOptimized(navNode, fe, parentNavNode);

						// Debug logging
						if (fe is Popup popupElement) {
							var placementTargetInfo = popupElement.PlacementTarget != null
								? $"{popupElement.PlacementTarget.GetType().Name} '{(string.IsNullOrEmpty((popupElement.PlacementTarget as FrameworkElement)?.Name) ? "(unnamed)" : (popupElement.PlacementTarget as FrameworkElement)?.Name)}'"
								: "NULL";
							Debug.WriteLine($"[Observer] NavNode discovered: Popup '{navNode.SimpleName}' with PlacementTarget={placementTargetInfo}");
						} else {
							Debug.WriteLine($"[Observer] NavNode discovered: {fe.GetType().Name} '{navNode.SimpleName}'");
						}

						// Track as newly added
						addedNodes.Add(navNode);

						if (navNode.IsModal && navNode.IsGroup) {
							newModalNodes.Add(navNode);
						}

						// Push children with updated visibility
						int childCount = 0;
						try { childCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
						for (int i = 0; i < childCount; i++) {
							try {
								var child = VisualTreeHelper.GetChild(node, i);
								if (child != null && !visited.Contains(child)) {
									visited.Add(child);
									stack.Push(new ScanContext(child, navNode, isVisible));
								}
							} catch { }
						}
						continue;
					}
				}

				// Traverse children for non-FrameworkElements
				int visualCount = 0;
				try { visualCount = VisualTreeHelper.GetChildrenCount(node); } catch { }
				for (int i = 0; i < visualCount; i++) {
					try {
						var child = VisualTreeHelper.GetChild(node, i);
						if (child != null && !visited.Contains(child)) {
							visited.Add(child);
							stack.Push(new ScanContext(child, parentNavNode, parentVisible));
						}
					} catch { }
				}
			}
		}

		/// <summary>
		/// Strips :HWND components from hierarchical path for pattern matching.
		/// 
		/// Example:
		///   Input: "(unnamed):PopupRoot:3F4A21B > (unnamed):MenuItem"
		///   Output: "(unnamed):PopupRoot > (unnamed):MenuItem"
		/// 
		/// This is needed because classification rules don't include HWND values,
		/// but node paths include them for uniqueness.
		/// </summary>
		private static string StripHwndFromPath(string path)
		{
			if (string.IsNullOrEmpty(path)) return path;

			// Split path into segments
			var segments = path.Split(new[] { " > " }, StringSplitOptions.None);
			
			// Strip HWND from each segment (format: Name:Type:HWND → Name:Type)
			for (int i = 0; i < segments.Length; i++) {
				var parts = segments[i].Split(':');
				if (parts.Length >= 3 && !segments[i].Contains("[")) {
					// Has HWND component (and no content) - keep only Name:Type
					segments[i] = $"{parts[0]}:{parts[1]}";
				}
			}

			return string.Join(" > ", segments);
		}

		/// <summary>
		/// Gets the hierarchical path without HWND components.
		/// DEPRECATED: Use StripHwndFromPath() instead.
		/// </summary>
		private static string GetPathWithoutHwnd(NavNode node)
		{
			if (node == null) return null;

			var path = node.HierarchicalPath;
			if (string.IsNullOrEmpty(path)) return path;

			// Split path into segments
			var segments = path.Split(new[] { " > " }, StringSplitOptions.None);
			
			// Strip HWND from each segment (format: Name:Type:HWND → Name:Type)
			for (int i = 0; i < segments.Length; i++) {
				var parts = segments[i].Split(':');
				if (parts.Length >= 3) {
					// Has HWND component - keep only Name:Type
					segments[i] = $"{parts[0]}:{parts[1]}";
				}
			}

			return string.Join(" > ", segments);
		}

		#endregion

		#region Query API

		public static IReadOnlyCollection<NavNode> GetAllNavNodes()
		{
			return _nodesByElement.Values.Distinct().ToArray();
		}

		public static bool TryGetNavNode(FrameworkElement fe, out NavNode node)
		{
			if (fe != null) {
				return _nodesByElement.TryGetValue(fe, out node);
			}
			node = null;
			return false;
		}

		#endregion

		#region Modal State Tracking

		private static void HandleRemovedNodeModalTracking(NavNode node)
		{
			if (node == null) return;

			try {
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