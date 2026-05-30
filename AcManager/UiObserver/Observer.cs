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
	/// REFACTORED ARCHITECTURE (NavRoot-based):
	/// - Each PresentationSource has its own NavRoot instance
	/// - NavRoot encapsulates: scanning, rate-limiting, lifecycle, state
	/// - Observer acts as coordinator: creates/disposes NavRoots, forwards events
	/// - Rate-limited scanning: Max 1 scan per 100ms with guaranteed final scan
	/// 
	/// Benefits:
	/// - No static state management complexity
	/// - Testable (NavRoot is injectable)
	/// - Thread-safe (instance-level isolation)
	/// - Automatic cleanup (IDisposable)
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

		#region State (SIMPLIFIED)

		// ✅ Primary index by SimpleName (e.g., "Window:MainWindow", "PopupRoot:(unnamed):3E0A44")
		private static readonly ConcurrentDictionary<string, NavRoot> _rootsBySimpleName 
			= new ConcurrentDictionary<string, NavRoot>();

		// ✅ Reverse index for cleanup (PresentationSource → SimpleName)
		private static readonly ConcurrentDictionary<PresentationSource, string> _simpleNameByPresentationSource 
			= new ConcurrentDictionary<PresentationSource, string>();

		// ✅ Track hooked Popups to avoid double-hooking
		private static readonly HashSet<Popup> _hookedPopups = new HashSet<Popup>();
		private static readonly object _popupHookLock = new object();
		
		private static NavConfiguration _navConfig;
		
		#endregion

		#region Events

		// ✅ UNIFIED: Single event for all node lifecycle changes (including modals)
		// Modal scope nodes (IsModal && IsGroup) are included in addedNodes/removedNodes arrays
		internal static event Action<NavNode[], NavNode[]> NodesUpdated;
		
		// Layout change notification (for overlay position updates)
		public static event EventHandler WindowLayoutChanged;

		#endregion

		#region Initialize

		internal static void Initialize(NavConfiguration navConfig)
		{
			_navConfig = navConfig ?? throw new ArgumentNullException(nameof(navConfig));
			
			try {
				// ✅ Hook Window.Loaded globally (catches new Windows)
				EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
						new RoutedEventHandler(OnWindowLoaded), true);

				// ✅ Fallback: Catch PresentationSource roots if Popup.Opened missed them
				EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
						new RoutedEventHandler(OnFrameworkElementLoaded), true);

				// ✅ Hook all existing windows
				if (Application.Current != null) {
					Application.Current.Dispatcher.BeginInvoke(new Action(() => {
						try {
							foreach (Window w in Application.Current.Windows) {
								OnWindowDiscovered(w);
							}
						} catch { }
					}), DispatcherPriority.Background);
				}
			} catch { }
		}

		#endregion

		#region Window Discovery

		/// <summary>
		/// Called when a Window is loaded (existing or new).
		/// </summary>
		private static void OnWindowLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is Window window) {
				OnWindowDiscovered(window);
			}
		}

		/// <summary>
		/// Handles Window discovery: registers as root + hooks layout events.
		/// </summary>
		private static void OnWindowDiscovered(Window window)
		{
			if (window == null) return;

			try {
				// Skip HighlightOverlay
				if (ReferenceEquals(window, Navigator._overlay)) {
					Debug.WriteLine("[Observer] Skipping HighlightOverlay");
					return;
				}

				Debug.WriteLine($"[Observer] Window discovered: {window.GetType().Name}");

				// Register as PresentationSource root
				RegisterRoot(window);

				// Hook layout events for overlay positioning
				window.LocationChanged -= OnWindowLayoutChanged;
				window.LocationChanged += OnWindowLayoutChanged;
				window.SizeChanged -= OnWindowLayoutChanged;
				window.SizeChanged += OnWindowLayoutChanged;
			} catch { }
		}

		private static void OnWindowLayoutChanged(object sender, EventArgs e)
		{
			try {
				// Notify subscribers (Navigator's overlay) for position updates
				WindowLayoutChanged?.Invoke(sender, e);
			} catch { }
		}

		#endregion

		#region Popup Discovery

		/// <summary>
		/// Called by NavRoot when it discovers a Popup during visual tree scanning.
		/// This is the KEY method that enables Popup discovery!
		/// </summary>
		public static void OnPopupDiscovered(Popup popup)
		{
			if (popup == null) return;

			lock (_popupHookLock) {
				// Already hooked?
				if (_hookedPopups.Contains(popup)) return;

				try {
					// Hook Opened/Closed events
					popup.Opened -= OnPopupOpened;
					popup.Opened += OnPopupOpened;
					popup.Closed -= OnPopupClosed;
					popup.Closed += OnPopupClosed;

					_hookedPopups.Add(popup);

					Debug.WriteLine($"[Observer] Popup discovered and hooked: {popup.Name ?? "(unnamed)"}");
				} catch (Exception ex) {
					Debug.WriteLine($"[Observer] OnPopupDiscovered exception: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Called when a hooked Popup opens.
		/// </summary>
		private static void OnPopupOpened(object sender, EventArgs e)
		{
			if (sender is Popup popup) {
				Debug.WriteLine($"[Observer] Popup.Opened: {popup.Name ?? "(unnamed)"}");

				try {
					var child = popup.Child;
					if (child == null) {
						Debug.WriteLine($"[Observer]   → Child is null");
						return;
					}

					var ps = PresentationSource.FromVisual(child);
					if (ps == null) {
						Debug.WriteLine($"[Observer]   → PresentationSource is null");
						return;
					}

					var rootVisual = ps.RootVisual as FrameworkElement;
					if (rootVisual == null) {
						Debug.WriteLine($"[Observer]   → RootVisual is null");
						return;
					}

					Debug.WriteLine($"[Observer]   → Registering PopupRoot: {NavNode.ComputeSimpleName(rootVisual)}");
					RegisterRoot(rootVisual);
				} catch (Exception ex) {
					Debug.WriteLine($"[Observer] OnPopupOpened exception: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Called when a hooked Popup closes.
		/// </summary>
		private static void OnPopupClosed(object sender, EventArgs e)
		{
			if (sender is Popup popup) {
				Debug.WriteLine($"[Observer] Popup.Closed: {popup.Name ?? "(unnamed)"}");

				try {
					var child = popup.Child;
					if (child == null) return;

					var ps = PresentationSource.FromVisual(child);
					if (ps == null) return;

					var rootVisual = ps.RootVisual as FrameworkElement;
					if (rootVisual != null) {
						Debug.WriteLine($"[Observer]   → Unregistering PopupRoot: {rootVisual.GetType().Name}");
						UnregisterRoot(rootVisual);
					}
				} catch (Exception ex) {
					Debug.WriteLine($"[Observer] OnPopupClosed exception: {ex.Message}");
				}
			}
		}

		#endregion

		#region Fallback (PopupRoot.Loaded)

		/// <summary>
		/// Fallback: Catches PresentationSource roots that weren't caught by Popup.Opened.
		/// This should rarely fire if Popup discovery works correctly.
		/// </summary>
		private static void OnFrameworkElementLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement fe) {
				try {
					var ps = PresentationSource.FromVisual(fe);
					if (ps != null && ReferenceEquals(ps.RootVisual, fe)) {
						// Skip Windows (handled by OnWindowLoaded)
						if (fe is Window == false) {
							Debug.WriteLine($"[Observer] PresentationSource root loaded (fallback): {fe.GetType().Name}");
							RegisterRoot(fe);
						}
					}
				} catch { }
			}
		}

		#endregion

		#region Root Management (SIMPLIFIED)

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

				// Redirect to actual PresentationSource root
				if (!object.ReferenceEquals(root, psRootVisual)) {
					var simpleName = NavNode.ComputeSimpleName(psRootVisual);
					if (_rootsBySimpleName.TryGetValue(simpleName, out var existingNavRoot)) {
						Debug.WriteLine($"[NavRoot] Redirecting through PS, Root {simpleName} is already registered. Scheduling scan only");
						existingNavRoot.ScheduleScan();
						return;
					}

					root = psRootVisual;
				}

				// Compute SimpleName for indexing
				string rootSimpleName = NavNode.ComputeSimpleName(root);
				
				// Check if already exists
				if (_rootsBySimpleName.TryGetValue(rootSimpleName, out var existingRoot)) {
					Debug.WriteLine($"[NavRoot] Root {rootSimpleName} is already registered. Scheduling scan only");
					existingRoot.ScheduleScan();
					return;
				}

				var navRoot = new NavRoot(root, rootSimpleName, _navConfig, OnNodesUpdated);

				// Index by SimpleName
				_rootsBySimpleName[rootSimpleName] = navRoot;
				_simpleNameByPresentationSource[ps] = rootSimpleName;

				// Created and added a new NavRoot
				Debug.WriteLine($"[Observer] RegisterRoot: {rootSimpleName} (NEW) [Total roots: {_rootsBySimpleName.Count}]");

				// Trigger initial scan
				navRoot.ScheduleScan();
			} catch { }
		}

		public static void UnregisterRoot(FrameworkElement root)
		{
			if (root == null) return;

			try {
				var ps = PresentationSource.FromVisual(root);
				if (ps == null) return;
				
				// Remove from dictionaries using reverse lookup
				if (_simpleNameByPresentationSource.TryRemove(ps, out var simpleName)) {
					if (_rootsBySimpleName.TryRemove(simpleName, out var navRoot)) {
						var typeName = root.GetType().Name;
						var elementName = string.IsNullOrEmpty(root.Name) ? "(unnamed)" : root.Name;
						Debug.WriteLine($"[Observer] UnregisterRoot: {simpleName} [Total roots: {_rootsBySimpleName.Count}]");
						
						navRoot.Dispose(); // Fires removal events automatically
					}
				}
			} catch { }
		}

		#endregion

		#region Event Forwarding

		/// <summary>
		/// Called by NavRoot instances when nodes are added/removed.
		/// Forwards the event to Navigator and other subscribers.
		/// </summary>
		private static void OnNodesUpdated(NavNode[] addedNodes, NavNode[] removedNodes)
		{
			try {
				NodesUpdated?.Invoke(addedNodes, removedNodes);
			} catch (Exception ex) {
				if (_verboseDebug) {
					Debug.WriteLine($"[Observer] NodesUpdated event handler threw exception: {ex.Message}");
				}
			}
		}

		#endregion

		#region Query API (SIMPLIFIED)

		/// <summary>
		/// Extracts the root SimpleName from a hierarchical path.
		/// The root SimpleName is the first segment before " > " separator.
		/// </summary>
		/// <param name="hierarchicalPath">Full hierarchical path (e.g., "Window:MainWindow > Border:Border > Button:Go")</param>
		/// <returns>Root SimpleName (e.g., "Window:MainWindow")</returns>
		private static string ExtractRootSimpleName(string hierarchicalPath)
		{
			if (string.IsNullOrWhiteSpace(hierarchicalPath))
				return null;
			
			// Extract first segment: "Window:MainWindow > Border:Border" → "Window:MainWindow"
			int separatorIndex = hierarchicalPath.IndexOf(" > ", StringComparison.Ordinal);
			return separatorIndex > 0 
				? hierarchicalPath.Substring(0, separatorIndex) 
				: hierarchicalPath;
		}

		/// <summary>
		/// Gets all NavNodes under the given hierarchical path prefix.
		/// Efficient O(1) lookup - goes directly to the NavRoot by SimpleName.
		/// 
		/// ✅ NEW (Phase 3): Efficient scoped query API.
		/// Use this instead of GetAllNavNodes() + filtering for better performance.
		/// </summary>
		/// <param name="pathPrefix">
		/// Hierarchical path prefix to match.
		/// Examples:
		///   "Window:MainWindow" - all nodes in MainWindow
		///   "PopupRoot:(unnamed):3E0A44" - all nodes in specific popup
		///   "Window:MainWindow > Border:PART_Content" - nodes under specific Border
		/// </param>
		/// <returns>Collection of NavNodes under the specified path</returns>
		public static IReadOnlyCollection<NavNode> GetNodesUnderPath(string pathPrefix)
		{
			if (string.IsNullOrWhiteSpace(pathPrefix))
				return new List<NavNode>();
			
			// Extract root SimpleName (first segment before " > ")
			string rootSimpleName = ExtractRootSimpleName(pathPrefix);
			
			if (string.IsNullOrWhiteSpace(rootSimpleName))
				return new List<NavNode>();

			Debug.WriteLine($"[Observer] GetNodesUnderPath: Looking up root '{rootSimpleName}' for path prefix '{pathPrefix}' [Total roots: {_rootsBySimpleName.Count}]");

			// ✅ NEW: Lookup NavRoot by SimpleName (O(1))
			if (!_rootsBySimpleName.TryGetValue(rootSimpleName, out var navRoot))
			{
				// Root not found - return empty collection
				if (_verboseDebug)
				{
					Debug.WriteLine($"[Observer] GetNodesUnderPath: Root not found (SimpleName='{rootSimpleName}')");
				}
				return new List<NavNode>();
			}
			
			// Get all nodes from this root
			var allNodesInRoot = navRoot.GetNodes();
			
			if (_verboseDebug)
			{
				Debug.WriteLine($"[Observer] GetNodesUnderPath: Found root '{rootSimpleName}' with {allNodesInRoot.Count} nodes");
			}
			
			// If requesting entire root, return all nodes
			if (pathPrefix.Equals(rootSimpleName, StringComparison.OrdinalIgnoreCase))
			{
				return allNodesInRoot;
			}
			
			// Filter by path prefix (case-insensitive StartsWith)
			var filtered = allNodesInRoot
				.Where(node => node.HierarchicalPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
				.ToList();
			
			if (_verboseDebug)
			{
				Debug.WriteLine($"[Observer] GetNodesUnderPath: Filtered to {filtered.Count} nodes under '{pathPrefix}'");
			}
			
			return filtered;
		}

		/// <summary>
		/// Gets all NavNodes from all registered NavRoots.
		/// 
		/// ⚠️ DEPRECATED: This method iterates ALL NavRoots (windows + popups).
		/// For scoped queries, use GetNodesUnderPath() instead for better performance.
		/// 
		/// Example migration:
		/// <code>
		/// // ❌ OLD (inefficient - iterates all roots):
		/// var allNodes = Observer.GetAllNavNodes();
		/// var scoped = allNodes.Where(n => n.HierarchicalPath.StartsWith(scopePath));
		/// 
		/// // ✅ NEW (efficient - O(1) root lookup):
		/// var scoped = Observer.GetNodesUnderPath(scopePath);
		/// </code>
		/// 
		/// Debug tools may continue using this method since performance is not critical.
		/// </summary>
		/// <returns>All NavNodes from all NavRoots</returns>
		[Obsolete("Use GetNodesUnderPath(pathPrefix) for scoped queries. GetAllNavNodes() is inefficient for most use cases. Only use for debug/diagnostic tools.", false)]
		public static IReadOnlyCollection<NavNode> GetAllNavNodes()
		{
			var allNodes = new List<NavNode>();
			foreach (var navRoot in _rootsBySimpleName.Values) {
				allNodes.AddRange(navRoot.GetNodes());
			}
			return allNodes;
		}

		public static bool TryGetNavNode(FrameworkElement fe, out NavNode node, FrameworkElement rootFe = null)
		{
			if (fe == null) {
				node = null;
				return false;
			}

			try {
				string rootSimpleName = rootFe is null ? null : NavNode.ComputeSimpleName(rootFe);
				if (rootSimpleName is null) {
					var ps = PresentationSource.FromVisual(fe);
					var rootFrameworkElement = ps?.RootVisual as FrameworkElement;
					rootSimpleName = NavNode.ComputeSimpleName(rootFrameworkElement);
				}
				
				if (!string.IsNullOrWhiteSpace(rootSimpleName) && 
				    _rootsBySimpleName.TryGetValue(rootSimpleName, out var navRoot)) {
					if (navRoot.TryGetNode(fe, out node)) {
						return true;
					}
				}
			} catch { }
	
			node = null;
			return false;
		}

		#endregion
	}
}