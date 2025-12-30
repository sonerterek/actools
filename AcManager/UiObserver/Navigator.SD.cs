using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FirstFloor.ModernUI;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator - StreamDeck Integration
	/// 
	/// This partial class contains all StreamDeck-specific functionality:
	/// - Connection management
	/// - Key and page definition
	/// - Event handling
	/// - Icon discovery and management
	/// </summary>
	internal static partial class Navigator
	{
		#region StreamDeck Fields

		// StreamDeck integration
		private static SDPClient _streamDeckClient;
		
		// ✅ FIX #2: Track first connection to suppress startup Toast notifications
		private static bool _streamDeckHasConnectedAtLeastOnce = false;
		
		// ✅ FIX: Track if discovery session header has been written for this app run
		private static bool _discoverySessionHeaderWritten = false;
		
		// ✅ NEW: Configuration and shortcut mappings
		private static NavConfiguration _navConfig;
		private static Dictionary<string, NavShortcutKey> _shortcutsByKey = new Dictionary<string, NavShortcutKey>(StringComparer.OrdinalIgnoreCase);

		// Page name constants
		private const string PageNavigation = "Navigation";
		private const string PageUpDown = "UpDown";
		private const string PageSlider = "Slider";
		private const string PageDoubleSlider = "DoubleSlider";
		private const string PageRoundSlider = "RoundSlider";

		#endregion

		#region StreamDeck Initialization

		/// <summary>
		/// Initialize StreamDeck integration.
		/// Sets up client, defines keys/pages, subscribes to events, and starts connection.
		/// Fire-and-forget: Does not wait for or check connection status.
		/// </summary>
		private static void InitializeStreamDeck()
		{
			if (_streamDeckClient != null)
			{
				Debug.WriteLine("[Navigator] StreamDeck already initialized");
				return;
			}

			Debug.WriteLine("[Navigator] Initializing StreamDeck integration...");

			try
			{
				// ✅ NEW: Load configuration first
				_navConfig = NavConfigParser.Load();
				Debug.WriteLine($"[Navigator] Loaded config: {_navConfig.ShortcutKeys.Count} shortcuts, {_navConfig.Pages.Count} custom pages");

				// Create StreamDeck client
				_streamDeckClient = new SDPClient
				{
					SDPVerboseDebug = true
				};

				// Subscribe to connection events for status notifications
				_streamDeckClient.ConnectionEstablished += OnStreamDeckConnected;
				_streamDeckClient.ConnectionLost += OnStreamDeckDisconnected;
				_streamDeckClient.ReconnectionAttempt += OnStreamDeckReconnecting;

				// Discover icons
				var icons = SDPIconHelper.DiscoverIcons();
				Debug.WriteLine($"[Navigator] Discovered {icons.Count} StreamDeck icons");

				// Define keys (built-in + shortcuts from config)
				DefineStreamDeckKeys(icons);

				// Define pages (built-in + custom from config)
				DefineStreamDeckPages();

				// Subscribe to KeyPressed events
				_streamDeckClient.KeyPressed += OnStreamDeckKeyPressed;

				// Start connection (fire-and-forget, automatic reconnection)
				Task.Run(async () => await _streamDeckClient.ConnectAsync());

				Debug.WriteLine("[Navigator] StreamDeck initialization complete");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] StreamDeck initialization failed: {ex.Message}");
				ShowStreamDeckError("StreamDeck Initialization Failed", ex.Message);
			}
		}

		#endregion

		#region StreamDeck Key and Page Definition

		/// <summary>
		/// Defines all StreamDeck keys with their icons.
		/// Fire-and-forget: All definitions stored in SDPClient state.
		/// </summary>
		private static void DefineStreamDeckKeys(Dictionary<string, string> icons)
		{
			// Define built-in navigation keys
			_streamDeckClient.DefineKey("Back", null, GetIconPath(icons, "Back"));
			_streamDeckClient.DefineKey("Up", null, GetIconPath(icons, "Up"));
			_streamDeckClient.DefineKey("Down", null, GetIconPath(icons, "Down"));
			_streamDeckClient.DefineKey("Left", null, GetIconPath(icons, "Left"));
			_streamDeckClient.DefineKey("Right", null, GetIconPath(icons, "Right"));
			_streamDeckClient.DefineKey("MouseLeft", null, GetIconPath(icons, "Mouse Left"));
			
			// ✅ Slider value adjustment keys (use Left/Right icons for now)
			_streamDeckClient.DefineKey("SliderDecrease", null, GetIconPath(icons, "Left"));
			_streamDeckClient.DefineKey("SliderIncrease", null, GetIconPath(icons, "Right"));
			
			// ✅ NEW: Slider range adjustment keys (use Up/Down icons for now)
			_streamDeckClient.DefineKey("SliderRangeDecrease", null, GetIconPath(icons, "Down"));
			_streamDeckClient.DefineKey("SliderRangeIncrease", null, GetIconPath(icons, "Up"));
			
			// Define built-in discovery keys
			_streamDeckClient.DefineKey("WriteModalFilter", "Modal", null);
			_streamDeckClient.DefineKey("WriteElementFilter", "Element", null);
			
			// ✅ FIX: Only define keys for shortcuts that actually have a KeyName
			// Classifications can be modals, page mappings, or shortcuts
			// Only shortcuts have KeyName defined
			foreach (var shortcut in _navConfig.ShortcutKeys)
			{
				// Skip classifications without KeyName (modals, page mappings)
				if (string.IsNullOrEmpty(shortcut.KeyName))
				{
					Debug.WriteLine($"[Navigator] Skipping classification without KeyName: {shortcut.PathFilter}");
					continue;
				}
				
				// Get icon path (check if it's a file path or icon name)
				string iconSpec = null;
				if (!string.IsNullOrEmpty(shortcut.KeyIcon))
				{
					// Try to get icon from discovered icons first
					iconSpec = GetIconPath(icons, shortcut.KeyIcon);
					
					// If not found, check if it's a file path
					if (iconSpec == null && File.Exists(shortcut.KeyIcon))
					{
						iconSpec = shortcut.KeyIcon;
					}
					
					// If still null, use text-based icon
					if (iconSpec == null)
					{
						iconSpec = SDPIconHelper.CreateTextIcon(shortcut.KeyIcon);
					}
				}
				
				_streamDeckClient.DefineKey(shortcut.KeyName, shortcut.KeyTitle, iconSpec);
				
				// Store shortcut for later lookup
				_shortcutsByKey[shortcut.KeyName] = shortcut;
				
				Debug.WriteLine($"[Navigator] Defined shortcut key: {shortcut.KeyName} → {shortcut.PathFilter}");
			}
		}

		/// <summary>
		/// Defines all StreamDeck pages.
		/// Fire-and-forget: All definitions stored in SDPClient state.
		/// </summary>
		private static void DefineStreamDeckPages()
		{
			// Define built-in pages
			DefineBuiltInPages();
			
			// ✅ NEW: Define custom pages from configuration
			foreach (var page in _navConfig.Pages)
			{
				// ✅ Use GetFullPageName() to send full name with inheritance to plugin
				_streamDeckClient.DefinePage(page.GetFullPageName(), page.KeyGrid);
				Debug.WriteLine($"[Navigator] Defined custom page: {page.GetFullPageName()}");
			}
		}

		/// <summary>
		/// Defines the built-in page templates.
		/// Uses fire-and-forget API - all definitions stored in SDPClient.
		/// </summary>
		private static void DefineBuiltInPages()
		{
			Debug.WriteLine("[Navigator] DefineBuiltInPages() START");
			
			// Navigation page (full 6-direction navigation)
			Debug.WriteLine($"[Navigator] Defining page: {PageNavigation}");
			_streamDeckClient.DefinePage(PageNavigation, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new[] { "","",""},
				new[] { "", "Up", "" },
				new[] { "Left", "MouseLeft", "Right" },
				new[] { "", "Down", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageNavigation}");
			
			// UpDown page (vertical navigation only, for menus)
			Debug.WriteLine($"[Navigator] Defining page: {PageUpDown}");
			_streamDeckClient.DefinePage(PageUpDown, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new[] { "", "", "" },
				new[] { "", "Up", "" },
				new[] { "", "MouseLeft", "" },
				new[] { "", "Down", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageUpDown}");
			
			// ✅ Slider page (value adjustment only, no range)
			Debug.WriteLine($"[Navigator] Defining page: {PageSlider}");
			_streamDeckClient.DefinePage(PageSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new[] { "", "", "" },
				new[] { "", "", "" },
				new[] { "SliderDecrease", "MouseLeft", "SliderIncrease" },
				new[] { "", "", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageSlider}");
			
			// ✅ DoubleSlider page (value + range adjustment)
			Debug.WriteLine($"[Navigator] Defining page: {PageDoubleSlider}");
			_streamDeckClient.DefinePage(PageDoubleSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new[] { "", "", "" },
				new[] { "", "SliderRangeIncrease", "" },
				new[] { "SliderDecrease", "MouseLeft", "SliderIncrease" },
				new[] { "", "SliderRangeDecrease", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageDoubleSlider}");
			
			// ✅ RoundSlider page (value adjustment only, circular slider doesn't have range)
			Debug.WriteLine($"[Navigator] Defining page: {PageRoundSlider}");
			_streamDeckClient.DefinePage(PageRoundSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new[] { "", "", "" },
				new[] { "", "", "" },
				new[] { "SliderDecrease", "MouseLeft", "SliderIncrease" },
				new[] { "", "", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageRoundSlider}");
			
			Debug.WriteLine("[Navigator] DefineBuiltInPages() END");
			Debug.WriteLine($"[Navigator] SDPClient page count: {_streamDeckClient.PageCount}");
		}

		/// <summary>
		/// Selects the appropriate built-in page for a modal based on its type.
		/// Returns page name, or null to use default Navigation page.
		/// </summary>
		private static string SelectBuiltInPageForModal(NavNode scopeNode)
		{
			if (scopeNode == null) return null;
			if (!scopeNode.TryGetVisual(out var element)) return null;
			
			var typeName = element.GetType().Name;
			
			// PopupRoot indicates menu/dropdown (vertical navigation)
			if (typeName == "PopupRoot")
			{
				return PageUpDown;
			}
			
			// Other modal types use default Navigation page
			return null;
		}

		#endregion

		#region StreamDeck Event Handlers

		/// <summary>
		/// Handle StreamDeck key press events.
		/// Marshals to UI thread and executes navigation commands.
		/// </summary>
		private static void OnStreamDeckKeyPressed(object sender, SDPKeyPressEventArgs e)
		{
			Debug.WriteLine($"[Navigator] StreamDeck key pressed: {e.KeyName}");
			
			// Marshal to UI thread (StreamDeck events fire on background thread)
			Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
				try {
					Debug.WriteLine($"[Navigator] Executing command for '{e.KeyName}' on UI thread");
					
					// ✅ Check if this is a shortcut key first
					if (_shortcutsByKey.ContainsKey(e.KeyName))
					{
						Debug.WriteLine($"[Navigator] Executing shortcut key: {e.KeyName}");
						ExecuteShortcutKey(e.KeyName);
						return;
					}
					
					// ✅ Built-in keys - each has ONE purpose, no context checking
					switch (e.KeyName) {
						// Navigation keys - ALWAYS navigate
						case "Up":
							MoveInDirection(NavDirection.Up);
							break;
						case "Down":
							MoveInDirection(NavDirection.Down);
							break;
						case "Left":
							MoveInDirection(NavDirection.Left);
							break;
						case "Right":
							MoveInDirection(NavDirection.Right);
							break;
						
						// Activation key - context-aware (Confirm in interaction mode, Activate otherwise)
						case "MouseLeft":
							if (CurrentContext?.ContextType == NavContextType.InteractiveControl)
								ExitInteractionMode(revertChanges: false);  // Confirm
							else
								ActivateFocusedNode();
							break;
						
						// Exit key - context-aware (Cancel in interaction mode, Close otherwise)
						case "Back":
							ExitGroup();  // Already handles interaction mode internally
							break;
						
						// Slider value adjustment keys - ALWAYS adjust value
						case "SliderDecrease":
							AdjustSliderValue(SliderAdjustment.SmallDecrement);
							break;
						case "SliderIncrease":
							AdjustSliderValue(SliderAdjustment.SmallIncrement);
							break;
						
						// Slider range adjustment keys - ALWAYS adjust range
						case "SliderRangeDecrease":
							AdjustSliderRange(SliderAdjustment.SmallDecrement);
							break;
						case "SliderRangeIncrease":
							AdjustSliderRange(SliderAdjustment.SmallIncrement);
							break;
						
						// Discovery keys
						case "WriteModalFilter":
							WriteModalFilterToDiscovery();
							break;
						case "WriteElementFilter":
							WriteElementFilterToDiscovery();
							break;
						
						default:
							Debug.WriteLine($"[Navigator] Unknown key: {e.KeyName}");
							break;
					}
					
					Debug.WriteLine($"[Navigator] Command completed for '{e.KeyName}'");
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] StreamDeck command error: {ex.Message}");
					Debug.WriteLine($"[Navigator] Stack trace: {ex.StackTrace}");
				}
			}), DispatcherPriority.Input);
		}

		/// <summary>
		/// Executes a shortcut key by finding and focusing the matching element.
		/// ✅ NEW: Supports both Element and Group targeting via TargetType property.
		/// - Element: Targets specific element (existing behavior)
		/// - Group: Targets first navigable child of group container
		/// </summary>
		private static void ExecuteShortcutKey(string keyName)
		{
			if (!_shortcutsByKey.TryGetValue(keyName, out var shortcut))
			{
				Debug.WriteLine($"[Navigator] Shortcut not found: {keyName}");
				return;
			}

			Debug.WriteLine($"[Navigator] Executing shortcut: {shortcut}");

			var candidates = GetCandidatesInScope();
			
			NavNode targetNode = null;
			
			// ✅ NEW: Handle Group vs Element targeting
			if (shortcut.TargetType == ShortcutTargetType.Group)
			{
				Debug.WriteLine($"[Navigator] Group targeting mode - finding group container");
				
				// Find the group container
				NavNode groupNode = FindGroupNode(candidates, shortcut);
				
				if (groupNode != null)
				{
					Debug.WriteLine($"[Navigator] Found group: {groupNode.SimpleName} @ {GetPathWithoutHwnd(groupNode)}");
					
					// Find first navigable child of the group
					targetNode = FindFirstNavigableChild(groupNode);
					
					if (targetNode != null)
					{
						Debug.WriteLine($"[Navigator] ✅ Found first child: '{targetNode.SimpleName}' in group '{groupNode.SimpleName}'");
					}
					else
					{
						Debug.WriteLine($"[Navigator] ❌ Group '{groupNode.SimpleName}' has no navigable children");
					}
				}
				else
				{
					Debug.WriteLine($"[Navigator] ❌ No matching group found for shortcut: {keyName}");
				}
			}
			else // Element targeting (existing behavior)
			{
				Debug.WriteLine($"[Navigator] Element targeting mode - finding specific element");
				Debug.WriteLine($"[Navigator] Searching {candidates.Count} candidates for path: {shortcut.PathFilter}");
				
				foreach (var node in candidates)
				{
					var nodePath = GetPathWithoutHwnd(node);
					
					if (shortcut.Matches(nodePath))
					{
						targetNode = node;
						Debug.WriteLine($"[Navigator] ✅ Found matching element: {node.SimpleName}");
						break;
					}
				}
				
				if (targetNode == null)
				{
					Debug.WriteLine($"[Navigator] ❌ No matching element found for shortcut: {keyName}");
				}
			}

			// Focus and optionally click the target
			if (targetNode != null)
			{
				if (SetFocus(targetNode))
				{
					Debug.WriteLine($"[Navigator] ✅ Focused target: {targetNode.SimpleName}");
					
					// ✅ Auto-click unless NoAutoClick is set
					if (!shortcut.NoAutoClick)
					{
						Debug.WriteLine($"[Navigator] Auto-clicking target: {targetNode.SimpleName}");
						
						if (Application.Current != null)
						{
							Application.Current.Dispatcher.BeginInvoke(
								new Action(() => {
									if (CurrentContext?.FocusedNode == targetNode)
									{
										targetNode.Activate();
									}
									else
									{
										Debug.WriteLine($"[Navigator] Skipped auto-click - focus changed before activation");
									}
								}),
								DispatcherPriority.Input
							);
						}
						else
						{
							targetNode.Activate();
						}
					}
					else
					{
						Debug.WriteLine($"[Navigator] Skipped auto-click (NoAutoClick=true): {targetNode.SimpleName}");
					}
				}
				else
				{
					Debug.WriteLine($"[Navigator] ❌ Failed to focus target: {targetNode.SimpleName}");
				}
			}
		}

		/// <summary>
		/// Finds a group node that matches the shortcut's path filter.
		/// Groups can match even if they're not in the candidates list (they might be filtered out).
		/// </summary>
		private static NavNode FindGroupNode(List<NavNode> candidates, NavShortcutKey shortcut)
		{
			// Get ALL nodes (including groups) from Observer
			var allNodes = Observer.GetAllNavNodes();
			
			// Filter to only nodes in active modal scope
			var scopedNodes = allNodes.Where(n => IsInActiveModalScope(n)).ToList();
			
			Debug.WriteLine($"[Navigator] Searching {scopedNodes.Count} scoped nodes for group");
			
			// Find matching group
			foreach (var node in scopedNodes)
			{
				if (!node.IsGroup) continue; // Only consider groups
				
				var nodePath = GetPathWithoutHwnd(node);
				
				if (shortcut.Matches(nodePath))
				{
					Debug.WriteLine($"[Navigator] Found group node: {node.SimpleName} @ {nodePath}");
					return node;
				}
			}
			
			Debug.WriteLine($"[Navigator] No matching group found");
			return null;
		}

		/// <summary>
		/// Finds the first navigable child of a group node.
		/// Uses top-to-bottom, left-to-right ordering based on screen position.
		/// </summary>
		private static NavNode FindFirstNavigableChild(NavNode groupNode)
		{
			if (groupNode == null) return null;
			
			// Get all nodes
			var allNodes = Observer.GetAllNavNodes();
			
			// Find children of this group that are navigable
			var children = allNodes
				.Where(n => IsNavigableForSelection(n) && IsDescendantOf(n, groupNode))
				.ToList();
			
			if (children.Count == 0)
			{
				Debug.WriteLine($"[Navigator] No navigable children found in group '{groupNode.SimpleName}'");
				return null;
			}
			
			Debug.WriteLine($"[Navigator] Found {children.Count} navigable children in group '{groupNode.SimpleName}'");
			
			// Sort by position (top-to-bottom, left-to-right)
			// Same algorithm as TryInitializeFocusIfNeeded
			var sorted = children
				.Select(n => {
					var center = n.GetCenterDip();
					// Y * 10000 + X ensures top-to-bottom primary sort, left-to-right secondary
					var score = center.HasValue ? center.Value.X + center.Value.Y * 10000.0 : double.MaxValue;
					return new { Node = n, Score = score };
				})
				.OrderBy(x => x.Score)
				.ToList();
			
			var firstChild = sorted.First().Node;
			Debug.WriteLine($"[Navigator] First child: {firstChild.SimpleName} (score: {sorted.First().Score:F1})");
			
			return firstChild;
		}

		#endregion

		#region StreamDeck Helpers

		/// <summary>
		/// Helper to get icon path from discovered icons dictionary.
		/// Returns null if not found (will use text-based icon fallback).
		/// </summary>
		private static string GetIconPath(Dictionary<string, string> icons, string iconName)
		{
			if (icons.TryGetValue(iconName, out var path)) {
				return path;
			}
			Debug.WriteLine($"[Navigator] Icon not found: {iconName}");
			return null;
		}

		#endregion

		#region StreamDeck Discovery

		/// <summary>
		/// Writes the current modal node's filter to the discovery file.
		/// Called when user presses WriteModalFilter key on StreamDeck.
		/// </summary>
		private static void WriteModalFilterToDiscovery()
		{
			try {
				if (CurrentContext?.ScopeNode == null) {
					Debug.WriteLine("[Navigator] No current modal to write filter for");
					return;
				}

				var scopeNode = CurrentContext.ScopeNode;
				var filter = GenerateFilterRule(scopeNode, isModal: true);
				
				AppendToDiscoveryFile($"# Modal: {scopeNode.SimpleName}", filter);
				
				Debug.WriteLine($"[Navigator] ✅ Written modal filter to discovery file: {scopeNode.SimpleName}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write modal filter: {ex.Message}");
			}
		}

		/// <summary>
		/// Writes the currently focused element's filter to the discovery file.
		/// Called when user presses WriteElementFilter key on StreamDeck.
		/// ✅ NEW: Writes BOTH element classification AND its parent group classification.
		/// </summary>
		private static void WriteElementFilterToDiscovery()
		{
			try {
				if (CurrentContext?.FocusedNode == null) {
					Debug.WriteLine("[Navigator] No focused element to write filter for");
					return;
				}

				var focusedNode = CurrentContext.FocusedNode;
				
				// ✅ NEW: Generate element filter
				var elementFilter = GenerateFilterRule(focusedNode, isModal: false, isGroup: false);
				
				// ✅ NEW: Find parent group and generate group filter
				var parentGroup = FindClosestGroup(focusedNode);
				string groupFilter = null;
				
				if (parentGroup != null)
				{
					groupFilter = GenerateFilterRule(parentGroup, isModal: false, isGroup: true);
				}
				
				// Write to discovery file with header
				var headerComment = $"# Focused Element: {focusedNode.SimpleName}";
				
				if (groupFilter != null)
				{
					// Write both: group (for TargetType=Group) and element (for TargetType=Element)
					var combinedEntry = $"{headerComment}\r\n" +
					                   $"# Option 1: Target group (jumps to first item in list)\r\n" +
					                   $"{groupFilter}\r\n" +
					                   $"# Option 2: Target specific element (jumps to this exact element)\r\n" +
					                   $"{elementFilter}\r\n";
					
					AppendToDiscoveryFile(combinedEntry);
				}
				else
				{
					// No parent group found - write element only
					AppendToDiscoveryFile(headerComment, elementFilter);
				}
				
				Debug.WriteLine($"[Navigator] ✅ Written element filter to discovery file: {focusedNode.SimpleName}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write element filter: {ex.Message}");
			}
		}

		/// <summary>
		/// Generates a CLASSIFY filter rule for a given NavNode.
		/// Uses the full HierarchicalPath without abbreviation or HWND.
		/// ✅ NEW: Supports generating rules for groups with TargetType=Group.
		/// </summary>
		private static string GenerateFilterRule(NavNode node, bool isModal, bool isGroup = false)
		{
			if (node == null) return null;

			// Get the full hierarchical path, but strip HWND from it
			var path = GetPathWithoutHwnd(node);

			// Determine role based on node type
			string role;
			if (node.TryGetVisual(out var element)) {
				var typeName = element.GetType().Name;
				role = typeName.ToLowerInvariant();
			} else {
			role = node.IsGroup ? "group" : "element";
			}

			// Build CLASSIFY rule with properties
			var properties = new List<string>();
			properties.Add($"role={role}");
			
			if (isModal) {
				properties.Add("modal=true");
			}
			
			// ✅ NEW: Add TargetType for groups
			if (isGroup) {
				properties.Add("TargetType=\"Group\"");
				properties.Add("NoAutoClick=true");  // Groups should not auto-click
			}
			
			// Add template properties for user to fill in
			properties.Add("KeyName=\"TODO\"");
			properties.Add("KeyTitle=\"TODO\"");

			return $"CLASSIFY: {path} => {string.Join("; ", properties)}";
		}

		/// <summary>
		/// Finds the closest group ancestor of a node (skips modal groups).
		/// Returns the first non-modal group parent, or null if none found.
		/// </summary>
		private static NavNode FindClosestGroup(NavNode node)
		{
			if (node == null) return null;

			var current = node.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (parentNode.IsGroup && !parentNode.IsModal)
				{
					return parentNode;
				}
				current = parentNode.Parent;
			}
			
			return null;
		}

		#endregion
		
		#region StreamDeck Page Switching

		/// <summary>
		/// Switches StreamDeck to the appropriate page for the given scope node.
		/// Uses built-in page selection based on scope node type, or custom page from configuration.
		/// </summary>
		private static void SwitchStreamDeckPageForModal(NavNode scopeNode)
		{
			if (_streamDeckClient == null) return;
			
			try {
				string pageName = null;
				
				// ✅ NEW: Check configuration for custom page mapping first
				if (_navConfig != null && scopeNode != null)
				{
					var scopePath = GetPathWithoutHwnd(scopeNode);
					pageName = _navConfig.FindPageForElement(scopePath);
					
					if (!string.IsNullOrEmpty(pageName))
					{
						Debug.WriteLine($"[Navigator] Using custom page from config: '{pageName}' for scope '{scopeNode.SimpleName}'");
					}
				}
				
				// Fallback to built-in page selection if no custom mapping
				if (string.IsNullOrEmpty(pageName))
				{
					pageName = SelectBuiltInPageForModal(scopeNode);
				}
				
				// Default to Navigation if no specific page selected
				if (string.IsNullOrEmpty(pageName)) {
					pageName = PageNavigation;
				}
				
				// ✅ NOTE: pageName here is just the PageName (not FullPageName)
				// This is correct - we switch to pages by their PageName only
				Debug.WriteLine($"[Navigator] Switching StreamDeck to '{pageName}' page for scope '{scopeNode.SimpleName}'");
				_streamDeckClient.SwitchPage(pageName);
			} catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] Failed to switch StreamDeck page: {ex.Message}");
			}
		}

		#endregion
		
		#region StreamDeck Connection Event Handlers

		private static void OnStreamDeckConnected(object sender, EventArgs e)
		{
			Debug.WriteLine("[Navigator] StreamDeck connected event received");
			
			// ✅ FIX #2: Only show Toast if this is NOT the first connection
			if (_streamDeckHasConnectedAtLeastOnce)
			{
				// This is a REconnection - show Toast
				ActionExtension.InvokeInMainThreadAsync(() =>
				{
					try
					{
						FirstFloor.ModernUI.Windows.Toast.Show(
							"StreamDeck Reconnected",
							"UI Navigation restored"
						);
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[Navigator] Toast notification failed: {ex.Message}");
					}
				});
			}
			else
			{
				// First connection - no Toast (expected behavior at startup)
				Debug.WriteLine("[Navigator] First connection established (no Toast)");
				_streamDeckHasConnectedAtLeastOnce = true;
			}
		}

		private static void OnStreamDeckDisconnected(object sender, EventArgs e)
		{
			Debug.WriteLine("[Navigator] StreamDeck disconnected event received");
			
			// ✅ FIX #2: Only show Toast if we had a successful connection before
			if (_streamDeckHasConnectedAtLeastOnce)
			{
				// Unexpected disconnect after successful connection - show Toast
				ActionExtension.InvokeInMainThreadAsync(() =>
				{
					try
					{
						FirstFloor.ModernUI.Windows.Toast.Show(
							"StreamDeck Disconnected",
							"Attempting to reconnect automatically..."
						);
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[Navigator] Toast notification failed: {ex.Message}");
					}
				});
			}
			else
			{
				// Disconnected before first successful connection - no Toast (startup sequence)
				Debug.WriteLine("[Navigator] Disconnected during startup (no Toast)");
			}
		}

		private static int _lastReconnectAttemptNotified = 0;

		private static void OnStreamDeckReconnecting(object sender, int attemptNumber)
		{
			Debug.WriteLine($"[Navigator] StreamDeck reconnection attempt {attemptNumber}");
			
			// Only show toast every 5 attempts to avoid spam
			if (attemptNumber == 1 || (attemptNumber > 0 && attemptNumber % 5 == 0 && attemptNumber != _lastReconnectAttemptNotified))
			{
				_lastReconnectAttemptNotified = attemptNumber;
				
				ActionExtension.InvokeInMainThreadAsync(() =>
				{
					try
					{
						FirstFloor.ModernUI.Windows.Toast.Show(
							"StreamDeck Reconnecting",
							$"Attempt {attemptNumber}... Please check StreamDeck connection"
						);
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[Navigator] Toast notification failed: {ex.Message}");
					}
				});
			}
		}

		private static void ShowStreamDeckError(string title, string message)
		{
			ActionExtension.InvokeInMainThreadAsync(() =>
			{
				try
				{
					FirstFloor.ModernUI.Windows.Toast.Show(title, message);
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[Navigator] Error toast failed: {ex.Message}");
				}
			});
		}

		#endregion
		
		#region StreamDeck Helpers

		/// <summary>
		/// Gets the hierarchical path without HWND components.
		/// Strips :HWND from each path segment that contains it.
		/// 
		/// Example:
		///   Input: "(unnamed):PopupRoot:3F4A21B > (unnamed):MenuItem"
		///   Output: "(unnamed):PopupRoot > (unnamed):MenuItem"
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

		/// <summary>
		/// Appends a timestamped entry to the discovery file.
		/// Creates the file with session header if it doesn't exist.
		/// Session header is written only once per application run.
		/// ✅ NEW: Supports writing pre-formatted entries (comment + rules combined).
		/// </summary>
		private static void AppendToDiscoveryFile(string commentOrEntry, string filterRule = null)
		{
			try {
				// Get AppData path
				var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				var configDir = Path.Combine(appDataPath, "AcTools Content Manager");
				var discoveryFile = Path.Combine(configDir, "NWRS Navigation Discovery.txt");

				// Ensure directory exists
				Directory.CreateDirectory(configDir);

				// Check if file exists and needs initial header
				bool fileExists = File.Exists(discoveryFile);
				
				// Write file header only if file doesn't exist or is empty
				if (!fileExists || new FileInfo(discoveryFile).Length == 0) {
					var header = "# NWRS Navigation Discovery Session\r\n" +
					            "# Append-only file - copy filters from here to NWRS Navigation.cfg\r\n";
					File.WriteAllText(discoveryFile, header);
					fileExists = true; // File now exists after we created it
				}

				// Write session header ONCE per application run
				if (!_discoverySessionHeaderWritten) {
					// Add empty line before session header (if file has content)
					var sessionHeader = fileExists && new FileInfo(discoveryFile).Length > 0
						? $"\r\n# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n"
						: $"# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n";
					
					File.AppendAllText(discoveryFile, sessionHeader);
					_discoverySessionHeaderWritten = true;
					
					Debug.WriteLine($"[Navigator] Discovery session header written for this app run");
				}

				// Support pre-formatted entries (element + group combined)
				string entry;
				if (filterRule != null) {
					// Old format: separate comment and rule
					entry = $"{commentOrEntry}\r\n{filterRule}\r\n";
				} else {
					// New format: pre-formatted entry (already contains comments and rules)
					entry = $"{commentOrEntry}\r\n";
				}
				
				File.AppendAllText(discoveryFile, entry);

				Debug.WriteLine($"[Navigator] Discovery entry written to: {discoveryFile}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write to discovery file: {ex.Message}");
				
				// Try to log error to error file
				try {
					var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
					var configDir = Path.Combine(appDataPath, "AcTools Content Manager");
					var errorFile = Path.Combine(configDir, "NWRS Navigation Errors.log");
					
					Directory.CreateDirectory(configDir);
					
					var errorEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] Discovery file write failed: {ex.Message}\r\n";
					File.AppendAllText(errorFile, errorEntry);
				} catch {
					// If error logging fails, just give up silently
				}
			}
		}

		#endregion
	}
}
