using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Manages discovery and tracking of NavNodes for a single PresentationSource root.
	/// Handles rate-limited scanning with guaranteed final scan after layout updates.
	/// 
	/// Architecture:
	/// - Encapsulates all root-specific state (discovered elements, nodes, scan timing)
	/// - Self-contained lifecycle (IDisposable pattern for cleanup)
	/// - Rate limiting: Max 1 scan per 100ms with guaranteed final scan
	/// - Thread-safe: Uses lock for scan scheduling, callbacks on UI thread
	/// </summary>
	internal sealed class NavRoot : IDisposable
	{
		#region Private Fields
		
		private readonly FrameworkElement _root;
		private readonly string _rootSimpleName;
		private readonly NavConfiguration _navConfig;
		private readonly Action<NavNode[], NavNode[]> _nodesUpdatedCallback;
		
		// Discovered elements for this root
		private HashSet<FrameworkElement> _discoveredElements = new HashSet<FrameworkElement>();
		
		// Nodes tracked by this root
		private readonly Dictionary<FrameworkElement, NavNode> _nodesByElement = new Dictionary<FrameworkElement, NavNode>();
		
		// Rate limiting state
		private DateTime _lastScanTime = DateTime.MinValue;
		private DispatcherTimer _pendingFinalScan = null;
		private readonly object _scanLock = new object();
		
		// Constants
		private const int SCAN_RATE_LIMIT_MS = 500;
		
		private bool _disposed = false;
		
		#endregion
		
		#region Constructor & Disposal
	
		/// <summary>
		/// NavRoot contructor.  
		/// This must only be called from RegisterRoot in Observer.
		/// </summary>
		/// <param name="root"></param>
		/// <param name="simpleName"></param>
		/// <param name="navConfig"></param>
		/// <param name="nodesUpdatedCallback"></param>
		/// <exception cref="ArgumentNullException"></exception>
		internal NavRoot(FrameworkElement root, string simpleName, NavConfiguration navConfig, Action<NavNode[], NavNode[]> nodesUpdatedCallback)
		{
			Debug.WriteLine($"[NavRoot] NavRoot contructor begin for {simpleName}");
			_root = root ?? throw new ArgumentNullException(nameof(root));
			_rootSimpleName = simpleName;
			_navConfig = navConfig ?? throw new ArgumentNullException(nameof(navConfig));
			_nodesUpdatedCallback = nodesUpdatedCallback;
			
			// Attach event handlers
			_root.Unloaded += OnRootUnloaded;
			_root.LayoutUpdated += OnLayoutUpdated;
			
			if (_root is Window win) {
				win.Closed += OnRootClosed;
				win.Loaded += OnWindowLoaded;
				win.LocationChanged += OnWindowLayoutChanged;
				win.SizeChanged += OnWindowLayoutChanged;
			} else if (_root is Popup popup) {
				popup.Closed += OnRootClosed;
			} else if (_root is ContextMenu cm) {
				cm.Closed += OnRootClosed;
			}
			
			Debug.WriteLine($"[NavRoot] NavRoot contructor completed for {simpleName}");
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			
			// Detach event handlers
			try { _root.Unloaded -= OnRootUnloaded; } catch { }
			try { _root.LayoutUpdated -= OnLayoutUpdated; } catch { }
			
			if (_root is Window win) {
				try { win.Closed -= OnRootClosed; } catch { }
				try { win.Loaded -= OnWindowLoaded; } catch { }
				try { win.LocationChanged -= OnWindowLayoutChanged; } catch { }
				try { win.SizeChanged -= OnWindowLayoutChanged; } catch { }
			} else if (_root is Popup popup) {
				try { popup.Closed -= OnRootClosed; } catch { }
			} else if (_root is ContextMenu cm) {
				try { cm.Closed -= OnRootClosed; } catch { }
			}
			
			// Stop pending scan
			lock (_scanLock) {
				if (_pendingFinalScan != null) {
					_pendingFinalScan.Stop();
					_pendingFinalScan = null;
				}
			}
			
			// Fire removal event for all nodes
			if (_nodesByElement.Count > 0) {
				var removedNodes = _nodesByElement.Values.ToArray();
				_nodesUpdatedCallback?.Invoke(new NavNode[0], removedNodes);
			}
			
			_nodesByElement.Clear();
			_discoveredElements.Clear();
		}
		
		#endregion
		
		#region Public API
		
		/// <summary>
		/// Request a scan of this root's visual tree.
		/// Automatically rate-limited with guaranteed final scan.
		/// 
		/// Rate limiting algorithm:
		/// - If >= 100ms since last scan: Scan immediately
		/// - If < 100ms: Schedule ONE final scan timer (if not already scheduled)
		/// - Never abort pending final scans - guarantees final scan after last LayoutUpdated
		/// </summary>
		public void ScheduleScan()
		{
			if (_disposed) return;
			
			if (!_root.Dispatcher.CheckAccess()) {
				_root.Dispatcher.BeginInvoke(new Action(ScheduleScan), DispatcherPriority.Normal);
				return;
			}
			
			lock (_scanLock) {
				var now = DateTime.UtcNow;
				var timeSinceLastScan = now - _lastScanTime;
				
				// Rate limit: Max 1 scan per 100ms
				if (timeSinceLastScan >= TimeSpan.FromMilliseconds(SCAN_RATE_LIMIT_MS)) {
					// ✅ Scan immediately (rate limit satisfied)
					_lastScanTime = now;
					
					Debug.WriteLine($"[NavRoot] ScheduleScan: {_rootSimpleName}' → IMMEDIATE scan (timeSince={timeSinceLastScan.TotalMilliseconds:F1}ms)");
					
					// Cancel any pending final scan (we're scanning now)
					if (_pendingFinalScan != null) {
						_pendingFinalScan.Stop();
						_pendingFinalScan = null;
					}
					
					ExecuteScan();
				} else {
					// ⏱️ Rate limit active - ensure final scan is scheduled
					// ✅ KEY: Only schedule if NOT already scheduled (no abort!)
					if (_pendingFinalScan == null) {
						Debug.WriteLine($"[NavRoot] ScheduleScan: {_rootSimpleName}' → SCHEDULE final scan (timeSince={timeSinceLastScan.TotalMilliseconds:F1}ms)");
						ScheduleFinalScan();
					} else {
						Debug.WriteLine($"[NavRoot] ScheduleScan: {_rootSimpleName}' → IGNORE (final scan already pending, timeSince={timeSinceLastScan.TotalMilliseconds:F1}ms)");
					}
					// Otherwise: Final scan already pending, ignore this request
				}
			}
		}
		
		public FrameworkElement Root => _root;
		
		public IReadOnlyCollection<NavNode> GetNodes() => _nodesByElement.Values.ToArray();
		
		public bool TryGetNode(FrameworkElement element, out NavNode node) => _nodesByElement.TryGetValue(element, out node);
		
		#endregion
		
		#region Private Event Handlers
		
		private void OnLayoutUpdated(object sender, EventArgs e)
		{
			Debug.WriteLine($"[NavRoot] LayoutUpdated fired: {_rootSimpleName}'");
			ScheduleScan();
		}
		
		private void OnWindowLoaded(object sender, RoutedEventArgs e)
		{
			ScheduleScan();
		}
		
		private void OnWindowLayoutChanged(object sender, EventArgs e)
		{
			ScheduleScan();
		}
		
		private void OnRootUnloaded(object sender, RoutedEventArgs e)
		{
			Dispose();
		}
		
		private void OnRootClosed(object sender, EventArgs e)
		{
			Dispose();
		}
		
		#endregion
		
		#region Private Scanning Logic
		
		private void ScheduleFinalScan()
		{
			// Called within lock(_scanLock)
			
			_pendingFinalScan = new DispatcherTimer {
				Interval = TimeSpan.FromMilliseconds(SCAN_RATE_LIMIT_MS)
			};
			
			_pendingFinalScan.Tick += (s, e) => {
				lock (_scanLock) {
					if (_pendingFinalScan != null) {
						_pendingFinalScan.Stop();
						_pendingFinalScan = null;
					}
					
					_lastScanTime = DateTime.UtcNow;
					ExecuteScan();
				}
			};
			
			_pendingFinalScan.Start();
		}
		
		private void ExecuteScan()
		{
			// Called within lock(_scanLock) OR immediately when rate limit satisfied
			
			if (_disposed) return;
			
			try {
				Debug.WriteLine($"[NavRoot] SyncRoot START: {_rootSimpleName}'");
				
				var newElements = new HashSet<FrameworkElement>();
				var addedNodes = new List<NavNode>();
				
				// ✅ Track scanned elements separately
				int scannedElementCount = 0;
				
				// Scan visual tree
				ScanVisualTree(_root, newElements, addedNodes, ref scannedElementCount);
				
				// ✅ Report BOTH scanned elements AND discovered nodes
				Debug.WriteLine($"[NavRoot] SyncRoot END: {_rootSimpleName}' - scanned {scannedElementCount} elements, discovered {newElements.Count} nodes");
				
				// Detect removed nodes
				var removedNodes = new List<NavNode>();
				foreach (var oldFe in _discoveredElements) {
					if (!newElements.Contains(oldFe)) {
						if (_nodesByElement.TryGetValue(oldFe, out var oldNode)) {
							_nodesByElement.Remove(oldFe);
							removedNodes.Add(oldNode);
							UnlinkNode(oldNode);
						}
					}
				}
				
				_discoveredElements = newElements;
				
				// Fire event
				if (addedNodes.Count > 0 || removedNodes.Count > 0) {
					Debug.WriteLine($"[NavRoot] Firing NodesUpdated: {addedNodes.Count} added, {removedNodes.Count} removed");
					try {
						_nodesUpdatedCallback?.Invoke(addedNodes.ToArray(), removedNodes.ToArray());
					} catch (Exception ex) {
						Debug.WriteLine($"[NavRoot] NodesUpdated callback threw exception: {ex.Message}");
					}
				}
			} catch (Exception ex) {
				Debug.WriteLine($"[NavRoot] ExecuteScan exception: {ex.Message}");
			}
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
		
		private void ScanVisualTree(FrameworkElement root, HashSet<FrameworkElement> discoveredElements, List<NavNode> addedNodes, ref int scannedElementCount)
		{
			if (root == null) return;

			// ✅ DEBUG: Log root info and child count
			int rootChildCount = 0;
			try { rootChildCount = VisualTreeHelper.GetChildrenCount(root); } catch { }
			
			var rootTypeName = root.GetType().Name;
			var rootElementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
			
			Debug.WriteLine($"[NavRoot] ScanVisualTree: root={rootTypeName} '{rootElementName}', childCount={rootChildCount}");
			
			// ✅ DEBUG: Track visibility statistics for PopupRoot
			bool isPopupRoot = rootTypeName == "PopupRoot";
			int popupVisibleCount = 0;
			int popupInvisibleCount = 0;
			int popupNotLoadedCount = 0;

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
					// ✅ Count every FrameworkElement we visit
					scannedElementCount++;
					
					// ✅ NEW: Discover Popups during scanning!
					if (fe is Popup popup) {
						Observer.OnPopupDiscovered(popup);
					}
					
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

					// ✅ DEBUG: Track visibility stats for PopupRoot
					if (isPopupRoot) {
						if (!parentVisible) {
							popupInvisibleCount++;
						} else if (fe.Visibility != Visibility.Visible) {
							popupInvisibleCount++;
						} else if (!fe.IsVisible) {
							popupInvisibleCount++;
						} else if (!fe.IsLoaded) {
							popupNotLoadedCount++;
						} else {
							popupVisibleCount++;
						}
					}

					if (!isVisible) {
						// ✅ DEBUG: Log why element is invisible (for PopupRoot only)
						if (isPopupRoot && scannedElementCount <= 15) {
							var feTypeName = fe.GetType().Name;
							var feElementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
							Debug.WriteLine($"[NavRoot]   INVISIBLE: {feTypeName} '{feElementName}' - parentNavNode={parentNavNode?.SimpleName}, parentVisible={parentVisible}, Visibility={fe.Visibility}, IsVisible={fe.IsVisible}, IsLoaded={fe.IsLoaded}");
						}
						
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

					// ✅ DEBUG: Log visible elements that might create nodes (PopupRoot only)
					if (isPopupRoot && scannedElementCount <= 15) {
						var feTypeName = fe.GetType().Name;
						var feElementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
						Debug.WriteLine($"[NavRoot]   VISIBLE: {feTypeName} '{feElementName}' - will attempt node creation - parentNavNode={parentNavNode?.SimpleName}");
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
							// Different PresentationSource - register as separate root with Observer
							Observer.RegisterRoot(fe);
							continue;
						}

						if (isOwnPresentationSourceRoot && !object.ReferenceEquals(fe, root)) {
							// This is a PresentationSource root but not the one we're scanning
							Observer.RegisterRoot(fe);
							continue;
						}
					} catch { continue; }

					// Build hierarchical path
					var hierarchicalPath = NavNode.GetHierarchicalPath(fe);
					
					// Check classification FIRST (highest priority)
					var classification = _navConfig?.GetClassification(hierarchicalPath);
				
					// Create node based on priority
					NavNode navNode = null;
					
					if (classification != null) {
						// Classification exists → force create WITH classification
						navNode = NavNode.CreateNavNode(fe, hierarchicalPath, classification);
						Debug.WriteLine($"[NavRoot] ✅ Created via CLASSIFY rule: {navNode.SimpleName}, parentNavNode={parentNavNode?.SimpleName}");
					} else {
						// No classification → use type-based rules
						navNode = NavNode.TryCreateNavNode(fe, hierarchicalPath, _navConfig);
						
						if (navNode != null) {
							Debug.WriteLine($"[NavRoot] ✅ Created via type rules: {navNode.SimpleName}, parentNavNode={parentNavNode?.SimpleName}");
						} else if (isPopupRoot && scannedElementCount <= 15) {
							// ✅ DEBUG: Log why node wasn't created (PopupRoot only)
							var feTypeName = fe.GetType().Name;
							var feElementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
							Debug.WriteLine($"[NavRoot]   NO NODE: {feTypeName} '{feElementName}' - type not navigable");
						}
					}
					
					// If node was created, apply classification properties & link
					if (navNode != null) {
						_nodesByElement[fe] = navNode;
						discoveredElements.Add(fe);

						Debug.WriteLine($"[NavRoot] Created NavNode");
						
						// Apply classification properties
						if (classification != null) {
							if (!string.IsNullOrEmpty(classification.PageName)) {
								navNode.PageName = classification.PageName;
							}
							if (!string.IsNullOrEmpty(classification.KeyName)) {
								navNode.KeyName = classification.KeyName;
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
						Debug.WriteLine($"[NavRoot] Ready to LinkToParent. navNode={navNode.SimpleName}, parentNavNode={parentNavNode?.SimpleName}");
						LinkToParent(navNode, parentNavNode);

						// Add to results
						addedNodes.Add(navNode);

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
			
			// ✅ DEBUG: Report visibility statistics for PopupRoot
			if (isPopupRoot) {
				Debug.WriteLine($"[NavRoot] PopupRoot visibility: {popupVisibleCount} visible, {popupInvisibleCount} invisible, {popupNotLoadedCount} not loaded");
			}
		}
		
		private static void LinkToParent(NavNode childNode, NavNode parentNavNode)
		{
			if (childNode == null) return;

			try {
				if (parentNavNode != null) {
					childNode.Parent = new WeakReference<NavNode>(parentNavNode);
					parentNavNode.Children.Add(new WeakReference<NavNode>(childNode));
					return;
				}

				// No parent - this is a root node
				Debug.WriteLine($"[NavRoot] Root node: {childNode.SimpleName} (no parent)");
			} catch (Exception ex) {
				Debug.WriteLine($"[NavRoot] Error linking parent for {childNode.SimpleName}: {ex.Message}");
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
				Debug.WriteLine($"[NavRoot] Error unlinking {node.SimpleName}: {ex.Message}");
			}
		}
		
		#endregion
	}
}
