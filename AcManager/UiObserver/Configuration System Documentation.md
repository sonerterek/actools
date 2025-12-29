# Configuration File System - Implementation Summary

## ?? **Overview**

The configuration file system allows users to:
- **Classify elements** - Define element roles and behavior
- **Define modals** - Mark elements as modal containers
- **Map custom pages** - Switch StreamDeck pages based on focus or modal state
- **Create shortcuts** - Define keys that jump directly to specific elements
- **Organize pages** - Create custom StreamDeck page layouts

All of this is done through a unified `CLASSIFY` rule syntax that supports any combination of these features.

---

## ??? **Architecture**

### **Components:**

1. **`NavConfig.cs`** - Configuration model and parser
   - `NavShortcutKey` - Represents element classification (modal, page mapping, shortcut, etc.)
   - `NavPageDef` - Represents a custom page definition
   - `NavConfigParser` - Parses configuration file
   - `NavConfiguration` - Container for parsed config

2. **`Navigator.SD.cs`** - Integration with StreamDeck
   - Loads configuration at startup
   - Defines shortcut keys in SDPClient
   - Defines custom pages in SDPClient
   - Handles shortcut key presses
   - Switches pages based on focus/modal changes

3. **`Navigator.cs`** - Focus and modal management
   - Tracks modal stack and focus state
   - Switches pages when focus changes
   - Switches pages when modals open

---

## ?? **Configuration File Format**

### **Location:**
```
%LOCALAPPDATA%\AcTools Content Manager\NWRS Navigation.cfg
```

### **Syntax:**

#### **1. Element Classification (CLASSIFY Rule):**
```
CLASSIFY: <path_filter> => <properties>
```

**Properties:**
- `role` - Element type (optional, for documentation)
- `modal` - Whether element is a modal container (optional, default: false)
- `PageName` - StreamDeck page to switch to (optional)
- `KeyName` - Unique key identifier for shortcut (optional)
- `KeyTitle` - Text shown on StreamDeck button (optional)
- `KeyIcon` - Icon specification (optional)

**Path Filter Wildcards:**
- `*` - Matches any single path segment
- `**` - Matches any number of segments (0 or more)

**Examples:**

```
# Define modal with custom page
CLASSIFY: ** > *:SelectTrackDialog => \
    role=group; \
    modal=true; \
    PageName=TrackSelection

# Define shortcut with page switching
CLASSIFY: Window:MainWindow > ** > (unnamed):ModernButton["Change car"] => \
    role=modernbutton; \
    KeyName=QuickChangeCar; \
    KeyTitle="Car"; \
    KeyIcon=Car; \
    PageName=CarSelection

# Define page switching only (no shortcut)
CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["PRACTICE"] => \
    role=listboxitem; \
    PageName=UpDown

# Define shortcut only (no page switching)
CLASSIFY: Window:MainWindow > ** > (unnamed):Button["Go!"] => \
    KeyName=QuickGo; \
    KeyTitle="GO!"
```

#### **2. Custom Page Definition:**
```
PAGE: <name> => <5x3 JSON array>
```

**Format:** 5 rows × 3 columns = 15 buttons
**Keys:** Use key names (built-in or shortcuts)
**Empty slots:** Use `null`

**Example:**
```json
PAGE: QuickDrive => [
    ["Back", "WriteModalFilter", "WriteElementFilter"],
    ["QuickChangeCar", "QuickChangeTrack", "QuickGo"],
    ["QuickPractice", "QuickRace", null],
    ["Left", "MouseLeft", "Right"],
    [null, "Down", null]
]
```

---

## ?? **Page Switching Logic**

### **Priority Order (Highest to Lowest):**

1. **Focused Element Page Mapping**
   - When focus moves to an element with `PageName` property
   - Example: Focus moves to "Change Car" button ? Switch to `CarSelection` page

2. **Modal Page Mapping**
   - When modal opens with `PageName` property
   - Example: `SelectTrackDialog` opens ? Switch to `TrackSelection` page

3. **Built-in Page Selection**
   - Automatic page selection based on modal type
   - Example: Menu modal ? Switch to `UpDown` page

4. **Default Navigation Page**
   - Fallback if no custom mapping found
   - Always: `Navigation` page

### **Page Name Resolution (Inheritance Support):**

When a `PageName` is specified in a CLASSIFY rule, the system resolves it using this priority:

1. **Exact Match**
   - `PageName=Settings` ? Finds page `Settings`

2. **Derived Page Match**
   - `PageName=MainWindow` ? Finds page `MainWindow:Navigation`
   - The part before `:` is matched against requested name

3. **Not Found**
   - Page name used as-is (plugin will handle unknown page)

**Examples:**

```
# Define pages
PAGE: Navigation => [...]
PAGE: MainWindow:Navigation => [...]

# CLASSIFY rules with page mapping
CLASSIFY: Window:MainWindow => PageName=MainWindow
# ? Resolves to "MainWindow:Navigation" (derived page match)

CLASSIFY: ** > *:SelectTrackDialog => PageName=TrackSelection
# ? Resolves to "TrackSelection" (exact match, if defined)

CLASSIFY: ** > *:Menu => PageName=UpDown
# ? Resolves to "UpDown" (exact match, built-in page)
```

### **Examples:**

#### **Scenario 1: Modal Opens with Derived Page**
```
User clicks "Change Track" button
  ?
SelectTrackDialog modal opens
  ?
CLASSIFY rule: ** > *:SelectTrackDialog => PageName=SelectTrack
  ?
Page resolution: "SelectTrack" ? "SelectTrack:Navigation" (derived page found)
  ?
StreamDeck switches to "SelectTrack:Navigation" page
```

#### **Scenario 2: Modal Opens with Exact Page**
```
MainWindow modal opens
  ?
CLASSIFY rule: Window:MainWindow => PageName=MainWindow
  ?
Page resolution: "MainWindow" ? "MainWindow:Navigation" (derived page found)
  ?
StreamDeck switches to "MainWindow:Navigation" page
```

#### **Scenario 3: Focus Changes Within Modal**
```
User navigates Down in SelectTrackDialog
  ?
Focus moves to a ListBoxItem
  ?
CLASSIFY rule matches: ** > (unnamed):ListBoxItem => PageName=UpDown
  ?
Page resolution: "UpDown" ? "UpDown" (exact match, built-in page)
  ?
StreamDeck switches to "UpDown" page (optimized for vertical navigation)
```

#### **Scenario 4: Shortcut Key Pressed**
```
User presses "QuickChangeCar" on StreamDeck
  ?
Navigator finds "Change Car" button and focuses it
  ?
CLASSIFY rule matches: ** > (unnamed):ModernButton["Change car"] => PageName=CarSelection
  ?
Page resolution: "CarSelection" ? "CarSelection:Navigation" (derived page found)
  ?
StreamDeck switches to "CarSelection:Navigation" page
```

---

## ?? **Use Cases**

### **Use Case 1: Context-Aware Navigation**

```
# When mode tabs get focus, use vertical-only navigation
CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["PRACTICE"] => \
    PageName=UpDown

CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["RACE"] => \
    PageName=UpDown
```

**Result:** Pressing Up/Down navigates between modes, Left/Right are disabled (no horizontal buttons on page)

---

### **Use Case 2: Dialog-Specific Pages**

```
# Track selection dialog uses custom page
CLASSIFY: ** > *:SelectTrackDialog => \
    role=group; \
    modal=true; \
    PageName=TrackSelection

# TrackSelection page has extra shortcuts for common filters
PAGE: TrackSelection => [
    ["Back", "FilterFavorites", "FilterRecent"],
    ["FilterCountry", "FilterTag", "SortName"],
    [null, "Up", null],
    ["Left", "MouseLeft", "Right"],
    [null, "Down", null]
]
```

**Result:** When track dialog opens, StreamDeck shows track-specific shortcuts

---

### **Use Case 3: Shortcut with Context Switch**

```
# Shortcut to open settings, with custom page when dialog opens
CLASSIFY: ** > *:SettingsDialog => \
    role=group; \
    modal=true; \
    PageName=Settings; \
    KeyName=QuickSettings; \
    KeyTitle="Settings"

# Settings page has category shortcuts
PAGE: Settings => [
    ["Back", "CatGeneral", "CatVideo"],
    ["CatAudio", "CatControls", "CatContent"],
    [null, "Up", null],
    ["Left", "MouseLeft", "Right"],
    [null, "Down", null]
]
```

**Result:** Press "QuickSettings" ? Opens dialog AND switches to Settings page with category shortcuts

---

## ?? **Property Combinations**

### **Valid Combinations:**

| modal | PageName | KeyName | Description | Example |
|-------|----------|---------|-------------|---------|
| ? | ? | ? | Modal with custom page | SelectTrackDialog |
| ? | ? | ? | Page switch on focus | Mode tabs |
| ? | ? | ? | Shortcut only | Quick Go button |
| ? | ? | ? | Shortcut with page switch | Change Car button |
| ? | ? | ? | Modal with page and shortcut | Settings dialog |

### **Examples:**

```
# Modal only (use built-in page selection)
CLASSIFY: ** > *:SomeDialog => \
    modal=true

# Modal with custom page
CLASSIFY: ** > *:SelectTrackDialog => \
    modal=true; \
    PageName=TrackSelection

# Page switching only
CLASSIFY: ** > *:Slider => \
    PageName=Slider

# Shortcut only
CLASSIFY: ** > (unnamed):Button["Go!"] => \
    KeyName=QuickGo; \
    KeyTitle="GO!"

# Shortcut with page switching
CLASSIFY: ** > (unnamed):ModernButton["Change car"] => \
    KeyName=QuickChangeCar; \
    KeyTitle="Car"; \
    PageName=CarSelection

# Everything combined
CLASSIFY: ** > *:SettingsDialog => \
    modal=true; \
    PageName=Settings; \
    KeyName=QuickSettings; \
    KeyTitle="Settings"
```

---

## ?? **Configuration Examples**

### **Example 1: Quick Drive Shortcuts**

```
# Quick access to common Quick Drive buttons
CLASSIFY: Window:MainWindow > ** > (unnamed):ModernButton["Change car"] => \
    KeyName=QuickChangeCar; KeyTitle="Car"; KeyIcon=Car

CLASSIFY: Window:MainWindow > ** > (unnamed):ModernButton["Change track"] => \
    KeyName=QuickChangeTrack; KeyTitle="Track"; KeyIcon=Track

CLASSIFY: Window:MainWindow > ** > (unnamed):Button["Go!"] => \
    KeyName=QuickGo; KeyTitle="GO!"; KeyIcon=!GO

# Custom page with shortcuts
PAGE: QuickDrive => [
    ["Back", "WriteModalFilter", "WriteElementFilter"],
    ["QuickChangeCar", "QuickChangeTrack", "QuickGo"],
    [null, "Up", null],
    ["Left", "MouseLeft", "Right"],
    [null, "Down", null]
]
```

### **Example 2: Mode Shortcuts**

```
# Quick access to race mode tabs
CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["PRACTICE"] => \
    KeyName=QuickPractice; KeyTitle="Practice"

CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["RACE"] => \
    KeyName=QuickRace; KeyTitle="Race"

CLASSIFY: Window:MainWindow > ** > (unnamed):ListBoxItem["HOTLAP"] => \
    KeyName=QuickHotlap; KeyTitle="Hotlap"

# Custom page with mode shortcuts
PAGE: QuickModes => [
    ["Back", "QuickPractice", "QuickRace"],
    [null, "QuickHotlap", null],
    [null, "Up", null],
    ["Left", "MouseLeft", "Right"],
    [null, "Down", null]
]
```

---

## ??? **Discovery Workflow**

### **How to Create Shortcuts:**

1. **Navigate to element** using StreamDeck (Up, Down, Left, Right)
2. **Press WriteElementFilter** on StreamDeck
3. **Open discovery file:**
   ```
   %LOCALAPPDATA%\AcTools Content Manager\NWRS Navigation Discovery.txt
   ```
4. **Copy the generated CLASSIFY rule:**
   ```
   # Focused: (unnamed):ModernButton
   CLASSIFY: Window:MainWindow > ... > (unnamed):ModernButton["Change car"] => role=modernbutton
   ```
5. **Add shortcut properties:**
   ```
   CLASSIFY: Window:MainWindow > ** > (unnamed):ModernButton["Change car"] => \
       role=modernbutton; \
       KeyName=QuickChangeCar; \
       KeyTitle="Car"; \
       KeyIcon=Car
   ```
6. **Add to configuration file:**
   ```
   %LOCALAPPDATA%\AcTools Content Manager\NWRS Navigation.cfg
   ```
7. **Restart app** (configuration is loaded at startup)

---

## ?? **Path Filter Patterns**

### **Wildcard Rules:**

| Pattern | Matches | Example |
|---------|---------|---------|
| `Exact > Match` | Exact path only | `Window:MainWindow > (unnamed):Button` |
| `* > Match` | Any one segment | `Window:MainWindow > *:Border > (unnamed):Button` |
| `** > Match` | Any number of segments (0+) | `Window:MainWindow > ** > (unnamed):Button` |
| `*** > Match` | Any number of segments (1+) | `Window:MainWindow > *** > (unnamed):Button` |

### **Matching Algorithm:**

```
Path Filter:    Window:MainWindow > ** > (unnamed):Button["Go!"]
Compiles to:    ^Window:MainWindow > .*? > \(unnamed\):Button\["Go!"\]$

Element Path:   Window:MainWindow > ... > ... > (unnamed):Button["Go!"]
Result:         ? MATCH
```

### **Tips:**

- ? **Use `**` for flexible matching** (works across UI structure changes)
- ? **Include element content in quotes** for exact matching: `["Go!"]`
- ? **Avoid overly specific paths** (break when UI structure changes)
- ? **Don't use HWND in path filters** (changes every app run)

---

## ?? **Built-in Keys & Pages**

### **Built-in Keys (Always Available):**
- `Back` - Exit current modal/group
- `Up`, `Down`, `Left`, `Right` - Navigation
- `MouseLeft` - Activate focused element
- `WriteModalFilter` - Write modal path to discovery file
- `WriteElementFilter` - Write focused element path to discovery file

### **Built-in Pages (Always Available):**
- `Navigation` - Full 6-direction navigation
- `UpDown` - Vertical only (for menus)
- `Slider` - Horizontal only (for value adjustments)
- `DoubleSlider` - Vertical coarse + horizontal fine
- `RoundSlider` - All 4 directions (for circular controls)

---

## ?? **Troubleshooting**

### **Shortcut key not working:**

1. **Check debug output:**
   ```
   [Navigator] Loaded config: 5 shortcuts, 2 custom pages
   [Navigator] Defined shortcut key: QuickChangeCar ? Window:MainWindow > ** > ...
   [Navigator] Searching 64 candidates for path: Window:MainWindow > ** > ...
   [Navigator] ? Found matching node: (unnamed):ModernButton @ ...
   ```

2. **Common issues:**
   - ? Path doesn't match (element renamed or moved)
   - ? Element not in current scope (wrong modal/window)
   - ? Wildcard pattern too specific/generic
   - ? Configuration file not loaded (check file location)

3. **Solutions:**
   - ? Re-discover element path using WriteElementFilter
   - ? Use `**` wildcard for more flexible matching
   - ? Check element is in current window/modal
   - ? Verify config file location and syntax

---

## ?? **API Reference**

### **NavShortcutKey Class:**
```csharp
public class NavShortcutKey
{
    string KeyName      // Unique identifier (required)
    string KeyTitle     // Display title (optional)
    string KeyIcon      // Icon specification (optional)
    string PathFilter   // Hierarchical path filter (required)
    string Role         // Element role (optional)
    Regex PathPattern   // Compiled regex (auto-generated)
    
    bool Matches(string elementPath)  // Check if element matches filter
}
```

### **NavPageDef Class:**
```csharp
public class NavPageDef
{
    string PageName       // Page identifier
    string[][] KeyGrid    // 5x3 grid of key names
}
```

### **NavConfigParser Class:**
```csharp
public static class NavConfigParser
{
    NavConfiguration Load()                      // Load from standard location
    NavConfiguration Parse(string content)       // Parse configuration content
}
```

### **NavConfiguration Class:**
```csharp
public class NavConfiguration
{
    List<NavShortcutKey> ShortcutKeys           // All shortcut definitions
    List<NavPageDef> Pages                       // All custom pages
    
    NavShortcutKey FindShortcut(string keyName)           // Find by key name
    List<NavShortcutKey> FindShortcutsForPath(string path) // Find by element path
    NavPageDef FindPage(string pageName)                   // Find by page name
}
```

---

## ?? **Best Practices**

### **1. Use Wildcards Wisely:**
```
? GOOD:  Window:MainWindow > ** > (unnamed):Button["Go!"]
? BAD:   Window:MainWindow > WindowBorder:Border > ... (too specific)
```

### **2. Name Keys Consistently:**
```
? GOOD:  Quick<Action> (QuickChangeCar, QuickGo, QuickRace)
? BAD:   car, go, race (not descriptive)
```

### **3. Organize Pages Logically:**
```
? GOOD:  Group related shortcuts (QuickDrive page for drive shortcuts)
? BAD:   Random mix of unrelated shortcuts
```

### **4. Test Shortcuts After UI Changes:**
```
After app updates:
  1. Test all shortcuts
  2. Re-discover any broken paths
  3. Update configuration file
```

### **5. Document Custom Shortcuts:**
```
# Quick access to "Change Car" button (added 2025-12-28)
CLASSIFY: Window:MainWindow > ** > (unnamed):ModernButton["Change car"] => \
    role=modernbutton; \
    KeyName=QuickChangeCar; \
    KeyTitle="Car"
```

---

## ?? **Future Enhancements**

- [ ] **Hot reload** - Reload config without restarting app
- [ ] **Visual config editor** - GUI for creating shortcuts
- [ ] **Shortcut validation** - Check if shortcuts work at startup
- [ ] **Page inheritance** - Define base pages, override specific slots
- [ ] **Conditional shortcuts** - Different shortcuts per modal/page
- [ ] **Shortcut macros** - Execute multiple actions per key press
