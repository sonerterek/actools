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
?                      Navigator                         ?
?  (Navigation Logic & Modal Stack Management)            ?
?  - Subscribes to Observer MODAL events ONLY             ?
?  - Manages modal context stack (scope + focus)          ?
?  - Handles keyboard input (Ctrl+Shift+Arrow navigation) ?
?  - Initializes focus on modal open (complete info!)     ?
?  - Filters candidates by modal scope                    ?
?  - Spatial navigation algorithm                         ?
?  - Focus highlighting (HighlightOverlay)                ?
???????????????????????????????????????????????????????????
                           ? events (modal lifecycle only)
                           ? (ModalGroupOpened, ModalGroupClosed)
???????????????????????????????????????????????????????????
?                      Observer                           ?
?  (Discovery Engine - Silent Scanning)                   ?
?  - Auto-discovers PresentationSource roots              ?
?  - Scans visual trees SILENTLY (no per-node events!)    ?
?  - Creates NavNodes via factory pattern                 ?
?  - Builds Parent/Child relationships                    ?
?  - Tracks Popup?PlacementTarget bridges                 ?
?  - Emits events ONLY for modal lifecycle                ?
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

**Key Architectural Principle:** 
- **Silent Discovery:** Observer discovers nodes without firing events
- **Modal-Only Events:** Only modal lifecycle changes trigger events
- **Complete Information:** When ModalGroupOpened fires, ALL nodes in the modal scope are already discovered
- **Single Focus Attempt:** Focus initialization happens once per modal with complete candidate list

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
**Group Types:** Popup, ListBox, TabControl, DataGrid, **Window** (see below)

**? CRITICAL: Window Added to Group Types**

**What changed:** `typeof(Window)` was added to `_groupTypes` whitelist in `NavNode.cs`.

**Why it was needed:** Window wasn't being discovered as a NavNode, so MainWindow couldn't become the root modal context. This caused `CurrentContext` to remain `null`, breaking the entire navigation system.

**Impact:**
- Root window discovery now works correctly
- Modal stack initialization succeeds
- First architectural fix that made the system functional

**Code:**
```csharp
private static readonly HashSet<Type> _groupTypes = new HashSet<Type>
{
    typeof(Window),        // Root modal: application windows (MainWindow, dialogs, etc.)
    typeof(Popup),         // Pure container: never directly navigable
    // ...rest of types
};
```

---

### 3. Modal Context Stack = Linear Chain with Focus

**Decision:** Modal stack is a **List<NavContext>** where each context bundles **modal scope + focused node**.

**Why:**
- **Atomic State:** Modal scope and focus are inseparable - bundling prevents desync
- **No Special Cases:** Root window (MainWindow) is just the first modal context
- **Stack Never Empty:** After initialization, stack always has ?1 context (root)
- **Automatic Focus Restore:** When modal closes, previous context's focus is automatically restored

**Structure:**
```csharp
class NavContext {
    NavNode ModalNode;    // Scope root (Window, Popup, PopupRoot)
    NavNode FocusedNode;  // Currently focused node in this scope (or null)
}
```

**Example Valid Stack:**
```
[0] NavContext(MainWindow, Button1)        ? Root context, always present
[1] NavContext(Popup, MenuItem3)           ? Dropdown menu opened
[2] NavContext(PopupRoot, SubMenuItem5)    ? Submenu opened
```

**Stack Lifecycle:**
```
User opens dropdown:
  OnModalGroupOpened(Popup)
  ? Push NavContext(Popup, null)
  ? TryInitializeFocusIfNeeded() ? finds MenuItem3 ? sets focus

User closes dropdown:
  OnModalGroupClosed(Popup)
  ? Pop context
  ? Restore focus from previous context (Button1) ?
```

**Invariant:** `_modalContextStack.Count >= 1` at all times after first modal discovered

---

### 4. Opportunistic Focus Initialization

**Decision:** Focus is initialized **lazily and opportunistically** when navigable nodes appear.

**Why:**
- **Handles Bulk Discovery:** After bulk scan completes (`RootChanged`), first navigable node gets focus
- **Handles Dynamic Loading:** As UI loads incrementally (lazy tabs, etc.), first navigable node gets focus
- **Modal-Aware:** Each modal context initializes focus independently when it opens

**Triggers:**
1. `OnNavNodeAdded` ? Check if current context needs focus
2. `OnRootChanged` ? Check if current context needs focus (after bulk scan)
3. `OnModalGroupOpened` ? New context created with null focus, then checked

**Algorithm:**
```csharp
TryInitializeFocusIfNeeded() {
    if (CurrentContext == null) return;          // No context yet
    if (CurrentContext.FocusedNode != null) return;  // Already has focus
    
    var firstNode = FindFirstNavigableInScope(CurrentContext.ModalNode);
    if (firstNode != null) {
        CurrentContext.FocusedNode = firstNode;
        SetFocusVisuals(firstNode);
    }
}
```

**Selection Strategy:** Top-left preference (Y-coordinate weighted higher than X)

**? Debug Output:**:

When diagnosing focus selection issues, `TryInitializeFocusIfNeeded()` logs detailed candidate scoring:

```
[Navigator] Finding first navigable in scope 'PopupRoot'...
  Candidate: MenuItem @ path...
    Center: 150.0,50.0 | Score: 500150.0
  Candidate: ScrollBar @ path...
    Center: 450.0,0.0 | Score: 450.0
  ? WINNER: ScrollBar (score: 450.0)
```

This debug output was instrumental in discovering the ScrollBar hijacking issue (see below).

---

### 5. HierarchicalPath = Computed Once, Stored Forever

**Decision:** Compute `HierarchicalPath` in `NavNode.CreateNavNode()` and pass to constructor.

**Why:**
- **Exclusion Check:** Needed BEFORE node creation to filter out unwanted elements
- **Performance:** Compute once during creation, not repeatedly
- **Consistency:** Path never changes after creation (element's position in tree is fixed)

**Format:** `"WindowName:Window > PanelName:StackPanel > ButtonName:Button"`

**Key Insight:** Path includes ALL ancestors in visual tree, not just NavNode ancestors.

---

### 6. Popup?PlacementTarget Bridging

**Decision:** `Observer.LinkToParent()` walks through `Popup.PlacementTarget` to bridge visual tree gaps.

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

### 7. Persistent HighlightOverlay (Not Dispose/Recreate)

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

### Observer.cs - Discovery Engine

**Purpose:** Scans visual trees silently, creates NavNodes, builds hierarchy, emits modal lifecycle events only.

**Key Methods:**
- `RegisterRoot(fe)` - Register a PresentationSource root for scanning
- `SyncRoot(root)` - Rescan visual tree and sync with tracked nodes (silent operation)
- `LinkToParent(node, fe)` - Build Parent/Child relationships (handles Popup bridging)
- `TryCreateNavNodeForElement(fe)` - Create node for dynamically loaded elements (silent)

**Key Events (modal lifecycle only):**
- `ModalGroupOpened` - Modal opened (Window, PopupRoot) - ALL children already discovered
- `ModalGroupClosed` - Modal closed

**Key Data Structures:**
- `_nodesByElement` - ConcurrentDictionary<FrameworkElement, NavNode> (source of truth)
- `_presentationSourceRoots` - Map PresentationSource ? root element
- `_rootIndex` - Map root ? all elements in that tree
- `_pendingSyncs` - Debouncing for layout changes

**Design Patterns:**
- **Event-Driven Architecture:** Emits events only for modal lifecycle (not per-node!)
- **Silent Discovery:** Nodes are tracked internally without firing events
- **Debouncing:** Coalesces multiple layout changes into single scan
- **Weak References:** Parent/Child links use WeakReference to avoid memory leaks

**Architectural Decision:** Per-node events (NavNodeAdded/NavNodeRemoved) were removed in favor of modal-only events. This simplifies the architecture and provides complete information to Navigator when modals open.

---

### Navigator.cs - Navigation Logic

**Purpose:** Subscribes to modal lifecycle events, manages modal context stack, handles keyboard input, spatial navigation.

**Key Methods:**
- `MoveInDirection(dir)` - Find best candidate in direction using spatial algorithm
- `ActivateFocusedNode()` - Activate current focus
- `ExitGroup()` - Close topmost modal
- `TryInitializeFocusIfNeeded()` - Initialize focus when modal opens (complete candidate list!)
- `FindFirstNavigableInScope(scopeNode)` - Find first navigable element (top-left preference)
- `SetFocusVisuals(node)` - Update overlay highlight

**Key Data:**
- `_modalContextStack` - List<NavContext> (modal scope + focused node per context)
- `CurrentContext` - Helper property for top of stack
- `_overlay` - HighlightOverlay for visual feedback

**Event Handlers (modal lifecycle only):**
- `OnModalGroupOpened` - Push new context, initialize focus with complete info
- `OnModalGroupClosed` - Pop context, restore previous focus
- `OnPreviewKeyDown` - Handle Ctrl+Shift+Arrow keys

**Design Patterns:**
- **Observer Pattern:** Subscribes to Observer modal lifecycle events only
- **Command Pattern:** Keyboard shortcuts trigger navigation commands
- **Strategy Pattern:** Spatial navigation algorithm (directional cost calculation)
- **Single-Pass Initialization:** Focus initialized once per modal with complete information

**Architectural Decision:** Removed subscriptions to NavNodeAdded/NavNodeRemoved events. Navigator now reacts only to modal lifecycle changes, receiving complete information about all nodes in the modal scope at once. This eliminates redundant focus attempts and ensures optimal candidate selection.

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
- **Singleton-ish:** Navigator keeps one persistent instance
- **Observer Pattern:** Updates when focus changes

---

## ? Design Philosophy Summary

1. **Observe, Don't Predict:** React to actual UI state, not guessed behavior
2. **Whitelist Everything:** Performance through explicit tracking
3. **Modal-Only Events:** Silent node discovery, events only for modal lifecycle
4. **Complete Information:** Focus selection sees all candidates at once (not incremental)
5. **Weak References Everywhere:** Prevent memory leaks in Parent/Child links
6. **Compute Once, Store Forever:** HierarchicalPath computed at creation
7. **Popup Bridging is Critical:** Jump across Popup boundaries via PlacementTarget
8. **Modal Context = Scope + Focus:** Atomic bundling prevents desync
9. **Stack Never Empty:** Root context always present after initialization
10. **Single-Pass Initialization:** Focus initialized once per modal with complete candidate list
11. **Persistent Resources:** Reuse windows/objects instead of recreate
12. **Finalizers Are Dangerous:** Always suppress when implementing Dispose()
13. **? Optimistic Validation:** Accept unlinked modals, reject only provably wrong hierarchies
14. **? Pattern-Based Exclusion:** Context-aware filtering better than global type rules
15. **? Silent Discovery:** Node tracking is internal, events signal user-facing state changes only

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

### 2. Modal Context Stack Management

```csharp
private static void OnModalGroupOpened(NavNode modalNode) {
    // Validate linear chain: new modal MUST be descendant of current top
    if (_modalContextStack.Count > 0) {
        var currentTop = CurrentContext.ModalNode;
        if (!IsDescendantOf(modalNode, currentTop)) {
            Debug.WriteLine("ERROR: Modal not descendant of top!");
            return; // Reject!
        }
    }
    
    // Push new context
    var newContext = new NavContext(modalNode, focusedNode: null);
    _modalContextStack.Add(newContext);
    
    // Clear old focus visuals
    _overlay?.HideFocusRect();
    
    // Try to initialize focus in new context
    TryInitializeFocusIfNeeded();
}

private static void OnModalGroupClosed(NavNode modalNode) {
    // Pop context
    if (_modalContextStack.Count > 0 
        && CurrentContext.ModalNode.HierarchicalPath == modalNode.HierarchicalPath) {
        _modalContextStack.RemoveAt(_modalContextStack.Count - 1);
    }
    
    // Restore focus from previous context
    if (CurrentContext != null && CurrentContext.FocusedNode != null) {
        SetFocusVisuals(CurrentContext.FocusedNode);
    } else {
        // No previous focus - try to initialize
        TryInitializeFocusIfNeeded();
    }
}
```

---

### 3. Optimistic Modal Validation

**? ARCHITECTURAL CHANGE:** Modified modal validation to handle timing-sensitive initialization.

**Problem:** PopupRoot fires `ModalGroupOpened` event BEFORE `LinkToParent()` runs, so `modalNode.Parent` is `null` when validation occurs. The original strict validation rejected these modals, breaking popup navigation.

**Solution:** Accept modals with no parent optimistically, reject only if they have a parent but it's the wrong one:

```csharp
private static void OnModalGroupOpened(NavNode modalNode) {
    if (_modalContextStack.Count > 0) {
        var currentTop = CurrentContext.ModalNode;
        
        // ? NEW: Only reject if HAS parent but WRONG parent
        if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
            Debug.WriteLine($"ERROR: Modal {modalNode.SimpleName} not descendant!");
            return; // Reject invalid hierarchy
        }
        
        // If Parent == null, accept optimistically
        // (Observer will link it immediately after this event)
    }
    
    // Push new context
    var newContext = new NavContext(modalNode, focusedNode: null);
    _modalContextStack.Add(newContext);
    
    // Clear old focus visuals
    _overlay?.HideFocusRect();
    
    // Try to initialize focus in new context
    TryInitializeFocusIfNeeded();
}
```

**Event Sequence:**
1. PopupRoot discovered ? `ModalGroupOpened` fires (`Parent = null`)
2. Validation check passes (`Parent == null` is OK)
3. New context pushed to stack ?
4. `LinkToParent()` runs immediately after ? `Parent` set correctly
5. Validation would pass retroactively

**Risk Mitigation:** Invalid modals with wrong parent hierarchy are still rejected (have parent but not descendant of current top). Only nodes with no parent yet are accepted optimistically.

**Impact:**
- PopupRoot modals now work correctly
- Dropdown menus can be navigated
- Timing-sensitive initialization issues resolved

---

### 4. Modal-Driven Focus Initialization

```csharp
private static void TryInitializeFocusIfNeeded() {
    // No context yet? Wait for first modal (MainWindow)
    if (CurrentContext == null) return;
    
    // Already has focus? Nothing to do
    if (CurrentContext.FocusedNode != null) return;
    
    // Find first navigable node in current scope (top-left preference)
    // All nodes are ALREADY discovered - complete candidate list!
    var firstNode = FindFirstNavigableInScope(CurrentContext.ModalNode);
    if (firstNode != null) {
        CurrentContext.FocusedNode = firstNode;
        SetFocusVisuals(firstNode);
        Debug.WriteLine($"Initialized focus in '{CurrentContext.ModalNode.SimpleName}' -> '{firstNode.SimpleName}'");
    }
}

private static NavNode FindFirstNavigableInScope(NavNode scopeNode) {
    var candidates = GetCandidatesInScope()
        .Where(n => scopeNode == null || IsDescendantOf(n, scopeNode))
        .OrderBy(n => {
            // Prefer top-left (Y weight > X weight)
            var center = n.GetCenterDip();
            if (!center.HasValue) return double.MaxValue;
            return center.Value.X + center.Value.Y * 10000.0;
        })
        .ToList();
    
    return candidates.FirstOrDefault();
}
```

**Trigger Points (modal lifecycle only):**
- `OnModalGroupOpened` ? New context created, initialize focus with complete node list
- `OnModalGroupClosed` ? Previous context may need focus if it had none

**Selection Strategy:** Top-left position preferred (Y-coordinate has 10,000× weight)

**Key Improvement:** Unlike the old per-node event model, this executes **once per modal** with a **complete candidate list**, ensuring optimal selection every time.

---
