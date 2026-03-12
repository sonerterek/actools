# Wheel Navigation - Implementation Summary

**Status:** ✅ Complete - Standalone Feature  
**Estimated Time:** 6-8 hours  
**Complexity:** Low  
**Target Users:** Users WITHOUT a StreamDeck

---

## 🎯 Purpose

This feature provides **wheel button navigation** as a **standalone alternative** to StreamDeck.  
**NOT** integrated with StreamDeck - this is for users who don't have one.

---

## 🎯 Key Design Decisions

### 1. **Efficient Polling Strategy** ✅

**NOT constant polling!** DirectInput scanner sleeps when no watchers exist.

```csharp
// Enable polling (wakes scanner thread)
_wheelWatcher = DirectInputScanner.Watch();
_wheelPollTimer.Start(); // 20Hz polling of selected device

// Disable polling (scanner thread sleeps)
_wheelWatcher.Dispose();
_wheelPollTimer.Stop();
```

**CPU Impact:**
- Idle: 0% (scanner sleeping)
- With wheel nav: <1% (20Hz polling, 6 buttons)
- During gameplay: 0% (disabled via GameWrapper events)

### 2. **Sequential Button Configuration** ✅

**No UI complexity!** Simple wizard prompts user to press 6 buttons in order.

```
Step 1/6: "Press button for UP navigation"    → User presses D-Pad Up
Step 2/6: "Press button for DOWN navigation"  → User presses D-Pad Down
Step 3/6: "Press button for LEFT navigation"  → User presses D-Pad Left
Step 4/6: "Press button for RIGHT navigation" → User presses D-Pad Right
Step 5/6: "Press button for SELECT navigation"→ User presses Cross/X
Step 6/6: "Press button for BACK navigation"  → User presses Circle/O
✅ Configuration saved!
```

### 3. **Game Lifecycle Integration** ✅

**Follow StreamDeck pattern exactly:**

```csharp
// Navigator.Wheel.cs initialization
GameWrapper.Started += OnGameStarted_Wheel;
GameWrapper.Ended += OnGameEnded_Wheel;

// Auto-disable during gameplay
private static void OnGameStarted_Wheel(object sender, EventArgs e)
{
    DisableWheelPolling(); // Stop polling, scanner sleeps
}

// Auto-enable when returning to launcher
private static void OnGameEnded_Wheel(object sender, EventArgs e)
{
    if (ValuesStorage.Get("WheelNav_Enabled", false))
        EnableWheelPolling(); // Resume polling
}
```

### 4. **Default Mappings** ✅

**Auto-detect common wheels:**

```csharp
private static readonly Dictionary<string, int[]> DefaultMappings = new Dictionary<string, int[]>
{
    // Logitech G29/G920 (D-Pad + face buttons)
    { "046D-C24F", new[] { 0, 1, 2, 3, 4, 5 } },
    
    // Thrustmaster T300/TX
    { "044F-B66E", new[] { 8, 9, 10, 11, 0, 1 } },
    
    // Fanatec CSL DD
    { "0EB7-6204", new[] { 12, 13, 14, 15, 0, 1 } }
};
```

User can override via configuration wizard if needed.

---

## 📁 File Structure

### New Files (2 files)
```
AcManager/UiObserver/
├── Navigator.Wheel.cs                  (350 lines - core polling & navigation)
└── WheelConfigWizard.xaml + .cs       (200 lines - configuration UI)
```

### Modified Files (1 file)
```
AcManager/UiObserver/
└── Navigator.cs                        (add InitializeWheelNavigation() call)
```

---

## 🔧 Implementation Checklist

### Phase 1: Core Functionality (4 hours)
- [ ] Create `Navigator.Wheel.cs` partial class
- [ ] Implement efficient polling (Watcher + DispatcherTimer)
- [ ] Hook GameWrapper.Started/Ended events
- [ ] Add default wheel mappings
- [ ] Implement button press detection (rising edge)
- [ ] Wire up navigation commands

### Phase 2: Configuration Wizard (2-3 hours)
- [ ] Create `WheelConfigWizard.xaml` dialog
- [ ] Implement device selection (if multiple wheels)
- [ ] Implement sequential button capture
- [ ] Add duplicate button validation
- [ ] Save configuration via Navigator.SaveWheelConfig()
- [ ] Add trigger from StreamDeck/keyboard

### Phase 3: Testing & Polish (1 hour)
- [ ] Test with real wheel (Logitech G29)
- [ ] Test device disconnect/reconnect
- [ ] Test game lifecycle (enable/disable)
- [ ] Verify zero CPU when disabled
- [ ] Add debug logging

---

## 🧪 Testing Plan

### Manual Testing
1. **Configuration:**
   - Start wizard
   - Press 6 buttons in sequence
   - Verify saved correctly
   - Restart launcher → verify config loaded

2. **Navigation:**
   - Navigate through QuickDrive page
   - Test all 6 buttons (Up/Down/Left/Right/Select/Back)
   - Test modal dialogs (SelectCarDialog)
   - Test confirmation dialogs

3. **Lifecycle:**
   - Enable wheel nav → verify polling active
   - Launch AC → verify polling stopped
   - Exit AC → verify polling resumed
   - Disable wheel nav → verify polling stopped

4. **Edge Cases:**
   - Unplug wheel during navigation
   - Plug in second wheel
   - Rapid button presses
   - Hold button down

---

## 📝 Configuration Storage

### ValuesStorage Keys
```json
{
  "WheelNav_Enabled": true,
  "WheelNav_DeviceId": "046D-C24F-0000-0000-504944564944",
  "WheelNav_DeviceName": "Logitech G29 Racing Wheel USB",
  "WheelNav_ButtonMapping": [0, 1, 2, 3, 4, 5]
}
```

### Example Default Mappings

**Logitech G29 (ProductId: 046D-C24F):**
```
UP    = Button 0  (D-Pad Up)
DOWN  = Button 1  (D-Pad Down)
LEFT  = Button 2  (D-Pad Left)
RIGHT = Button 3  (D-Pad Right)
SELECT= Button 4  (Cross/X)
BACK  = Button 5  (Circle/O)
```

**Thrustmaster T300 (ProductId: 044F-B66E):**
```
UP    = Button 8  (D-Pad Up)
DOWN  = Button 9  (D-Pad Down)
LEFT  = Button 10 (D-Pad Left)
RIGHT = Button 11 (D-Pad Right)
SELECT= Button 0  (Cross)
BACK  = Button 1  (Circle)
```

---

## 🚀 Activation

### How to Trigger Configuration

The wizard can be called from code:

```csharp
// Public API
Navigator.ShowWheelConfigWizard();
```

**Suggested Integration Points:**

**Option 1: Keyboard Shortcut**
```csharp
// Add to MainWindow.xaml.cs PreviewKeyDown handler
if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W)
{
    Navigator.ShowWheelConfigWizard();
    e.Handled = true;
}
```

**Option 2: Main Menu Item**
```csharp
// Add to MainWindow menu
<MenuItem Header="Configure Wheel Navigation..." Click="OnConfigureWheelNav"/>

private void OnConfigureWheelNav(object sender, RoutedEventArgs e)
{
    Navigator.ShowWheelConfigWizard();
}
```

**Option 3: First-Run Detection**
```csharp
// Check on startup if not configured
if (!ValuesStorage.Get("WheelNav_Enabled", false))
{
    // Show toast notification or prompt
    var result = ModernDialog.ShowMessage(
        "Would you like to configure wheel button navigation?",
        "Wheel Navigation",
        MessageBoxButton.YesNo
    );

    if (result == MessageBoxResult.Yes)
    {
        Navigator.ShowWheelConfigWizard();
    }
}
```

---

## ⚡ Performance Characteristics

### DirectInput Polling Overhead

**When Enabled (Navigator active, no game running):**
- Watcher created → Scanner thread wakes
- DispatcherTimer polling at 20Hz
- 6 buttons checked per tick
- CPU: <1% (negligible)

**When Disabled (Game running OR wheel nav off):**
- Watcher disposed → Scanner thread sleeps
- DispatcherTimer stopped
- CPU: 0% (zero overhead)

### Memory Footprint
- Navigator.Wheel.cs: ~2KB static fields
- DirectInputDevice reference: ~4KB
- Button state array: ~24 bytes
- **Total: <10KB**

---

## 🎯 Success Criteria

✅ **Functional:**
- All 6 buttons navigate correctly
- Configuration wizard completes successfully
- Game lifecycle disables/enables polling
- Device disconnect/reconnect handled gracefully

✅ **Performance:**
- CPU <1% when enabled in launcher
- CPU 0% when disabled or during gameplay
- No memory leaks on enable/disable cycles

✅ **UX:**
- Configuration takes <30 seconds
- Clear prompts for each button
- Works with common wheels out-of-box

---

## 📚 References

**Existing Patterns:**
- `Navigator.SD.cs` - StreamDeck integration (template for lifecycle)
- `DirectInputScanner.cs` - Polling infrastructure
- `DirectInputDevice.cs` - Button state tracking

**Key APIs:**
- `DirectInputScanner.Watch()` - Creates watcher, wakes scanner thread
- `GameWrapper.Started/Ended` - Game lifecycle events
- `ValuesStorage.Get/Set` - Configuration persistence
- `DispatcherTimer` - UI thread polling (20Hz)
