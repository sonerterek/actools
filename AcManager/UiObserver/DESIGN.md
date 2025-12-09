# Navigation System (UiObserver) - Design Document

## ?? Table of Contents
1. [Architecture Overview](#architecture-overview)
2. [Key Design Decisions](#key-design-decisions)
3. [File Responsibilities](#file-responsibilities)
4. [Critical Implementation Details](#critical-implementation-details)
5. [Known Issues & Solutions](#known-issues--solutions)
6. [Pattern Syntax Reference](#pattern-syntax-reference)

---

## ??? Architecture Overview

### Three-Layer "Observe and React" Architecture

```
???????????????????????????????????????????????????????????
?                      NavMapper                          ?
?  (Navigation Logic & Modal Stack Management)            ?
?  - Subscribes to NavForest events                       ?
?  - Manages modal stack (pushed/popped via events)       ?
?  - Handles keyboard input (Alt+Arrow navigation)        ?
?  - Filters candidates by modal scope                    ?
?  - Spatial navigation algorithm                         ?
?  - Focus highlighting (HighlightOverlay)                ?
???????????????????????????????????????????????????????????
                           ? events
                           ? (NavNodeAdded, ModalGroupOpened, etc.)
???????????????????????????????????????????????????????????
?                      NavForest                          ?
?  (Discovery Engine - Scans & Tracks Nodes)              ?
?  - Auto-discovers PresentationSource roots              ?
?  - Scans visual trees recursively                       ?
?  - Creates NavNodes via factory pattern                 ?
?  - Builds Parent/Child relationships                    ?
?  - Tracks Popup?PlacementTarget bridges                 ?
?  - Emits events when nodes added/removed                ?
???????????????????????????????????????????????????????????
                           ? creates
                           ?
???????????????????????????????????????????????????????????
?                       NavNode                           ?
?  (Pure Data + Type-Specific Behaviors)                  ?
?  - Factory method: CreateNavNode(fe)                    ?
?  - Type classification (leaf vs group)                  ?
?  - Whitelist-based filtering                            ?
?  - Path-based exclusion rules (NavPathFilter)           ?
?  - Behaviors: Activate(), Close()                       ?
?  - Stores: HierarchicalPath, Parent, Children           ?
???????????????????????????????????????????????????????????
```

---

## ?? Key Design Decisions

### 1. "Observe, Don't Predict" Philosophy

**Decision:** We observe actual UI state (Popup.IsOpen, Window visibility) rather than predicting behavior.

**Why:**
- **Simplicity:** No complex state machines tracking "will this ComboBox open a popup?"
- **Correctness:** We react to what WPF actually creates (PopupRoot) instead of guessing
- **Maintainability:** Less code to break when WPF internals change

**Example:**
```csharp
// ? OLD (Predict): ComboBox is a "dual-role" modal
// ? NEW (Observe): ComboBox is a leaf; PopupRoot (created by WPF) is the modal
```

---

### 2. Whitelist-Based Type Classification

**Decision:** Only track types explicitly added to `_leafTypes` or `_groupTypes`.

**Why:**
- **Performance:** Avoid tracking every TextBlock, Border, Grid in the app (thousands of elements)
- **Intent:** Only navigation-relevant controls are tracked
- **Safety:** Unknown types are ignored by default

**Leaf Types:** Buttons, MenuItems, ComboBox, Menu, Sliders, etc.  
**Group Types:** Popup, ListBox, TabControl, DataGrid, etc.

---

### 3. Modal Stack = Linear Chain (Not Tree)

**Decision:** Modal stack is a **List<NavNode>** representing a linear parent?child chain.

**Why:**
- **Simplicity:** Matches WPF's actual behavior (nested modals always descend from parent modals)
- **Validation:** We validate that each new modal is a descendant of the current top modal
- **No Siblings:** WPF doesn't allow sibling modals at the same level (one popup blocks another)

**Example Valid Stack:**
```
[0] MainWindow (root context)
[1] Popup inside MainWindow (dropdown menu)
[2] PopupRoot inside that Popup (submenu)
```

**Example Invalid Stack (Rejected):**
```
[0] MainWindow
[1] Popup in Panel A
[2] Popup in Panel B ? Not a descendant of [1]! Rejected.
```

---

### 4. HierarchicalPath = Computed Once, Stored Forever

**Decision:** Compute `HierarchicalPath` in `NavNode.CreateNavNode()` and pass to constructor.

**Why:**
- **Exclusion Check:** Needed BEFORE node creation to filter out unwanted elements
- **Performance:** Compute once during creation, not repeatedly
- **Consistency:** Path never changes after creation (element's position in tree is fixed)

**Format:** `"WindowName:Window > PanelName:StackPanel > ButtonName:Button"`

**Key Insight:** Path includes ALL ancestors in visual tree, not just NavNode ancestors.

---

### 5. Popup?PlacementTarget Bridging

**Decision:** `NavForest.LinkToParent()` walks through `Popup.PlacementTarget` to bridge visual tree gaps.

**Why:**
- **WPF Limitation:** Elements inside a Popup have NO visual tree parent (VisualTreeHelper.GetParent returns null)
- **Logical Parent:** Popup's `PlacementTarget` points to the owner control outside the Popup
- **Navigation Correctness:** MenuItem inside a Popup should be able to navigate back to the Button that opened it

**Implementation:**
```csharp
// If we hit a Popup boundary during parent walk:
if (current is Popup popup && popup.PlacementTarget != null) {
    current = popup.PlacementTarget; // Jump across boundary
}
```

---

### 6. Persistent HighlightOverlay (Not Dispose/Recreate)

**Decision:** Create overlay once, reuse it (Hide/Show instead of Dispose/Create).

**Why:**
- **Gen 2 GC Deadlock:** Old approach created 7 dead overlay instances ? finalizers tried to use Dispatcher ? circular wait ? permanent deadlock
- **Performance:** Reusing window is faster than creating/destroying
- **Simplicity:** Less code, fewer edge cases

**Implementation:**
```csharp
// OLD (BAD):
_overlay?.Dispose();
_overlay = null;
_overlay = new HighlightOverlay(); // Creates new instance

// NEW (GOOD):
_overlay?.Hide(); // Reuse same instance
```

**Critical Fix:** `GC.SuppressFinalize(this)` in `HighlightOverlay.Dispose()` prevents finalizer deadlock.

---

## ?? File Responsibilities

### NavNode.cs - Data Structure + Factory

**Purpose:** Pure data + type-specific behaviors + creation logic.

**Key Methods:**
- `CreateNavNode(fe)` - Factory method with 11-step validation pipeline
- `Activate()` - Type-specific activation (click button, open menu, select item)
- `Close()` - Type-specific close (close window, close popup, collapse menu)
- `GetHierarchicalPath(fe)` - Walks visual tree to build path string

**Key Data:**
- `SimpleName` - Human-readable name (not unique)
- `HierarchicalPath` - Full path for filtering (unique)
- `Parent` - WeakReference to parent NavNode
- `Children` - List of WeakReferences to child NavNodes
- `IsGroup` / `IsModal` / `IsNavigable` - Classification flags

**Design Patterns:**
- **Factory Pattern:** `CreateNavNode()` centralizes creation logic
- **Strategy Pattern:** `Activate()` / `Close()` behavior varies by type
- **Whitelist Pattern:** Only whitelisted types are tracked

---

### NavForest.cs - Discovery Engine

**Purpose:** Scans visual trees, creates NavNodes, builds hierarchy, emits events.

**Key Methods:**
- `RegisterRoot(fe)` - Register a PresentationSource root for scanning
- `SyncRoot(root)` - Rescan visual tree and sync with tracked nodes
- `LinkToParent(node, fe)` - Build Parent/Child relationships (handles Popup bridging)
- `TryCreateNavNodeForElement(fe)` - Create node for dynamically loaded elements

**Key Events:**
- `NavNodeAdded` - New node discovered
- `NavNodeRemoved` - Node removed from tree
- `ModalGroupOpened` - Modal opened (Popup, Window, PopupRoot)
- `ModalGroupClosed` - Modal closed

**Key Data Structures:**
- `_nodesByElement` - ConcurrentDictionary<FrameworkElement, NavNode> (source of truth)
- `_presentationSourceRoots` - Map PresentationSource ? root element
- `_rootIndex` - Map root ? all elements in that tree
- `_pendingSyncs` - Debouncing for layout changes

**Design Patterns:**
- **Event-Driven Architecture:** Emits events instead of direct calls
- **Debouncing:** Coalesces multiple layout changes into single scan
- **Weak References:** Parent/Child links use WeakReference to avoid memory leaks

---

### NavMapper.cs - Navigation Logic

**Purpose:** Subscribes to events, manages modal stack, handles keyboard input, spatial navigation.

**Key Methods:**
- `MoveInDirection(dir)` - Find best candidate in direction using spatial algorithm
- `ActivateFocusedNode()` - Activate current focus
- `ExitGroup()` - Close topmost modal
- `ToggleHighlighting()` - Debug visualization (Ctrl+Shift+F12)

**Key Data:**
- `_activeModalStack` - List<NavNode> representing modal stack
- `_focusedNode` - Currently focused NavNode
- `_overlay` - HighlightOverlay for visual feedback

**Event Handlers:**
- `OnNavNodeAdded` / `OnNavNodeRemoved` - React to discovery changes
- `OnModalGroupOpened` / `OnModalGroupClosed` - Push/pop modal stack
- `OnPreviewKeyDown` - Handle Alt+Arrow keys

**Design Patterns:**
- **Observer Pattern:** Subscribes to NavForest events
- **Command Pattern:** Keyboard shortcuts trigger navigation commands
- **Strategy Pattern:** Spatial navigation algorithm (directional cost calculation)

---

### NavPathFilter.cs - Pattern Matching

**Purpose:** Filter/classify nodes based on hierarchical path patterns.

**Pattern Syntax:**
```
EXCLUDE: *:HighlightOverlay > **
CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true
```

**Wildcards:**
- `*` - Match any single segment (name or type)
- `**` - Match 0+ segments (any depth including current level)
- `***` - Match 1+ segments (at least one ancestor away)

**Key Methods:**
- `IsExcluded(path)` - Check if path matches any exclusion rule
- `GetClassification(path)` - Get classification override for path
- `ParseRules(rules[])` - Parse rule array

**Design Patterns:**
- **Interpreter Pattern:** Parse and evaluate path patterns
- **Chain of Responsibility:** Check rules in priority order

---

### HighlightOverlay.cs - Visual Feedback

**Purpose:** Topmost transparent window showing focus rectangle + debug rectangles.

**Key Methods:**
- `ShowFocusRect(rect)` - Show blue rectangle for focused node
- `HideFocusRect()` - Hide focus rectangle
- `ShowDebugRects(leafs, groups)` - Show all navigable elements (debug mode)
- `ClearDebugRects()` - Clear debug rectangles
- `Dispose()` - **CRITICAL:** Includes `GC.SuppressFinalize(this)` to prevent deadlock

**Key Properties:**
- `AllowsTransparency = true` - Transparent background
- `Topmost = true` - Always on top
- `IsHitTestVisible = false` - Click-through
- `ShowInTaskbar = false` - Hidden from taskbar

**Design Patterns:**
- **Singleton-ish:** NavMapper keeps one persistent instance
- **Observer Pattern:** Updates when focus changes

---

## ?? Critical Implementation Details

### 1. CreateNavNode() - 11-Step Pipeline

```csharp
public static NavNode CreateNavNode(FrameworkElement fe) {
    // Step 1: Type-based detection (IsLeafType / IsGroupType)
    // Step 2: Compute HierarchicalPath (for filtering)
    // Step 3: Check classification overrides (CLASSIFY rules)
    // Step 4: Whitelist check (reject if not leaf/group)
    // Step 5: Nested leaf constraint (reject button inside button)
    // Step 6: Compute ID (AutomationId > Name > Type+Hash)
    // Step 7: Exclusion check (EXCLUDE rules)
    // Step 8: Modal type detection (Window, Popup, PopupRoot)
    // Step 9: Modal override (from classification)
    // Step 10: Non-modal group nesting validation
    // Step 11: Create node with all computed values
}
```

**Key Insight:** HierarchicalPath computed at Step 2, used at Step 7 (exclusion), then passed to constructor.

---

### 2. Modal Stack Validation

```csharp
private static void OnModalGroupOpened(NavNode modalNode) {
    // Validate linear chain: new modal MUST be descendant of current top
    if (_activeModalStack.Count > 0) {
        var currentTop = _activeModalStack[_activeModalStack.Count - 1];
        if (!IsDescendantOf(modalNode, currentTop)) {
            Debug.WriteLine("ERROR: Modal not descendant of top!");
            return; // Reject!
        }
    }
    _activeModalStack.Add(modalNode);
}
```

---

### 3. Popup Bridging

```csharp
private static void LinkToParent(NavNode childNode, FrameworkElement childFe) {
    DependencyObject current = childFe;
    while (current != null) {
        current = VisualTreeHelper.GetParent(current);
        
        // Check if parent is a NavNode
        if (current is FrameworkElement parentFe && _nodesByElement.TryGetValue(parentFe, out var parentNode)) {
            childNode.Parent = new WeakReference<NavNode>(parentNode);
            return;
        }
        
        // Jump across Popup boundary
        if (current is Popup popup && popup.PlacementTarget != null) {
            current = popup.PlacementTarget; // Continue from PlacementTarget
        }
    }
}
```

---

## ?? Known Issues & Solutions

### Issue 1: Gen 2 GC Deadlock (SOLVED)

**Symptom:** UI freezes permanently when memory reaches ~1GB after toggling overlay 7 times.

**Root Cause:**
1. Old design disposed/recreated overlay on each toggle
2. 7 dead overlay instances accumulated
3. Gen 2 GC triggered at 1GB
4. Window finalizers tried to close windows via Dispatcher
5. Dispatcher blocked by GC
6. Circular wait: GC ? finalizers ? Dispatcher ? GC ? **DEADLOCK**

**Solution:**
```csharp
// 1. Persistent overlay (reuse instead of recreate)
if (_debugMode) {
    _overlay?.Hide(); // Just hide, don't dispose
}

// 2. Suppress finalizer in Dispose()
public void Dispose() {
    GC.SuppressFinalize(this); // ? Prevents finalizer from running during GC
    // ... cleanup code
}
```

---

### Issue 2: NavForest Not Tracking Overlay (NOT AN ISSUE)

**Symptom:** HighlightOverlay rectangles not appearing in NavNode list.

**Root Cause:** Canvas and Rectangle are **not whitelisted** ? filtered out at Step 4 (whitelist check).

**Solution:** Not needed! Overlay elements should NOT be tracked.

**Safety Net:** Exclusion rule `"EXCLUDE: *:HighlightOverlay > **"` acts as redundant protection.

---

## ?? Pattern Syntax Reference

### Basic Syntax

```
Name:Type               Match specific name and type
*                       Match any name OR type (single wildcard)
*:Button                Match any name, type = Button
MyButton:*              Match name = MyButton, any type
**                      Match 0+ elements (any depth)
***                     Match 1+ elements (at least one ancestor away)
>                       Parent-child separator
```

### Rule Types

```
EXCLUDE: pattern                    Skip element from navigation
CLASSIFY: pattern => properties     Override classification
```

### Classification Properties

```
role=leaf|group|dual    Override element role
modal=true|false        Override modal behavior
priority=10             Rule priority (higher = applied first)
```

### Examples

```csharp
// Exclude main menu from navigation
"EXCLUDE: Window:MainWindow > ** > PART_Menu:ModernMenu"

// Force SelectCarDialog to be a modal group
"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true"

// Exclude all descendants of HistoricalTextBox
"EXCLUDE: ** > *:HistoricalTextBox > **"

// Exclude HighlightOverlay and all children (safety net)
"EXCLUDE: *:HighlightOverlay > **"
```

---

## ?? Design Philosophy Summary

1. **Observe, Don't Predict:** React to actual UI state, not guessed behavior
2. **Whitelist Everything:** Performance through explicit tracking
3. **Events Over Polling:** NavForest emits events, NavMapper reacts
4. **Weak References Everywhere:** Prevent memory leaks in Parent/Child links
5. **Compute Once, Store Forever:** HierarchicalPath computed at creation
6. **Popup Bridging is Critical:** Jump across Popup boundaries via PlacementTarget
7. **Modal Stack = Linear Chain:** Validate descendant relationship
8. **Persistent Resources:** Reuse windows/objects instead of recreate
9. **Finalizers Are Dangerous:** Always suppress when implementing Dispose()

---

**To onboard a new thread:**
> "Read `AcManager/UiObserver/DESIGN.md` - it explains the navigation system architecture, key decisions, and why things work the way they do."
