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
			Debug.WriteLine("[Navigator] Starting StreamDeck initialization...");

			// Create client
			_streamDeckClient = new SDPClient { SDPVerboseDebug = true };

			// Subscribe to events FIRST (before any connection attempt)
			Debug.WriteLine("[Navigator] Subscribing to KeyPressed event...");
			_streamDeckClient.KeyPressed += OnStreamDeckKeyPressed;
			Debug.WriteLine("[Navigator] KeyPressed event subscription complete");

			// Discover icons
			Debug.WriteLine("[Navigator] Discovering icons...");
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

			// Define all keys (stored in state, sent when connected)
			Debug.WriteLine("[Navigator] Defining navigation keys...");
			_streamDeckClient.DefineKey("Back", "", GetIconPath(icons, "Back"));
			_streamDeckClient.DefineKey("MouseLeft", "", GetIconPath(icons, "Mouse Left"));
			_streamDeckClient.DefineKey("Up", "", GetIconPath(icons, "Up"));
			_streamDeckClient.DefineKey("Down", "", GetIconPath(icons, "Down"));
			_streamDeckClient.DefineKey("Right", "", GetIconPath(icons, "Right"));
			_streamDeckClient.DefineKey("Left", "", GetIconPath(icons, "Left"));
			_streamDeckClient.DefineKey("Close", "", GetIconPath(icons, "Close"));
			_streamDeckClient.DefineKey("WriteModalFilter", "Modal", "!M");
			_streamDeckClient.DefineKey("WriteElementFilter", "Element", "!E");
			Debug.WriteLine($"[Navigator] ✅ Defined {_streamDeckClient.KeyCount} keys in authoritative state");

			// Define all pages
			Debug.WriteLine("[Navigator] Defining navigation pages...");
			DefineBuiltInPages();
			Debug.WriteLine($"[Navigator] ✅ Defined {_streamDeckClient.PageCount} pages in authoritative state");

			// Set initial page
			_streamDeckClient.SwitchPage(PageNavigation);
			Debug.WriteLine($"[Navigator] ✅ Set initial page to '{PageNavigation}'");

			// Start connection attempt (fire-and-forget, non-blocking)
			Debug.WriteLine("[Navigator] Connecting to StreamDeck plugin...");
			Task.Run(() => _streamDeckClient.ConnectAsync());
			Debug.WriteLine("[Navigator] ✅ StreamDeck initialization complete (connection in progress)");

			// Try to disable WPF tooltips (best-effort, doesn't affect functionality)
			try
			{
				var tooltipServiceType = typeof(System.Windows.Controls.ToolTipService);
				var showDurationProperty = tooltipServiceType.GetField("ShowDurationProperty", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
				if (showDurationProperty != null)
				{
					var dp = (System.Windows.DependencyProperty)showDurationProperty.GetValue(null);
					dp.OverrideMetadata(typeof(System.Windows.DependencyObject), new System.Windows.FrameworkPropertyMetadata(0));
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] Failed to disable tooltips: {ex.Message}");
			}
		}

		#endregion

		#region StreamDeck Key and Page Definition

		/// <summary>
		/// Defines the built-in page templates.
		/// Uses fire-and-forget API - all definitions stored in SDPClient.
		/// </summary>
		private static void DefineBuiltInPages()
		{
			// Navigation page (full 6-direction navigation)
			_streamDeckClient.DefinePage(PageNavigation, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new string[] { null, null, null },
				new[] { null, "Up", null },
				new[] { "Left", "MouseLeft", "Right" },
				new[] { null, "Down", null }
			});
			
			// UpDown page (vertical navigation only, for menus)
			_streamDeckClient.DefinePage(PageUpDown, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new string[] { null, null, null },
				new[] { null, "Up", null },
				new[] { null, "MouseLeft", null },
				new[] { null, "Down", null }
			});
			
			// Slider page (left/right for value adjustment)
			_streamDeckClient.DefinePage(PageSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new string[] { null, null, null },
				new string[] { null, null, null },
				new[] { "Left", "MouseLeft", "Right" },
				new string[] { null, null, null }
			});
			
			// DoubleSlider page (up/down for coarse, left/right for fine)
			_streamDeckClient.DefinePage(PageDoubleSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new string[] { null, null, null },
				new[] { null, "Up", null },
				new[] { "Left", "MouseLeft", "Right" },
				new[] { null, "Down", null }
			});
			
			// RoundSlider page (all 4 directions for circular control)
			_streamDeckClient.DefinePage(PageRoundSlider, new[] {
				new[] { "Back", "WriteModalFilter", "WriteElementFilter" },
				new string[] { null, null, null },
				new[] { null, "Up", null },
				new[] { "Left", "MouseLeft", "Right" },
				new[] { null, "Down", null }
			});
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
			Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed ENTRY: KeyName={e.KeyName}");
			Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed: Sender={sender?.GetType().Name ?? "null"}");
			Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed: Application.Current={Application.Current != null}");
			
			Debug.WriteLine($"[Navigator] StreamDeck key pressed: {e.KeyName}");
			
			// Marshal to UI thread (StreamDeck events fire on background thread)
			Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
				try {
					Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed: Executing on UI thread for '{e.KeyName}'");
					
					switch (e.KeyName) {
						case "Up":
							Debug.WriteLine($"[Navigator] Executing MoveInDirection(Up)");
							MoveInDirection(NavDirection.Up);
							break;
						case "Down":
							Debug.WriteLine($"[Navigator] Executing MoveInDirection(Down)");
							MoveInDirection(NavDirection.Down);
							break;
						case "Left":
							Debug.WriteLine($"[Navigator] Executing MoveInDirection(Left)");
							MoveInDirection(NavDirection.Left);
							break;
						case "Right":
							Debug.WriteLine($"[Navigator] Executing MoveInDirection(Right)");
							MoveInDirection(NavDirection.Right);
							break;
						case "MouseLeft":
							Debug.WriteLine($"[Navigator] Executing ActivateFocusedNode()");
							ActivateFocusedNode();
							break;
						case "Back":
							Debug.WriteLine($"[Navigator] Executing ExitGroup()");
							ExitGroup();
							break;
						// Discovery keys
						case "WriteModalFilter":
							Debug.WriteLine($"[Navigator] Executing WriteModalFilterToDiscovery()");
							WriteModalFilterToDiscovery();
							break;
						case "WriteElementFilter":
							Debug.WriteLine($"[Navigator] Executing WriteElementFilterToDiscovery()");
							WriteElementFilterToDiscovery();
							break;
						default:
							Debug.WriteLine($"[Navigator] Unknown key: {e.KeyName}");
							break;
					}
					
					Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed: Command completed for '{e.KeyName}'");
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] StreamDeck command error: {ex.Message}");
					Debug.WriteLine($"[Navigator] StreamDeck command stack trace: {ex.StackTrace}");
				}
			}), DispatcherPriority.Input);
			
			Debug.WriteLine($"[Navigator] OnStreamDeckKeyPressed: Dispatched to UI thread for '{e.KeyName}'");
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
				
				// Add empty line before session header (if file has content)
				var sessionHeader = fileExists && new System.IO.FileInfo(discoveryFile).Length > 0
					? $"\r\n# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n"
					: $"# ===== Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n";
				
				System.IO.File.AppendAllText(discoveryFile, sessionHeader);

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
			if (_streamDeckClient == null) return;
			
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
