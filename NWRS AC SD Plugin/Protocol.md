# NWRS AC StreamDeck Plugin Communication Protocol

## Overview

This document describes the communication protocol between Content Manager (CM) and the NWRS AC StreamDeck Plugin. The protocol uses a Named Pipe for bidirectional communication with a simple text-based command/event format.

## Connection

- **Named Pipe**: `NWRS_AC_SDPlugin_Pipe`
- **Direction**: Bidirectional (InOut)
- **Format**: Text lines (UTF-8), one command/event per line
- **Max Clients**: 1 (only one CM instance at a time)

## Message Format

All messages are single-line text strings terminated by newline (`\n`).

### Commands (CM ? Plugin)

Commands are sent from Content Manager to the plugin. No response is expected (fire-and-forget).

### Events (Plugin ? CM)

Events are sent from the plugin to Content Manager when user actions occur. No acknowledgment is expected.

---

## Commands Reference

### 1. DefineKey

Defines a key that can be used in pages.

**Format:**
```
DefineKey <KeyName> [Title] [IconFileName]
```

**Parameters:**
- `KeyName`: Unique identifier for the key (no spaces, required)
- `Title`: Display title (optional - use `null` or omit for blank/icon-only keys)
- `IconFileName`: Icon file path or special format (optional - use `null` or omit for blank/title-only keys)

**Examples:**
```
DefineKey PitLimiter "Pit Limiter" C:\MyApp\Icons\pit_limiter.png
DefineKey TractionControl TC C:\MyApp\Icons\tc.png
DefineKey Back "" C:\MyApp\Icons\back.png
DefineKey QuickAccess "" !QA
DefineKey TitleOnly "Title Only Key" null
DefineKey SimpleTitle "Simple Title"
DefineKey IconOnly null C:\Icons\icon.png
DefineKey BlankKey null null
DefineKey Blank
```

**Icon Formats:**
- **Absolute path**: `C:\MyApp\Icons\icon.png` (recommended - full path to icon file)
- **Text-based**: `!TextHere` (generates icon with text, max 2-3 characters recommended)
- **Base64**: `data:image/png;base64,...` (embedded image data)
- **Title-only**: Omit or set icon to `null` - displays only the title text, no icon
- **Blank**: Omit both title and icon - creates blank key placeholder
- **Relative path**: `icon.png` (legacy - searches in plugin's `assets\SD-Icons\` folder, not recommended)

**Notes:**
- **Parameter order**: Title comes before IconFileName (title is usually shorter)
- **Use absolute paths** for icons when available
- **Blank keys**: Omit or set both title and icon to `null` to create placeholder keys
- **Dynamic updates**: Use SetKeyVisuals to update keys in the current page after creation
- Keys must be defined before using them in DefinePage
- Redefining a key overwrites the previous definition
- Missing icon files fall back to text-based rendering using the filename
- Icon files should be PNG format, 72x72 pixels for best results
- Icons are cached - same path won't be reloaded

---

### 2. SetKeyVisuals

Updates the title and/or icon of an existing key in the **currently active page only**. Does not affect the key definition or other pages.

**Format:**
```
SetKeyVisuals <KeyName> [Title] [IconFileName]
```

**Parameters:**
- `KeyName`: Name of an existing key in the current page (required)
- `Title`: New title text (optional - use `null` or omit to clear title)
- `IconFileName`: New icon path or format (optional - use `null` or omit to clear icon)

**Examples:**
```
SetKeyVisuals PitLimiter "Pit Active" C:\Icons\pit_active.png
SetKeyVisuals Status "Connected" !OK
SetKeyVisuals Speed "120 mph"
SetKeyVisuals Icon null C:\Icons\new.png
SetKeyVisuals ClearBoth null null
SetKeyVisuals BlankIt
```

**Notes:**
- **Page-specific**: Only updates the key in the currently active page
- **Creates new instance**: The key becomes independent from other pages using the same definition
- Key must exist in the currently active page
- Key must be defined with DefineKey (but may not be on current page)
- Use `null` or omit parameters to clear title/icon
- Supports all icon formats (absolute path, text-based, base64)
- Returns KeyVisualsSet event with OK or ERROR status

---

### 3. DefinePage

Creates a page with a grid of keys. Supports inheritance from existing pages using C++ style syntax.

**Format:**
```
DefinePage <PageName>[:<BasePageName>] <KeyGrid>
```

**Parameters:**
- `PageName`: Unique identifier for the page (no spaces, required)
- `:<BasePageName>`: Optional - inherit layout from an existing page
- `KeyGrid`: JSON array of arrays defining the 5x3 key layout

**Key Grid Format:**
- Must be a valid JSON array: `[[row0],[row1],[row2],[row3],[row4]]`
- Each row must have exactly 3 elements (columns)
- Use `null` to inherit key from base page (requires base page)
- Use `""` (empty string) to clear position (override base or set empty)
- Use `"KeyName"` to use specific key (override base with this key)

**Inheritance Semantics:**
When a base page is specified with `:BasePageName`:
- **`null`** = Keep/inherit the key from the base page at this position
- **`""`** (empty string) = Clear this position (override base with empty key)
- **`"KeyName"`** = Use specified key (override base with this key)

**Examples:**

**Standard page (no inheritance):**
```
DefinePage MainMenu [["PitLimiter","TractionControl","ABSControl"],["Back",null,"Next"],[null,null,null],[null,null,null],[null,null,null]]
```

**Inherit and override specific keys:**
```
# Base navigation page with common buttons
DefineKey Back "Back" C:\Icons\back.png
DefineKey Home "Home" C:\Icons\home.png
DefineKey Next "Next" C:\Icons\next.png

DefinePage Navigation [["Back","Home","Next"],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]

# Edit page: inherit navigation, add Save/Cancel buttons at top
DefineKey Save "Save" C:\Icons\save.png
DefineKey Cancel "Cancel" C:\Icons\cancel.png

DefinePage EditPage:Navigation [["Save","Cancel",null],["","",""],["","",""],["","",""],["","",""]]

# Result: EditPage has Save, Cancel, Next (inherited via null) at row 0
# Back and Home positions are overridden, position (0,2) inherits Next
```

**Inherit most, override one:**
```
DefinePage ViewPage:Navigation [[null,null,null],["Export","Print",""],["","",""],["","",""],["","",""]]

# Result: ViewPage has Back, Home, Next (all inherited via null) at row 0
#         Plus Export, Print at row 1
```

**Clear inherited keys:**
```
DefinePage MinimalPage:Navigation [["","",""],["","",""],["","",""],["","",""],["","",""]]

# Result: MinimalPage has no keys (all base keys cleared with "")
```

**Notes:**
- All pages are exactly 5 rows x 3 columns (StreamDeck standard layout)
- Base page must exist before creating child page
- `null` in child grid means "inherit from base" - requires a base page to be specified
- `""` (empty string) means "clear this position" - works with or without base page
- Keys specified in child grid must be defined with DefineKey
- Inheritance is resolved at definition time (one-time merge)
- Changes to base page don't affect existing child pages
- Pages can be redefined to update layout
- Multi-level inheritance not supported (ChildA:ParentB where ParentB:GrandparentC)

---

### 5. SwitchPage

Switches the active page to display different keys.

**Format:**
```
SwitchPage <PageName>
```

**Parameters:**
- `PageName`: Name of a previously defined page

**Example:**
```
SwitchPage MainMenu
```

**Notes:**
- Page must exist (defined with DefinePage)
- Unknown pages are ignored with warning
- Can only switch when SDeck is active (after CM connects)

---

### 6. SwitchProfile

Switches the StreamDeck hardware profile.

**Format:**
```
SwitchProfile <ProfileName>
```

**Parameters:**
- `ProfileName`: Name of the StreamDeck profile (must exist in StreamDeck software)

**Example:**
```
SwitchProfile F1 2024
```

**Notes:**
- Profile must exist in StreamDeck software configuration
- Profile names are case-sensitive
- Previous profile is remembered in a stack

---

### 7. SwitchProfileBack

Returns to the previous StreamDeck profile in the stack.

**Format:**
```
SwitchProfileBack
```

**Parameters:** None

**Example:**
```
SwitchProfileBack
```

**Notes:**
- Pops the profile stack and returns to previous profile
- If stack is empty, command is ignored
- Useful for returning from game-specific profiles

---

## Events Reference

### 1. KeyPress

Sent when user presses a key on the StreamDeck.

**Format:**
```
KeyPress <KeyName>
```

**Parameters:**
- `KeyName`: The name of the key that was pressed

**Example:**
```
KeyPress PitLimiter
```

**Notes:**
- Only sent for keys defined with DefineKey
- Sent after minimum press duration is satisfied
- Empty/undefined keys don't generate events

---

### 2. KeyDefined

Sent in response to DefineKey command to confirm success or report failure.

**Format:**
```
KeyDefined <KeyName> <Status> [ErrorMessage]
```

**Parameters:**
- `KeyName`: The name of the key being defined (or "unknown" if name couldn't be parsed)
- `Status`: Either `OK` or `ERROR`
- `ErrorMessage`: (Required if Status is ERROR) Description of the error

**Examples:**
```
KeyDefined PitLimiter OK
KeyDefined TractionControl OK
KeyDefined BlankKey OK
KeyDefined unknown ERROR Missing required parameter: KeyName required
KeyDefined BadKey ERROR Invalid icon path format
```

**Notes:**
- Sent immediately after processing DefineKey command
- Content Manager should check for ERROR status and handle appropriately
- Errors indicate the key was NOT defined successfully

---

### 3. KeyVisualsSet

Sent in response to SetKeyVisuals command to confirm success or report failure.

**Format:**
```
KeyVisualsSet <KeyName> <Status> [ErrorMessage]
```

**Parameters:**
- `KeyName`: The name of the key being updated
- `Status`: Either `OK` or `ERROR`
- `ErrorMessage`: (Required if Status is ERROR) Description of the error

**Examples:**
```
KeyVisualsSet PitLimiter OK
KeyVisualsSet Status OK
KeyVisualsSet UnknownKey ERROR Key 'UnknownKey' not defined
KeyVisualsSet MissingKey ERROR Key 'MissingKey' not found in current page
KeyVisualsSet NoPage ERROR No active page
```

**Notes:**
- Sent immediately after processing SetKeyVisuals command
- Updates only affect the key in the currently active page
- Errors indicate the visuals were NOT updated

---

### 4. PageDefined

Sent in response to DefinePage command to confirm success or report failure.

**Format:**
```
PageDefined <PageName> <Status> [ErrorMessage]
```

**Parameters:**
- `PageName`: The name of the page being defined (or "unknown" if name couldn't be parsed)
- `Status`: Either `OK` or `ERROR`
- `ErrorMessage`: (Required if Status is ERROR) Description of the error

**Examples:**
```
PageDefined MainMenu OK
PageDefined Settings OK
PageDefined unknown ERROR Missing required parameters: PageName and KeyGrid required
PageDefined GameMenu ERROR Invalid JSON grid format
PageDefined BadPage ERROR Undefined keys: MissingKey@(0,1), Another@(2,3)
```

**Notes:**
- Sent immediately after processing DefinePage command
- **Strict validation**: If ANY referenced keys are undefined, page creation fails completely
- No partial pages are created - it's all or nothing
- Content Manager must ensure all keys are defined before creating pages
- Errors indicate the page was NOT created

---

## Connection Lifecycle

### 1. Connection Established

When CM connects to the named pipe:

1. Plugin activates StreamDeck management
2. Plugin switches to "NWRS AC" profile
3. Plugin subscribes to key press events
4. CM can begin sending commands

### 2. Initial Setup Phase

Typical command sequence after connection:

```
CM ? DefineKey Back Back C:\Icons\back.png
Plugin ? KeyDefined Back OK

CM ? DefineKey PitLimiter "Pit Limiter" C:\Icons\pit_limiter.png
Plugin ? KeyDefined PitLimiter OK

CM ? DefineKey TractionControl TC C:\Icons\tc.png
Plugin ? KeyDefined TractionControl OK

CM ? DefineKey ABSControl ABS C:\Icons\abs.png
Plugin ? KeyDefined ABSControl OK

CM ? DefineKey Next Next C:\Icons\next.png
Plugin ? KeyDefined Next OK

CM ? DefinePage MainMenu [["PitLimiter","TractionControl","ABSControl"],["Back",null,"Next"],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined MainMenu OK

CM ? DefinePage Settings [[null,null,null],["Back",null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined Settings OK

CM ? SwitchPage MainMenu
```

### 3. Runtime

During active session:

```
CM ? SwitchPage Settings
CM ? SwitchProfile "F1 2024"

[User presses key]
Plugin ? KeyPress PitLimiter

CM ? SwitchProfileBack
CM ? SwitchPage MainMenu
```

### 4. Error Handling Examples

Examples of error scenarios:

```
# Missing parameters
CM ? DefineKey BadKey
Plugin ? KeyDefined unknown ERROR Missing required parameters: KeyName and IconFileName required

# Undefined keys in page
CM ? DefineKey Key1 icon1.png "Key 1"
Plugin ? KeyDefined Key1 OK

CM ? DefinePage BadPage [["Key1","UndefinedKey",null],...]
Plugin ? PageDefined BadPage ERROR Undefined keys: UndefinedKey@(0,1)

# Invalid JSON
CM ? DefinePage BadPage [invalid json
Plugin ? PageDefined BadPage ERROR Invalid JSON grid format
```

### 5. Disconnection

When CM disconnects:

1. Plugin unsubscribes from key press events
2. Plugin clears all key definitions
3. Plugin clears all page definitions
4. Plugin deactivates StreamDeck management (passive mode)
5. StreamDeck profile is NOT changed (stays wherever it was)

---

## Error Handling

### Command Errors

- **Invalid format**: Logged, command ignored
- **Missing parameters**: Logged, command ignored  
- **Unknown command**: Logged, command ignored
- **Undefined keys in page**: Logged, position left blank
- **Missing page**: Logged, switch ignored
- **JSON parse errors**: Logged, command ignored

### Event Errors

- **Pipe disconnected**: Event dropped silently
- **Write errors**: Logged, event dropped

### Best Practices

1. **Define all keys before creating pages** - Pages will fail if keys are undefined
2. **Check KeyDefined events** - Verify each key was defined successfully before proceeding
3. **Check PageDefined events** - Verify page was created before switching to it
4. **Handle errors immediately** - Don't continue if a definition fails
5. **Validate JSON grid format** - Ensure valid JSON before sending DefinePage
6. **Keep KeyNames simple** - Alphanumeric, no spaces, must be unique
7. **Handle KeyPress events asynchronously** - Don't block event processing
8. **Use absolute file paths** for icons (e.g., `C:\MyApp\Icons\icon.png`)
9. **Verify icon files exist** before sending DefineKey commands (optional, but recommended)
10. **No partial pages** - All keys must exist or page creation fails completely
11. **Use page inheritance for common layouts** - Define base pages for navigation/common elements
12. **Use `null` to inherit from base** - Let child pages inherit keys from parent at specific positions
13. **Use `""` to clear positions** - Explicitly override inherited keys with empty positions
14. **Define base pages first** - Child pages cannot be created before their parents
15. **Inheritance is one-time merge** - Changes to base page don't affect existing children

---

## Protocol Examples

### Example 1: Simple Menu

```
DefineKey Option1 !1 "Option 1"
DefineKey Option2 !2 "Option 2"
DefineKey Option3 !3 "Option 3"

DefinePage SimpleMenu [["Option1","Option2","Option3"],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]

SwitchPage SimpleMenu
```

Result: 3 buttons in top row with text-based icons "1", "2", "3"

### Example 2: Full Application Setup with Absolute Paths

```
# Define keys with absolute paths from Content Manager's icon directory
DefineKey PitLimiter "Pit Limiter" C:\Program Files\ContentManager\Icons\pit_limiter.png
DefineKey TractionControl TC C:\Program Files\ContentManager\Icons\tc.png
DefineKey ABSControl ABS C:\Program Files\ContentManager\Icons\abs.png
DefineKey Back Back C:\Program Files\ContentManager\Icons\back.png
DefineKey Settings Settings C:\Program Files\ContentManager\Icons\settings.png

# Define title-only keys (no icon)
DefineKey Option1 "Option 1" null
DefineKey Option2 "Option 2"

# Create main menu page
DefinePage MainMenu [["PitLimiter","TractionControl","ABSControl"],["Back","Option1","Settings"],[null,null,"Option2"],[null,null,null],[null,null,null]]

# Create settings page
DefinePage SettingsMenu [[null,null,null],["Back",null,null],[null,null,null],[null,null,null],[null,null,null]]

# Switch to main menu
SwitchPage MainMenu
```

Result: Full menu with custom icons and some title-only keys loaded from Content Manager's installation directory

### Example 3: Game Profile Switching

```
# Main menu initially
SwitchProfile "NWRS AC"
SwitchPage MainMenu

# Game launches
SwitchProfile "F1 2024"

# User plays game...

# Game exits
SwitchProfileBack
SwitchPage MainMenu
```

### Example 4: Dynamic Updates

```
# Initial setup
DefineKey Status C:\MyApp\Icons\status.png "Ready"
DefinePage Info [["Status",null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
SwitchPage Info

# Update the status key
DefineKey Status C:\MyApp\Icons\status_active.png "Active"
DefinePage Info [["Status",null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
# No need to SwitchPage again, existing page updates
```

### Example 5: Mixed Icon Types

```
# Use different icon formats as needed
DefineKey Option1 "Option 1" C:\MyApp\Icons\option1.png
DefineKey Option2 "Option 2" !2
DefineKey Option3 "Option 3" data:image/png;base64,iVBORw0KG...
DefineKey Option4 "Option 4" null
DefineKey Option5 "Title Only"

DefinePage MixedMenu [["Option1","Option2","Option3"],["Option4","Option5",null],[null,null,null],[null,null,null],[null,null,null]]
SwitchPage MixedMenu
```

Result: Page with absolute path icon, text-generated icon, base64 icon, and two title-only keys

### Example 6: Dynamic Key Updates (Page-Specific)

```
# Define blank keys as placeholders
DefineKey Status
DefineKey Icon
DefineKey Title

# Create page with blank keys
DefinePage StatusPage [["Status","Icon","Title"],[null,null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined StatusPage OK

SwitchPage StatusPage

# Update keys dynamically in the current page only
SetKeyVisuals Status "Connecting..." !...
Plugin ? KeyVisualsSet Status OK

# Later, update to show connected state
SetKeyVisuals Status "Connected" C:\Icons\connected.png
Plugin ? KeyVisualsSet Status OK

# Update other keys
SetKeyVisuals Icon null C:\Icons\info.png
Plugin ? KeyVisualsSet Icon OK

SetKeyVisuals Title "Information"
Plugin ? KeyVisualsSet Title OK

# Clear a key to make it blank again
SetKeyVisuals Status null null
Plugin ? KeyVisualsSet Status OK

# Create another page with same keys - they still use original definitions
DefinePage AnotherPage [["Status","Icon","Title"],[null,null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
SwitchPage AnotherPage
# Keys appear blank here - StatusPage updates didn't affect them
```

Result: Keys can be updated in real-time on current page without affecting key definitions or other pages

### Example 7: Page Inheritance - Navigation Template

```
# Define common navigation keys
DefineKey Back "Back" C:\Icons\back.png
DefineKey Home "Home" C:\Icons\home.png
DefineKey Next "Next" C:\Icons\next.png
Plugin ? KeyDefined Back OK
Plugin ? KeyDefined Home OK
Plugin ? KeyDefined Next OK

# Create base navigation page
DefinePage Navigation [["Back","Home","Next"],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined Navigation OK

# Define context-specific keys
DefineKey Save "Save" C:\Icons\save.png
DefineKey Cancel "Cancel" C:\Icons\cancel.png
DefineKey Export "Export" C:\Icons\export.png
DefineKey Print "Print" C:\Icons\print.png
Plugin ? KeyDefined Save OK
Plugin ? KeyDefined Cancel OK
Plugin ? KeyDefined Export OK
Plugin ? KeyDefined Print OK

# Create edit page inheriting navigation
DefinePage EditMenu:Navigation [["Save","Cancel",null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined EditMenu OK
# Result: Save, Cancel, Next (inherited via null) at row 0

# Create view page inheriting navigation
DefinePage ViewMenu:Navigation [[null,null,null],["Export","Print",null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined ViewMenu OK
# Result: Back, Home, Next (all inherited) at row 0, Export/Print at row 1

# Create minimal page clearing everything
DefinePage MinimalMenu:Navigation [["","",""],["","",""],["","",""],["","",""],["","",""]]
Plugin ? PageDefined MinimalMenu OK
# Result: All keys cleared with ""

# Switch between pages
SwitchPage EditMenu   # Shows: Save, Cancel, Next
SwitchPage ViewMenu   # Shows: Back, Home, Next, Export, Print
SwitchPage MinimalMenu # Shows: All keys cleared
```

Result: Multiple pages share common navigation with context-specific additions

### Example 8: Page Inheritance - Error Scenarios

```
# Error: Base page doesn't exist
DefinePage ChildPage:NonExistent [[null,null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined ChildPage ERROR Base page 'NonExistent' not defined

# Error: Using null without base page specified
DefinePage InvalidPage [[null,null,null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined InvalidPage ERROR null@(0,0)-no_base_page
```

Result: Proper error handling for inheritance edge cases

### Example 9: Page Inheritance - Complex Layouts

```
# Define full set of keys
DefineKey A "A" !A
DefineKey B "B" !B
DefineKey C "C" !C
DefineKey D "D" !D
DefineKey E "E" !E
DefineKey F "F" !F

# Create base layout with scattered keys
DefinePage BaseLayout [["A",null,"B"],[null,"C",null],["D",null,"E"],[null,null,null],[null,null,null]]
Plugin ? PageDefined BaseLayout OK
# Layout: A[_]B / [_]C[_] / D[_]E / [_][_][_] / [_][_][_]

# Child 1: Keep most, add F at bottom
DefinePage Child1:BaseLayout [[null,null,null],[null,null,null],[null,null,null],[null,null,null],["F",null,null]]
Plugin ? PageDefined Child1 OK
# Layout: A[_]B / [_]C[_] / D[_]E / [_][_][_] / F[_][_]

# Child 2: Clear middle, keep edges
DefinePage Child2:BaseLayout [[null,null,null],["","",""],["",null,""],[null,null,null],[null,null,null]]
Plugin ? PageDefined Child2 OK
# Layout: A[_]B / [_][_][_] / D[_]E / [_][_][_] / [_][_][_]

# Child 3: Swap some keys
DefinePage Child3:BaseLayout [[null,"F",null],[null,null,null],[null,null,null],[null,null,null],[null,null,null]]
Plugin ? PageDefined Child3 OK
# Layout: A F B / [_]C[_] / D[_]E / [_][_][_] / [_][_][_]
```

Result: Flexible inheritance allows complex layout variations

---

## Version History

- **v1.2** (2024-12-24): Updated inheritance syntax
  - Changed from `"base"` keyword to `null` for inheritance (more intuitive)
  - `null` = inherit key from base page at this position
  - `""` (empty string) = explicitly clear position
  - `"KeyName"` = use specific key (override)
  - Updated all examples and documentation

- **v1.1** (2024-12-24): Page inheritance feature
  - Added `:BasePageName` syntax for page inheritance (C++ style)
  - Inheritance with grid semantics
  - Enhanced error handling for inheritance edge cases
  - One-time merge at definition (no dynamic inheritance)

- **v1.0** (2024-12-23): Initial protocol design
  - DefineKey, DefinePage, SwitchPage, SwitchProfile, SwitchProfileBack
  - KeyPress event
  - 5x3 fixed grid layout
