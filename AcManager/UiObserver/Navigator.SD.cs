using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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
		private static bool _streamDeckEnabled = false;

		// Session tracking for discovery file
		private static bool _discoverySessionHeaderWritten = false;

		// Built-in page names
		private const string PageNavigation = "Navigation";
		private const string PageUpDown = "UpDown";
		private const string PageSlider = "Slider";
		private const string PageDoubleSlider = "DoubleSlider";
		private const string PageRoundSlider = "RoundSlider";

		#endregion

		#region StreamDeck Initialization

		/// <summary>
		/// Initialize StreamDeck integration.
		/// Discovers icons, defines keys/pages, and connects to plugin.
		/// </summary>
		private static void InitializeStreamDeck()
		{
			try {
				// Initialize client with verbose debugging enabled
				_streamDeckClient = new SDPClient { VerboseDebug = true };
				
				// Kick off async setup (non-blocking)
				Task.Run(async () => {
					try {
						Debug.WriteLine("[Navigator] Starting StreamDeck initialization...");
						
						// Start connection attempt (don't await yet)
						Debug.WriteLine("[Navigator] Connecting to StreamDeck plugin...");
						var connectTask = _streamDeckClient.ConnectAsync();
						
						// While connection is in progress, discover icons from Assets/SDIcons directory
						Debug.WriteLine("[Navigator] Discovering icons while connecting...");
						var icons = SDPIconHelper.DiscoverIcons();
						Debug.WriteLine($"[Navigator] Found {icons.Count} icons");
						
						if (icons.Count > 0)
						{
							Debug.WriteLine("[Navigator] Available icons:");
							foreach (var icon in icons)
							{
								Debug.WriteLine($"[Navigator]   - {icon.Key} = {icon.Value}");
							}
						}
						
						// Define navigation keys with icons from SDIcons
						var keyDefs = new List<SDPKeyDef>
						{
							new SDPKeyDef("Back", "", GetIconPath(icons, "Back")),
							new SDPKeyDef("MouseLeft", "", GetIconPath(icons, "Mouse Left")),
							new SDPKeyDef("Up", "", GetIconPath(icons, "Up")),
							new SDPKeyDef("Down", "", GetIconPath(icons, "Down")),
							new SDPKeyDef("Right", "", GetIconPath(icons, "Right")),
							new SDPKeyDef("Left", "", GetIconPath(icons, "Left")),
							new SDPKeyDef("Close", "", GetIconPath(icons, "Close")),
							// Discovery keys (text-based icons)
							new SDPKeyDef("WriteModalFilter", "Modal", ""),
							new SDPKeyDef("WriteElementFilter", "Element", "")
						};
						
						// Now wait for connection to complete
						Debug.WriteLine("[Navigator] Waiting for connection to complete...");
						bool connected = await connectTask;
						
						if (!connected) {
							Debug.WriteLine("[Navigator] ⚠️ StreamDeck plugin not available - Please ensure:");
							Debug.WriteLine("[Navigator]   1. StreamDeck plugin is installed");
							Debug.WriteLine("[Navigator]   2. StreamDeck plugin is running");
							Debug.WriteLine("[Navigator]   3. Named pipe 'NWRS_AC_SDPlugin_Pipe' is created");
							Debug.WriteLine("[Navigator] StreamDeck navigation will not be available.");
							return;
						}
						
						Debug.WriteLine("[Navigator] ✅ StreamDeck connected, setting up navigation page...");
						
						// Define keys and pages
						await DefineKeysAndPages(icons);
						
						// Switch to navigation page
						Debug.WriteLine("[Navigator] Switching to Navigation page...");
						bool switched = _streamDeckClient.SwitchPage(PageNavigation);
						Debug.WriteLine($"[Navigator] SwitchPage result: {switched}");
						
						// Subscribe to key presses
						_streamDeckClient.KeyPressed += OnStreamDeckKeyPressed;
						
						_streamDeckEnabled = true;
						Debug.WriteLine("[Navigator] ✅ StreamDeck integration ready");
						
					} catch (Exception ex) {
						Debug.WriteLine($"[Navigator] ❌ StreamDeck setup error: {ex.Message}");
						Debug.WriteLine($"[Navigator] Stack trace: {ex.StackTrace}");
					}
				});
				
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ StreamDeck initialization error: {ex.Message}");
			}
		}

		#endregion

		#region StreamDeck Key and Page Definition

		/// <summary>
		/// Defines built-in keys and pages, then loads additional definitions from config file.
		/// </summary>
		private static async Task DefineKeysAndPages(Dictionary<string, string> icons)
		{
			// Step 1: Define built-in navigation keys
			var keyDefs = new List<SDPKeyDef>
			{
				new SDPKeyDef("Back", "", GetIconPath(icons, "Back")),
				new SDPKeyDef("MouseLeft", "", GetIconPath(icons, "Mouse Left")),
				new SDPKeyDef("Up", "", GetIconPath(icons, "Up")),
				new SDPKeyDef("Down", "", GetIconPath(icons, "Down")),
				new SDPKeyDef("Right", "", GetIconPath(icons, "Right")),
				new SDPKeyDef("Left", "", GetIconPath(icons, "Left")),
				new SDPKeyDef("Close", "", GetIconPath(icons, "Close")),
				// Discovery keys (text-based icons)
				new SDPKeyDef("WriteModalFilter", "Modal", "!M"),
				new SDPKeyDef("WriteElementFilter", "Element", "!E")
			};
			
			Debug.WriteLine($"[Navigator] Defining {keyDefs.Count} keys...");
			
			// Define all keys
			var keyResult = await _streamDeckClient.DefineKeysAsync(keyDefs);
			
			Debug.WriteLine($"[Navigator] Key definition result: {keyResult.SuccessCount}/{keyResult.TotalCount} succeeded");
			
			if (!keyResult.AllSucceeded) {
				Debug.WriteLine($"[Navigator] Failed to define {keyResult.FailedKeys.Count} keys");
				Debug.WriteLine($"[Navigator] Error details:");
				Debug.WriteLine(keyResult.ErrorSummary);
				return;
			}
			
			Debug.WriteLine($"[Navigator] ✅ Defined {keyResult.SuccessCount} StreamDeck keys");
			
			// Step 2: Define built-in pages
			await DefineBuiltInPages();
			
			// Step 3: TODO - Load additional pages from config file
			// LoadConfigFile();
		}

		/// <summary>
		/// Defines the built-in page templates.
		/// </summary>
		private static async Task DefineBuiltInPages()
		{
			// Navigation page (full 6-direction navigation)
			Debug.WriteLine($"[Navigator] Creating {PageNavigation} page...");
			var navPage = new SDPPageDef(PageNavigation);
			navPage.SetKey(0, 0, "Back");
			navPage.SetKey(0, 1, "WriteModalFilter");
			navPage.SetKey(0, 2, "WriteElementFilter");
			navPage.SetKey(2, 1, "Up");
			navPage.SetKey(3, 0, "Left");
			navPage.SetKey(3, 1, "MouseLeft");
			navPage.SetKey(3, 2, "Right");
			navPage.SetKey(4, 1, "Down");
			
			var navResult = await _streamDeckClient.DefinePageAsync(navPage);
			if (!navResult.Success) {
				Debug.WriteLine($"[Navigator] ❌ Failed to define {PageNavigation} page: {navResult.ErrorMessage}");
				return;
			}
			Debug.WriteLine($"[Navigator] ✅ Defined {PageNavigation} page");
			
			// UpDown page (vertical navigation only, for menus)
			Debug.WriteLine($"[Navigator] Creating {PageUpDown} page...");
			var upDownPage = new SDPPageDef(PageUpDown);
			upDownPage.SetKey(0, 0, "Back");
			upDownPage.SetKey(0, 1, "WriteModalFilter");
			upDownPage.SetKey(0, 2, "WriteElementFilter");
			upDownPage.SetKey(2, 1, "Up");
			upDownPage.SetKey(3, 1, "MouseLeft");
			upDownPage.SetKey(4, 1, "Down");
			
			var upDownResult = await _streamDeckClient.DefinePageAsync(upDownPage);
			if (!upDownResult.Success) {
				Debug.WriteLine($"[Navigator] ❌ Failed to define {PageUpDown} page: {upDownResult.ErrorMessage}");
				return;
			}
			Debug.WriteLine($"[Navigator] ✅ Defined {PageUpDown} page");
			
			// Slider page (left/right for value adjustment)
			Debug.WriteLine($"[Navigator] Creating {PageSlider} page...");
			var sliderPage = new SDPPageDef(PageSlider);
			sliderPage.SetKey(0, 0, "Back");
			sliderPage.SetKey(0, 1, "WriteModalFilter");
			sliderPage.SetKey(0, 2, "WriteElementFilter");
			sliderPage.SetKey(3, 0, "Left");
			sliderPage.SetKey(3, 1, "MouseLeft");
			sliderPage.SetKey(3, 2, "Right");
			
			var sliderResult = await _streamDeckClient.DefinePageAsync(sliderPage);
			if (!sliderResult.Success) {
				Debug.WriteLine($"[Navigator] ❌ Failed to define {PageSlider} page: {sliderResult.ErrorMessage}");
				return;
			}
			Debug.WriteLine($"[Navigator] ✅ Defined {PageSlider} page");
			
			// DoubleSlider page (up/down for coarse, left/right for fine)
			Debug.WriteLine($"[Navigator] Creating {PageDoubleSlider} page...");
			var doubleSliderPage = new SDPPageDef(PageDoubleSlider);
			doubleSliderPage.SetKey(0, 0, "Back");
			doubleSliderPage.SetKey(0, 1, "WriteModalFilter");
			doubleSliderPage.SetKey(0, 2, "WriteElementFilter");
			doubleSliderPage.SetKey(2, 1, "Up");
			doubleSliderPage.SetKey(3, 0, "Left");
			doubleSliderPage.SetKey(3, 1, "MouseLeft");
			doubleSliderPage.SetKey(3, 2, "Right");
			doubleSliderPage.SetKey(4, 1, "Down");
			
			var doubleSliderResult = await _streamDeckClient.DefinePageAsync(doubleSliderPage);
			if (!doubleSliderResult.Success) {
				Debug.WriteLine($"[Navigator] ❌ Failed to define {PageDoubleSlider} page: {doubleSliderResult.ErrorMessage}");
				return;
			}
			Debug.WriteLine($"[Navigator] ✅ Defined {PageDoubleSlider} page");
			
			// RoundSlider page (all 4 directions for circular control)
			Debug.WriteLine($"[Navigator] Creating {PageRoundSlider} page...");
			var roundSliderPage = new SDPPageDef(PageRoundSlider);
			roundSliderPage.SetKey(0, 0, "Back");
			roundSliderPage.SetKey(0, 1, "WriteModalFilter");
			roundSliderPage.SetKey(0, 2, "WriteElementFilter");
			roundSliderPage.SetKey(2, 1, "Up");
			roundSliderPage.SetKey(3, 0, "Left");
			roundSliderPage.SetKey(3, 1, "MouseLeft");
			roundSliderPage.SetKey(3, 2, "Right");
			roundSliderPage.SetKey(4, 1, "Down");
			
			var roundSliderResult = await _streamDeckClient.DefinePageAsync(roundSliderPage);
			if (!roundSliderResult.Success) {
				Debug.WriteLine($"[Navigator] ❌ Failed to define {PageRoundSlider} page: {roundSliderResult.ErrorMessage}");
				return;
			}
			Debug.WriteLine($"[Navigator] ✅ Defined {PageRoundSlider} page");
		}

		/// <summary>
		/// Selects the appropriate built-in page for a modal based on its type.
		/// Returns page name, or null to use default Navigation page.
		/// </summary>
		private static string SelectBuiltInPageForModal(NavNode modalNode)
		{
			if (modalNode == null) return null;
			
			// Check if modal contains menu-like controls (use UpDown page)
			if (modalNode.TryGetVisual(out var element)) {
				var typeName = element.GetType().Name;
				
				// Menu types use vertical-only navigation
				if (typeName == "ContextMenu" || typeName == "Menu" || typeName == "PopupRoot") {
					// PopupRoot is the container for Menu/ContextMenu dropdowns
					// Check if it contains MenuItems (vertical navigation only)
					var children = Observer.GetAllNavNodes()
						.Where(n => IsDescendantOf(n, modalNode))
						.ToList();
					
					// If contains MenuItem types, use UpDown page
					foreach (var child in children) {
						if (child.TryGetVisual(out var childElement)) {
							var childTypeName = childElement.GetType().Name;
							if (childTypeName == "MenuItem" || childTypeName == "HierarchicalItem") {
								Debug.WriteLine($"[Navigator] Modal '{modalNode.SimpleName}' contains MenuItems → using {PageUpDown} page");
								return PageUpDown;
							}
						}
					}
				}
			}
			
			// Default: use Navigation page
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
			if (!_streamDeckEnabled) return;
			
			Debug.WriteLine($"[Navigator] StreamDeck key pressed: {e.KeyName}");
			
			// Marshal to UI thread (StreamDeck events fire on background thread)
			Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
				try {
					switch (e.KeyName) {
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
						case "MouseLeft":
							ActivateFocusedNode();
							break;
						case "Back":
							ExitGroup();
							break;
						// Discovery keys
						case "WriteModalFilter":
							WriteModalFilterToDiscovery();
							break;
						case "WriteElementFilter":
							WriteElementFilterToDiscovery();
							break;
					}
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] StreamDeck command error: {ex.Message}");
				}
			}), DispatcherPriority.Input);
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
				if (CurrentContext?.ModalNode == null) {
					Debug.WriteLine("[Navigator] No current modal to write filter for");
					return;
				}

				var modalNode = CurrentContext.ModalNode;
				var filter = GenerateFilterRule(modalNode, isModal: true);
				
				AppendToDiscoveryFile($"# Modal: {modalNode.SimpleName}", filter);
				
				Debug.WriteLine($"[Navigator] ✅ Written modal filter to discovery file: {modalNode.SimpleName}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write modal filter: {ex.Message}");
			}
		}

		/// <summary>
		/// Writes the currently focused element's filter to the discovery file.
		/// Called when user presses WriteElementFilter key on StreamDeck.
		/// </summary>
		private static void WriteElementFilterToDiscovery()
		{
			try {
				if (CurrentContext?.FocusedNode == null) {
					Debug.WriteLine("[Navigator] No focused element to write filter for");
					return;
				}

				var focusedNode = CurrentContext.FocusedNode;
				var filter = GenerateFilterRule(focusedNode, isModal: false);
				
				AppendToDiscoveryFile($"# Focused: {focusedNode.SimpleName}", filter);
				
				Debug.WriteLine($"[Navigator] ✅ Written element filter to discovery file: {focusedNode.SimpleName}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write element filter: {ex.Message}");
			}
		}

		/// <summary>
		/// Generates a CLASSIFY filter rule for a given NavNode.
		/// Uses the full HierarchicalPath without abbreviation or HWND.
		/// </summary>
		private static string GenerateFilterRule(NavNode node, bool isModal)
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

			// Build CLASSIFY rule
			var properties = $"role={role}";
			
			if (isModal) {
				properties += "; modal=true";
			}

			return $"CLASSIFY: {path} => {properties}";
		}

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
		/// </summary>
		private static void AppendToDiscoveryFile(string comment, string filterRule)
		{
			try {
				// Get AppData path
				var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				var configDir = System.IO.Path.Combine(appDataPath, "AcTools Content Manager");
				var discoveryFile = System.IO.Path.Combine(configDir, "NWRS Navigation Discovery.txt");

				// Ensure directory exists
				System.IO.Directory.CreateDirectory(configDir);

				// Check if file exists and needs initial header
				bool fileExists = System.IO.File.Exists(discoveryFile);
				
				// If file doesn't exist or is empty, add initial header
				if (!fileExists || new System.IO.FileInfo(discoveryFile).Length == 0) {
					var header = "# NWRS Navigation Discovery Session\r\n" +
					            "# Append-only file - copy filters from here to NWRS Navigation.cfg\r\n";
					System.IO.File.WriteAllText(discoveryFile, header);
				}

				// Write session header once per application run (first discovery write of this session)
				if (!_discoverySessionHeaderWritten) {
					// Add empty line before session header (if file has content)
					var sessionHeader = fileExists && new System.IO.FileInfo(discoveryFile).Length > 0
						? $"\r\n# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n"
						: $"# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n";
					
					System.IO.File.AppendAllText(discoveryFile, sessionHeader);
					_discoverySessionHeaderWritten = true;
				}

				// Write the comment and filter rule (no extra empty lines)
				var entry = $"{comment}\r\n{filterRule}\r\n";
				System.IO.File.AppendAllText(discoveryFile, entry);

				Debug.WriteLine($"[Navigator] Discovery entry written to: {discoveryFile}");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ❌ Failed to write to discovery file: {ex.Message}");
				
				// Try to log error to error file
				try {
					var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
					var configDir = System.IO.Path.Combine(appDataPath, "AcTools Content Manager");
					var errorFile = System.IO.Path.Combine(configDir, "NWRS Navigation Errors.log");
					
					System.IO.Directory.CreateDirectory(configDir);
					
					var errorEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] Discovery file write failed: {ex.Message}\r\n";
					System.IO.File.AppendAllText(errorFile, errorEntry);
				} catch {
					// If error logging fails, just give up silently
				}
			}
		}

		#endregion

		#region StreamDeck Page Switching

		/// <summary>
		/// Switches StreamDeck to the appropriate page for the given modal.
		/// Uses built-in page selection based on modal type.
		/// </summary>
		private static void SwitchStreamDeckPageForModal(NavNode modalNode)
		{
			if (!_streamDeckEnabled || _streamDeckClient == null) return;
			
			try {
				// Select appropriate page based on modal type
				var pageName = SelectBuiltInPageForModal(modalNode);
				
				// Default to Navigation if no specific page selected
				if (string.IsNullOrEmpty(pageName)) {
					pageName = PageNavigation;
				}
				
				Debug.WriteLine($"[Navigator] Switching StreamDeck to '{pageName}' page for modal '{modalNode.SimpleName}'");
				_streamDeckClient.SwitchPage(pageName);
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to switch StreamDeck page: {ex.Message}");
			}
		}

		#endregion
	}
}
