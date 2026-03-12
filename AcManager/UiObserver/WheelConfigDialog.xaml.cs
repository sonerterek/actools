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

		private readonly string[] _stepNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };
		private readonly int[] _capturedButtons = new int[6];
		private int _currentStep = 0;

		private DirectInputScanner.Watcher _watcher;
		private DirectInputDevice _selectedDevice;
		private List<DirectInputDevice> _availableDevices;

		// Polling timer for button detection
		private System.Windows.Threading.DispatcherTimer _pollTimer;

		#endregion
		
		#region Constructor
		
		public WheelConfigDialog()
		{
			InitializeComponent();
			
			// Hook cleanup event
			Closed += OnDialogClosed;
			
			// Start in device selection
			StartDeviceSelection();
		}
		
		#endregion
		
		#region Device Selection
		
		/// <summary>
		/// Scans for wheels and shows device selection or auto-proceeds if only one found.
		/// </summary>
		private async void StartDeviceSelection()
		{
			Debug.WriteLine("[WheelConfig] Starting device selection...");

			// Show "Scanning..." message immediately
			StepTitle.Text = "Detecting steering wheels...";
			StepPrompt.Text = "Please wait";
			StepProgress.Text = "";
			ButtonCapturePanel.Visibility = Visibility.Visible;

			// Wait for DirectInput scan to complete (async operation)
			var joysticks = await DirectInputScanner.GetAsync();

			if (joysticks == null || joysticks.Count == 0)
			{
				ShowError("No steering wheels found.\n\nPlease connect a wheel and try again.");
				Close();
				return;
			}

			// Convert to DirectInputDevice list (exclude Xbox controllers)
			_availableDevices = new List<DirectInputDevice>();
			foreach (var joystick in joysticks)
			{
				var device = DirectInputDevice.Create(joystick, -1);
				if (device != null && !device.IsController && device.Buttons.Length >= 6)
				{
					_availableDevices.Add(device);
				}
			}

			if (_availableDevices.Count == 0)
			{
				ShowError("No compatible steering wheels found.\n\n" +
						 "Wheels must have at least 6 buttons.");
				Close();
				return;
			}

			Debug.WriteLine($"[WheelConfig] Found {_availableDevices.Count} compatible device(s)");

			// Auto-select if only one device
			if (_availableDevices.Count == 1)
			{
				_selectedDevice = _availableDevices[0];
				Debug.WriteLine($"[WheelConfig] Auto-selected: {_selectedDevice.DisplayName}");

				// Create watcher for button polling
				_watcher = DirectInputScanner.Watch();

				StartButtonCapture();
			}
			else
			{
				// Hide scanning message, show device selection UI
				ButtonCapturePanel.Visibility = Visibility.Collapsed;
				DeviceListBox.ItemsSource = _availableDevices;
				DeviceSelectionPanel.Visibility = Visibility.Visible;
			}
		}
		
		private void OnDeviceSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			ContinueButton.IsEnabled = DeviceListBox.SelectedItem != null;
		}
		
		private void OnContinueClicked(object sender, RoutedEventArgs e)
		{
			_selectedDevice = DeviceListBox.SelectedItem as DirectInputDevice;
			if (_selectedDevice == null) return;

			Debug.WriteLine($"[WheelConfig] User selected: {_selectedDevice.DisplayName}");

			// Create watcher for button polling
			_watcher = DirectInputScanner.Watch();

			StartButtonCapture();
		}
		
		#endregion
		
		#region Button Capture
		
		/// <summary>
		/// Starts sequential button capture process.
		/// </summary>
		private void StartButtonCapture()
		{
			Debug.WriteLine("[WheelConfig] Starting button capture...");

			// Hide device selection, show capture UI
			DeviceSelectionPanel.Visibility = Visibility.Collapsed;
			ButtonCapturePanel.Visibility = Visibility.Visible;

			// Reset state
			_currentStep = 0;
			Array.Clear(_capturedButtons, 0, _capturedButtons.Length);

			// Attach handlers to ALL buttons
			foreach (var button in _selectedDevice.Buttons)
			{
				button.PropertyChanged += OnButtonPressedDuringConfig;
			}

			// ✅ CRITICAL: Start polling timer to detect button presses
			// Without this, PropertyChanged events will never fire!
			_pollTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(20) // 50Hz polling
			};
			_pollTimer.Tick += OnPollTick;
			_pollTimer.Start();

			Debug.WriteLine("[WheelConfig] Polling started at 50Hz");

			// Update prompt for first step
			UpdatePrompt();
			ResetButton.IsEnabled = false;
		}

		/// <summary>
		/// Polls the device to update button states.
		/// This MUST run for PropertyChanged events to fire!
		/// </summary>
		private void OnPollTick(object sender, EventArgs e)
		{
			_selectedDevice?.OnTick();
		}
		
		/// <summary>
		/// Handles button presses during configuration.
		/// Captures button IDs sequentially.
		/// </summary>
		private void OnButtonPressedDuringConfig(object sender, PropertyChangedEventArgs e)
		{
			var button = (DirectInputButton)sender;

			// Only react to rising edge (button pressed, not released)
			if (e.PropertyName == nameof(DirectInputButton.Value) && button.Value)
			{
				// Capture this button
				_capturedButtons[_currentStep] = button.Id;

				Debug.WriteLine($"[WheelConfig] Step {_currentStep} ({_stepNames[_currentStep]}): Button {button.Id} captured");

				_currentStep++;
				ResetButton.IsEnabled = true;

				if (_currentStep >= 6)
				{
					// All buttons captured - finish
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
		/// </summary>
		private void UpdatePrompt()
		{
			StepTitle.Text = $"Press button for {_stepNames[_currentStep]} navigation";
			StepProgress.Text = $"Step {_currentStep + 1} of 6";
			
			// Show captured buttons so far
			if (_currentStep > 0)
			{
				var captured = string.Join(", ", _capturedButtons.Take(_currentStep));
				StepPrompt.Text = $"Captured: [{captured}]";
			}
			else
			{
				StepPrompt.Text = "Press any button on your wheel";
			}
		}
		
		private void OnResetClicked(object sender, RoutedEventArgs e)
		{
			Debug.WriteLine("[WheelConfig] User reset configuration");
			
			// Reset to first step
			_currentStep = 0;
			Array.Clear(_capturedButtons, 0, _capturedButtons.Length);
			UpdatePrompt();
			ResetButton.IsEnabled = false;
		}
		
		#endregion
		
		#region Completion
		
		/// <summary>
		/// Validates and saves configuration.
		/// </summary>
		private void FinishConfiguration()
		{
			Debug.WriteLine("[WheelConfig] Finishing configuration...");

			// Stop polling
			_pollTimer?.Stop();
			_pollTimer = null;

			// Detach button handlers
			foreach (var button in _selectedDevice.Buttons)
			{
				button.PropertyChanged -= OnButtonPressedDuringConfig;
			}

			// Validate - check for duplicate buttons
			if (_capturedButtons.Distinct().Count() != 6)
			{
				ShowError("Error: You selected the same button multiple times.\n\n" +
						 "Each navigation function must use a different button.\n\n" +
						 "Click Reset to try again.");
				_currentStep = 0;
				UpdatePrompt();
				return;
			}

			// ✅ DIAGNOSTIC: Show device details BEFORE saving
			MessageBox.Show(
				$"🎮 Selected Device Details:\n\n" +
				$"Display Name: {_selectedDevice.DisplayName}\n" +
				$"ProductId: {_selectedDevice.ProductId}\n\n" +
				$"⚠️ USING ProductId (NOT InstanceId)\n" +
				$"ProductId persists across reboots and USB ports.\n\n" +
				$"Button Mapping: [{string.Join(", ", _capturedButtons)}]\n\n" +
				$"About to save this configuration...",
				"DEBUG - Device Info",
				MessageBoxButton.OK
			);

			// Save configuration via Navigator API
			Navigator.SaveWheelConfig(
				deviceId: _selectedDevice.ProductId,
				deviceName: _selectedDevice.DisplayName,
				buttonMapping: _capturedButtons
			);

			Debug.WriteLine("[WheelConfig] ✅ Configuration saved successfully");

			// Show completion
			ShowCompletion();
		}
		
		/// <summary>
		/// Shows completion summary.
		/// </summary>
		private void ShowCompletion()
		{
			ButtonCapturePanel.Visibility = Visibility.Collapsed;
			CompletionPanel.Visibility = Visibility.Visible;
			
			// Build summary text
			var summary = $"Device: {_selectedDevice.DisplayName}\n\n" +
			             $"Button Mapping:\n" +
			             $"  UP:     Button {_capturedButtons[0]}\n" +
			             $"  DOWN:   Button {_capturedButtons[1]}\n" +
			             $"  LEFT:   Button {_capturedButtons[2]}\n" +
			             $"  RIGHT:  Button {_capturedButtons[3]}\n" +
			             $"  SELECT: Button {_capturedButtons[4]}\n" +
			             $"  BACK:   Button {_capturedButtons[5]}\n\n";
			
			// Add note for modular bases
			var productKey = _selectedDevice.ProductId.Length >= 9 
				? _selectedDevice.ProductId.Substring(0, 9) 
				: _selectedDevice.ProductId;
			
			var modularBases = new[] { "346E-0006", "0EB7-6204", "3416-0301" };
			if (modularBases.Contains(productKey))
			{
				summary += "📌 Note: Swappable Wheel Rims\n" +
				          "Your wheel base supports interchangeable rims. If you swap rims, " +
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

			// Cleanup - detach handlers and dispose watcher
			if (_selectedDevice != null)
			{
				foreach (var button in _selectedDevice.Buttons)
				{
					button.PropertyChanged -= OnButtonPressedDuringConfig;
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
			ModernDialog.ShowMessage(message, "Wheel Configuration", MessageBoxButton.OK);
		}
		
		#endregion
	}
}
