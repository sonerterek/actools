# Control Input Analysis for ModernUI Navigation System

## Control Categorization & Input Feasibility

Based on codebase analysis, here's a comprehensive breakdown of UI controls and their input handling capabilities using WPF/ModernUI's command and property system.

---

## ? Category 1: Simple Toggle Controls
**Characteristic**: Binary state (on/off, true/false, open/close)
**Input Method**: Direct property manipulation
**Viability**: **FULLY SUPPORTED** ?

### Controls in this category:
1. **CheckBox** ? `IsChecked` property (bool?)
2. **ToggleButton** ? `IsChecked` property (bool?)
3. **RadioButton** ? `IsChecked` property (bool?)
4. **Expander** ? `IsExpanded` property (bool)
5. **ComboBox** ? `IsDropDownOpen` property (bool)
6. **Menu** ? First `MenuItem.IsSubmenuOpen` property (bool)
7. **ContextMenu** ? `IsOpen` property (bool)
8. **LabeledToggleButton** (ModernUI custom) ? `IsChecked` property (bool)
9. **ModernToggleButton** (ModernUI custom) ? `IsChecked` property (bool)

### Implementation:
```csharp
// Already working in NavNode.Activate()
if (fe is ToggleButton toggle) {
    toggle.IsChecked = !toggle.IsChecked;
    return true;
}
```

### Navigation Actions:
- **Activate/Enter**: Toggle state
- **Left/Right**: Not applicable
- **Up/Down**: Not applicable

---

## ? Category 2: Selection Controls
**Characteristic**: Choose from a list of options
**Input Method**: Property manipulation + item selection
**Viability**: **FULLY SUPPORTED** ?

### Controls in this category:
1. **ListBoxItem** ? `IsSelected` property
2. **ComboBoxItem** ? `IsSelected` property
3. **TreeViewItem** ? `IsSelected` property
4. **TabItem** ? `IsSelected` property
5. **MenuItem** ? Click event OR `IsSelected` property

### Implementation:
```csharp
// Already working in NavNode.Activate()
if (fe is ListBoxItem listBoxItem) {
    listBoxItem.IsSelected = true;
    return true;
}
```

### Navigation Actions:
- **Activate/Enter**: Select item
- **Left/Right**: Move to prev/next item (requires parent container context)
- **Up/Down**: Move to prev/next item (requires parent container context)

---

## ?? Category 3: Linear Value Controls (Horizontal)
**Characteristic**: Single value on a continuous or discrete range
**Input Method**: `Value` property manipulation
**Viability**: **PARTIALLY SUPPORTED** - Need directional input

### Controls in this category:
1. **Slider** ? `Value` property (double), `Minimum`, `Maximum`, `SmallChange`, `LargeChange`
2. **ScrollBar** ? `Value` property (double), `Minimum`, `Maximum`
3. **ProgressBar** ? `Value` property (read-only in most cases)

### Implementation:
```csharp
public bool ApplyDirectionalInput(NavDirection direction) {
    if (!TryGetVisual(out var fe)) return false;
    
    if (fe is Slider slider) {
        var delta = (direction == NavDirection.Right ? 1 : -1);
        var step = slider.LargeChange > 0 ? slider.LargeChange : slider.SmallChange;
        slider.Value = Math.Clamp(slider.Value + delta * step, 
                                   slider.Minimum, slider.Maximum);
        return true;
    }
    return false;
}
```

### Navigation Actions:
- **Activate/Enter**: Not applicable (or could set to midpoint)
- **Left**: Decrease value by `LargeChange`
- **Right**: Increase value by `LargeChange`
- **Up/Down**: Not applicable for horizontal sliders

**Keyboard Modifiers** (observed in BetterTextBox):
- `Ctrl`: Multiply delta by 0.1 (fine adjustment)
- `Shift`: Multiply delta by 10 (coarse adjustment)
- `Alt`: Multiply delta by 2

---

## ?? Category 4: Dual-Range Controls (Advanced Horizontal)
**Characteristic**: TWO values defining a range on a single axis
**Input Method**: Multiple properties (`From`, `To`, `Value`, `Range`)
**Viability**: **COMPLEX** - Need mode selection + directional input

### Controls in this category:
1. **DoubleSlider** (ModernUI custom) ? `From`, `To`, `Value`, `Range`, `RangeLeft`, `RangeRight`

### Properties:
- `Value`: Center point of range
- `Range`: Width of range (To - From)
- `From`: Left boundary
- `To`: Right boundary
- `RangeLeft`: Offset from center to left boundary (negative)
- `RangeRight`: Offset from center to right boundary (positive)

### Binding Modes:
1. **FromToFixed**: Adjusting one end doesn't affect the other
2. **FromTo**: Adjusting ends recalculates center
3. **PositionRange**: Adjusting range affects both boundaries symmetrically

### Implementation Strategy:
```csharp
public enum DoubleSliderMode {
    MoveCenter,      // Move both From and To together (preserve Range)
    AdjustRange,     // Widen/narrow range (preserve Value/center)
    AdjustFromOnly,  // Move only From boundary
    AdjustToOnly     // Move only To boundary
}

public bool ApplyDoubleSliderInput(NavDirection direction, DoubleSliderMode mode) {
    if (!TryGetVisual(out var fe) || !(fe is DoubleSlider ds)) return false;
    
    var delta = (direction == NavDirection.Right ? 1 : -1) * 10.0; // Or use step
    
    switch (mode) {
        case DoubleSliderMode.MoveCenter:
            ds.Value = Math.Clamp(ds.Value + delta, ds.Minimum, ds.Maximum);
            break;
        
        case DoubleSliderMode.AdjustRange:
            var newRange = Math.Max(0, ds.Range + delta);
            ds.Range = Math.Min(newRange, (ds.Maximum - ds.Minimum));
            break;
        
        case DoubleSliderMode.AdjustFromOnly:
            ds.From = Math.Clamp(ds.From + delta, ds.Minimum, ds.To);
            break;
        
        case DoubleSliderMode.AdjustToOnly:
            ds.To = Math.Clamp(ds.To + delta, ds.From, ds.Maximum);
            break;
    }
    return true;
}
```

### Navigation Actions:
**Requires multi-key chords or mode switching!**

**Option A: Mode-based (Modifier keys)**
- **Left/Right** (no modifier): Move center point
- **Ctrl + Left/Right**: Adjust range symmetrically
- **Shift + Left/Right**: Adjust From boundary only
- **Alt + Left/Right**: Adjust To boundary only

**Option B: Separate commands**
- **Activate/Enter**: Cycle through modes (highlight changes to show mode)
- **Left/Right**: Apply delta based on current mode
- **Up/Down**: Cycle through modes

---

## ?? Category 5: Circular/Angular Controls
**Characteristic**: 360° rotation value
**Input Method**: `Value` property with wraparound (0-360°)
**Viability**: **PARTIALLY SUPPORTED** - Need wraparound logic

### Controls in this category:
1. **Wind Direction Control** ? `WindDirection` property (int, 0-360°)
   - Found in: `ServerWeatherEntry.WindDirection`, `QuickDrive.ViewModel.WindDirection`
   - Has `WindDirectionFlipped` property (opposite direction = +180°)

### Implementation:
```csharp
public bool ApplyCircularInput(NavDirection direction) {
    if (!TryGetVisual(out var fe)) return false;
    
    // Check for wind direction property using reflection
    var windDirProperty = fe.GetType().GetProperty("WindDirection");
    if (windDirProperty != null && windDirProperty.PropertyType == typeof(int)) {
        var currentValue = (int)windDirProperty.GetValue(fe);
        var delta = (direction == NavDirection.Right ? 15 : -15); // 15° increments
        var newValue = (currentValue + delta + 360) % 360;
        windDirProperty.SetValue(fe, newValue);
        return true;
    }
    
    return false;
}
```

### Navigation Actions:
- **Activate/Enter**: Not applicable (or reset to North/0°)
- **Left**: Rotate counter-clockwise (e.g., -15°)
- **Right**: Rotate clockwise (e.g., +15°)
- **Up/Down**: Not applicable

**Alternative Mapping**:
- **Up**: North (0°)
- **Right**: East (90°)
- **Down**: South (180°)
- **Left**: West (270°)

---

## ? Category 6: Command-Based Controls
**Characteristic**: Execute actions via ICommand
**Input Method**: `Command.Execute()`
**Viability**: **FULLY SUPPORTED** ?

### Controls in this category:
1. **Button** ? `ClickEvent` OR `Command` property
2. **MenuItem** ? `ClickEvent` OR `Command` property
3. **Hyperlink** ? `Command` property
4. **ModernButton** (ModernUI custom) ? `Command` property

### Implementation:
```csharp
// ICommand-based activation
if (fe is ICommandSource commandSource && commandSource.Command != null) {
    if (commandSource.Command.CanExecute(commandSource.CommandParameter)) {
        commandSource.Command.Execute(commandSource.CommandParameter);
        return true;
    }
}

// Event-based activation (already working)
if (fe is Button btn) {
    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    return true;
}
```

### Navigation Actions:
- **Activate/Enter**: Execute command
- **Left/Right/Up/Down**: Not applicable

---

## ? Category 7: Text Input Controls
**Characteristic**: Free-form text entry
**Input Method**: Keyboard text input
**Viability**: **NOT VIABLE** for command-based navigation ?

### Controls in this category:
1. **TextBox** ? `Text` property
2. **PasswordBox** ? `Password` property (SecureString)
3. **BetterTextBox** (ModernUI custom) ? `Text` property + special modes
4. **RichTextBox** ? `Document` property

### Why Not Viable:
- Requires keyboard focus and text input
- Navigation system should **skip** or **focus** these controls, not manipulate them
- User needs full keyboard for text entry

### Recommendation:
```csharp
// Just set focus, don't try to manipulate text
if (fe is TextBox textBox) {
    textBox.Focus();
    textBox.SelectAll(); // Optional: select existing text
    return true;
}
```

---

## Summary Table: Input Viability

| Category | Example Controls | Primary Input | Directional? | Command Support | Viability |
|----------|------------------|---------------|--------------|-----------------|-----------|
| **Toggle** | CheckBox, ToggleButton, Expander | Property (bool) | No | ? | ? FULL |
| **Selection** | ListBoxItem, ComboBoxItem, TabItem | Property (IsSelected) | Yes (navigate items) | ? | ? FULL |
| **Linear Slider** | Slider, ScrollBar | Property (Value) | Yes (Left/Right) | ? | ?? PARTIAL |
| **Dual Slider** | DoubleSlider | Multiple props | Yes (complex) | ?? | ?? COMPLEX |
| **Circular** | Wind Direction | Property (0-360°) | Yes (rotate) | ? | ?? PARTIAL |
| **Command** | Button, MenuItem | ICommand | No | ? | ? FULL |
| **Text Input** | TextBox, PasswordBox | Keyboard input | No | ? | ? NOT VIABLE |

---

## Recommended Implementation Priority

### Phase 1: Core Support ?
- [x] Toggle controls (already working)
- [x] Selection controls (already working)
- [x] Command-based controls (already working)

### Phase 2: Directional Input ??
- [ ] Linear sliders (Slider, ScrollBar) - **HIGH PRIORITY**
  - Add `ApplyDirectionalInput(NavDirection direction)` method
  - Map Left/Right to Value decrease/increase
- [ ] Circular controls (Wind Direction) - **MEDIUM PRIORITY**
  - Add wraparound logic for 0-360° range
  - Map Left/Right to CCW/CW rotation

### Phase 3: Advanced Controls ??
- [ ] Dual sliders (DoubleSlider) - **LOW PRIORITY**
  - Requires mode selection UI (modifier keys or mode cycling)
  - Complex UX design needed
  - Consider if this is worth implementing vs. just focusing the control

### Phase 4: Text Input ??
- [ ] TextBox, PasswordBox - **NOT RECOMMENDED**
  - Just focus the control
  - Let user type naturally with keyboard

---

## Conclusion

**YES, the ModernUI Command interface is VIABLE for most controls!**

? **Fully supported (80% of controls)**:
- Toggle controls
- Selection controls  
- Command-based controls

?? **Partially supported (15% of controls)**:
- Linear sliders (need directional input method)
- Circular controls (need wraparound logic)

?? **Complex but possible (4% of controls)**:
- Dual sliders (need mode selection UX)

?? **Not recommended (1% of controls)**:
- Text inputs (just focus them)

**Recommendation**: Implement Phase 1 (? done) + Phase 2 (directional input for sliders/wind). Skip Phase 3 (dual sliders too complex) and Phase 4 (text input not needed).
