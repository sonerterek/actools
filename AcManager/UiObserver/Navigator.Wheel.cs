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

		// Multi-device support: Each button can be from a different device
		private static Dictionary<string, DirectInputDevice> _navigationDevices = 
			new Dictionary<string, DirectInputDevice>();  // Key: Full ProductId (36 chars), Value: Device

		private static Dictionary<string, ButtonBinding> _buttonBindings = 
			new Dictionary<string, ButtonBinding>();  // Key: NavKey ("UP", "DOWN", etc.)

		// Tracks attached event handlers for cleanup (lambda-based approach)
		private static List<AttachedHandler> _attachedHandlers = 
			new List<AttachedHandler>();

		private static bool _wheelNavigationEnabled;
		private static System.Threading.Timer _wheelPollTimer;  // Background thread timer (not DispatcherTimer)
		private static readonly object _wheelStateLock = new object();  // Thread-safety for shared state

		// Button names for debug output
		private static readonly string[] _stepNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };


		/// <summary>
		/// Default button mappings for common wheels.
		/// Key format: VID-PID prefix (9 chars, e.g., "046D-C24F") - used for DefaultMappings lookup only.
		/// Note: Actual device identification uses full 36-char ProductId for exact matching.
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
		/// HYBRID APPROACH: Device creation on UI thread, polling on background thread.
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

				try
				{
					enabled = ValuesStorage.Get("WheelNav_Enabled", false);
					DebugLog.WriteLine($"[Navigator.Wheel] Config read from UI thread: enabled={enabled}");
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
						if (await LoadWheelButtonConfigAsync(enabled))
						{
							var deviceCount = _navigationDevices.Count;
							var deviceNames = string.Join(", ", _navigationDevices.Values.Select(d => d.DisplayName));
							DebugLog.WriteLine($"[Navigator.Wheel] ✅ Device(s) created: {deviceNames}");

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
		/// Parses button configuration string into ButtonBinding dictionary.
		/// Format: "UP:046D-C24F-0000-0000-504944564944:13,DOWN:046D-C260-0000-0000-504944564944:11,..."
		/// </summary>
		private static Dictionary<string, ButtonBinding> ParseButtonConfig(string configStr)
		{
			var bindings = new Dictionary<string, ButtonBinding>();

			if (string.IsNullOrEmpty(configStr))
			{
				DebugLog.WriteLine("[Navigator.Wheel] Empty config string");
				return bindings;
			}

			try
			{
				var bindingStrings = configStr.Split(',');

				foreach (var bindingStr in bindingStrings)
				{
					var binding = ButtonBinding.Parse(bindingStr.Trim());
					if (binding != null && !string.IsNullOrEmpty(binding.NavKey))
					{
						bindings[binding.NavKey] = binding;
					}
				}

				DebugLog.WriteLine($"[Navigator.Wheel] Parsed {bindings.Count} button bindings");
			}
			catch (Exception ex)
			{
				DebugLog.WriteLine($"[Navigator.Wheel] Error parsing config: {ex.Message}");
			}

			return bindings;
		}

		/// <summary>
		/// Loads wheel button configuration from ValuesStorage (ASYNC VERSION).
		/// Returns true if valid configuration loaded and device found.
		/// Waits for DirectInput scan to complete before checking devices.
		/// ✅ MUST RUN ON UI THREAD: DirectInputDevice.Create() requires UI thread (WPF dispatcher affinity).
		/// ✅ Multi-device support: Each button can be from a different device.
		/// </summary>
		private static async Task<bool> LoadWheelButtonConfigAsync(bool enabled)
		{
			try
			{
				if (!enabled)
				{
					DebugLog.WriteLine("[Navigator.Wheel] Wheel navigation disabled in config");
					return false;
				}

				// Load multi-device config format
				var buttonConfigString = ValuesStorage.Get<string>("WheelNav_ButtonConfig");

				if (string.IsNullOrEmpty(buttonConfigString))
				{
					DebugLog.WriteLine("[Navigator.Wheel] No button configuration found");
					return false;
				}

				DebugLog.WriteLine($"[Navigator.Wheel] Loading config: {buttonConfigString}");

				// Parse button configuration
				var bindings = ParseButtonConfig(buttonConfigString);

				if (bindings.Count == 0)
				{
					DebugLog.WriteLine("[Navigator.Wheel] No valid bindings found in config");
					return false;
				}

				// Extract unique device IDs from bindings
				var requiredDeviceIds = bindings.Values
					.Select(b => b.DeviceId)
					.Distinct()
					.ToList();

				DebugLog.WriteLine($"[Navigator.Wheel] Config requires {requiredDeviceIds.Count} device(s)");

				// Wait for DirectInput scan to complete
				var joysticks = await DirectInputScanner.GetAsync();
				DebugLog.WriteLine($"[Navigator.Wheel] GetAsync() returned: {(joysticks == null ? "NULL" : $"{joysticks.Count} devices")}");

				if (joysticks == null || joysticks.Count == 0)
				{
					DebugLog.WriteLine("[Navigator.Wheel] No devices found - scan failed or too early");
					return false;
				}

				// Find and create ALL required devices
				var foundDevices = new Dictionary<string, DirectInputDevice>();

				foreach (var joystick in joysticks)
				{
					// ✅ CRITICAL: Create on UI thread (DirectInputDevice has WPF dispatcher affinity)
					var device = DirectInputDevice.Create(joystick, -1);
					if (device == null) continue;

					// ✅ Exact ProductId match only
					foreach (var deviceId in requiredDeviceIds)
					{
						if (device.ProductId == deviceId)
						{
							if (!foundDevices.ContainsKey(deviceId))
							{
								foundDevices[deviceId] = device;
								DebugLog.WriteLine($"[Navigator.Wheel] ✅ Found device: {device.DisplayName} (ID: {device.ProductId})");

								// Update binding with full device name
								foreach (var binding in bindings.Values.Where(b => b.DeviceId == deviceId))
								{
									binding.DeviceName = device.DisplayName;
								}
							}
							break; // Found match, no need to check other requiredDeviceIds
						}
					}
				}

				// Check if all required devices were found
				var missingDevices = requiredDeviceIds.Except(foundDevices.Keys).ToList();
				if (missingDevices.Count > 0)
				{
					DebugLog.WriteLine($"[Navigator.Wheel] ⚠ Missing {missingDevices.Count} required device(s):");
					foreach (var deviceId in missingDevices)
					{
						DebugLog.WriteLine($"[Navigator.Wheel]   - {deviceId}");
					}

					// ✅ Show Toast warning but continue with available devices
					var missingCount = missingDevices.Count;
					var totalCount = requiredDeviceIds.Count;

					ActionExtension.InvokeInMainThreadAsync(() =>
					{
						FirstFloor.ModernUI.Windows.Toast.Show(
							"Wheel Navigation - Device Missing",
							$"{missingCount} of {totalCount} configured device(s) not found.\nNavigation may be partially functional."
						);
					});

					// Continue with available devices instead of failing
					DebugLog.WriteLine($"[Navigator.Wheel] Continuing with {foundDevices.Count} available device(s)");
				}

				// Skip validation for missing devices - only validate connected ones
				foreach (var binding in bindings.Values.Where(b => foundDevices.ContainsKey(b.DeviceId)))
				{
					var device = foundDevices[binding.DeviceId];
					if (binding.ButtonIndex < 0 || binding.ButtonIndex >= device.Buttons.Length)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Invalid button index for {binding.NavKey}: {binding.ButtonIndex} (device has {device.Buttons.Length} buttons)");
						return false;
					}
				}

				// ✅ SUCCESS: Store devices and bindings
				_navigationDevices = foundDevices;
				_buttonBindings = bindings;

				DebugLog.WriteLine($"[Navigator.Wheel] ✅ Configuration loaded: {bindings.Count} bindings across {foundDevices.Count} device(s)");

				foreach (var kvp in bindings)
				{
					var binding = kvp.Value;
					DebugLog.WriteLine($"[Navigator.Wheel]   {binding.NavKey}: {binding.DeviceName} Button {binding.ButtonIndex}");
				}

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
				/// Simplified - just checks if config exists (device loading happens async on UI thread).
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

						var buttonConfigString = ValuesStorage.Get<string>("WheelNav_ButtonConfig");
						if (!string.IsNullOrEmpty(buttonConfigString))
						{
							DebugLog.WriteLine("[Navigator.Wheel] Config exists (will load devices on UI thread)");
							return true;
						}

						DebugLog.WriteLine("[Navigator.Wheel] No config found");
						return false;
					}
					catch (Exception ex)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Error checking config: {ex.Message}");
						return false;
					}
				}
		
		/// <summary>
		/// Gets default button mapping for a wheel model by VID-PID prefix.
		/// Note: DefaultMappings uses VID-PID (9 chars) as key for convenience,
		/// but actual device identification uses full 36-char ProductId.
		/// Returns null if no default available.
		/// </summary>
		private static WheelMapping GetDefaultMapping(DirectInputDevice device)
		{
			if (device == null || string.IsNullOrEmpty(device.ProductId))
				return null;

			// Extract VID-PID prefix from ProductId (first 9 chars: "046D-C24F")
			// Used only for DefaultMappings lookup - NOT for device identity
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
		/// Uses lambda handlers: Each configured button gets a specific handler that captures its NavKey.
		/// EFFICIENT: Only attaches handlers to configured buttons (not all buttons).
		/// </summary>
		private static void EnableWheelPolling()
		{
			lock (_wheelStateLock) {
				if (_wheelWatcher != null) {
					DebugLog.WriteLine("[Navigator.Wheel] Already enabled");
					return;
				}

				DebugLog.WriteLine("[Navigator.Wheel] Enabling wheel polling...");

				// Create watcher - this wakes up the DirectInput scanner thread
				_wheelWatcher = DirectInputScanner.Watch();
				_wheelWatcher.Update += OnWheelDevicesUpdated;

				// Clear any previous handlers
				_attachedHandlers.Clear();

				// Attach lambda handlers ONLY to configured buttons
				int attachedCount = 0;
				foreach (var kvp in _buttonBindings) {
					var navKey = kvp.Key;
					var binding = kvp.Value;

					// Find the device for this binding
					if (!_navigationDevices.TryGetValue(binding.DeviceId, out var device)) {
						DebugLog.WriteLine($"[Navigator.Wheel] Warning: Device not found for {navKey} ({binding.DeviceId})");
						continue;
					}

					// Validate button index
					if (binding.ButtonIndex < 0 || binding.ButtonIndex >= device.Buttons.Length) {
						DebugLog.WriteLine($"[Navigator.Wheel] Warning: Invalid button index for {navKey}: {binding.ButtonIndex}");
						continue;
					}

					// Get the specific button
					var button = device.Buttons[binding.ButtonIndex];

					// Create lambda that captures navKey (closure)
					PropertyChangedEventHandler handler = (sender, e) => {
						var btn = (DirectInputButton)sender;

						// Only react to rising edge (button pressed, not released)
						if (e.PropertyName == nameof(DirectInputButton.Value) && btn.Value) {
							DebugLog.WriteLine($"[Navigator.Wheel] Button pressed: {navKey} ({device.DisplayName} Button {btn.Id})");

							// Marshal to UI thread for navigation
							Application.Current?.Dispatcher.BeginInvoke(
								DispatcherPriority.Normal,
								new Action(() => OnWheelButtonPressed(navKey))
							);
						}
					};

					// Attach handler to button
					button.PropertyChanged += handler;
					_attachedHandlers.Add(new AttachedHandler { Button = button, Handler = handler });
					attachedCount++;

					DebugLog.WriteLine($"[Navigator.Wheel]   {navKey}: {device.DisplayName} Button {binding.ButtonIndex}");
				}

				_wheelNavigationEnabled = true;

				// Use System.Threading.Timer for BACKGROUND polling (not DispatcherTimer)
				// This guarantees 20Hz polling regardless of UI thread load
				_wheelPollTimer = new System.Threading.Timer(
					callback: _ => OnWheelPollTick(),
					state: null,
					dueTime: 0,        // Start immediately
					period: 50         // 20Hz (50ms interval)
				);

				DebugLog.WriteLine($"[Navigator.Wheel] ✅ Polling enabled (20Hz on background thread)");
				DebugLog.WriteLine($"[Navigator.Wheel] Attached {attachedCount} button handler(s) across {_navigationDevices.Count} device(s)");
			}
		}
		
		/// <summary>
		/// Disables wheel button polling.
		/// Disposes watcher (scanner thread goes to sleep) and stops polling timer.
		/// Detaches all lambda handlers from configured buttons.
		/// ZERO CPU OVERHEAD when disabled.
		/// </summary>
		private static void DisableWheelPolling()
		{
			lock (_wheelStateLock)
			{
				DebugLog.WriteLine("[Navigator.Wheel] Disabling wheel polling...");

				_wheelNavigationEnabled = false;

				// Dispose System.Threading.Timer (not Stop like DispatcherTimer)
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

				// Detach all lambda handlers
				DebugLog.WriteLine($"[Navigator.Wheel] Detaching {_attachedHandlers.Count} handler(s)...");
				foreach (var attached in _attachedHandlers)
				{
					attached.Button.PropertyChanged -= attached.Handler;
				}
				_attachedHandlers.Clear();

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
		/// Runs on BACKGROUND THREAD (System.Threading.Timer callback thread).
		/// This updates button states, which triggers PropertyChanged events on THIS background thread.
		/// Guaranteed 20Hz polling regardless of UI thread load - NO MISSED BUTTON PRESSES.
		/// </summary>
		private static void OnWheelPollTick()
		{
			// Thread-safe read of shared state
			Dictionary<string, DirectInputDevice> devices;
			bool enabled;

			lock (_wheelStateLock) {
				enabled = _wheelNavigationEnabled;
				devices = _navigationDevices;
			}

			if (!enabled || devices == null || devices.Count == 0)
				return;

			try {
				// Poll ALL devices - this updates button values and triggers PropertyChanged
				// PropertyChanged events will fire on THIS background thread
				foreach (var device in devices.Values) {
					device.OnTick();
				}
			} catch (Exception ex) {
				DebugLog.WriteLine($"[Navigator.Wheel] Polling error: {ex.Message}");
			}
		}

		/// <summary>
		/// Handles wheel button press events and executes navigation commands.
		/// Always runs on UI THREAD (marshaled from lambda handlers on background thread).
		/// Takes navKey parameter directly from lambda closure - no lookup needed.
		/// </summary>
		private static void OnWheelButtonPressed(string navKey)
		{
			DebugLog.WriteLine($"[Navigator.Wheel] Executing: {navKey} ({GetActionDescription(navKey)})");

			try
			{
				switch (navKey)
				{
					case "UP":
						MoveInDirection(NavDirection.Up);
						break;
					case "DOWN":
						MoveInDirection(NavDirection.Down);
						break;
					case "LEFT":
						MoveInDirection(NavDirection.Left);
						break;
					case "RIGHT":
						MoveInDirection(NavDirection.Right);
						break;
					case "SELECT":
						ActivateFocusedNode();
						break;
					case "BACK":
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
				DebugLog.WriteLine($"[Navigator.Wheel] Error handling button {navKey}: {ex.Message}");
			}
		}

		/// <summary>
		/// Gets a human-readable description of what a button does.
		/// </summary>
		private static string GetActionDescription(string navKey)
		{
			switch (navKey)
			{
				case "UP": return "Move focus UP";
				case "DOWN": return "Move focus DOWN";
				case "LEFT": return "Move focus LEFT";
				case "RIGHT": return "Move focus RIGHT";
				case "SELECT": return "ACTIVATE focused item (click)";
				case "BACK": return "Go BACK / Exit group";
				default: return "Unknown action";
			}
		}
		
		/// <summary>
		/// Handles DirectInput device list changes (plug/unplug).
		/// Updates device references when devices reconnect and recreates lambda handlers.
		/// Thread-safe: Uses lock for shared state access.
		/// </summary>
		private static void OnWheelDevicesUpdated(object sender, EventArgs e)
		{
			lock (_wheelStateLock)
			{
				// Get required device IDs from current bindings
				var requiredDeviceIds = _buttonBindings.Values
					.Select(b => b.DeviceId)
					.Distinct()
					.ToList();

				bool allDevicesPresent = true;
				bool devicesChanged = false;

				foreach (var deviceIdRequired in requiredDeviceIds)
				{
					var deviceFound = FindDeviceByProductId(deviceIdRequired);

					if (deviceFound == null && _navigationDevices.ContainsKey(deviceIdRequired))
					{
						// Device disconnected
						DebugLog.WriteLine($"[Navigator.Wheel] ⚠ Device disconnected: {deviceIdRequired}");

						// Detach handlers for this device's buttons
						var disconnectedDevice = _navigationDevices[deviceIdRequired];
						var handlersToRemove = _attachedHandlers
							.Where(h => disconnectedDevice.Buttons.Contains(h.Button))
							.ToList();

						foreach (var attached in handlersToRemove)
						{
							attached.Button.PropertyChanged -= attached.Handler;
							_attachedHandlers.Remove(attached);
						}

						_navigationDevices.Remove(deviceIdRequired);
						allDevicesPresent = false;
						devicesChanged = true;
					}
					else if (deviceFound != null && !_navigationDevices.ContainsKey(deviceIdRequired))
					{
						// Device reconnected
						DebugLog.WriteLine($"[Navigator.Wheel] ✅ Device reconnected: {deviceFound.DisplayName}");
						_navigationDevices[deviceIdRequired] = deviceFound;

						// Recreate lambda handlers for this device's bindings
						foreach (var kvp in _buttonBindings.Where(b => b.Value.DeviceId == deviceIdRequired))
						{
							var navKey = kvp.Key;
							var binding = kvp.Value;

							if (binding.ButtonIndex >= 0 && binding.ButtonIndex < deviceFound.Buttons.Length)
							{
								var button = deviceFound.Buttons[binding.ButtonIndex];

								// Create lambda that captures navKey
								PropertyChangedEventHandler handler = (s, ev) => {
									var btn = (DirectInputButton)s;
									if (ev.PropertyName == nameof(DirectInputButton.Value) && btn.Value)
									{
										DebugLog.WriteLine($"[Navigator.Wheel] Button pressed: {navKey} ({deviceFound.DisplayName} Button {btn.Id})");
										Application.Current?.Dispatcher.BeginInvoke(
											DispatcherPriority.Normal,
											new Action(() => OnWheelButtonPressed(navKey))
										);
									}
								};

								button.PropertyChanged += handler;
								_attachedHandlers.Add(new AttachedHandler { Button = button, Handler = handler });

								DebugLog.WriteLine($"[Navigator.Wheel]   Reattached handler for {navKey}");
							}
						}

						devicesChanged = true;
					}
				}

				// Update enabled state based on device presence
				if (!allDevicesPresent || _navigationDevices.Count < requiredDeviceIds.Count)
				{
					if (_wheelNavigationEnabled)
					{
						DebugLog.WriteLine("[Navigator.Wheel] ⚠ Disabling navigation (device missing)");
						_wheelNavigationEnabled = false;
					}
				}
				else if (!_wheelNavigationEnabled && _navigationDevices.Count == requiredDeviceIds.Count && devicesChanged)
				{
					DebugLog.WriteLine("[Navigator.Wheel] ✅ Re-enabling navigation (all devices present)");
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
		/// Saves multi-device wheel navigation configuration.
		/// Called by wizard after user configures all 6 buttons.
		/// </summary>
		/// <param name="bindings">Dictionary of NavKey → ButtonBinding</param>
		public static void SaveWheelConfig(Dictionary<string, ButtonBinding> bindings)
		{
			if (bindings == null || bindings.Count != 6)
				throw new ArgumentException("Configuration must contain exactly 6 button bindings", nameof(bindings));

			// Validate all nav keys are present
			string[] requiredKeys = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };
			foreach (var key in requiredKeys)
			{
				if (!bindings.ContainsKey(key))
					throw new ArgumentException($"Missing required navigation key: {key}", nameof(bindings));
			}

			// Validate ProductId lengths and button indices
			foreach (var binding in bindings.Values)
			{
				if (binding.DeviceId.Length != 36)
					throw new ArgumentException($"Invalid ProductId length for {binding.NavKey}: {binding.DeviceId.Length} (expected 36)", nameof(bindings));

				if (binding.ButtonIndex < 0)
					throw new ArgumentException($"Invalid button index for {binding.NavKey}: {binding.ButtonIndex}", nameof(bindings));
			}

			// Convert bindings to config string
			var configString = string.Join(",", bindings.Values.Select(b => b.ToString()));

			DebugLog.WriteLine($"[Navigator.Wheel] Saving multi-device configuration:");
			DebugLog.WriteLine($"[Navigator.Wheel]   Config: {configString}");

			foreach (var kvp in bindings)
			{
				var b = kvp.Value;
				DebugLog.WriteLine($"[Navigator.Wheel]   {b.NavKey}: {b.DeviceName} ({b.DeviceId}) Button {b.ButtonIndex}");
			}

			// Save to storage
			ValuesStorage.Set("WheelNav_Enabled", true);
			ValuesStorage.Set("WheelNav_ButtonConfig", configString);

			DebugLog.WriteLine($"[Navigator.Wheel] ✅ Configuration saved successfully");

			// Stop old polling before reloading (critical for wizard completion)
			DisableWheelPolling();
			DebugLog.WriteLine("[Navigator.Wheel] Stopped old polling to reload new config");

			// Reload and enable immediately
			Application.Current?.Dispatcher.BeginInvoke(
				DispatcherPriority.ApplicationIdle,
				new Action(async () =>
				{
					try
					{
						DebugLog.WriteLine("[Navigator.Wheel] Reloading configuration...");
						if (await LoadWheelButtonConfigAsync(true))
						{
							EnableWheelPolling();
							DebugLog.WriteLine("[Navigator.Wheel] ✅ Configuration activated immediately");
						}
						else
						{
							DebugLog.WriteLine("[Navigator.Wheel] ⚠ Failed to load new configuration");
						}
					}
					catch (Exception ex)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] ❌ Failed to activate config: {ex.Message}");
					}
				})
			);
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

			// Get multi-device info from configuration
			var buttonConfig = ValuesStorage.Get<string>("WheelNav_ButtonConfig");
			int deviceCount = 0;
			List<string> deviceNames = new List<string>();

			if (!string.IsNullOrEmpty(buttonConfig))
			{
				var bindings = ParseButtonConfig(buttonConfig);
				deviceCount = bindings.Values.Select(b => b.DeviceId).Distinct().Count();

				lock (_wheelStateLock)
				{
					deviceNames = _navigationDevices.Values.Select(d => d.DisplayName).ToList();
				}
			}

			return new WheelConfigStatus
			{
				Enabled = enabled,
				DeviceName = deviceNames.FirstOrDefault() ?? "Not configured",
				DeviceId = null,  // Legacy field, not used in multi-device
				ButtonMapping = null,  // Legacy field, not used in multi-device
				IsConnected = _navigationDevices.Count > 0,
				IsPolling = _wheelNavigationEnabled,
				DeviceCount = deviceCount,
				DeviceNames = deviceNames
			};
		}

		#endregion
		
		#region Helper Classes

		/// <summary>
		/// Tracks an attached event handler for proper cleanup.
		/// Used to detach lambda handlers when disabling polling.
		/// </summary>
		private class AttachedHandler
		{
			public DirectInputButton Button { get; set; }
			public PropertyChangedEventHandler Handler { get; set; }
		}

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
		/// Represents a binding between a navigation key and a physical button on a device.
		/// Multi-device support: Each navigation key can be bound to a button from a different device.
		/// Uses full 36-character ProductId for device identification.
		/// </summary>
		public class ButtonBinding
		{
			public string NavKey { get; set; }        // "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK"
			public string DeviceId { get; set; }      // ProductId: "046D-C24F-0000-0000-504944564944"
			public int ButtonIndex { get; set; }      // Physical button ID on the device
			public string DeviceName { get; set; }    // "Logitech G29" (for display/debugging)

			/// <summary>
			/// Parses a binding from string format: "UP:046D-C24F-0000-0000-504944564944:13"
			/// </summary>
			public static ButtonBinding Parse(string bindingStr)
			{
				try
				{
					var parts = bindingStr.Split(':');
					if (parts.Length != 3)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Invalid binding format: {bindingStr}");
						return null;
					}

					var deviceId = parts[1].Trim();

					// Validate ProductId length (should be 36 characters)
					if (deviceId.Length != 36)
					{
						DebugLog.WriteLine($"[Navigator.Wheel] Invalid ProductId length: {deviceId.Length} (expected 36)");
						return null;
					}

					return new ButtonBinding
					{
						NavKey = parts[0].Trim(),
						DeviceId = deviceId,
						ButtonIndex = int.Parse(parts[2].Trim()),
						DeviceName = ""  // Will be populated when device is found
					};
				}
				catch (Exception ex)
				{
					DebugLog.WriteLine($"[Navigator.Wheel] Error parsing binding '{bindingStr}': {ex.Message}");
					return null;
				}
			}

			/// <summary>
			/// Converts binding to string format: "UP:046D-C24F-0000-0000-504944564944:13"
			/// </summary>
			public override string ToString()
			{
				return $"{NavKey}:{DeviceId}:{ButtonIndex}";
			}
		}

		/// <summary>
		/// Represents the current wheel navigation configuration status.
		/// Used for UI display.
		/// Supports both single-device and multi-device configurations.
		/// </summary>
		public class WheelConfigStatus
		{
			public bool Enabled { get; set; }
			public string DeviceName { get; set; }
			public string DeviceId { get; set; }
			public int[] ButtonMapping { get; set; }
			public bool IsConnected { get; set; }
			public bool IsPolling { get; set; }

			// Multi-device support
			public int DeviceCount { get; set; }
			public List<string> DeviceNames { get; set; }

			public override string ToString()
			{
				if (!Enabled)
					return "Wheel navigation: Disabled";

				var status = IsConnected ? (IsPolling ? "Active" : "Paused") : "Disconnected";

				// Show device count if multi-device
				if (DeviceCount > 1)
				{
					return $"Wheel navigation: {status} ({DeviceCount} devices)";
				}
				else
				{
					return $"Wheel navigation: {status} ({DeviceName})";
				}
			}
		}
		
		#endregion
	}
}
