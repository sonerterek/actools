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
?  - Subscribes to Observer events ONLY                   ?
?  - Manages modal context stack (scope + focus)          ?
?  - Handles keyboard input (Ctrl+Shift+Arrow navigation) ?
?  - Initializes focus on modal open (complete info!)     ?
?  - Filters candidates by modal scope                    ?
?  - Spatial navigation algorithm                         ?
?  - Focus highlighting overlay                           ?
???????????????????????????????????????????????????????????
                           ? events (Observer ? Navigator)
                           ? • ModalGroupOpened
                           ? • ModalGroupClosed
                           ? • WindowLayoutChanged
???????????????????????????????????????????????????????????
?                      Observer                           ?
?  (Discovery Engine - Silent Scanning)                   ?
?  - Auto-discovers PresentationSource roots              ?
?  - Hooks existing windows for lifecycle management      ?
?  - Scans visual trees SILENTLY (no per-node events!)    ?
?  - Creates NavNodes via factory pattern                 ?
?  - Builds Parent/Child relationships                    ?
?  - Tracks Popup?PlacementTarget bridges                 ?
?  - Emits events ONLY for modal lifecycle & layout       ?
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
- **Event-Driven Navigation:** Navigator reacts ONLY to Observer's events, never calls Observer directly

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

**?? CRITICAL: Window Added to Group Types**

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

### 4. Clean Separation: Observer vs Navigator

**Decision:** Observer handles ALL discovery and lifecycle management. Navigator ONLY reacts to Observer's events.

**Why:**
- **Single Responsibility:** Observer = discovery, Navigator = navigation/overlay
- **No Leaky Abstractions:** Navigator never calls `Observer.RegisterRoot()` or manages windows
- **Testability:** Can test Observer without Navigator (and vice versa)
- **Maintainability:** Discovery changes stay in Observer, navigation changes stay in Navigator

**Observer's Responsibilities:**
- ? Auto-discover PresentationSource roots
- ? Hook existing windows for lifecycle events
- ? Re-register roots when window layout changes
- ? Emit `WindowLayoutChanged` event for overlay synchronization

**Navigator's Responsibilities:**
- ? Subscribe to Observer events (`ModalGroupOpened`, `ModalGroupClosed`, `WindowLayoutChanged`)
- ? Manage modal context stack
- ? Handle keyboard input
- ? Update overlay position (react to `WindowLayoutChanged`)
- ? Never call `Observer.RegisterRoot()` or hook windows

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

### 8. Minimal Public API Surface

**Decision:** Only `Navigator.Initialize()` is truly public. Most APIs are `internal` or `private`.

**Why:**
- **Encapsulation:** External code should only initialize the system, not manage it
- **Simplicity:** Fewer entry points = fewer potential misuses
- **Maintainability:** Internal refactoring doesn't affect external callers

**Public API (Navigator):**
- `Initialize()` - Main initialization *(called from AppUi.cs)*
- `MoveInDirection(dir)` - Keyboard navigation
- `ActivateFocusedNode()` - Activate current focus
- `ExitGroup()` - Exit current modal

**Internal API (Navigator):**
- `FocusChanged` event - Internal subscribers only
- `GetAllNavNodes()` - Internal queries only

**Public API (Observer):**
- `Initialize()` - Main initialization (auto-hooks windows)
- `RegisterRoot(...)` / `UnregisterRoot(...)` - Manual root management
- `SyncRoot(...)` - Manual sync
- `GetAllNavNodes()` - Query API (used by Navigator)
- `TryGetNavNode(...)` - Lookup API (used by NavNode validation)
- `ModalGroupOpened` / `ModalGroupClosed` events - Modal lifecycle
- `WindowLayoutChanged` event - Layout change notifications

**Architectural Note:** All other classes (NavNode, NavPathFilter, HighlightOverlay, NavContext) are `internal` - never exposed outside UiObserver directory.

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

**Purpose:** Scans visual trees silently, creates NavNodes, builds hierarchy, emits events only for modal lifecycle and layout changes.

**Key Methods:**
- `Initialize()` - Auto-discover roots + hook existing windows (self-sufficient!)
- `HookExistingWindow(Window w)` - Hook layout events for window (private)
- `OnWindowLayoutChanged(object, EventArgs)` - Re-register root + fire event
- `RegisterRoot(fe)` - Register a PresentationSource root for scanning
- `UnregisterRoot(fe)` - Cleanup when root is unloaded
- `SyncRoot(root)` - Rescan visual tree and sync with tracked nodes (silent operation)
- `LinkToParent(node, fe)` - Build Parent/Child relationships (handles Popup bridging)
- `UnlinkNode(node)` - Clean up Parent/Child links on removal
- `TryCreateNavNodeForElement(fe)` - Create node for dynamically loaded elements (silent)
- `HandleRemovedNodeModalTracking(node)` - Fire ModalGroupClosed for removed modals

**Key Events:**
- `ModalGroupOpened` - Modal opened (Window, PopupRoot) - ALL children already discovered
- `ModalGroupClosed` - Modal closed
- `WindowLayoutChanged` - Window layout changed (position, size, loaded state) - for overlay sync

**Key Data Structures:**
- `_nodesByElement` - ConcurrentDictionary<FrameworkElement, NavNode> (source of truth)
- `_presentationSourceRoots` - Map PresentationSource ? root element
- `_rootIndex` - Map root ? all elements in that tree
- `_pendingSyncs` - Debouncing for layout changes

**Design Patterns:**
- **Event-Driven Architecture:** Emits events only for modal lifecycle & layout (not per-node!)
- **Silent Discovery:** Nodes are tracked internally without firing events
- **Debouncing:** Coalesces multiple layout changes into single scan
- **Weak References:** Parent/Child links use WeakReference to avoid memory leaks
- **Self-Sufficient:** Manages its own lifecycle, doesn't require Navigator's help

**Architectural Decision:** Observer is completely self-sufficient. It hooks windows, manages roots, and fires events. Navigator never calls Observer for discovery purposes, only subscribes to events.

---

### Navigator.cs - Navigation Logic

**Purpose:** Subscribes to Observer events, manages modal context stack, handles keyboard input, spatial navigation, overlay management.

**Key Methods:**
- `Initialize()` - One-time setup (subscribes to Observer events, registers keyboard handler)
- `MoveInDirection(dir)` - Find best candidate in direction using spatial algorithm
- `ActivateFocusedNode()` - Activate current focus
- `ExitGroup()` - Close topmost modal
- `TryInitializeFocusIfNeeded()` - Initialize focus when modal opens (complete candidate list!)
- `SetFocusVisuals(node)` - Update overlay highlight

**Key Data:**
- `_modalContextStack` - List<NavContext> (modal scope + focused node per context)
- `CurrentContext` - Helper property for top of stack
- `_overlay` - HighlightOverlay for visual feedback

**Event Handlers (reactive architecture):**
- `OnModalGroupOpened` - Push new context, initialize focus with complete info
- `OnModalGroupClosed` - Pop context, restore previous focus
- `OnWindowLayoutChanged` - Update overlay position (no Observer calls!)

**Internal API:**
- `FocusChanged` event - Internal subscribers only (not public)

**Design Patterns:**
- **Observer Pattern:** Subscribes to Observer events only (never calls Observer)
- **Command Pattern:** Keyboard shortcuts trigger navigation commands
- **Strategy Pattern:** Spatial navigation algorithm (directional cost calculation)
- **Single-Pass Initialization:** Focus initialized once per modal with complete information
- **Reactive Architecture:** All actions are reactions to Observer's events

**Architectural Decision:** Navigator is purely reactive. It never calls `Observer.RegisterRoot()` or manages windows. It only subscribes to events and updates its own state (modal stack, focus) and visual feedback (overlay).

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
- **Observer Pattern:** Updates when focus changes (via Navigator)

---

### NavContext.cs - Context Data Structure

**Purpose:** Bundle modal scope with focused node (atomic state management).

**Key Properties:**
- `ModalNode` - The modal defining this context's scope (Window, Popup, PopupRoot)
- `FocusedNode` - Currently focused node within this scope (or null)

**Design Pattern:**
- **Value Object:** Immutable modal node, mutable focus

---

## ? Design Philosophy Summary

1. **Observe, Don't Predict:** React to actual UI state, not guessed behavior
2. **Whitelist Everything:** Performance through explicit tracking
3. **Modal-Only Events:** Silent node discovery, events only for modal lifecycle & layout
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
16. **? Minimal Public API:** Only expose what external code needs (`Initialize()` + navigation commands)
17. **? Clean Separation:** Observer handles discovery, Navigator handles navigation - NO overlap!

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

### 2. Observer.Initialize() - Self-Sufficient Discovery

```csharp
internal static void Initialize() {
    // Register global event handlers
    EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, ...);
    EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, ...);
    EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, ...);
    
    // ? NEW: Hook all existing windows (self-sufficient!)
    if (Application.Current != null) {
        Application.Current.Dispatcher.BeginInvoke(new Action(() => {
            foreach (Window w in Application.Current.Windows) {
                HookExistingWindow(w);
            }
        }), DispatcherPriority.ApplicationIdle);
    }
}

private static void HookExistingWindow(Window w) {
    // Register root for discovery
    var content = w.Content as FrameworkElement;
    if (content != null) {
        RegisterRoot(content);
    }
    
    // Hook layout events to fire WindowLayoutChanged
    w.Loaded += OnWindowLayoutChanged;
    w.LayoutUpdated += OnWindowLayoutChanged;
    w.LocationChanged += OnWindowLayoutChanged;
    w.SizeChanged += OnWindowLayoutChanged;
}

private static void OnWindowLayoutChanged(object sender, EventArgs e) {
    // Re-register root to handle dynamic content changes
    if (sender is Window w) {
        var content = w.Content as FrameworkElement;
        if (content != null) {
            RegisterRoot(content);
        }
    }
    
    // Notify subscribers (Navigator's overlay)
    WindowLayoutChanged?.Invoke(sender, e);
}
```

**Key Insight:** Observer is completely self-sufficient. It manages its own lifecycle and fires events. Navigator never calls Observer for discovery purposes.

---

### 3. Navigator.Initialize() - Pure Subscriber

```csharp
public static void Initialize() {
    // Initialize navigation rules
    InitializeNavigationRules();

    // ? Subscribe to Observer events ONLY
    Observer.ModalGroupOpened += OnModalGroupOpened;
    Observer.ModalGroupClosed += OnModalGroupClosed;
    Observer.WindowLayoutChanged += OnWindowLayoutChanged;  // ? NEW!
    
    // Startup Observer (it will hook windows itself)
    Observer.Initialize();

    // Register keyboard handler
    EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, ...);

    // Initialize overlay
    if (Application.Current != null) {
        Application.Current.Dispatcher.BeginInvoke(new Action(() => {
            EnsureOverlay();
        }), DispatcherPriority.ApplicationIdle);
    }
}

private static void OnWindowLayoutChanged(object sender, EventArgs e) {
    // ? Only update overlay (no Observer calls!)
    if (CurrentContext?.FocusedNode != null) {
        UpdateFocusRect(CurrentContext.FocusedNode);
    }
}
```

**Key Insight:** Navigator is purely reactive. All its actions are reactions to Observer's events. It never calls `Observer.RegisterRoot()` or manages windows.

---

### 4. Modal Context Stack Management

```csharp
private static void OnModalGroupOpened(NavNode modalNode) {
    // Validate linear chain: new modal MUST be descendant of current top
    if (_modalContextStack.Count > 0) {
        var currentTop = CurrentContext.ModalNode;
        if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
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

### 5. Optimistic Modal Validation

**?? ARCHITECTURAL CHANGE:** Modified modal validation to handle timing-sensitive initialization.

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

### 6. Modal-Driven Focus Initialization

```csharp
private static void TryInitializeFocusIfNeeded() {
    // No context yet? Wait for first modal (MainWindow)
    if (CurrentContext == null) return;
    
    // Already has focus? Nothing to do
    if (CurrentContext.FocusedNode != null) return;
    
    // Find first navigable node in current scope (top-left preference)
    // All nodes are ALREADY discovered - complete candidate list!
    var candidates = GetCandidatesInScope()
        .Where(n => CurrentContext.ModalNode == null || IsDescendantOf(n, CurrentContext.ModalNode))
        .OrderBy(n => {
            // Prefer top-left (Y weight > X weight)
            var center = n.GetCenterDip();
            if (!center.HasValue) return double.MaxValue;
            return center.Value.X + center.Value.Y * 10000.0;
        })
        .ToList();
    
    var firstNode = candidates.FirstOrDefault()?.Node;
    if (firstNode != null) {
        CurrentContext.FocusedNode = firstNode;
        SetFocusVisuals(firstNode);
        Debug.WriteLine($"Initialized focus in '{CurrentContext.ModalNode.SimpleName}' -> '{firstNode.SimpleName}'");
    }
}
```

**Trigger Points (modal lifecycle only):**
- `OnModalGroupOpened` ? New context created, initialize focus with complete node list
- `OnModalGroupClosed` ? Previous context may need focus if it had none

**Selection Strategy:** Top-left position preferred (Y-coordinate has 10,000× weight)

**Key Improvement:** Unlike the old per-node event model, this executes **once per modal** with a **complete candidate list**, ensuring optimal selection every time.

---

## ?? Known Issues & Solutions

### Issue 1: ? SOLVED - ScrollBar Hijacking Focus

**Problem:** ScrollBars inside dropdown menus were winning focus selection because they had lower Y-coordinates than menu items.

**Why:** Top-left preference algorithm (Y weight = 10,000×) made ScrollBar (Y=0) beat MenuItem (Y=50).

**Solution:** Added exclusion rules to filter ScrollBars from navigation:
```csharp
"EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
"EXCLUDE: *:PopupRoot > ** > *:BetterScrollBar",
```

**Impact:** Menu items now correctly get focus when dropdowns open.

---

### Issue 2: ? SOLVED - MainWindow Not Modal

**Problem:** Root window wasn't being discovered, so modal stack remained empty and navigation failed.

**Why:** Window wasn't in `_groupTypes` whitelist, so it was ignored during scanning.

**Solution:** Added `typeof(Window)` to `_groupTypes`:
```csharp
private static readonly HashSet<Type> _groupTypes = new HashSet<Type>
{
    typeof(Window),  // ? Added this
    // ...rest
};
```

**Impact:** Root modal context now initializes correctly, navigation system functional.

---

### Issue 3: ? SOLVED - HighlightOverlay GC Deadlock

**Problem:** App freezes during Gen 2 GC, debugger shows circular wait between GC thread and Dispatcher thread.

**Why:** Multiple overlay instances created ? finalizers queued ? Gen 2 GC runs finalizers ? finalizers try to use Dispatcher ? circular deadlock.

**Solution 1:** Reuse single overlay instance (Hide/Show instead of Dispose/Create)
**Solution 2:** Add `GC.SuppressFinalize(this)` in `Dispose()` method

**Impact:** No more freezes, overlay works reliably.

---

### Issue 4: ? SOLVED - PopupRoot Modal Validation Failure

**Problem:** PopupRoot modals were rejected during validation because `Parent == null` when event fired.

**Why:** `ModalGroupOpened` event fires immediately after node creation, before `LinkToParent()` runs.

**Solution:** Optimistic validation - accept modals with no parent yet:
```csharp
// Only reject if HAS parent but WRONG parent
if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
    return; // Reject
}
// If Parent == null, accept (will be linked immediately after)
```

**Impact:** Dropdown menus now navigate correctly.

---

### Issue 5: ? SOLVED - Navigator Managing Windows (Architectural Violation)

**Problem:** Navigator was calling `Observer.RegisterRoot()` and hooking windows, violating separation of concerns.

**Why:** Initial implementation didn't fully separate discovery (Observer) from navigation (Navigator).

**Solution:** Moved window hooking to `Observer.Initialize()` and added `WindowLayoutChanged` event:
```csharp
// Observer now hooks windows itself
private static void HookExistingWindow(Window w) {
    RegisterRoot(w.Content);  // Discovery
    w.Loaded += OnWindowLayoutChanged;  // Fire event for overlay
}

// Navigator only subscribes to event
Observer.WindowLayoutChanged += OnWindowLayoutChanged;
```

**Impact:** 
- Clean architectural separation achieved
- Observer is self-sufficient (doesn't need Navigator's help)
- Navigator is purely reactive (only subscribes to events)
- No more `Observer.RegisterRoot()` calls from Navigator

---

## ?? Pattern Syntax Reference

### Exclusion Rules

```csharp
// Exclude HighlightOverlay and all descendants
"EXCLUDE: *:HighlightOverlay > **"

// Exclude ModernMenu inside MainWindow
"EXCLUDE: Window:MainWindow > WindowBorder:Border > ** > PART_Menu:ModernMenu"

// Exclude ScrollBars inside popups
"EXCLUDE: *:PopupRoot > ** > *:ScrollBar"
```

### Classification Rules

```csharp
// Make SelectCarDialog a modal group
"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true"

// Make SettingsPanel a non-modal group
"CLASSIFY: ** > SettingsPanel:Border => role=group; modal=false"

// Override ComboBox modality
"CLASSIFY: *** > QuickFilter:ComboBox => modal=false"
```

### Wildcard Reference

| Wildcard | Meaning | Example |
|----------|---------|---------|
| `*` | Match any single segment | `*:Button` = any Button |
| `**` | Match 0+ segments | `** > *:Button` = Button at any depth |
| `***` | Match 1+ segments | `*** > *:Button` = Button with at least 1 ancestor |
| `>` | Parent-child separator | `A:Panel > B:Button` = Button B inside Panel A |

### Segment Format

```
Name:Type
```

- **Name:** Element's `fe.Name` property (use `*` for any name)
- **Type:** Element's type name (use `*` for any type)

**Examples:**
- `SaveButton:Button` - Button named "SaveButton"
- `*:Button` - Any Button (any name)
- `PART_Content:*` - Any element named "PART_Content"
- `*:*` - Any element (same as `*`)

---

## ?? Debugging Tips

### 1. Enable Verbose Debug Output

Set `VERBOSE_DEBUG = true` in `NavNode.cs`:

```csharp
private const bool VERBOSE_DEBUG = true;
```

Output example:
```
[NavNode] Evaluating: Button 'SaveButton'
[NavNode]   -> Type-based: IsGroup=false, IsLeaf=true
[NavNode]   -> CREATED: Button Id=N:SaveButton
[NavNode]   -> Final: IsGroup=false, IsModal=false
[NavNode]   -> Path: MainWindow:Window > SaveButton:Button
```

---

### 2. Toggle Debug Overlay

Press **Ctrl+Shift+F12** to show all navigable elements with colored rectangles:
- **Orange:** Leaf nodes (navigable targets)
- **Gray:** Group nodes (containers)

Press **Ctrl+Shift+F11** to show ALL discovered nodes (including out-of-scope).

---

### 3. Enable Consistency Validation (DEBUG builds only)

Consistency validation runs automatically in DEBUG builds when toggling the overlay:

```csharp
#if DEBUG
    ValidateNodeConsistency();  // Runs on Ctrl+Shift+F12/F11
#endif
```

Output example:
```
========== NavNode Consistency Check ==========
Total nodes to validate: 47
Dead visuals: 0
Parent mismatches: 0
Child consistency errors: 0
Orphaned nodes: 0
Circular references: 0
? All nodes are CONSISTENT with visual tree!
```

---

### 4. Watch Modal Stack State

Modal stack is logged during overlay toggle (Ctrl+Shift+F12/F11):

```
Modal Stack (2 contexts):
  [0] MainWindow:Window > LayoutRoot:DockPanel [focused: Button1:Button]
  [1] Popup:Popup [focused: MenuItem3:MenuItem]
```

---

### 5. Watch Focus Selection

`TryInitializeFocusIfNeeded()` logs detailed candidate scoring:

```
[Navigator] Finding first navigable in scope 'PopupRoot'...
  Candidate: MenuItem @ path...
    Center: 150.0,50.0 | Score: 500150.0
  Candidate: ScrollBar @ path...
    Center: 450.0,0.0 | Score: 450.0
  ? WINNER: ScrollBar (score: 450.0)
```

This helped identify the ScrollBar hijacking issue.

---

## DPI Scaling & Coordinate Systems

### WPF Coordinate Systems
- **DIP (Device Independent Pixels):** WPF's native unit (96 DPI base)
- **Device Pixels:** Physical screen pixels (scaled by Windows DPI setting)
- **Screen Coordinates:** Absolute position on screen

### Transformation Rules
1. **Overlay positioning:** Uses DIP (screen-absolute)
2. **Mouse input (Win32):** Requires device pixels
3. **Coordinate calculations:** Always done in DIP first
4. **Final conversion:** `TransformToDevice` before Win32 API calls

### WPF Popup Timing
Popup positioning happens AFTER `DispatcherPriority.ApplicationIdle`:
- `Loaded` event: Popup created, temporary position
- `Measure/Arrange`: Layout calculated
- `ApplicationIdle`: Pending UI updates processed
- **Popup positioning:** PlacementTarget calculations, screen bounds checks ← HAPPENS HERE
- Use 50ms timer after ApplicationIdle to get final coordinates

### Critical Methods
- `GetCenterDip()`: Returns DIP (screen-absolute) - for overlays
- `MoveMouseToFocusedNode()`: DIP → device pixels → mouse input
- `UpdateFocusRect()`: Device pixels → DIP for overlay

---

## ?? Further Reading

- **CHANGELOG.md** - Chronological history of architectural changes
- **NavNode.cs** - Factory pattern + type classification
- **Observer.cs** - Discovery engine implementation
- **Navigator.cs** - Navigation logic + modal stack management
- **NavPathFilter.cs** - Pattern matching implementation

---

**Last Updated:** After architectural cleanup (Observer self-sufficient, Navigator purely reactive, WindowLayoutChanged event added)
