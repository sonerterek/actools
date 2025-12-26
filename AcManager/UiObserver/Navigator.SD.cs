using System;
using System.Collections.Generic;
using System.Diagnostics;
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
				
				// Try to connect (non-blocking)
				Task.Run(async () => {
					try {
						Debug.WriteLine("[Navigator] Connecting to StreamDeck plugin...");
						
						bool connected = await _streamDeckClient.ConnectAsync();
						if (!connected) {
							Debug.WriteLine("[Navigator] ?? StreamDeck plugin not available - Please ensure:");
							Debug.WriteLine("[Navigator]   1. StreamDeck plugin is installed");
							Debug.WriteLine("[Navigator]   2. StreamDeck plugin is running");
							Debug.WriteLine("[Navigator]   3. Named pipe 'NWRS_AC_SDPlugin_Pipe' is created");
							Debug.WriteLine("[Navigator] StreamDeck navigation will not be available.");
							return;
						}
						
						Debug.WriteLine("[Navigator] ? StreamDeck connected, setting up navigation page...");
						
						// Discover icons from Assets/SDIcons directory
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
							new SDPKeyDef("Close", "", GetIconPath(icons, "Close"))
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
						
						Debug.WriteLine($"[Navigator] ? Defined {keyResult.SuccessCount} StreamDeck keys");
						
						// Create navigation page (5x3 grid)
						Debug.WriteLine("[Navigator] Creating Navigation page...");
						var navPage = new SDPPageDef("Navigation");
						// Row 0: [Back] [empty] [empty]
						navPage.SetKey(0, 0, "Back");
						// Row 2: [empty] [Up] [empty]
						navPage.SetKey(2, 1, "Up");
						// Row 3: [Left] [MouseLeft] [Right]
						navPage.SetKey(3, 0, "Left");
						navPage.SetKey(3, 1, "MouseLeft");
						navPage.SetKey(3, 2, "Right");
						// Row 4: [empty] [Down] [empty]
						navPage.SetKey(4, 1, "Down");
						
						Debug.WriteLine("[Navigator] Sending DefinePage command...");
						var pageResult = await _streamDeckClient.DefinePageAsync(navPage);
						
						Debug.WriteLine($"[Navigator] Page definition result: Success={pageResult.Success}");
						
						if (!pageResult.Success) {
							Debug.WriteLine($"[Navigator] ? Failed to define navigation page: {pageResult.ErrorMessage}");
							return;
						}
						
						Debug.WriteLine("[Navigator] ? Defined Navigation page");
						
						// Switch to navigation page
						Debug.WriteLine("[Navigator] Switching to Navigation page...");
						bool switched = _streamDeckClient.SwitchPage("Navigation");
						Debug.WriteLine($"[Navigator] SwitchPage result: {switched}");
						
						// Subscribe to key presses
						_streamDeckClient.KeyPressed += OnStreamDeckKeyPressed;
						
						_streamDeckEnabled = true;
						Debug.WriteLine("[Navigator] ? StreamDeck integration ready");
						
					} catch (Exception ex) {
						Debug.WriteLine($"[Navigator] ? StreamDeck setup error: {ex.Message}");
						Debug.WriteLine($"[Navigator] Stack trace: {ex.StackTrace}");
					}
				});
				
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] ? StreamDeck initialization error: {ex.Message}");
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
	}
}
