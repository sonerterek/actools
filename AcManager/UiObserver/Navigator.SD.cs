using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FirstFloor.ModernUI;
using FirstFloor.ModernUI.Helpers;

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
		private static SDPClient _streamDeckClient;
		private static bool _streamDeckHasConnectedAtLeastOnce;
		private static bool _discoverySessionHeaderWritten;
		private static NavConfiguration _navConfig;
		
		/// <summary>
		/// Runtime shortcut keys indexed by KeyName.
		/// Built from Classifications during initialization.
		/// Bound to NavNodes during NodesUpdated processing.
		/// </summary>
		private static Dictionary<string, NavShortcutKey> _shortcutKeysByKeyName = new Dictionary<string, NavShortcutKey>();
		
		private static int _lastReconnectAttemptNotified;

		/// <summary>
		/// Initializes the StreamDeck integration subsystem.
		/// Called once during Navigator initialization.
		/// </summary>
		private static void InitializeStreamDeck()
		{
			Debug.WriteLine("[Navigator] Initializing StreamDeck integration...");
			
			// Build runtime shortcut keys from classifications
			BuildShortcutKeys();
			
			// Discover icons
			var icons = SDPIconHelper.DiscoverIcons();
			Debug.WriteLine($"[Navigator] Discovered {icons.Count} StreamDeck icons");
			
			// Create StreamDeck client
			_streamDeckClient = new SDPClient();
			
			// Hook up events
			_streamDeckClient.KeyPressed += OnStreamDeckKeyPressed;
			_streamDeckClient.ConnectionEstablished += OnStreamDeckConnected;
			_streamDeckClient.ConnectionLost += OnStreamDeckDisconnected;
			_streamDeckClient.ReconnectionAttempt += OnStreamDeckReconnecting;
			
			// ✅ NEW: Hook up game lifecycle events for StreamDeck profile management
			AcManager.Tools.SemiGui.GameWrapper.Started += OnGameStarted;
			AcManager.Tools.SemiGui.GameWrapper.Ended += OnGameEnded;
			
			// Define all keys and pages
			DefineStreamDeckKeys(icons);
			DefineStreamDeckPages();
			
			Debug.WriteLine("[Navigator] StreamDeck initialization complete");
			
			// Connect to StreamDeck plugin
			Task.Run(async () => await _streamDeckClient.ConnectAsync());
		}

		/// <summary>
		/// Builds runtime shortcut key dictionary from classification rules.
		/// Only includes classifications that have KeyName property set.
		/// Called during StreamDeck initialization.
		/// </summary>
		private static void BuildShortcutKeys()
		{
			_shortcutKeysByKeyName.Clear();
			
			if (_navConfig == null) return;
			
			foreach (var rule in _navConfig.Classifications)
			{
				// Skip rules without KeyName (they're not shortcuts)
				if (string.IsNullOrEmpty(rule.KeyName))
					continue;
				
				// Create runtime shortcut from classification rule
				var shortcut = new NavShortcutKey
				{
					KeyName = rule.KeyName,
					KeyTitle = rule.KeyTitle,
					KeyIcon = rule.KeyIcon,
					NoAutoClick = rule.NoAutoClick,
					TargetType = rule.TargetType,
					RequireConfirmation = rule.RequireConfirmation,
					ConfirmationMessage = rule.ConfirmationMessage,
					BoundNode = null  // Not bound yet
				};
				
				_shortcutKeysByKeyName[rule.KeyName] = shortcut;
				
				Debug.WriteLine($"[Navigator] Defined shortcut key: {rule.KeyName} (Title: {rule.KeyTitle})");
			}
			
			Debug.WriteLine($"[Navigator] Built {_shortcutKeysByKeyName.Count} runtime shortcut keys");
		}

		#region StreamDeck Key and Page Definition

		/// <summary>
		/// Defines all StreamDeck pages.
		/// Fire-and-forget: All definitions stored in SDPClient state.
		/// </summary>
		private static void DefineStreamDeckPages()
		{
			if (_streamDeckClient == null || _navConfig == null)
				return;

			try
			{
				// ═══════════════════════════════════════════════════════════
				// STEP 1: Define built-in pages
				// ═══════════════════════════════════════════════════════════
				
				DefineBuiltInPages();

				// ═══════════════════════════════════════════════════════════
				// STEP 2: Define all custom pages from configuration
				// ═══════════════════════════════════════════════════════════
				
				Debug.WriteLine($"[Navigator] Defining {_navConfig.Pages.Count} custom pages...");
				
				foreach (var page in _navConfig.Pages)
				{
					var pageName = page.GetFullPageName(); // e.g., "QuickDrive:Navigation"
					
					Debug.WriteLine($"[Navigator] → DefinePage: {pageName}");
					
					_streamDeckClient.DefinePage(pageName, page.KeyGrid);
				}
				
				Debug.WriteLine($"[Navigator] ✅ All pages defined (will be sent when plugin connects)");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] ❌ DefineStreamDeckPages error: {ex.Message}");
				Logging.Write($"[Navigator] DefineStreamDeckPages error: {ex}");
			}
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

					// ✅ Check if Ctrl+Shift are held down
					var modifiers = Keyboard.Modifiers;
					bool ctrlShiftHeld = modifiers.HasFlag(ModifierKeys.Control) &&
										 modifiers.HasFlag(ModifierKeys.Shift);

					if (ctrlShiftHeld) {
						Debug.WriteLine($"[Navigator] Ctrl+Shift detected with '{e.KeyName}'");

						// Handle discovery commands
						switch (e.KeyName) {
							case "Left":  // Or "Select"
								WriteElementFilterToDiscovery();
								break;
							case "Right":
								WriteModalFilterToDiscovery();
								break; 
							// Add other keys as needed
						}
						return; // Do not process the key further (Ctrl-Shift is held down)
					}

					// ✅ Check if this is a shortcut key first
					if (_shortcutKeysByKeyName.ContainsKey(e.KeyName))
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

						// Exit key (including Interaction mode)
						case "Back":
							// ✅ Check if we would be exiting the application - we need confirmation
							if (CurrentContext?.ScopeNode?.TryGetVisual(out var scopeElement) == true
								&& scopeElement is Window window
								&& window.GetType().Name == "MainWindow")
							{
								RequestConfirmation(
									description: "Exit Application",
									onConfirm: () =>
									{
										Debug.WriteLine($"[Navigator] ✅ Exiting application (user confirmed)");
										Application.Current?.Dispatcher.Invoke(() =>
										{
											Application.Current.Shutdown();
										});
									},
									onCancel: () =>
									{
										Debug.WriteLine($"[Navigator] User cancelled exit, staying in application");
									}
								);
							}
							else
							{
								// Regular back navigation (exit group/modal/interaction mode)
								ExitGroup();
							}
							break;
						// Activation key (not used in Interaction mode any more
						case "MouseLeft":
							ActivateFocusedNode();
							break;
						// Same as MouseLeft. Only used in UpDown for now
						case "Select":
							ActivateFocusedNode();
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

						// Round Slider adjustment keys - ALWAYS adjust VALUE
						case "SliderTurnCCW":
							AdjustSliderValue(SliderAdjustment.SmallDecrement);
							break;
						case "SliderTurnCW":
							AdjustSliderValue(SliderAdjustment.SmallIncrement);
							break;

						// ✅ Confirmation keys
						case "Yes":
							ConfirmAction();
							break;
						case "No":
							CancelAction();
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
		/// Executes a shortcut key using its bound node.
		/// Handles confirmation, TargetType (Element vs Group), and NoAutoClick.
		/// </summary>
		private static void ExecuteShortcutKey(string keyName)
		{
			if (!_shortcutKeysByKeyName.TryGetValue(keyName, out var shortcut))
			{
				Debug.WriteLine($"[Navigator] Shortcut not found: {keyName}");
				return;
			}

			// Check if shortcut is bound to a node
			if (shortcut.BoundNode == null)
			{
				Debug.WriteLine($"[Navigator] ❌ Shortcut '{keyName}' not bound (element not in scope)");
				return;
			}

			Debug.WriteLine($"[Navigator] Executing shortcut: {shortcut}");

			// Handle confirmation
			if (shortcut.RequireConfirmation)
			{
				var confirmMessage = string.IsNullOrEmpty(shortcut.ConfirmationMessage)
					? $"Execute {keyName}"
					: shortcut.ConfirmationMessage;
				
				RequestConfirmation(
					description: confirmMessage,
					onConfirm: () => ExecuteShortcutOnNode(shortcut.BoundNode, shortcut),
					onCancel: () => Debug.WriteLine($"[Navigator] User cancelled '{keyName}'")
				);
				return;
			}

			// Direct execution
			ExecuteShortcutOnNode(shortcut.BoundNode, shortcut);
		}

		/// <summary>
		/// Executes a shortcut on a bound node.
		/// Handles TargetType (Element vs Group) and NoAutoClick.
		/// </summary>
		private static void ExecuteShortcutOnNode(NavNode node, NavShortcutKey shortcut)
		{
			// Handle TargetType=Group
			if (shortcut.TargetType == ShortcutTargetType.Group)
			{
				// Node is the group container - find first navigable child
				var firstChild = FindFirstNavigableChild(node);
				if (firstChild != null)
				{
					if (SetFocus(firstChild))
					{
						Debug.WriteLine($"[Navigator] ✅ Focused first child in group: {firstChild.SimpleName}");
					}
				}
				else
				{
					Debug.WriteLine($"[Navigator] ❌ Group '{node.SimpleName}' has no navigable children");
				}
				return;
			}
			
			// Handle TargetType=Element (default)
			if (SetFocus(node))
			{
				if (!shortcut.NoAutoClick)
				{
					node.Activate();
				}
			}
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

		/// <summary>
		/// Finds the first navigable child of a group node.
		/// Uses top-to-bottom, left-to-right ordering based on screen position.
		/// Called when executing a shortcut with TargetType=Group.
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
		
		/// <summary>
		/// Binds shortcut keys to their matching NavNodes based on KeyName property.
		/// Called during Observer.NodesUpdated event to update bindings as nodes are added/removed.
		/// </summary>
		private static void BindShortcutsToNodes()
		{
			if (_shortcutKeysByKeyName.Count == 0) return;
			
			// Clear all existing bindings
			foreach (var shortcut in _shortcutKeysByKeyName.Values)
			{
				shortcut.BoundNode = null;
			}
			
			// Get all discovered nodes
			var allNodes = Observer.GetAllNavNodes();
			
			// Bind shortcuts to nodes with matching KeyName
			int boundCount = 0;
			foreach (var node in allNodes)
			{
				if (string.IsNullOrEmpty(node.KeyName)) continue;
				
				if (_shortcutKeysByKeyName.TryGetValue(node.KeyName, out var shortcut))
				{
					shortcut.BoundNode = node;
					boundCount++;
					
					if (VerboseNavigationDebug)
					{
						Debug.WriteLine($"[Navigator] Bound shortcut '{node.KeyName}' → '{node.SimpleName}'");
					}
				}
			}
			
			if (boundCount > 0 || VerboseNavigationDebug)
			{
				Debug.WriteLine($"[Navigator] Bound {boundCount}/{_shortcutKeysByKeyName.Count} shortcuts to nodes");
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
				
				// ✅ REMOVED: Async validation is not needed
				// The plugin's successful replication IS the validation
				// Individual KeyDefined/PageDefined events are only sent when
				// keys/pages are defined AFTER initial connection, not during replication
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
		
		/// <summary>
		/// Called when game/simulator starts launching.
		/// Switches StreamDeck to the ACS profile for in-game controls.
		/// </summary>
		private static void OnGameStarted(object sender, AcManager.Tools.SemiGui.GameStartedArgs e)
		{
			Debug.WriteLine($"[Navigator] Game started: {e.Mode}, switching to ACS profile");
			_streamDeckClient?.SwitchProfile("ACS");
		}
		
		/// <summary>
		/// Called when game/simulator exits.
		/// Switches StreamDeck back to the previous profile (NWRS AC).
		/// </summary>
		private static void OnGameEnded(object sender, AcManager.Tools.SemiGui.GameEndedArgs e)
		{
			Debug.WriteLine($"[Navigator] Game ended, switching back from ACS profile");
			_streamDeckClient?.SwitchProfileBack();
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
		
		#region StreamDeck Definition Error Tracking

		// NOTE: Individual KeyDefined/PageDefined result events are only sent
		// when keys/pages are defined AFTER the initial connection.
		// During initial connection, the plugin replicates all state in bulk
		// and reports success/failure for the entire replication, not per-item.
		//
		// Therefore, we rely on the replication success as validation.
		// If you need per-item validation, you would need to:
		// 1. Wait for replication to complete
		// 2. Send individual DefineKey/DefinePage commands
		// 3. Wait for individual result events
		//
		// For now, we trust that replication success means all definitions worked.
		
		/// <summary>
		/// Callback for KeyDefined result events from StreamDeck plugin.
		/// Called by SDPClient when it receives a KeyDefined message.
		/// NOTE: These are only sent for keys defined AFTER initial connection.
		/// </summary>
		internal static void OnKeyDefinedResult(dynamic message)
		{
			try
			{
				string keyName = message.KeyName;
				bool success = message.Success;
				string error = message.Error;
				
				if (success)
				{
					Debug.WriteLine($"[Navigator] ✅ KeyDefined: {keyName}");
				}
				else
				{
					Debug.WriteLine($"[Navigator] ❌ KeyDefined FAILED: {keyName}");
					Debug.WriteLine($"[Navigator]    Error: {error}");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] OnKeyDefinedResult error: {ex.Message}");
			}
		}
		
		/// <summary>
		/// Callback for PageDefined result events from StreamDeck plugin.
		/// Called by SDPClient when it receives a PageDefined message.
		/// NOTE: These are only sent for pages defined AFTER initial connection.
		/// </summary>
		internal static void OnPageDefinedResult(dynamic message)
		{
			try
			{
				string pageName = message.PageName;
				bool success = message.Success;
				string error = message.Error;
				
				if (success)
				{
					Debug.WriteLine($"[Navigator] ✅ PageDefined: {pageName}");
				}
				else
				{
					Debug.WriteLine($"[Navigator] ❌ PageDefined FAILED: {pageName}");
					Debug.WriteLine($"[Navigator]    Error: {error}");
					Debug.WriteLine($"[Navigator]    This usually means the page references undefined keys");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] OnPageDefinedResult error: {ex.Message}");
			}
		}

		#endregion
	}
}
