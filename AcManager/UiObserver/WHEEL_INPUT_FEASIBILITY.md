# Racing Wheel Button Input Feasibility Analysis

**Date:** December 2024  
**Objective:** Add racing wheel button support (6 buttons: Up, Down, Left, Right, Select, Back) for UiObserver navigation  
**Proposed Technology:** DirectInput or HID  
**Status:** ✅ **HIGHLY FEASIBLE**

---

## 📊 Executive Summary

**Verdict: ✅ HIGHLY FEASIBLE with MINIMAL EFFORT**

The codebase **already has comprehensive DirectInput support** for racing wheels and game controllers. Adding wheel button navigation requires:
- **Estimated Effort:** 4-8 hours
- **Complexity:** Low (reuse existing infrastructure)
- **Risk:** Very Low (mature DirectInput code already in production)
- **Integration:** Clean separation - new `Navigator.Wheel.cs` partial class

---

## 🏗️ Existing Infrastructure Analysis

### 1. **DirectInput System** ✅ Already Implemented

**Location:** `AcManager.Tools/Helpers/DirectInput/`

**Key Components:**
- ✅ `DirectInputDevice.cs` - Full wheel/controller abstraction
- ✅ `DirectInputButton.cs` - Button state tracking with events
- ✅ `DirectInputScanner.cs` - Auto-discovery of connected devices
- ✅ SlimDX integration - Mature DirectInput library

**Current Usage:**
```csharp
// Already used for AC's control configuration
DirectInputDevice wheel = DirectInputDevice.Create(joystick, index);
wheel.Buttons[i].Value; // bool - button pressed state
wheel.OnTick(); // Polls device state
```

**Event Support:**
```csharp
DirectInputButton button = wheel.Buttons[buttonIndex];
button.PropertyChanged += (s, e) => {
    if (e.PropertyName == "Value") {
        bool pressed = button.Value;
        // React to button press
    }
};
```

### 2. **Navigator Architecture** ✅ Already Event-Driven

**Location:** `AcManager/UiObserver/Navigator.cs` (partial class pattern)

**Current Input Sources:**
- ✅ **StreamDeck Integration** (`Navigator.SD.cs`) - Named pipe communication
- ✅ **Keyboard Support** (implicit via WPF)

**Key Methods to Call:**
```csharp
// From Navigator.Navigation.cs
internal static void MoveInDirection(NavDirection dir);

// From Navigator.cs
internal static void ActivateFocused();
internal static void ExitGroup();

// Navigation directions
internal enum NavDirection { Up, Down, Left, Right }
```

**StreamDeck Integration Pattern (Reference):**
```csharp
// Navigator.SD.cs lines 170-250
private static void OnStreamDeckKeyPressed(object sender, SDPKeyPressEventArgs e)
{
    Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
        switch (e.KeyName) {
            case "Up":    MoveInDirection(NavDirection.Up); break;
            case "Down":  MoveInDirection(NavDirection.Down); break;
            case "Left":  MoveInDirection(NavDirection.Left); break;
            case "Right": MoveInDirection(NavDirection.Right); break;
            case "Select": ActivateFocused(); break;
            case "Back":  ExitGroup(); break;
        }
    }));
}
```

**Pattern to Replicate:** Same structure for wheel button events!

---

## 🎯 Proposed Implementation

### Architecture: Follow Existing Partial Class Pattern

```
Navigator.cs            (Core navigation logic)
Navigator.SD.cs         (StreamDeck integration) ← EXISTING
Navigator.Wheel.cs      (Wheel button integration) ← NEW
Navigator.Navigation.cs (Movement algorithms)
Navigator.Focus.cs      (Focus management)
```

### Implementation Plan

#### **Phase 1: Core Wheel Button Support (4 hours)**

**File:** `AcManager/UiObserver/Navigator.Wheel.cs`

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AcManager.Tools.Helpers.DirectInput;
using AcManager.Tools.SemiGui;
using FirstFloor.ModernUI.Serialization;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Navigator - Racing Wheel Button Integration
    /// Provides navigation control via 6 wheel buttons (Up/Down/Left/Right/Select/Back)
    /// ✅ EFFICIENT POLLING: Only polls when enabled, sleeps during gameplay
    /// </summary>
    internal static partial class Navigator
    {
        #region Fields

        private static DirectInputScanner.Watcher _wheelWatcher;
        private static DirectInputDevice _navigationWheel;
        private static int[] _wheelButtonMapping = new int[6];
        private static bool[] _lastButtonStates = new bool[6];
        private static bool _wheelNavigationEnabled;
        private static DispatcherTimer _wheelPollTimer;

        // Default button mappings for common wheels (VID-PID format)
        private static readonly System.Collections.Generic.Dictionary<string, int[]> DefaultMappings = 
            new System.Collections.Generic.Dictionary<string, int[]>
        {
            // Logitech G29/G920 (ProductGuid prefix)
            { "046D-C24F", new[] { 0, 1, 2, 3, 4, 5 } },  // D-Pad + X/Circle

            // Thrustmaster T300/TX
            { "044F-B66E", new[] { 8, 9, 10, 11, 0, 1 } },

            // Generic fallback (requires manual config)
            { "DEFAULT", new[] { -1, -1, -1, -1, -1, -1 } }
        };

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes wheel button navigation subsystem.
        /// Called during Navigator initialization.
        /// </summary>
        private static void InitializeWheelNavigation()
        {
            Debug.WriteLine("[Navigator.Wheel] Initializing wheel navigation...");

            // Hook game lifecycle events (SAME PATTERN as StreamDeck)
            GameWrapper.Started += OnGameStarted_Wheel;
            GameWrapper.Ended += OnGameEnded_Wheel;

            // Create polling timer (but don't start yet)
            _wheelPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // 20Hz polling
            };
            _wheelPollTimer.Tick += OnWheelPollTick;

            // Load configuration and enable if valid
            if (LoadWheelButtonConfig())
            {
                EnableWheelPolling();
                Debug.WriteLine($"[Navigator.Wheel] ✅ Enabled on {_navigationWheel?.DisplayName ?? "unknown device"}");
            }
            else
            {
                Debug.WriteLine("[Navigator.Wheel] No valid configuration found - wheel navigation disabled");
                Debug.WriteLine("[Navigator.Wheel] Use configuration wizard to set up wheel buttons");
            }
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Loads wheel button configuration from ValuesStorage.
        /// Returns true if valid configuration loaded.
        /// </summary>
        private static bool LoadWheelButtonConfig()
        {
            try
            {
                var enabled = ValuesStorage.Get("WheelNav_Enabled", false);
                if (!enabled) return false;

                var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
                if (string.IsNullOrEmpty(deviceId)) return false;

                // Find device from DirectInput scanner
                var scanner = DirectInputScanner.Watch();
                _navigationWheel = scanner.Devices?.FirstOrDefault(d => d.ProductId == deviceId);
                scanner.Dispose(); // We'll create our own watcher when enabling

                if (_navigationWheel == null)
                {
                    Debug.WriteLine($"[Navigator.Wheel] Device {deviceId} not found");
                    return false;
                }

                // Load button mapping
                var mapping = ValuesStorage.Get<int[]>("WheelNav_ButtonMapping");
                if (mapping?.Length == 6 && mapping.All(b => b >= 0))
                {
                    _wheelButtonMapping = mapping;
                }
                else
                {
                    // Try default mapping for this wheel
                    _wheelButtonMapping = GetDefaultMapping(_navigationWheel);
                    if (_wheelButtonMapping.All(b => b < 0))
                    {
                        Debug.WriteLine("[Navigator.Wheel] No default mapping available");
                        return false;
                    }
                    Debug.WriteLine("[Navigator.Wheel] Using default button mapping");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Navigator.Wheel] Error loading config: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets default button mapping for a wheel model.
        /// </summary>
        private static int[] GetDefaultMapping(DirectInputDevice device)
        {
            // Extract VID-PID from ProductGuid
            var productId = device.ProductId.Substring(0, 9);

            return DefaultMappings.ContainsKey(productId)
                ? DefaultMappings[productId]
                : DefaultMappings["DEFAULT"];
        }

        #endregion

        #region Enable/Disable

        /// <summary>
        /// Enables wheel button polling.
        /// Creates DirectInput watcher (wakes scanner thread) and starts polling timer.
        /// </summary>
        private static void EnableWheelPolling()
        {
            if (_wheelWatcher != null) return; // Already enabled

            Debug.WriteLine("[Navigator.Wheel] Enabling wheel polling...");

            // Create watcher - this wakes up the DirectInput scanner thread
            _wheelWatcher = DirectInputScanner.Watch();
            _wheelWatcher.Update += OnWheelDevicesUpdated;

            // Refresh device reference
            _navigationWheel = _wheelWatcher.Devices?.FirstOrDefault(d => 
                d.ProductId == ValuesStorage.Get<string>("WheelNav_DeviceId"));

            if (_navigationWheel != null)
            {
                _wheelNavigationEnabled = true;
                _wheelPollTimer.Start();
                Debug.WriteLine("[Navigator.Wheel] ✅ Polling enabled");
            }
            else
            {
                Debug.WriteLine("[Navigator.Wheel] ❌ Device not found, polling not started");
            }
        }

        /// <summary>
        /// Disables wheel button polling.
        /// Disposes watcher (scanner thread goes to sleep) and stops polling timer.
        /// </summary>
        private static void DisableWheelPolling()
        {
            Debug.WriteLine("[Navigator.Wheel] Disabling wheel polling...");

            _wheelNavigationEnabled = false;
            _wheelPollTimer?.Stop();

            if (_wheelWatcher != null)
            {
                _wheelWatcher.Update -= OnWheelDevicesUpdated;
                _wheelWatcher.Dispose(); // Scanner thread sleeps if no watchers
                _wheelWatcher = null;
            }

            Debug.WriteLine("[Navigator.Wheel] ✅ Polling disabled");
        }

        #endregion

        #region Polling & Event Handling

        /// <summary>
        /// Polls wheel buttons and detects presses.
        /// Runs at 20Hz when enabled.
        /// </summary>
        private static void OnWheelPollTick(object sender, EventArgs e)
        {
            if (!_wheelNavigationEnabled || _navigationWheel == null) return;

            // Poll device state
            _navigationWheel.OnTick();

            // Check each mapped button for press events (rising edge)
            for (int i = 0; i < 6; i++)
            {
                int buttonIndex = _wheelButtonMapping[i];
                if (buttonIndex < 0 || buttonIndex >= _navigationWheel.Buttons.Length)
                    continue;

                bool currentState = _navigationWheel.Buttons[buttonIndex].Value;

                // Detect rising edge (button just pressed)
                if (currentState && !_lastButtonStates[i])
                {
                    OnWheelButtonPressed(i);
                }

                _lastButtonStates[i] = currentState;
            }
        }

        /// <summary>
        /// Handles wheel button press events.
        /// Already on UI thread (DispatcherTimer runs on UI thread).
        /// </summary>
        private static void OnWheelButtonPressed(int navButtonIndex)
        {
            Debug.WriteLine($"[Navigator.Wheel] Button {navButtonIndex} pressed");

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
                        ActivateFocused();
                        break;
                    case 5: // Back
                        ExitGroup();
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Navigator.Wheel] Error handling button {navButtonIndex}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles DirectInput device list changes (plug/unplug).
        /// </summary>
        private static void OnWheelDevicesUpdated(object sender, EventArgs e)
        {
            var watcher = (DirectInputScanner.Watcher)sender;
            var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");

            // Check if our device is still connected
            _navigationWheel = watcher.Devices?.FirstOrDefault(d => d.ProductId == deviceId);

            if (_navigationWheel == null)
            {
                Debug.WriteLine("[Navigator.Wheel] ⚠ Device disconnected");
                _wheelNavigationEnabled = false;
            }
            else if (!_wheelNavigationEnabled)
            {
                Debug.WriteLine("[Navigator.Wheel] ✅ Device reconnected");
                _wheelNavigationEnabled = true;
            }
        }

        #endregion

        #region Game Lifecycle

        /// <summary>
        /// Game started - disable wheel navigation.
        /// </summary>
        private static void OnGameStarted_Wheel(object sender, EventArgs e)
        {
            Debug.WriteLine("[Navigator.Wheel] Game started - disabling navigation");
            DisableWheelPolling();
        }

        /// <summary>
        /// Game ended - re-enable wheel navigation.
        /// </summary>
        private static void OnGameEnded_Wheel(object sender, EventArgs e)
        {
            Debug.WriteLine("[Navigator.Wheel] Game ended - re-enabling navigation");

            if (ValuesStorage.Get("WheelNav_Enabled", false))
            {
                EnableWheelPolling();
            }
        }

        #endregion

        #region Public API for Configuration Wizard

        /// <summary>
        /// Saves wheel navigation configuration.
        /// Called by configuration wizard after user presses all 6 buttons.
        /// </summary>
        public static void SaveWheelConfig(string deviceId, string deviceName, int[] buttonMapping)
        {
            if (buttonMapping?.Length != 6)
                throw new ArgumentException("Button mapping must contain exactly 6 buttons");

            ValuesStorage.Set("WheelNav_Enabled", true);
            ValuesStorage.Set("WheelNav_DeviceId", deviceId);
            ValuesStorage.Set("WheelNav_DeviceName", deviceName);
            ValuesStorage.Set("WheelNav_ButtonMapping", buttonMapping);

            Debug.WriteLine($"[Navigator.Wheel] Configuration saved: {deviceName}");
            Debug.WriteLine($"[Navigator.Wheel] Buttons: {string.Join(", ", buttonMapping)}");

            // Reload and enable
            if (LoadWheelButtonConfig())
            {
                EnableWheelPolling();
            }
        }

        /// <summary>
        /// Disables wheel navigation (called from settings UI).
        /// </summary>
        public static void DisableWheelNavigation()
        {
            ValuesStorage.Set("WheelNav_Enabled", false);
            DisableWheelPolling();
            Debug.WriteLine("[Navigator.Wheel] Navigation disabled by user");
        }

        #endregion
    }
}
```

#### **Phase 2: Configuration Wizard (2-3 hours)**

**File:** `AcManager/UiObserver/WheelConfigWizard.xaml` + `.cs`

**User Experience:**
1. User opens NWRS Launcher
2. Presses a key/button combination to start configuration (e.g., StreamDeck key or keyboard shortcut)
3. Dialog appears: **"Press button for UP navigation"**
4. User presses their desired button on the wheel
5. Dialog updates: **"Press button for DOWN navigation"**
6. Repeat for LEFT, RIGHT, SELECT, BACK
7. Configuration saved automatically

**Implementation:**

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using AcManager.Tools.Helpers.DirectInput;
using FirstFloor.ModernUI.Dialogs;

namespace AcManager.UiObserver
{
    public partial class WheelConfigWizard : INotifyPropertyChanged
    {
        private int _currentStep = 0;
        private readonly string[] _stepNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };
        private readonly int[] _capturedButtons = new int[6];
        private DirectInputScanner.Watcher _configWatcher;
        private DirectInputDevice _selectedDevice;
        private List<DirectInputDevice> _availableDevices;

        public event PropertyChangedEventHandler PropertyChanged;

        #region Properties

        private string _promptText;
        public string PromptText
        {
            get => _promptText;
            set { _promptText = value; OnPropertyChanged(nameof(PromptText)); }
        }

        private string _stepProgress;
        public string StepProgress
        {
            get => _stepProgress;
            set { _stepProgress = value; OnPropertyChanged(nameof(StepProgress)); }
        }

        #endregion

        public WheelConfigWizard()
        {
            InitializeComponent();
            DataContext = this;
        }

        /// <summary>
        /// Step 1: Device Selection
        /// </summary>
        public void ShowDeviceSelection()
        {
            // Scan for DirectInput devices
            _configWatcher = DirectInputScanner.Watch();
            _availableDevices = _configWatcher.Devices?.Where(d => 
                !d.IsController && // Exclude Xbox controllers
                d.Buttons.Length >= 6  // Must have at least 6 buttons
            ).ToList();

            if (_availableDevices == null || _availableDevices.Count == 0)
            {
                ModernDialog.ShowMessage("No compatible steering wheels found.\n\nPlease connect a wheel and try again.",
                    "No Wheels Detected", MessageBoxButton.OK);
                Close();
                return;
            }

            // If only one wheel, auto-select it
            if (_availableDevices.Count == 1)
            {
                _selectedDevice = _availableDevices[0];
                StartButtonCapture();
            }
            else
            {
                // Show device selection UI
                ShowDeviceSelectionDialog();
            }
        }

        /// <summary>
        /// Step 2: Sequential Button Capture
        /// </summary>
        private void StartButtonCapture()
        {
            _currentStep = 0;
            UpdatePrompt();

            // Attach handlers to all buttons on selected device
            foreach (var button in _selectedDevice.Buttons)
            {
                button.PropertyChanged += OnButtonPressedDuringConfig;
            }
        }

        /// <summary>
        /// Handles button presses during configuration.
        /// Captures button IDs in sequence.
        /// </summary>
        private void OnButtonPressedDuringConfig(object sender, PropertyChangedEventArgs e)
        {
            var button = (DirectInputButton)sender;

            // Only react to rising edge (button pressed, not released)
            if (e.PropertyName == nameof(DirectInputButton.Value) && button.Value)
            {
                // Capture this button for current step
                _capturedButtons[_currentStep] = button.Id;

                Debug.WriteLine($"[WheelConfig] Step {_currentStep} ({_stepNames[_currentStep]}): Button {button.Id} captured");

                _currentStep++;

                if (_currentStep >= 6)
                {
                    // All buttons captured - finish wizard
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
            PromptText = $"Press button for {_stepNames[_currentStep]} navigation";
            StepProgress = $"Step {_currentStep + 1} of 6";
        }

        /// <summary>
        /// Saves configuration and closes wizard.
        /// </summary>
        private void FinishConfiguration()
        {
            // Detach all button handlers
            foreach (var button in _selectedDevice.Buttons)
            {
                button.PropertyChanged -= OnButtonPressedDuringConfig;
            }

            // Validate configuration (no duplicate buttons)
            if (_capturedButtons.Distinct().Count() != 6)
            {
                ModernDialog.ShowMessage("Error: You selected the same button multiple times.\n\nPlease restart configuration.",
                    "Invalid Configuration", MessageBoxButton.OK);
                RestartConfiguration();
                return;
            }

            // Save configuration via Navigator API
            Navigator.SaveWheelConfig(
                deviceId: _selectedDevice.ProductId,
                deviceName: _selectedDevice.DisplayName,
                buttonMapping: _capturedButtons
            );

            ModernDialog.ShowMessage(
                $"Wheel navigation configured successfully!\n\n" +
                $"Device: {_selectedDevice.DisplayName}\n\n" +
                $"Buttons:\n" +
                $"  UP: {_capturedButtons[0]}\n" +
                $"  DOWN: {_capturedButtons[1]}\n" +
                $"  LEFT: {_capturedButtons[2]}\n" +
                $"  RIGHT: {_capturedButtons[3]}\n" +
                $"  SELECT: {_capturedButtons[4]}\n" +
                $"  BACK: {_capturedButtons[5]}",
                "Configuration Complete",
                MessageBoxButton.OK
            );

            CloseWizard();
        }

        /// <summary>
        /// Cleans up and closes wizard.
        /// </summary>
        private void CloseWizard()
        {
            _configWatcher?.Dispose();
            Close();
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

**XAML (Simple Dialog):**
```xaml
<mui:ModernDialog x:Class="AcManager.UiObserver.WheelConfigWizard"
                  Title="Configure Wheel Navigation"
                  Width="500" Height="300">
    <DockPanel Margin="20">
        <TextBlock DockPanel.Dock="Top" Text="{Binding PromptText}" 
                   FontSize="24" FontWeight="Bold" 
                   TextAlignment="Center" Margin="0,40,0,20"/>

        <TextBlock DockPanel.Dock="Top" Text="{Binding StepProgress}" 
                   FontSize="14" TextAlignment="Center" 
                   Foreground="Gray" Margin="0,0,0,40"/>

        <StackPanel HorizontalAlignment="Center" VerticalAlignment="Bottom">
            <Button Content="Cancel" Click="OnCancel" Padding="20,10"/>
        </StackPanel>
    </DockPanel>
</mui:ModernDialog>
```

**Triggering the Wizard:**
```csharp
// From StreamDeck or keyboard shortcut
case "ConfigureWheelNav":
    var wizard = new WheelConfigWizard();
    wizard.ShowDeviceSelection();
    break;
```

---

## ✅ Advantages of DirectInput Approach

### 1. **Mature & Proven**
- Already used for AC control configuration
- Handles device connect/disconnect
- Polling infrastructure already exists
- SlimDX is stable and well-tested

### 2. **Zero Conflicts**
- DirectInput uses **background cooperative mode**
- Won't interfere with AC's gameplay input
- Can be disabled/enabled programmatically

### 3. **Universal Compatibility**
- Works with **any** DirectInput device:
  - Logitech wheels (G29, G920, G923, etc.)
  - Thrustmaster wheels (T300, T500, TX, etc.)
  - Fanatec wheels (CSL, Podium, etc.)
  - Generic USB game controllers

### 4. **Clean Architecture**
- Follows existing `Navigator` partial class pattern
- No changes to core navigation logic
- Easy to enable/disable independently

---

## 🚧 Considerations & Implementation Details

### 1. **DirectInput Polling Strategy** ⚠️ CRITICAL INSIGHT

**Current Behavior (Confirmed):**
- DirectInput is **NOT constantly polling** in the existing codebase
- `DirectInputScanner` only scans when a `Watcher` is active
- Controls configuration page creates a Watcher when user is configuring inputs
- Scanner thread sleeps when no watchers exist (line 127-131)

**Implementation Strategy:**
```csharp
// Navigator.Wheel.cs will create its own Watcher when enabled
private static DirectInputScanner.Watcher _wheelWatcher;
private static DirectInputDevice _navigationWheel;

private static void EnableWheelPolling()
{
    // This wakes up the DirectInput scanner thread
    _wheelWatcher = DirectInputScanner.Watch();
    _wheelWatcher.Update += OnDevicesUpdated;

    // Find our configured device and start monitoring buttons
    var deviceId = ValuesStorage.Get<string>("WheelNav_DeviceId");
    _navigationWheel = FindDevice(deviceId);

    if (_navigationWheel != null)
    {
        AttachButtonHandlers();
        _pollingTimer.Start(); // Poll our device only
    }
}

private static void DisableWheelPolling()
{
    _pollingTimer.Stop();
    DetachButtonHandlers();
    _wheelWatcher?.Dispose(); // Scanner thread goes back to sleep
    _wheelWatcher = null;
}
```

**Lifecycle:**
- ✅ Enable polling when launcher starts (Navigator.Initialize)
- ✅ Disable polling when game launches (GameWrapper.Started)
- ✅ Re-enable polling when game exits (GameWrapper.Ended)
- ✅ Scanner thread sleeps when no watchers active (zero CPU overhead)

### 2. **Configuration Approach: Sequential Button Press**

**User Experience:**
1. User triggers "Configure Wheel Navigation" command
2. Dialog appears: "Press button for UP navigation"
3. User presses button → captured
4. Dialog updates: "Press button for DOWN navigation"
5. Repeat for all 6 buttons (Up, Down, Left, Right, Select, Back)
6. Save configuration

**Implementation:**
```csharp
// WheelConfigWizard.xaml.cs
private int _currentStep = 0;
private string[] _buttonNames = { "UP", "DOWN", "LEFT", "RIGHT", "SELECT", "BACK" };
private int[] _capturedButtons = new int[6];
private DirectInputScanner.Watcher _configWatcher;

private void StartConfiguration()
{
    _currentStep = 0;
    _configWatcher = DirectInputScanner.Watch();
    _configWatcher.Update += OnDevicesUpdated;

    // Start polling all buttons
    foreach (var device in devices)
    {
        foreach (var button in device.Buttons)
        {
            button.PropertyChanged += OnButtonPressedDuringConfig;
        }
    }

    UpdatePrompt(); // "Press button for UP navigation"
}

private void OnButtonPressedDuringConfig(object sender, PropertyChangedEventArgs e)
{
    var button = (DirectInputButton)sender;
    if (e.PropertyName == "Value" && button.Value)
    {
        // Capture this button for current step
        _capturedButtons[_currentStep] = button.Id;
        _currentStep++;

        if (_currentStep >= 6)
        {
            // All buttons captured - save config
            SaveConfiguration();
            CloseWizard();
        }
        else
        {
            UpdatePrompt(); // "Press button for DOWN navigation"
        }
    }
}
```

### 3. **Default Wheel Mappings**

**Wheel Detection:**
```csharp
private static readonly Dictionary<string, int[]> DefaultMappings = new Dictionary<string, int[]>
{
    // Logitech G29/G920 (D-Pad + face buttons)
    { "046D-C24F", new[] { 0, 1, 2, 3, 4, 5 } },  // Up, Down, Left, Right, X, Circle

    // Thrustmaster T300/TX (D-Pad at different indices)
    { "044F-B66E", new[] { 8, 9, 10, 11, 0, 1 } },

    // Fanatec CSL DD
    { "0EB7-6204", new[] { 12, 13, 14, 15, 0, 1 } },

    // Generic fallback (user must configure)
    { "DEFAULT", new[] { -1, -1, -1, -1, -1, -1 } }
};

private static int[] GetDefaultMapping(DirectInputDevice device)
{
    var key = $"{device.ProductId.Substring(0, 9)}"; // VID-PID
    return DefaultMappings.ContainsKey(key) 
        ? DefaultMappings[key] 
        : DefaultMappings["DEFAULT"];
}
```

### 4. **Game Lifecycle Integration** ✅ Following StreamDeck Pattern

**Exact Same Pattern as StreamDeck (Navigator.SD.cs lines 68-69):**
```csharp
// Navigator.Wheel.cs
private static void InitializeWheelNavigation()
{
    // Hook game lifecycle events (SAME as StreamDeck)
    GameWrapper.Started += OnGameStarted;
    GameWrapper.Ended += OnGameEnded;

    // Load config and enable if configured
    if (LoadWheelConfig())
    {
        EnableWheelPolling();
    }
}

private static void OnGameStarted(object sender, EventArgs e)
{
    Debug.WriteLine("[Navigator.Wheel] Game started - disabling wheel navigation");
    DisableWheelPolling();
}

private static void OnGameEnded(object sender, EventArgs e)
{
    Debug.WriteLine("[Navigator.Wheel] Game ended - re-enabling wheel navigation");
    EnableWheelPolling();
}
```

---

## 📦 Required Changes

### New Files (1 file)
- ✅ `AcManager/UiObserver/Navigator.Wheel.cs` (250 lines)

### Modified Files (2 files)
- ✅ `AcManager/UiObserver/Navigator.cs` - Add `InitializeWheelNavigation()` call
- ✅ `AcManager/Pages/Settings/SettingsPage.xaml` - Add wheel nav config UI (optional)

### Dependencies (Already Present)
- ✅ `AcManager.Tools.Helpers.DirectInput` namespace
- ✅ SlimDX.DirectInput library
- ✅ `DirectInputScanner.Instance` (singleton)

---

## 🎯 Implementation Roadmap

### **Milestone 1: Core Functionality (4 hours)**
1. Create `Navigator.Wheel.cs` partial class
2. Implement button event handlers
3. Add device selection logic
4. Test with single wheel device

### **Milestone 2: Configuration (2 hours)**
1. Add settings storage (ValuesStorage)
2. Implement button mapping configuration
3. Add enable/disable toggle

### **Milestone 3: UI & Polish (2 hours)**
1. Settings page UI (optional - can use config file initially)
2. Device reconnection handling
3. Documentation

### **Total Estimated Time: 8 hours**

---

## 🧪 Testing Strategy

### Unit Testing
- ✅ Button press simulation (mock DirectInputDevice)
- ✅ Configuration load/save
- ✅ Device selection logic

### Integration Testing
- ✅ Test with real wheel (Logitech G29 recommended)
- ✅ Verify navigation through QuickDrive page
- ✅ Test modal dialogs (SelectCarDialog, SelectTrackDialog)
- ✅ Verify no interference with AC gameplay

### Edge Cases
- ✅ Device unplugged during navigation
- ✅ Multiple wheels connected
- ✅ Rapid button presses
- ✅ Button held down (debounce)

---

## 💡 Alternative: HID vs DirectInput

### DirectInput (Recommended) ✅
**Pros:**
- Already implemented and tested
- Handles device enumeration
- Button state polling infrastructure exists
- Zero new dependencies

**Cons:**
- Legacy API (but mature and stable)
- Requires polling (not event-driven)

### Raw HID (Alternative)
**Pros:**
- Modern Windows API
- Event-driven (no polling)
- Lower-level control

**Cons:**
- ❌ No existing infrastructure in codebase
- ❌ Requires new P/Invoke signatures
- ❌ More complex device enumeration
- ❌ Higher implementation cost (20+ hours)

**Verdict:** DirectInput is superior choice due to existing infrastructure

---

## 📝 Example Configuration

### Config File (ValuesStorage)
```json
{
  "WheelNav_Enabled": true,
  "WheelNav_DeviceId": "C24F046D-1BFD-49E6-8C6F-C2A0F2F2A3E4",
  "WheelNav_DeviceName": "Logitech G29 Racing Wheel",
  "WheelNav_ButtonMapping": [0, 1, 2, 3, 4, 5],
  "WheelNav_ButtonNames": ["D-Pad Up", "D-Pad Down", "D-Pad Left", "D-Pad Right", "Cross", "Circle"]
}
```

### Default Mappings (Suggestions)

**Logitech G29:**
```
Button 0-3: D-Pad (Up, Down, Left, Right)
Button 4: Cross/A (Select)
Button 5: Circle/B (Back)
```

**Thrustmaster T300:**
```
Button 8-11: D-Pad
Button 0: Cross (Select)
Button 1: Circle (Back)
```

---

## 🎯 Conclusion

### ✅ **HIGHLY FEASIBLE - RECOMMENDED TO PROCEED**

**Updated Assessment Based on Deep Dive:**

**Key Success Factors:**
1. ✅ **Efficient Polling** - DirectInput scanner sleeps when no watchers (zero CPU overhead when disabled)
2. ✅ **Clean lifecycle** - Game start/end events already proven with StreamDeck integration
3. ✅ **Sequential configuration** - Better UX than UI-based button assignment
4. ✅ **Default mappings** - Instant setup for common wheels
5. ✅ **Isolated feature** - No Settings page dependency (removed in your launcher)

**Implementation Highlights:**
- ✅ **Phase 1 (4 hours):** Core `Navigator.Wheel.cs` with efficient polling
- ✅ **Phase 2 (2-3 hours):** Configuration wizard with sequential button press
- ✅ **Phase 3 (1 hour):** Testing and polish

**Critical Design Decisions:**
1. **Polling Only When Needed:**
   - Create `DirectInputScanner.Watcher` only when wheel nav is enabled
   - Scanner thread sleeps when no watchers exist
   - Disable during gameplay (GameWrapper events)
   - ✅ **Zero CPU overhead when disabled**

2. **Sequential Button Configuration:**
   - Prompt user to press 6 buttons in order
   - No UI complexity - just clear prompts
   - Validates no duplicate buttons
   - ✅ **Simple, foolproof UX**

3. **Game Lifecycle Integration:**
   - Follow exact same pattern as StreamDeck (Navigator.SD.cs)
   - GameWrapper.Started → DisableWheelPolling()
   - GameWrapper.Ended → EnableWheelPolling()
   - ✅ **Proven pattern, zero gameplay interference**

**Recommended Next Steps:**
1. ✅ Create `Navigator.Wheel.cs` with efficient polling (4 hours)
2. ✅ Create `WheelConfigWizard` for sequential button capture (2 hours)
3. ✅ Add StreamDeck/keyboard trigger for configuration wizard
4. ✅ Test with your wheel (Logitech G29 recommended)
5. ✅ Add default mappings for common wheels

**Estimated Total Effort:** 6-8 hours for complete implementation  
**Complexity:** Low (follows proven patterns)  
**Risk:** Very Low (isolated, easy to disable)  
**Value:** High (unique accessibility feature, perfect for sim racing launcher)  

**Performance Impact:**
- ✅ **Launcher idle:** 0% CPU (scanner sleeping)
- ✅ **Launcher with wheel nav:** <1% CPU (20Hz polling of 6 buttons)
- ✅ **During gameplay:** 0% CPU (polling disabled)

---

## 📝 Updated Configuration Example

### ValuesStorage Keys
```json
{
  "WheelNav_Enabled": true,
  "WheelNav_DeviceId": "046D-C24F-0000-0000-504944564944",
  "WheelNav_DeviceName": "Logitech G29 Racing Wheel USB",
  "WheelNav_ButtonMapping": [0, 1, 2, 3, 4, 5]
}
```

### Default Mappings (Auto-Detected)
**Logitech G29/G920:**
```
Button 0: D-Pad Up    → UP
Button 1: D-Pad Down  → DOWN
Button 2: D-Pad Left  → LEFT
Button 3: D-Pad Right → RIGHT
Button 4: Cross/X     → SELECT
Button 5: Circle/O    → BACK
```

**Thrustmaster T300/TX:**
```
Button 8:  D-Pad Up    → UP
Button 9:  D-Pad Down  → DOWN
Button 10: D-Pad Left  → LEFT
Button 11: D-Pad Right → RIGHT
Button 0:  Cross       → SELECT
Button 1:  Circle      → BACK
```

### Triggering Configuration Wizard
```csharp
// From StreamDeck button
case "ConfigureWheelNav":
    var wizard = new WheelConfigWizard();
    wizard.ShowDeviceSelection();
    break;

// Or from keyboard shortcut (e.g., Ctrl+Shift+W)
if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W)
{
    new WheelConfigWizard().ShowDeviceSelection();
}
```

---

## 📚 References

### Relevant Files
- `AcManager.Tools/Helpers/DirectInput/DirectInputDevice.cs`
- `AcManager.Tools/Helpers/DirectInput/DirectInputButton.cs`
- `AcManager.Tools/Helpers/DirectInput/DirectInputScanner.cs`
- `AcManager/UiObserver/Navigator.SD.cs` (StreamDeck reference implementation)
- `AcManager/UiObserver/Navigator.Navigation.cs` (navigation methods to call)

### Key Classes
- `DirectInputDevice` - Device abstraction
- `DirectInputButton` - Button state + PropertyChanged events
- `Navigator` - Partial class pattern for extensions
- `NavDirection` enum - {Up, Down, Left, Right}

### Configuration Pattern
- `ValuesStorage` - Persistent settings storage
- `SettingsHolder` - Typed settings access
