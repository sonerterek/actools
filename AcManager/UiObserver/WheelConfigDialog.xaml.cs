using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AcManager.Tools.Helpers.DirectInput;
using FirstFloor.ModernUI.Dialogs;
using FirstFloor.ModernUI.Windows.Controls;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Configuration wizard for wheel button navigation.
	/// Simple sequential button capture - no StreamDeck integration needed.
	/// This is for users who DON'T have a StreamDeck.
	/// </summary>
	public partial class WheelConfigDialog : ModernDialog
	{
		#region Fields

		// Step configuration: 7 required buttons + 3 optional shortcuts
		private readonly string[] _stepNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK", "GO", "SC1", "SC2", "SC3" };
		private readonly bool[] _stepOptional = { false, false, false, false, false, false, false, true, true, true }; // SC1/SC2/SC3 are optional

		private readonly Dictionary<string, CapturedBinding> _capturedBindings = 
			new Dictionary<string, CapturedBinding>();
		private int _currentStep = 0;

		private DirectInputScanner.Watcher _watcher;
		private List<DirectInputDevice> _availableDevices;
		private List<DirectInputDevice> _selectedDevices;  // User-selected devices to scan

		// Polling timer for button detection
		private System.Windows.Threading.DispatcherTimer _pollTimer;

		/// <summary>
		/// Represents a captured button binding during configuration.
		/// </summary>
		private class CapturedBinding
		{
			public string DeviceId { get; set; }
			public string DeviceName { get; set; }
			public int ButtonIndex { get; set; }
		}

		#endregion
		
		#region Constructor
		
		public WheelConfigDialog()
		{
			InitializeComponent();

			// Hook cleanup event
			Closed += OnDialogClosed;

			// Start multi-device scan and button capture
			StartMultiDeviceCapture();
		}
		
		#endregion
		
		#region Multi-Device Scanning

		/// <summary>
		/// Scans for all compatible devices and shows device selection screen.
		/// Allows user to exclude vJoy or other virtual devices.
		/// </summary>
		private async void StartMultiDeviceCapture()
		{
			Debug.WriteLine("[WheelConfig] Starting device scan...");

			// Show "Scanning..." message immediately
			StepTitle.Text = "Detecting input devices...";
			StepPrompt.Text = "Please wait";
			StepProgress.Text = "";
			ButtonCapturePanel.Visibility = Visibility.Visible;

			// Wait for DirectInput scan to complete (async operation)
			var joysticks = await DirectInputScanner.GetAsync();

			if (joysticks == null || joysticks.Count == 0)
			{
				ShowError("No input devices found.\n\nPlease connect a wheel or controller and try again.");
				Close();
				return;
			}

			// Convert ALL devices to DirectInputDevice list (exclude Xbox controllers)
			_availableDevices = new List<DirectInputDevice>();
			foreach (var joystick in joysticks)
			{
				var device = DirectInputDevice.Create(joystick, -1);
				if (device != null && !device.IsController && device.Buttons.Length > 0)
				{
					_availableDevices.Add(device);
					Debug.WriteLine($"[WheelConfig] Found device: {device.DisplayName} ({device.Buttons.Length} buttons)");
				}
			}

			if (_availableDevices.Count == 0)
			{
				ShowError("No compatible input devices found.\n\n" +
						 "Devices must have at least 1 button.");
				Close();
				return;
			}

			Debug.WriteLine($"[WheelConfig] Found {_availableDevices.Count} compatible device(s)");

			// Show device selection UI (allow user to exclude vJoy, etc.)
			ShowDeviceSelection();
		}

		#endregion

		#region Device Selection

		/// <summary>
		/// Shows device selection screen.
		/// Allows user to check/uncheck devices to exclude vJoy or other virtual devices.
		/// </summary>
		private void ShowDeviceSelection()
		{
			Debug.WriteLine("[WheelConfig] Showing device selection screen...");

			// Hide button capture, show device selection
			ButtonCapturePanel.Visibility = Visibility.Collapsed;
			DeviceSelectionPanel.Visibility = Visibility.Visible;

			// Populate device list with checkboxes (all checked by default)
			DeviceListBox.ItemsSource = _availableDevices.Select(d => new DeviceCheckItem
			{
				Device = d,
				IsSelected = true,
				DisplayText = $"{d.DisplayName} ({d.Buttons.Length} buttons)"
			}).ToList();

			// Enable continue button
			ContinueButton.IsEnabled = true;

			Debug.WriteLine($"[WheelConfig] Device selection ready - {_availableDevices.Count} device(s) available");
		}

		/// <summary>
		/// Device selection item with checkbox state.
		/// </summary>
		private class DeviceCheckItem
		{
			public DirectInputDevice Device { get; set; }
			public bool IsSelected { get; set; }
			public string DisplayText { get; set; }
		}

		private void OnContinueClicked(object sender, RoutedEventArgs e)
		{
			// Get selected devices from checked items
			_selectedDevices = DeviceListBox.ItemsSource
				.Cast<DeviceCheckItem>()
				.Where(item => item.IsSelected)
				.Select(item => item.Device)
				.ToList();

			if (_selectedDevices.Count == 0)
			{
				ShowError("Please select at least one device to configure.");
				return;
			}

			Debug.WriteLine($"[WheelConfig] User selected {_selectedDevices.Count} device(s):");
			foreach (var device in _selectedDevices)
			{
				Debug.WriteLine($"[WheelConfig]   - {device.DisplayName}");
			}

			// Create watcher for button polling
			_watcher = DirectInputScanner.Watch();

			// Start button capture with selected devices only
			StartButtonCapture();
		}

		#endregion

		#region Button Capture
		
		/// <summary>
		/// Starts sequential button capture process.
		/// Polls ONLY user-selected devices - excludes vJoy and other unwanted devices.
		/// </summary>
		private void StartButtonCapture()
		{
			Debug.WriteLine("[WheelConfig] Starting button capture...");

			// Ensure button capture UI is visible
			DeviceSelectionPanel.Visibility = Visibility.Collapsed;
			ButtonCapturePanel.Visibility = Visibility.Visible;

			// Reset state
			_currentStep = 0;
			_capturedBindings.Clear();

			// Attach handlers to ALL buttons on SELECTED devices only
			foreach (var device in _selectedDevices)
			{
				foreach (var button in device.Buttons)
				{
					button.PropertyChanged += OnButtonPressedDuringConfig;
				}
				Debug.WriteLine($"[WheelConfig] Attached handlers to {device.DisplayName} ({device.Buttons.Length} buttons)");
			}

			// Start polling timer to detect button presses
			_pollTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(20) // 50Hz polling
			};
			_pollTimer.Tick += OnPollTick;
			_pollTimer.Start();

			Debug.WriteLine($"[WheelConfig] Polling started at 50Hz for {_selectedDevices.Count} selected device(s)");

			// Update prompt for first step
			UpdatePrompt();
			ResetButton.IsEnabled = false;
		}

		/// <summary>
		/// Polls SELECTED devices to update button states.
		/// This MUST run for PropertyChanged events to fire!
		/// </summary>
		private void OnPollTick(object sender, EventArgs e)
		{
			// Poll only user-selected devices (excludes vJoy, etc.)
			foreach (var device in _selectedDevices)
			{
				device.OnTick();
			}
		}
		
		/// <summary>
		/// Handles button presses during configuration.
		/// Captures button IDs sequentially from ANY selected device.
		/// Tracks which device each button came from.
		/// </summary>
		private void OnButtonPressedDuringConfig(object sender, PropertyChangedEventArgs e)
		{
			var button = (DirectInputButton)sender;

			// Only react to rising edge (button pressed, not released)
			if (e.PropertyName == nameof(DirectInputButton.Value) && button.Value)
			{
				// Find which device this button belongs to (from selected devices only)
				DirectInputDevice sourceDevice = null;
				foreach (var device in _selectedDevices)
				{
					if (device.Buttons.Contains(button))
					{
						sourceDevice = device;
						break;
					}
				}

				if (sourceDevice == null)
				{
					Debug.WriteLine("[WheelConfig] Button press from unknown device - ignoring");
					return;
				}

				var navKey = _stepNames[_currentStep];
				var isOptional = _stepOptional[_currentStep];

				// Check if user pressed SELECT to skip optional button
				if (isOptional && button.Id == GetSelectButtonIndex())
				{
					Debug.WriteLine($"[WheelConfig] Step {_currentStep} ({navKey}): User skipped optional button with SELECT");
					// Don't capture binding for this button
					_currentStep++;
					ResetButton.IsEnabled = true;

					if (_currentStep >= _stepNames.Length)
					{
						// All steps done - finish
						FinishConfiguration();
					}
					else
					{
						// Move to next step
						UpdatePrompt();
					}
					return;
				}

				// Capture this button with device info
				_capturedBindings[navKey] = new CapturedBinding
				{
					DeviceId = sourceDevice.ProductId,
					DeviceName = sourceDevice.DisplayName,
					ButtonIndex = button.Id
				};

				Debug.WriteLine($"[WheelConfig] Step {_currentStep} ({navKey}): {sourceDevice.DisplayName} Button {button.Id} captured");

				_currentStep++;
				ResetButton.IsEnabled = true;

				if (_currentStep >= _stepNames.Length)
				{
					// All steps done - finish
					FinishConfiguration();
				}
				else
				{
					// Move to next step
					UpdatePrompt();
				}
			}
		}
		
		/// <summary>
		/// Updates prompt text for current step.
		/// Shows which device(s) buttons were captured from.
		/// For optional buttons, shows that SELECT can skip.
		/// </summary>
		private void UpdatePrompt()
		{
			var navKey = _stepNames[_currentStep];
			var isOptional = _stepOptional[_currentStep];

			if (isOptional)
			{
				StepTitle.Text = $"Press button for {navKey} (optional shortcut)";
			}
			else
			{
				StepTitle.Text = $"Press button for {navKey}";
			}

			StepProgress.Text = $"Step {_currentStep + 1} of {_stepNames.Length}";

			// Show captured bindings so far (with device names)
			if (_currentStep > 0)
			{
				var capturedInfo = new List<string>();
				for (int i = 0; i < _currentStep; i++)
				{
					var key = _stepNames[i];
					if (_capturedBindings.ContainsKey(key))
					{
						var binding = _capturedBindings[key];
						capturedInfo.Add($"{key}: {binding.DeviceName} Btn{binding.ButtonIndex}");
					}
					else if (_stepOptional[i])
					{
						capturedInfo.Add($"{key}: (skipped)");
					}
				}
				var prompt = string.Join("\n", capturedInfo);
				if (isOptional)
				{
					prompt += "\n\nPress SELECT to skip";
				}
				StepPrompt.Text = prompt;
			}
			else
			{
				if (isOptional)
				{
					StepPrompt.Text = $"Press button on selected device(s)\n{_selectedDevices.Count} device(s) active\n\nPress SELECT to skip";
				}
				else
				{
					StepPrompt.Text = $"Press button on selected device(s)\n{_selectedDevices.Count} device(s) active";
				}
			}
		}
		
		private void OnResetClicked(object sender, RoutedEventArgs e)
		{
			Debug.WriteLine("[WheelConfig] User reset configuration");

			// Reset to first step
			_currentStep = 0;
			_capturedBindings.Clear();
			UpdatePrompt();
			ResetButton.IsEnabled = false;
		}

		/// <summary>
		/// Gets the button index for SELECT (if configured).
		/// Returns -1 if SELECT not yet configured.
		/// </summary>
		private int GetSelectButtonIndex()
		{
			if (_capturedBindings.ContainsKey("SELECT"))
			{
				return _capturedBindings["SELECT"].ButtonIndex;
			}
			return -1;
		}

		#endregion
		
		#region Completion
		
		/// <summary>
		/// Validates and saves multi-device configuration.
		/// </summary>
		private void FinishConfiguration()
		{
			Debug.WriteLine("[WheelConfig] Finishing multi-device configuration...");

			// Stop polling
			_pollTimer?.Stop();
			_pollTimer = null;

			// Detach button handlers from ALL devices
			foreach (var device in _availableDevices)
			{
				foreach (var button in device.Buttons)
				{
					button.PropertyChanged -= OnButtonPressedDuringConfig;
				}
			}

			// Validate - check for duplicate button+device combinations
			// Required: 6 navigation buttons + GO button = 7 minimum
			// Optional: SC1, SC2, SC3 (up to 10 total)
			var uniqueBindings = new HashSet<string>();
			foreach (var binding in _capturedBindings.Values)
			{
				var key = $"{binding.DeviceId}:{binding.ButtonIndex}";
				uniqueBindings.Add(key);
			}

			// Must have at least 7 unique bindings (6 nav + GO)
			if (uniqueBindings.Count < 7)
			{
				ShowError("Error: You selected the same button multiple times.\n\n" +
						 $"Required: 7 unique buttons (6 navigation + GO)\n" +
						 $"You configured: {uniqueBindings.Count} unique buttons\n\n" +
						 "Click Reset to try again.");
				_currentStep = 0;
				UpdatePrompt();
				return;
			}

			// Verify configured count matches captured count
			if (uniqueBindings.Count != _capturedBindings.Count)
			{
				ShowError("Error: Duplicate buttons detected.\n\n" +
						 $"You selected {_capturedBindings.Count} buttons, but only {uniqueBindings.Count} are unique.\n\n" +
						 "Each function must use a different button. Click Reset to try again.");
				_currentStep = 0;
				UpdatePrompt();
				return;
			}

			// Convert to Navigator.ButtonBinding format
			var bindings = new Dictionary<string, Navigator.ButtonBinding>();
			foreach (var kvp in _capturedBindings)
			{
				var navKey = kvp.Key;
				var binding = kvp.Value;

				bindings[navKey] = new Navigator.ButtonBinding
				{
					NavKey = navKey,
					DeviceId = binding.DeviceId,
					DeviceName = binding.DeviceName,
					ButtonIndex = binding.ButtonIndex
				};
			}

			Debug.WriteLine("[WheelConfig] Multi-device configuration:");
			foreach (var kvp in bindings)
			{
				var b = kvp.Value;
				Debug.WriteLine($"[WheelConfig]   {b.NavKey}: {b.DeviceName} ({b.DeviceId}) Button {b.ButtonIndex}");
			}

			// Save configuration via Navigator API (multi-device format)
			Navigator.SaveWheelConfig(bindings);

			Debug.WriteLine("[WheelConfig] ✅ Configuration saved successfully");

			// Show completion
			ShowCompletion();
		}
		
		/// <summary>
		/// Shows completion summary with multi-device info.
		/// </summary>
		private void ShowCompletion()
		{
			ButtonCapturePanel.Visibility = Visibility.Collapsed;
			CompletionPanel.Visibility = Visibility.Visible;

			// Build multi-device summary
			var deviceCount = _capturedBindings.Values.Select(b => b.DeviceId).Distinct().Count();
			var summary = $"Configuration Complete!\n\n";

			if (deviceCount == 1)
			{
				var deviceName = _capturedBindings.Values.First().DeviceName;
				summary += $"Device: {deviceName}\n\n";
			}
			else
			{
				summary += $"Using {deviceCount} devices\n\n";
			}

			summary += "Button Mapping:\n";
			foreach (var navKey in _stepNames)
			{
				if (_capturedBindings.ContainsKey(navKey))
				{
					var binding = _capturedBindings[navKey];
					var deviceDisplay = deviceCount > 1 ? $" ({binding.DeviceName})" : "";
					summary += $"  {navKey,-7}: Button {binding.ButtonIndex}{deviceDisplay}\n";
				}
			}

			summary += "\n";

			// Check if any device is a modular base
			var modularBases = new[] { "346E-0006", "0EB7-6204", "3416-0301" };
			var hasModularBase = _capturedBindings.Values.Any(b => 
				b.DeviceId.Length >= 9 && modularBases.Contains(b.DeviceId.Substring(0, 9)));

			if (hasModularBase)
			{
				summary += "📌 Note: Swappable Wheel Rims\n" +
						  "One or more devices support interchangeable rims. If you swap rims, " +
						  "ensure your wheel's control panel software maps the same physical " +
						  "buttons to the same button numbers across all rims.";
			}
			else
			{
				summary += "Wheel navigation is now active!\n" +
						  "Use your configured buttons to navigate the launcher.";
			}

			CompletionSummary.Text = summary;
		}
		
		#endregion
		
		#region Button Handlers
		
		private void OnCancelClicked(object sender, RoutedEventArgs e)
		{
			Debug.WriteLine("[WheelConfig] User cancelled configuration");
			Close();
		}
		
		private void OnDoneClicked(object sender, RoutedEventArgs e)
		{
			Debug.WriteLine("[WheelConfig] Configuration complete, closing dialog");
			DialogResult = true;
			Close();
		}
		
		#endregion
		
		#region Cleanup
		
		private void OnDialogClosed(object sender, EventArgs e)
		{
			// Stop polling timer
			_pollTimer?.Stop();
			_pollTimer = null;

			// Cleanup - detach handlers from selected devices
			if (_selectedDevices != null)
			{
				foreach (var device in _selectedDevices)
				{
					foreach (var button in device.Buttons)
					{
						button.PropertyChanged -= OnButtonPressedDuringConfig;
					}
				}
			}

			_watcher?.Dispose();
			_watcher = null;

			Debug.WriteLine("[WheelConfig] Dialog closed, cleanup complete");
		}
		
		#endregion
		
		#region Helper Methods
		
		private void ShowError(string message)
		{
			// CRITICAL: Pause button detection before showing error dialog
			// This prevents button presses from being captured while error dialog is visible
			// which would corrupt the DirectInput state and cause missed steps
			Debug.WriteLine($"[WheelConfig] Pausing button detection for error dialog");

			// Temporarily detach handlers
			if (_selectedDevices != null)
			{
				foreach (var device in _selectedDevices)
				{
					foreach (var button in device.Buttons)
					{
						button.PropertyChanged -= OnButtonPressedDuringConfig;
					}
				}
			}

			// Show error dialog (blocking)
			ModernDialog.ShowMessage(message, "Wheel Configuration", MessageBoxButton.OK);

			// Resume button detection after error dialog closes
			Debug.WriteLine($"[WheelConfig] Resuming button detection after error dialog");

			// Re-attach handlers
			if (_selectedDevices != null)
			{
				foreach (var device in _selectedDevices)
				{
					foreach (var button in device.Buttons)
					{
						button.PropertyChanged += OnButtonPressedDuringConfig;
					}
				}
			}
		}
		
		#endregion
	}
}
