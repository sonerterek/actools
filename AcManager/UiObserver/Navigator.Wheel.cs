using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AcManager.Tools.Helpers.DirectInput;
using AcManager.Tools.SemiGui;
using FirstFloor.ModernUI;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Serialization;
using FirstFloor.ModernUI.Windows.Controls;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator - Racing Wheel Button Integration
	/// 
	/// Provides navigation control via 6 wheel buttons (Up/Down/Left/Right/Select/Back).
	/// ✅ EFFICIENT POLLING: Only polls when enabled, sleeps during gameplay.
	/// ✅ GAME LIFECYCLE: Auto-disable during gameplay (follows StreamDeck pattern).
	/// ✅ PRODUCT-BASED CONFIG: Uses ProductId for device identity (persists across reconnects).
	/// </summary>
	internal static partial class Navigator
	{
		#region Fields

		private static DirectInputScanner.Watcher _wheelWatcher;
		private static DirectInputDevice _navigationWheel;
		private static int[] _wheelButtonMapping = new int[6]; // Map to physical button indices
		private static bool[] _lastButtonStates = new bool[6];
		private static bool _wheelNavigationEnabled;
		private static System.Threading.Timer _wheelPollTimer;  // ✅ Background thread timer (not DispatcherTimer)
		private static readonly object _wheelStateLock = new object();  // ✅ Thread-safety for shared state

		// Button names for debug output
		private static readonly string[] _stepNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };

		/// <summary>
		/// Default button mappings for common wheels (ProductId prefix → button array).
		/// Key format: First 9 chars of ProductId (VID-PID).
		/// Value: [Up, Down, Left, Right, Select, Back] button indices.
		/// </summary>
		private static readonly Dictionary<string, WheelMapping> DefaultMappings = 
			new Dictionary<string, WheelMapping>
		{
			// Logitech G29/G920 (ProductGuid: 046D-C24F-0000-0000-504944564944)
			{ 
				"046D-C24F", 
				new WheelMapping {
					Name = "Logitech G29/G920",
					Buttons = new[] { 0, 1, 2, 3, 4, 5 },  // D-Pad + Cross/Circle
					Notes = "D-Pad for navigation, Cross (X) for Select, Circle (O) for Back"
				}
			},
			
			// Thrustmaster T300/TX (ProductGuid: 044F-B66E-0000-0000-504944564944)
			{ 
				"044F-B66E", 
				new WheelMapping {
					Name = "Thrustmaster T300/TX",
					Buttons = new[] { 8, 9, 10, 11, 0, 1 },  // D-Pad at indices 8-11
					Notes = "D-Pad for navigation, face buttons for Select/Back"
				}
			},
			
			// Simagic Alpha series (base - swappable rims)
			{ 
				"346E-0006", 
				new WheelMapping {
					Name = "Simagic Alpha Series",
					Buttons = new[] { 0, 1, 2, 3, 4, 5 },
					Notes = "Base only - button layout varies by rim. Please configure manually.",
					IsModularBase = true
				}
			},
			
			// Fanatec CSL DD
			{ 
				"0EB7-6204", 
				new WheelMapping {
					Name = "Fanatec CSL DD",
					Buttons = new[] { 12, 13, 14, 15, 0, 1 },
					Notes = "Funky switch typically at indices 12-15. Rim-dependent - verify manually.",
					IsModularBase = true
				}
			},
			
			// Moza R9/R12/R16 series
			{ 
				"3416-0301", 
				new WheelMapping {
					Name = "Moza R-Series",
					Buttons = new[] { 0, 1, 2, 3, 4, 5 },
					Notes = "Base only - rim-dependent. Please configure manually.",
					IsModularBase = true
				}
			}
		};
		
		#endregion
		
		#region Initialization
		
		/// <summary>
		/// Initializes wheel button navigation subsystem.
		/// Called during Navigator initialization (same as StreamDeck).
		/// ✅ HYBRID APPROACH: Device creation on UI thread, polling on background thread.
		/// </summary>
		private static void InitializeWheelNavigation()
		{
			DebugLog.WriteLine("[Navigator.Wheel] Initializing wheel navigation...");

			// Hook game lifecycle events (SAME PATTERN as StreamDeck - Navigator.SD.cs lines 68-69)
			GameWrapper.Started += OnGameStarted_Wheel;
			GameWrapper.Ended += OnGameEnded_Wheel;

			// ✅ CRITICAL: Read config from ValuesStorage on UI thread FIRST
			// ValuesStorage has WPF thread affinity!
			bool enabled;
			string deviceId;
			string mappingString;

			try
			{
				enabled = ValuesStorage.Get("WheelNav_Enabled", false);
				deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
				mappingString = ValuesStorage.Get<string>("WheelNav_ButtonMapping");

				DebugLog.WriteLine($"[Navigator.Wheel] Config read from UI thread: enabled={enabled}, deviceId={deviceId}");
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Failed to read config from ValuesStorage: {ex.Message}");
				return;
			}

			if (!enabled)
			{
				DebugLog.WriteLine("[Navigator.Wheel] Wheel navigation disabled in config");
				return;
			}

			// ✅ Fire-and-forget UI thread initialization (MUST create device on UI thread!)
			Application.Current?.Dispatcher.BeginInvoke(
				DispatcherPriority.ApplicationIdle,
				new Action(async () =>
				{
					try
					{
						DebugLog.WriteLine("[Navigator.Wheel] UI thread initialization started...");

						// Create device on UI thread (DirectInputDevice has WPF dispatcher affinity!)
						if (await LoadWheelButtonConfigAsync(enabled, deviceId, mappingString))
						{
							DebugLog.WriteLine($"[Navigator.Wheel] ✅ Device created: {_navigationWheel?.DisplayName ?? "unknown"}");

							// Now start background thread polling
							EnableWheelPolling();
						}
						else
						{
							DebugLog.WriteLine("[Navigator.Wheel] No valid configuration found - wheel navigation disabled");
							DebugLog.WriteLine("[Navigator.Wheel] Tip: Use Ctrl+Shift+W to configure wheel buttons");
						}
					}
					catch (Exception ex)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] ❌ Initialization failed: {ex.Message}");
						DebugLog.WriteLine($"[Navigator.Wheel] Stack trace: {ex.StackTrace}");

						if (enabled)
						{
							try
							{
								ActionExtension.InvokeInMainThreadAsync(() =>
								{
									FirstFloor.ModernUI.Windows.Toast.Show(
										"Wheel Navigation Error",
										$"Failed to initialize: {ex.Message}"
									);
								});
							}
							catch { }
						}
					}
				})
			);
		}
		
		#endregion
		
		#region Configuration

		/// <summary>
		/// Loads wheel button configuration from ValuesStorage (ASYNC VERSION).
		/// Returns true if valid configuration loaded and device found.
		/// Waits for DirectInput scan to complete before checking devices.
		/// ✅ MUST RUN ON UI THREAD: DirectInputDevice.Create() requires UI thread (WPF dispatcher affinity).
		/// </summary>
		private static async Task<bool> LoadWheelButtonConfigAsync(bool enabled, string deviceId, string mappingString)
		{
			try
			{
				if (!enabled)
				{
					DebugLog.WriteLine("[Navigator.Wheel] Wheel navigation disabled in config");
					return false;
				}

				if (string.IsNullOrEmpty(deviceId))
				{
					DebugLog.WriteLine("[Navigator.Wheel] No device configured");
					return false;
				}

				// Use GetAsync() to wait for DirectInput scan to complete
				// We're on UI thread now - DirectInputDevice.Create() will work correctly
				DebugLog.WriteLine($"[Navigator.Wheel] Looking for device: {deviceId}");
				var joysticks = await DirectInputScanner.GetAsync();
				DebugLog.WriteLine($"[Navigator.Wheel] GetAsync() returned: {(joysticks == null ? "NULL" : $"{joysticks.Count} devices")}");

				if (joysticks == null || joysticks.Count == 0)
				{
					DebugLog.WriteLine("[Navigator.Wheel] No devices found - scan failed or too early");
					return false;
				}

				if (joysticks != null)
				{
					foreach (var joystick in joysticks)
					{
						// ✅ CRITICAL: Create on UI thread (DirectInputDevice has WPF dispatcher affinity)
						var device = DirectInputDevice.Create(joystick, -1);
						if (device != null && device.ProductId == deviceId)
						{
							_navigationWheel = device;
							DebugLog.WriteLine($"[Navigator.Wheel] ✅ Found device: {device.DisplayName}");
							break;
						}
					}
				}

				if (_navigationWheel == null)
				{
					DebugLog.WriteLine($"[Navigator.Wheel] Device {deviceId} not found (unplugged?)");
					return false;
				}

				DebugLog.WriteLine($"[Navigator.Wheel] Found device: {_navigationWheel.DisplayName}");
				DebugLog.WriteLine($"[Navigator.Wheel] Device has {_navigationWheel.Buttons.Length} buttons");

				// Parse button mapping from string (already read from ValuesStorage on UI thread)
				int[] mapping = null;

				if (!string.IsNullOrEmpty(mappingString))
				{
					try
					{
						mapping = mappingString.Split(',').Select(int.Parse).ToArray();
						DebugLog.WriteLine($"[Navigator.Wheel] Loaded mapping: [{string.Join(", ", mapping)}]");
					}
					catch (Exception parseEx)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Failed to parse button mapping: {parseEx.Message}");
						return false;
					}
				}
				else
				{
					DebugLog.WriteLine("[Navigator.Wheel] No button mapping configured");
					return false;
				}

				if (mapping.Length != 6)
				{
					DebugLog.WriteLine($"[Navigator.Wheel] Invalid button mapping length: {mapping.Length} (expected 6)");
					return false;
				}

						_wheelButtonMapping = mapping;
						DebugLog.WriteLine("[Navigator.Wheel] Configuration loaded successfully");
						return true;
					}
					catch (Exception ex)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Error loading config: {ex.Message}");
						return false;
					}
				}

				/// <summary>
				/// Loads wheel button configuration from ValuesStorage (SYNCHRONOUS - for game lifecycle).
				/// This version is kept for OnGameEnded where we can't use async easily.
				/// Creates a temporary watcher and checks immediately (may fail if scan not complete).
				/// </summary>
		private static bool LoadWheelButtonConfig()
		{
			try
			{
				var enabled = ValuesStorage.Get("WheelNav_Enabled", false);
				if (!enabled)
				{
					DebugLog.WriteLine("[Navigator.Wheel] Wheel navigation disabled in config");
					return false;
				}

				var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
				if (string.IsNullOrEmpty(deviceId))
				{
					DebugLog.WriteLine("[Navigator.Wheel] No device configured");
					return false;
				}

				// Find device from existing watcher if available
				if (_wheelWatcher != null)
				{
					_navigationWheel = FindDeviceByProductId(deviceId);
					if (_navigationWheel != null)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Found device from watcher: {_navigationWheel.DisplayName}");

						// ✅ FIX: Load button mapping as STRING, then parse to int[]
						var mappingString = ValuesStorage.Get<string>("WheelNav_ButtonMapping");
						int[] mapping = null;

						if (!string.IsNullOrEmpty(mappingString))
						{
							try
							{
								mapping = mappingString.Split(',').Select(int.Parse).ToArray();
							}
							catch (Exception parseEx)
							{
								DebugLog.WriteLine($"[Navigator.Wheel] Failed to parse button mapping: {parseEx.Message}");
								return false;
							}
						}

						// Validate button mapping
						if (mapping?.Length == 6 && mapping.All(b => b >= 0 && b < _navigationWheel.Buttons.Length))
						{
							_wheelButtonMapping = mapping;
							return true;
						}
					}
				}

				DebugLog.WriteLine($"[Navigator.Wheel] Device {deviceId} not found in current watcher");
				return false;
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Error loading config: {ex.Message}");
				return false;
			}
		}
		
		/// <summary>
		/// Gets default button mapping for a wheel model.
		/// Returns null if no default available.
		/// </summary>
		private static WheelMapping GetDefaultMapping(DirectInputDevice device)
		{
			if (device == null || string.IsNullOrEmpty(device.ProductId))
				return null;
			
			// Extract VID-PID from ProductGuid (first 9 chars: "046D-C24F")
			var productKey = device.ProductId.Length >= 9 
				? device.ProductId.Substring(0, 9) 
				: device.ProductId;
			
			if (DefaultMappings.TryGetValue(productKey, out var mapping))
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Default mapping found for ProductId prefix: {productKey}");
				return mapping;
			}
			
			DebugLog.WriteLine($"[Navigator.Wheel] No default mapping for ProductId: {productKey}");
			return null;
		}
		
		#endregion
		
		#region Enable/Disable
		
		/// <summary>
		/// Enables wheel button polling.
		/// Creates DirectInput watcher (wakes scanner thread) and starts polling timer.
		/// ✅ NEW: Uses System.Threading.Timer for reliable background polling (20Hz guaranteed).
		/// ✅ EFFICIENT: Scanner sleeps when no watchers exist.
		/// </summary>
		private static void EnableWheelPolling()
		{
			lock (_wheelStateLock)
			{
				if (_wheelWatcher != null)
				{
					DebugLog.WriteLine("[Navigator.Wheel] Already enabled");
					return;
				}

				DebugLog.WriteLine("[Navigator.Wheel] Enabling wheel polling...");

				// Create watcher - this wakes up the DirectInput scanner thread
				_wheelWatcher = DirectInputScanner.Watch();
				_wheelWatcher.Update += OnWheelDevicesUpdated;

				// Refresh device reference
				var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
				_navigationWheel = FindDeviceByProductId(deviceId);

				if (_navigationWheel != null)
				{
					// ✅ Attach button event handlers for navigation
					// Events will fire on background thread (polling thread)
					foreach (var button in _navigationWheel.Buttons)
					{
						button.PropertyChanged += OnNavigationButtonPressed;
					}

					_wheelNavigationEnabled = true;

					// ✅ Use System.Threading.Timer for BACKGROUND polling (not DispatcherTimer)
					// This guarantees 20Hz polling regardless of UI thread load
					_wheelPollTimer = new System.Threading.Timer(
						callback: _ => OnWheelPollTick(),
						state: null,
						dueTime: 0,        // Start immediately
						period: 50         // 20Hz (50ms interval)
					);

					DebugLog.WriteLine("[Navigator.Wheel] ✅ Polling enabled (20Hz on background thread)");
					DebugLog.WriteLine($"[Navigator.Wheel] Monitoring {_navigationWheel.Buttons.Length} buttons on {_navigationWheel.DisplayName}");
				}
				else
				{
					DebugLog.WriteLine("[Navigator.Wheel] ❌ Device not found, polling not started");
				}
			}
		}
		
		/// <summary>
		/// Disables wheel button polling.
		/// Disposes watcher (scanner thread goes to sleep) and stops polling timer.
		/// ✅ ZERO CPU OVERHEAD when disabled.
		/// </summary>
		private static void DisableWheelPolling()
		{
			lock (_wheelStateLock)
			{
				DebugLog.WriteLine("[Navigator.Wheel] Disabling wheel polling...");

				_wheelNavigationEnabled = false;

				// ✅ Dispose System.Threading.Timer (not Stop like DispatcherTimer)
				if (_wheelPollTimer != null)
				{
					try
					{
						_wheelPollTimer.Dispose();
					}
					catch (Exception ex)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Error disposing poll timer: {ex.Message}");
					}
					_wheelPollTimer = null;
				}

				// ✅ Detach button event handlers
				if (_navigationWheel != null)
				{
					foreach (var button in _navigationWheel.Buttons)
					{
						button.PropertyChanged -= OnNavigationButtonPressed;
					}
				}

				if (_wheelWatcher != null)
				{
					_wheelWatcher.Update -= OnWheelDevicesUpdated;
					_wheelWatcher.Dispose(); // Scanner thread sleeps if no watchers
					_wheelWatcher = null;
				}

				DebugLog.WriteLine("[Navigator.Wheel] ✅ Polling disabled (zero CPU overhead)");
			}
		}
		
		#endregion
		
		#region Polling & Event Handling
		
		/// <summary>
		/// Polls wheel buttons and detects presses.
		/// ✅ NEW: Runs on BACKGROUND THREAD (System.Threading.Timer callback thread).
		/// This updates button states, which triggers PropertyChanged events on THIS background thread.
		/// Guaranteed 20Hz polling regardless of UI thread load - NO MISSED BUTTON PRESSES.
		/// </summary>
		private static void OnWheelPollTick()
		{
			// ✅ Thread-safe read of shared state
			DirectInputDevice device;
			bool enabled;

			lock (_wheelStateLock)
			{
				enabled = _wheelNavigationEnabled;
				device = _navigationWheel;
			}

			if (!enabled || device == null)
				return;

			try
			{
				// ✅ Poll device state - this updates button values and triggers PropertyChanged
				// PropertyChanged events will fire on THIS background thread
				device.OnTick();
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Polling error: {ex.Message}");
			}
		}

		/// <summary>
		/// Handles button presses for NAVIGATION (not wizard).
		/// ✅ NEW: Triggered on BACKGROUND THREAD (PropertyChanged fires on polling thread).
		/// Maps physical button presses to navigation commands.
		/// Marshals UI interactions to UI thread (SAME PATTERN as StreamDeck).
		/// </summary>
		private static void OnNavigationButtonPressed(object sender, PropertyChangedEventArgs e)
		{
			// ✅ We're on BACKGROUND THREAD here (polling thread)
			var button = (DirectInputButton)sender;

			// Only react to rising edge (button just pressed, not held)
			if (e.PropertyName == nameof(DirectInputButton.Value) && button.Value)
			{
				// Find which navigation function this button is mapped to
				int navIndex = -1;
				for (int i = 0; i < 6; i++)
				{
					if (_wheelButtonMapping[i] == button.Id)
					{
						navIndex = i;
						break;
					}
				}

				if (navIndex >= 0)
				{
					// Track button state for rising edge detection
					if (_lastButtonStates[navIndex])
					{
						// Button is being held, ignore (we only want press, not hold)
						return;
					}

					_lastButtonStates[navIndex] = true;

					// ✅ MARSHAL to UI thread for UI interactions (SAME PATTERN as StreamDeck)
					// This is the CRITICAL difference from old implementation
					Application.Current?.Dispatcher.BeginInvoke(
						DispatcherPriority.Normal,
						new Action(() =>
						{
							// Now on UI thread - safe to interact with WPF UI
							OnWheelButtonPressed(navIndex);
						})
					);
				}
			}
			else if (e.PropertyName == nameof(DirectInputButton.Value) && !button.Value)
			{
				// Button released - update state
				for (int i = 0; i < 6; i++)
				{
					if (_wheelButtonMapping[i] == button.Id)
					{
						_lastButtonStates[i] = false;
						break;
					}
				}
			}
		}
		
		/// <summary>
		/// Handles wheel button press events and executes navigation commands.
		/// ✅ NEW: Always runs on UI THREAD (marshaled from background thread in OnNavigationButtonPressed).
		/// Maps button index to navigation command and interacts with WPF UI safely.
		/// </summary>
		private static void OnWheelButtonPressed(int navButtonIndex)
		{
			var buttonName = navButtonIndex >= 0 && navButtonIndex < _stepNames.Length 
				? _stepNames[navButtonIndex] 
				: navButtonIndex.ToString();

			DebugLog.WriteLine($"[Navigator.Wheel] Button pressed: {buttonName} ({GetActionDescription(navButtonIndex)})");

			try
			{
				switch (navButtonIndex)
				{
					case 0: // Up
						MoveInDirection(NavDirection.Up);
						break;
					case 1: // Down
						MoveInDirection(NavDirection.Down);
						break;
					case 2: // Left
						MoveInDirection(NavDirection.Left);
						break;
					case 3: // Right
						MoveInDirection(NavDirection.Right);
						break;
					case 4: // Select
						ActivateFocusedNode();
						break;
					case 5: // Back
						// Check if we're exiting the application - require confirmation
						if (CurrentContext?.ScopeNode?.TryGetVisual(out var scopeElement) == true
							&& scopeElement is Window window
							&& window.GetType().Name == "MainWindow")
						{
							RequestConfirmation(
								description: "Exit Application",
								onConfirm: () =>
								{
									DebugLog.WriteLine("[Navigator.Wheel] ✅ Exiting application (user confirmed)");
									Application.Current?.Dispatcher.Invoke(() =>
									{
										Application.Current.Shutdown();
									});
								},
								onCancel: () =>
								{
									DebugLog.WriteLine("[Navigator.Wheel] User cancelled exit");
								}
							);
						}
						else
						{
							// Regular back navigation
							ExitGroup();
						}
						break;
				}
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Error handling button {navButtonIndex}: {ex.Message}");
			}
		}

		/// <summary>
		/// Gets a human-readable description of what a button does.
		/// </summary>
		private static string GetActionDescription(int navButtonIndex)
		{
			switch (navButtonIndex)
			{
				case 0: return "Move focus UP";
				case 1: return "Move focus DOWN";
				case 2: return "Move focus LEFT";
				case 3: return "Move focus RIGHT";
				case 4: return "ACTIVATE focused item (click)";
				case 5: return "Go BACK / Exit group";
				default: return "Unknown action";
			}
		}
		
		/// <summary>
		/// Handles DirectInput device list changes (plug/unplug).
		/// Updates device reference if wheel is reconnected.
		/// ✅ Thread-safe: Uses lock for shared state access.
		/// </summary>
		private static void OnWheelDevicesUpdated(object sender, EventArgs e)
		{
			var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
			var device = FindDeviceByProductId(deviceId);

			lock (_wheelStateLock)
			{
				if (device == null && _navigationWheel != null)
				{
					DebugLog.WriteLine("[Navigator.Wheel] ⚠ Device disconnected");
					_navigationWheel = null;
					_wheelNavigationEnabled = false;
				}
				else if (device != null && _navigationWheel == null)
				{
					DebugLog.WriteLine("[Navigator.Wheel] ✅ Device reconnected");
					_navigationWheel = device;
					_wheelNavigationEnabled = true;
				}
			}
		}

		/// <summary>
		/// Finds a DirectInputDevice by ProductId from the current scanner state.
		/// Helper method to encapsulate Joystick → DirectInputDevice conversion.
		/// </summary>
		private static DirectInputDevice FindDeviceByProductId(string productId)
		{
			if (string.IsNullOrEmpty(productId) || _wheelWatcher == null)
				return null;

			var joysticks = _wheelWatcher.Get();
			if (joysticks == null)
				return null;

			foreach (var joystick in joysticks)
			{
				var device = DirectInputDevice.Create(joystick, -1);
				if (device != null && device.ProductId == productId)
				{
					return device;
				}
			}

			return null;
		}
		
		#endregion
		
		#region Game Lifecycle
		
		/// <summary>
		/// Game started - disable wheel navigation.
		/// SAME PATTERN as StreamDeck (Navigator.SD.cs line 653).
		/// </summary>
		private static void OnGameStarted_Wheel(object sender, GameStartedArgs e)
		{
			DebugLog.WriteLine($"[Navigator.Wheel] Game started: {e.Mode}, disabling navigation");
			DisableWheelPolling();
		}
		
		/// <summary>
		/// Game ended - re-enable wheel navigation.
		/// SAME PATTERN as StreamDeck (Navigator.SD.cs line 661).
		/// </summary>
		private static void OnGameEnded_Wheel(object sender, GameEndedArgs e)
		{
			DebugLog.WriteLine("[Navigator.Wheel] Game ended, re-enabling navigation");
			
			if (ValuesStorage.Get("WheelNav_Enabled", false))
			{
				// Reload config in case user changed wheels while in-game
				if (LoadWheelButtonConfig())
				{
					EnableWheelPolling();
				}
			}
		}
		
		#endregion
		
		#region Public API for Configuration Wizard

		/// <summary>
		/// Launches the wheel configuration wizard.
		/// Shows a dialog for device selection and button capture.
		/// Returns true if configuration was completed successfully.
		/// </summary>
		public static bool ShowWheelConfigWizard()
		{
			DebugLog.WriteLine("[Navigator.Wheel] Launching configuration wizard...");

			try
			{
				// Ensure we're on UI thread
				if (Application.Current?.Dispatcher.CheckAccess() == false)
				{
					DebugLog.WriteLine("[Navigator.Wheel] Not on UI thread, invoking on UI thread");
					return (bool)Application.Current.Dispatcher.Invoke(() => ShowWheelConfigWizard());
				}

				DebugLog.WriteLine("[Navigator.Wheel] Creating WheelConfigDialog...");
				var dialog = new WheelConfigDialog();

				DebugLog.WriteLine("[Navigator.Wheel] Showing dialog...");
				var result = dialog.ShowDialog();

				DebugLog.WriteLine($"[Navigator.Wheel] Wizard result: {result}");
				return result == true;
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Wizard error: {ex.Message}");
				DebugLog.WriteLine($"[Navigator.Wheel] Stack trace: {ex.StackTrace}");

				// Show error to user
				try
				{
					ModernDialog.ShowMessage(
						$"Failed to open wheel configuration wizard:\n\n{ex.Message}",
						"Wheel Configuration Error",
						MessageBoxButton.OK
					);
				}
				catch { }

				return false;
			}
		}

		/// <summary>
		/// Saves wheel navigation configuration.
		/// Called by configuration wizard after user presses all 6 buttons.
		/// </summary>
		public static void SaveWheelConfig(string deviceId, string deviceName, int[] buttonMapping)
		{
			if (buttonMapping?.Length != 6)
				throw new ArgumentException("Button mapping must contain exactly 6 buttons", nameof(buttonMapping));

			if (buttonMapping.Any(b => b < 0))
				throw new ArgumentException("Button mapping contains invalid indices", nameof(buttonMapping));

			// Store array as comma-separated string (ValuesStorage doesn't support int[])
			var buttonMappingString = string.Join(",", buttonMapping);

			DebugLog.WriteLine($"[Navigator.Wheel] Saving configuration:");
			DebugLog.WriteLine($"[Navigator.Wheel]   Device: {deviceName}");
			DebugLog.WriteLine($"[Navigator.Wheel]   ProductId: {deviceId}");
			DebugLog.WriteLine($"[Navigator.Wheel]   Buttons: [{string.Join(", ", buttonMapping)}]");

			ValuesStorage.Set("WheelNav_Enabled", true);
			ValuesStorage.Set("WheelNav_DeviceId", deviceId);
			ValuesStorage.Set("WheelNav_DeviceName", deviceName);
			ValuesStorage.Set("WheelNav_ButtonMapping", buttonMappingString);

			DebugLog.WriteLine($"[Navigator.Wheel] ✅ Configuration saved successfully");

			// Reload and enable (may fail if watcher not created yet - that's OK)
			if (LoadWheelButtonConfig())
			{
				EnableWheelPolling();
				DebugLog.WriteLine("[Navigator.Wheel] Configuration activated immediately");
			}
			else
			{
				DebugLog.WriteLine("[Navigator.Wheel] Configuration will be loaded on next startup");
			}
		}

		/// <summary>
		/// Disables wheel navigation (called from settings UI or wizard).
		/// </summary>
		public static void DisableWheelNavigation()
		{
			ValuesStorage.Set("WheelNav_Enabled", false);
			DisableWheelPolling();
			DebugLog.WriteLine("[Navigator.Wheel] Navigation disabled by user");
		}

		/// <summary>
		/// Gets the current wheel configuration status for display in UI.
		/// </summary>
		public static WheelConfigStatus GetWheelConfigStatus()
		{
			var enabled = ValuesStorage.Get("WheelNav_Enabled", false);
			var deviceName = ValuesStorage.Get<string>("WheelNav_DeviceName");
			var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");

			// ✅ FIX: Load button mapping as STRING, then parse to int[]
			var mappingString = ValuesStorage.Get<string>("WheelNav_ButtonMapping");
			int[] mapping = null;

			if (!string.IsNullOrEmpty(mappingString))
			{
				try
				{
					mapping = mappingString.Split(',').Select(int.Parse).ToArray();
				}
				catch { }
			}

			return new WheelConfigStatus
			{
				Enabled = enabled,
				DeviceName = deviceName ?? "Not configured",
				DeviceId = deviceId,
				ButtonMapping = mapping,
				IsConnected = _navigationWheel != null,
				IsPolling = _wheelNavigationEnabled
			};
		}

		#endregion
		
		#region Helper Classes
		
		/// <summary>
		/// Represents a default button mapping for a specific wheel model.
		/// </summary>
		private class WheelMapping
		{
			public string Name { get; set; }
			public int[] Buttons { get; set; }
			public string Notes { get; set; }
			public bool IsModularBase { get; set; }
		}
		
		/// <summary>
		/// Represents the current wheel navigation configuration status.
		/// Used for UI display.
		/// </summary>
		public class WheelConfigStatus
		{
			public bool Enabled { get; set; }
			public string DeviceName { get; set; }
			public string DeviceId { get; set; }
			public int[] ButtonMapping { get; set; }
			public bool IsConnected { get; set; }
			public bool IsPolling { get; set; }
			
			public override string ToString()
			{
				if (!Enabled)
					return "Wheel navigation: Disabled";
				
				var status = IsConnected ? (IsPolling ? "Active" : "Paused") : "Disconnected";
				return $"Wheel navigation: {status} ({DeviceName})";
			}
		}
		
		#endregion
	}
}
