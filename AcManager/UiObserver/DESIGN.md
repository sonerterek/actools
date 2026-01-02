# Navigation System (UiObserver) - Design Document

**Version:** 2.0  
**Last Updated:** December 2024  
**Target Framework:** .NET Framework 4.5.2  
**Status:** Current implementation (reflects codebase as of Dec 2024)

---

## 📋 Table of Contents
1. [Architecture Overview](#architecture-overview)
2. [Key Design Decisions](#key-design-decisions)
3. [File Organization](#file-organization)
4. [Critical Implementation Details](#critical-implementation-details)
5. [Navigation Context System](#navigation-context-system)
6. [Interaction Mode System](#interaction-mode-system)
7. [Known Issues & Solutions](#known-issues--solutions)
8. [Pattern Syntax Reference](#pattern-syntax-reference)
9. [Debugging Guide](#debugging-guide)

---

## 🏗️ Architecture Overview

### Three-Layer "Observe and React" Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Navigator                              │
│  (Navigation Logic & Context Management)                    │
│  • Subscribes to Observer events ONLY                       │
│  • Manages context stack (RootWindow/Modal/Interaction)     │
│  • StreamDeck integration (see separate doc)                │
│  • Spatial navigation algorithm                             │
│  • Focus highlighting overlay                               │
│  • Interaction mode (slider adjustment, confirmation)       │
└─────────────────────────────────────────────────────────────┘
                           ↓ events (Observer → Navigator)
                           • ModalGroupOpened
                           • ModalGroupClosed
                           • WindowLayoutChanged
                           • NodesUpdated (batch)
┌─────────────────────────────────────────────────────────────┐
│                      Observer                               │
│  (Discovery Engine - Silent Scanning)                       │
│  • Auto-discovers PresentationSource roots                  │
│  • Hooks windows for lifecycle management                   │
│  • Scans visual trees SILENTLY (no per-node events!)        │
│  • Creates NavNodes via factory pattern                     │
│  • Builds Parent/Child relationships (for cleanup)          │
│  • Emits events ONLY for modal lifecycle & layout           │
└─────────────────────────────────────────────────────────────┘
                           ↓ creates
┌─────────────────────────────────────────────────────────────┐
│                       NavNode                               │
│  (Pure Data + Type-Specific Behaviors)                      │
│  • Factory: CreateNavNode(fe)                               │
│  • Type classification (leaf vs group)                      │
│  • Whitelist-based filtering                                │
│  • Path-based exclusion (NavPathFilter)                     │
│  • Behaviors: Activate(), Close()                           │
│  • Data: HierarchicalPath, Parent, Children                 │
└─────────────────────────────────────────────────────────────┘
```

### Key Architectural Principles

1. **Silent Discovery** - Observer discovers nodes without firing per-node events
2. **Modal-Only Events** - Events fire only for modal lifecycle changes and batch updates
3. **Complete Information** - When `ModalGroupOpened` fires, ALL nodes in scope are already discovered
4. **Event-Driven Navigation** - Navigator is purely reactive, never calls Observer for discovery
5. **Context Stack** - Three context types: RootWindow, ModalDialog, InteractiveControl

---

## 🎯 Key Design Decisions

### 1. "Observe, Don't Predict" Philosophy

**Decision:** We observe actual UI state (Popup.IsOpen, Window visibility) rather than predicting behavior.

**Why:**
- **Simplicity:** No complex state machines
- **Correctness:** React to what WPF actually creates (PopupRoot)
- **Maintainability:** Less code to break

**Example:**
```csharp
// ❌ OLD: ComboBox is a "dual-role" modal
// ✅ NEW: ComboBox is a leaf; PopupRoot (created by WPF) is the modal
```

---

### 2. Whitelist-Based Type Classification

**Decision:** Only track types explicitly in `_leafTypes` or `_groupTypes` (NavNode.cs).

**Why:**
- **Performance:** Avoid tracking every Border, Grid (thousands of elements)
- **Intent:** Only navigation-relevant controls tracked
- **Safety:** Unknown types ignored by default

**Leaf Types:** Button, MenuItem, ComboBox, Slider, DoubleSlider, RoundSlider  
**Group Types:** Window, Popup, ListBox, TabControl, DataGrid

**Critical:** `typeof(Window)` added to `_groupTypes` to enable root modal discovery.

---

### 3. Navigation Context Stack - Three Context Types

**Decision:** Context stack is `List<NavContext>` with **three distinct context types**.

**Structure:**
```csharp
class NavContext {
    NavNode ScopeNode;           // Defines context boundaries
    NavContextType ContextType;  // RootWindow, ModalDialog, InteractiveControl
    NavNode FocusedNode;         // Currently focused (or null)
    object OriginalValue;        // For Cancel in InteractiveControl
}

enum NavContextType {
    RootWindow,         // MainWindow - root context (always present)
    ModalDialog,        // Popup/Dialog - restricts navigation to descendants
    InteractiveControl  // Slider/Control - single-element scope for value adjustment
}
```

**Example Stack:**
```
[0] NavContext(MainWindow, RootWindow, Button1)
[1] NavContext(Popup, ModalDialog, MenuItem3)
[2] NavContext(DoubleSlider, InteractiveControl, DoubleSlider)  ← Slider interaction mode
```

**Why Three Types:**
- **RootWindow:** Root scope, always present after init
- **ModalDialog:** Observer-managed (ModalGroupOpened/Closed events)
- **InteractiveControl:** Navigator-managed (EnterInteractionMode/ExitInteractionMode)

**Scope Resolution:**
- `RootWindow` / `ModalDialog`: All descendants in scope
- `InteractiveControl`: **Only the control itself** (single-element scope)

---

### 4. Clean Separation: Observer vs Navigator

**Decision:** Observer handles ALL discovery. Navigator ONLY reacts to events.

**Observer's Responsibilities:**
- ✅ Auto-discover PresentationSource roots
- ✅ Hook windows for lifecycle events
- ✅ Re-register roots on layout changes
- ✅ Emit `WindowLayoutChanged` for overlay sync
- ✅ Fire `NodesUpdated` for batch node changes

**Navigator's Responsibilities:**
- ✅ Subscribe to Observer events (never calls Observer for discovery)
- ✅ Manage context stack
- ✅ Handle keyboard/StreamDeck input
- ✅ Update overlay position
- ✅ Manage interaction mode contexts

**Architectural Invariant:** Navigator NEVER calls `Observer.RegisterRoot()` or hooks windows.

---

### 5. HierarchicalPath = Computed Once, Stored Forever

**Decision:** Compute `HierarchicalPath` in `NavNode.CreateNavNode()` and pass to constructor.

**Format:** `"ElementName:ElementType[:WindowHandle] > ChildName:ChildType > ..."`

**Critical Feature - WindowHandle for Uniqueness:}

**Problem Solved:** Multiple PopupRoots had identical paths `(unnamed):PopupRoot`.

**Solution:** Include HWND as third component for top-level elements:
```
Name:Type[:WindowHandle]
```

**Examples:**
- Regular: `SaveButton:Button`
- Window: `Window:MainWindow:2E04AE`
- Popup: `(unnamed):PopupRoot:1A02D4` ← Unique HWND per popup

**Why It Works:** Every WPF Popup creates its own OS-level window (HWND), guaranteed unique.

**Result:** Each popup has unique path, making `IsDescendantOf()` work correctly.

---

### 6. Parent/Child Relationships - Cleanup Only

**Decision:** Bidirectional Parent/Child WeakReference chains for memory management.

**Primary Uses:**
1. **Memory Management** - `UnlinkNode()` removes child from parent's Children list
2. **Debug Validation** - Consistency checks (DEBUG builds only)
3. **Diagnostic Logging** - Parent chain info for debugging

**⚠️ IMPORTANT: Navigation Does NOT Use Parent/Child!**
- `IsDescendantOf()` uses **HierarchicalPath string comparison**
- Scope filtering uses **path prefix matching**
- Modal validation uses **path comparison**

**Implementation:**
```csharp
// Establish link
childNode.Parent = new WeakReference<NavNode>(parentNode);
parentNode.Children.Add(new WeakReference<NavNode>(childNode));

// Cleanup
parent.Children.RemoveAll(wr => {
    if (!wr.TryGetTarget(out var child)) return true; // Dead reference
    return ReferenceEquals(child, node);
});
```

**Why Keep It:** Fast cleanup without scanning all nodes, minimal memory overhead.

---

### 7. Persistent HighlightOverlay (Not Dispose/Recreate)

**Decision:** Create overlay once, reuse it (Hide/Show instead of Dispose/Create).

**Why:**
- **Gen 2 GC Deadlock:** Old approach created dead overlays → finalizers → circular wait → deadlock
- **Performance:** Reusing window is faster
- **Simplicity:** Less code, fewer edge cases

**Critical Fix:** `GC.SuppressFinalize(this)` in `HighlightOverlay.Dispose()`.

---

### 8. Ignored Modals - Empty Popup Handling

**Decision:** Track empty popups (tooltips) that shouldn't create contexts.

**Problem:** Tooltip popups have no navigable children but WPF still fires Unloaded.

**Solution:**
```csharp
// Navigator.cs
private static readonly HashSet<NavNode> _ignoredModals = new HashSet<NavNode>();

// OnModalGroupOpened: Check children count
if (children.Count == 0) {
    _ignoredModals.Add(scopeNode);
    return; // Don't create context
}

// OnModalGroupClosed: Check if ignored
if (_ignoredModals.Remove(scopeNode)) {
    return; // No action needed
}
```

**Result:** No warnings for tooltip closure, cleaner logs.

---

## 📁 File Organization

### Core Classes

| File | Purpose | Lines | Status |
|------|---------|-------|--------|
| `NavNode.cs` | Data + factory + behaviors | ~800 | ✅ Current |
| `Observer.cs` | Discovery engine | ~600 | ✅ Current |
| `Navigator.cs` | Core navigation logic | ~400 | ✅ Current |
| `Navigator.Focus.cs` | Focus management | ~300 | ✅ Current |
| `Navigator.Navigation.cs` | Navigation algorithm | ~200 | ✅ Current |
| `Navigator.InteractionMode.cs` | Slider interaction | ~500 | ✅ Current |
| `Navigator.Confirmation.cs` | Confirmation dialog | ~150 | ✅ Current |
| `Navigator.SD.cs` | StreamDeck integration | ~500 | ✅ See separate doc |
| `Navigator.SD.BuiltInDefinitions.cs` | SD pages/keys | ~200 | ✅ See separate doc |

### Supporting Classes

| File | Purpose | Lines | Status |
|------|---------|-------|--------|
| `NavContext.cs` | Context data structure | ~50 | ✅ Current |
| `NavPathFilter.cs` | Pattern matching | ~300 | ✅ Current |
| `HighlightOverlay.cs` | Visual feedback | ~200 | ✅ Current |
| `SDPClient.cs` | Named pipe client | ~800 | ✅ See separate doc |
| `NavConfig.cs` | Configuration model | ~150 | ✅ Current |
| `NavConfigParser.cs` | Config file parser | ~300 | ✅ Current |
| `SDPIconHelper.cs` | Icon management | ~100 | ✅ See separate doc |

**Total:** ~5,600 lines

---

## 🔧 Critical Implementation Details

### 1. Observer.Initialize() - Self-Sufficient Discovery

```csharp
internal static void Initialize() {
    // Register handler for Window.Loaded ONLY (not all FrameworkElements)
    EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, ...);
    
    // Listen for popup open events
    EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, ...);
    EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, ...);
    
    // Hook all existing windows (self-sufficient!)
    Application.Current.Dispatcher.BeginInvoke(new Action(() => {
        foreach (Window w in Application.Current.Windows) {
            HookExistingWindow(w);
        }
    }), DispatcherPriority.Background);
}

private static void HookExistingWindow(Window w) {
    // Register the Window itself as root (not Content)
    RegisterRoot(w);
    
    // Hook layout events
    w.Loaded += OnWindowLayoutChanged;
    w.LayoutUpdated += OnWindowLayoutChanged;
    w.LocationChanged += OnWindowLayoutChanged;
    w.SizeChanged += OnWindowLayoutChanged;
    
    // Periodic rescan for dynamic content (tab switches)
    var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), ...);
}

private static void OnWindowLayoutChanged(object sender, EventArgs e) {
    // Re-register root on layout changes
    if (sender is Window w) {
        RegisterRoot(w);
    }
    
    // Notify subscribers (Navigator's overlay)
    WindowLayoutChanged?.Invoke(sender, e);
}
```

**Key Insight:** Observer is completely self-sufficient. Navigator never calls Observer for discovery.

---

### 2. Navigator.Initialize() - Pure Subscriber + Subsystem Init

```csharp
public static void Initialize() {
    // Initialize StreamDeck (expensive but async)
    InitializeStreamDeck();
    
    // Initialize navigation rules (EXCLUDE/CLASSIFY patterns)
    InitializeNavigationRules();
    
    // Disable tooltips globally (prevent popup during mouse tracking)
    DisableTooltips();
    
    // Subscribe to Observer events ONLY
    Observer.ModalGroupOpened += OnModalGroupOpened;
    Observer.ModalGroupClosed += OnModalGroupClosed;
    Observer.WindowLayoutChanged += OnWindowLayoutChanged;
    Observer.NodesUpdated += Observer_NodesUpdated;
    
    // ✅ FIX: Create overlay BEFORE starting Observer (avoid race condition)
    EnsureOverlay();
    
    // Startup Observer (it hooks windows itself)
    Observer.Initialize();
    
    // Install focus guard (prevent WPF from stealing focus)
    InstallFocusGuard();
    
    // Register keyboard handler (DEBUG only)
#if DEBUG
    EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, ...);
#endif
}
```

**Key Changes from Old Design Doc:**
- ✅ `InitializeStreamDeck()` - Entire subsystem initialization
- ✅ `InitializeNavigationRules()` - Pattern parsing
- ✅ `DisableTooltips()` - Global tooltip management
- ✅ `Observer.NodesUpdated` - Batch update handler
- ✅ `EnsureOverlay()` - Synchronous, before Observer starts

---

### 3. NodesUpdated Event - Focus Re-initialization

**Purpose:** Handle focus when focused node is removed during a sync.

```csharp
// Observer.cs - Fires at end of SyncRoot
if (addedNodes.Count > 0 || removedNodes.Count > 0) {
    NodesUpdated?.Invoke(addedNodes.ToArray(), removedNodes.ToArray());
}

// Navigator.cs - Handler
private static void Observer_NodesUpdated(NavNode[] addedNodes, NavNode[] removedNodes) {
    // Check if our currently focused node was removed
    if (CurrentContext.FocusedNode != null && removedNodes != null) {
        foreach (var removedNode in removedNodes) {
            if (ReferenceEquals(CurrentContext.FocusedNode, removedNode)) {
                removedNode.HasFocus = false;
                CurrentContext.FocusedNode = null;
                _needsFocusReinit = true;
                break;
            }
        }
    }
    
    // If we need focus re-init and new navigable nodes were added, try to focus one
    if (_needsFocusReinit && addedNodes != null) {
        foreach (var addedNode in addedNodes) {
            if (addedNode.IsNavigable && IsInActiveModalScope(addedNode)) {
                SetFocus(addedNode);
                _needsFocusReinit = false;
                return;
            }
        }
    }
}
```

**Scenario:** Button focused → content changed (tab switch) → button removed → re-initialize to new button.

---

### 4. Modal Context Stack Management

```csharp
private static void OnModalGroupOpened(NavNode scopeNode) {
    // Check if modal has no navigable children (tooltip popup)
    var children = Observer.GetAllNavNodes()
        .Where(n => n.IsNavigable && !n.IsGroup && IsDescendantOf(n, scopeNode))
        .ToList();
    
    if (children.Count == 0) {
        _ignoredModals.Add(scopeNode);
        return; // Don't create context
    }
    
    // Determine context type (RootWindow vs ModalDialog)
    var contextType = DetermineModalContextType(scopeNode);
    
    // Create context
    _contextStack.Add(new NavContext(scopeNode, contextType, focusedNode: null));
    
    // Switch StreamDeck page
    SwitchStreamDeckPageForModal(scopeNode);
    
    // Defer focus initialization (wait for layout to complete)
    scopeNode.TryGetVisual(out var fe);
    fe.Dispatcher.BeginInvoke(
        isNestedModal ? DispatcherPriority.ApplicationIdle : DispatcherPriority.Loaded,
        new Action(() => TryInitializeFocusIfNeeded())
    );
}

private static void OnModalGroupClosed(NavNode scopeNode) {
    // Check if this was an ignored modal (tooltip)
    if (_ignoredModals.Remove(scopeNode)) {
        return; // No action needed
    }
    
    // Pop context (compare by path, not reference)
    for (int i = _contextStack.Count - 1; i >= 0; i--) {
        if (_contextStack[i].ScopeNode.HierarchicalPath == scopeNode.HierarchicalPath) {
            _contextStack.RemoveAt(i);
            break;
        }
    }
    
    // Defer parent focus restoration (wait for WPF to finish unload)
    CurrentContext.ScopeNode.TryGetVisual(out var parentElement);
    parentElement.Dispatcher.BeginInvoke(
        DispatcherPriority.Loaded,
        new Action(() => {
            ValidateAndRestoreParentFocus();
            SwitchStreamDeckPageForModal(CurrentContext.ScopeNode);
        })
    );
}
```

---

## 🎮 Navigation Context System

### Context Types and Scope Resolution

```csharp
enum NavContextType {
    RootWindow,         // MainWindow context (always present)
    ModalDialog,        // Popup/Dialog context (Observer-managed)
    InteractiveControl  // Slider interaction (Navigator-managed)
}
```

### Scope Resolution Logic

```csharp
internal static bool IsInActiveModalScope(NavNode node) {
    if (CurrentContext == null) return true;
    
    switch (CurrentContext.ContextType) {
        case NavContextType.RootWindow:
            // All descendants in scope
            return IsDescendantOf(node, CurrentContext.ScopeNode);
        
        case NavContextType.ModalDialog:
            // All descendants in scope
            return IsDescendantOf(node, CurrentContext.ScopeNode);
        
        case NavContextType.InteractiveControl:
            // ONLY the control itself is in scope (single-element)
            return ReferenceEquals(node, CurrentContext.ScopeNode);
    }
}
```

### Context Lifecycle

**RootWindow Context:**
- **Created:** When MainWindow discovered by Observer
- **Destroyed:** Never (persists until app exit)
- **Management:** Observer-managed

**ModalDialog Context:**
- **Created:** When Popup/PopupRoot discovered by Observer
- **Destroyed:** When Popup closes (Observer fires ModalGroupClosed)
- **Management:** Observer-managed

**InteractiveControl Context:**
- **Created:** When user activates slider (calls `EnterInteractionMode`)
- **Destroyed:** When user exits with Back/MouseLeft (calls `ExitInteractionMode`)
- **Management:** Navigator-managed

---

## 🎛️ Interaction Mode System

### Purpose

Allows controls (Sliders, DoubleSlider, RoundSlider) to enter a focused "mode" where:
- Navigation is locked to the control (single-element scope)
- Value adjustments are performed
- Original value is captured for Cancel
- StreamDeck page switches to control-specific page

### Enter Interaction Mode

```csharp
public static bool EnterInteractionMode(NavNode control, string pageName = null) {
    // Capture original value for Cancel
    object originalValue = CaptureControlValue(control);
    
    // Create interaction context
    var context = new NavContext(
        control, 
        NavContextType.InteractiveControl, 
        focusedNode: control
    ) {
        OriginalValue = originalValue
    };
    
    _contextStack.Add(context);
    
    // Switch StreamDeck page (auto-detected if null)
    if (string.IsNullOrEmpty(pageName)) {
        pageName = GetBuiltInPageForControl(control); // Slider, DoubleSlider, RoundSlider
    }
    _streamDeckClient?.SwitchPage(pageName);
    
    // Show focus visuals
    SetFocusVisuals(control);
}
```

### Exit Interaction Mode

```csharp
public static bool ExitInteractionMode(bool revertChanges = false) {
    var control = CurrentContext.ScopeNode;
    
    // Revert value if requested (Cancel)
    if (revertChanges && CurrentContext.OriginalValue != null) {
        RestoreControlValue(control, CurrentContext.OriginalValue);
    }
    
    // Pop the interaction context
    _contextStack.RemoveAt(_contextStack.Count - 1);
    
    // Restore parent context focus
    if (CurrentContext != null) {
        SetFocus(control); // Stay on control, just exit interaction mode
        SwitchStreamDeckPageForModal(CurrentContext.ScopeNode);
    }
}
```

### Slider Value Adjustment

**Proportional Adjustment:** 2% of total range (Maximum - Minimum)

```csharp
private static void AdjustSliderValue(SliderAdjustment adjustment) {
    var control = CurrentContext.ScopeNode;
    
    if (control.TryGetVisual(out var fe)) {
        if (fe is Slider slider) {
            var totalRange = Math.Abs(slider.Maximum - slider.Minimum);
            var adjustmentStep = totalRange * 0.02; // 2%
            
            switch (adjustment) {
                case SliderAdjustment.SmallIncrement:
                    slider.Value = Math.Min(slider.Maximum, slider.Value + adjustmentStep);
                    break;
                case SliderAdjustment.SmallDecrement:
                    slider.Value = Math.Max(slider.Minimum, slider.Value - adjustmentStep);
                    break;
            }
        }
        
        // DoubleSlider: Adjust value within current From/To range
        // RoundSlider: Adjust with wrap-around (0° ↔ 360°)
    }
}
```

### DoubleSlider Range Adjustment

**Proportional Adjustment:** 1% of total range (for boundaries)

```csharp
private static void AdjustDoubleSliderRange(FrameworkElement element, SliderAdjustment adjustment) {
    // Get properties via reflection
    var currentFrom = (double)fromProperty.GetValue(element);
    var currentTo = (double)toProperty.GetValue(element);
    var currentValue = (double)valueProperty.GetValue(element);
    
    var totalRange = Math.Abs(absoluteMax - absoluteMin);
    var rangeStep = Math.Max(0.1, totalRange * 0.01); // 1%
    
    switch (adjustment) {
        case SliderAdjustment.SmallIncrement: // EXPAND
            newFrom = Math.Max(absoluteMin, currentFrom - rangeStep);
            newTo = Math.Min(absoluteMax, currentTo + rangeStep);
            break;
        
        case SliderAdjustment.SmallDecrement: // CONTRACT
            // Move boundaries toward Value
            newFrom = Math.Min(currentValue - rangeStep, currentFrom + rangeStep);
            newTo = Math.Max(currentValue + rangeStep, currentTo - rangeStep);
            
            // Enforce minimum range
            if (newTo - newFrom < rangeStep * 2) {
                // Keep current range (too small)
            }
            break;
    }
    
    // Apply changes (Value will auto-center in FromToFixed mode)
    fromProperty.SetValue(element, newFrom);
    toProperty.SetValue(element, newTo);
}
```

### StreamDeck Page Mapping

**Built-in Pages:**
- `Navigation` - Standard 4-way navigation (default)
- `UpDown` - Vertical navigation only (menus)
- `Slider` - Value adjustment only (standard slider)
- `DoubleSlider` - Value + range adjustment (4-way)
- `RoundSlider` - Circular adjustment (wrap-around)
- `Confirm` - Yes/No confirmation dialog

**Page Selection Logic:**
```csharp
private static string GetBuiltInPageForControl(FrameworkElement element) {
    var typeName = element.GetType().Name;
    
    if (typeName == "DoubleSlider") return "DoubleSlider";
    if (typeName == "RoundSlider") return "RoundSlider";
    if (typeName == "Slider" || typeName == "FormattedSlider") return "Slider";
    
    return null; // Default to Navigation
}
```

---

## ✅ Known Issues & Solutions

### Issue 1: ✅ SOLVED - ScrollBar Hijacking Focus

**Problem:** ScrollBars had lower Y-coordinates than menu items.

**Solution:** Added exclusion rules:
```csharp
"EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
"EXCLUDE: *:PopupRoot > ** > *:BetterScrollBar",
```

---

### Issue 2: ✅ SOLVED - MainWindow Not Modal

**Problem:** Window wasn't in `_groupTypes` whitelist.

**Solution:** Added `typeof(Window)` to `_groupTypes`.

---

### Issue 3: ✅ SOLVED - HighlightOverlay GC Deadlock

**Problem:** Multiple overlay instances → finalizers → circular wait.

**Solution:** Reuse single instance + `GC.SuppressFinalize(this)`.

---

### Issue 4: ✅ SOLVED - PopupRoot Modal Validation Failure

**Problem:** `ModalGroupOpened` fires before `LinkToParent()`.

**Solution:** Optimistic validation:
```csharp
// Only reject if HAS parent but WRONG parent
if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
    return; // Reject
}
// If Parent == null, accept (will be linked after)
```

---

### Issue 5: ✅ SOLVED - Navigator Managing Windows

**Problem:** Navigator was calling `Observer.RegisterRoot()`.

**Solution:** Observer now hooks windows itself, Navigator only subscribes to events.

---

## 📝 Pattern Syntax Reference

### Exclusion Rules

```csharp
// Exclude HighlightOverlay
"EXCLUDE: *:HighlightOverlay > **"

// Exclude ScrollBars in popups
"EXCLUDE: *:PopupRoot > ** > *:ScrollBar"

// Exclude ModernMenu
"EXCLUDE: Window:MainWindow > WindowBorder:Border > ** > PART_Menu:ModernMenu"
```

### Classification Rules

```csharp
// Make SelectCarDialog a modal group
"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true"

// Configuration: Shortcut key with custom page
"CLASSIFY: ** > *:DoubleSlider => KeyName=\"AdjustFuel\"; KeyTitle=\"Fuel\"; TargetType=\"Element\""

// Configuration: Group targeting (jumps to first child)
"CLASSIFY: ** > *:ListBox => KeyName=\"GoToList\"; KeyTitle=\"List\"; TargetType=\"Group\""

// Configuration: Custom page mapping
"CLASSIFY: ** > *:SettingsPanel => Page=\"CustomSettings\""

// Configuration: Confirmation required
"CLASSIFY: ** > *:DeleteButton => KeyName=\"Delete\"; RequireConfirmation=true; ConfirmationMessage=\"Delete this item?\""
```

### Wildcard Reference

| Wildcard | Meaning | Example |
|----------|---------|---------|
| `*` | Match any single segment | `*:Button` = any Button |
| `**` | Match 0+ segments | `** > *:Button` = Button at any depth |
| `***` | Match 1+ segments | `*** > *:Button` = Button with ≥1 ancestor |
| `>` | Parent-child separator | `A:Panel > B:Button` |

### Segment Format

```
Name:Type[:WindowHandle]
```

- **Name:** Element's `fe.Name` (use `*` for any)
- **Type:** Element's type name (use `*` for any)
- **WindowHandle:** HWND (top-level elements only)

---

## 🐛 Debugging Guide

### 1. Enable Verbose Debug

**Hotkey:** Ctrl+Shift+F9 (toggles on/off)

**Output:**
```
[Observer] NavNode discovered: Button 'SaveButton'
[Navigator] Initialized focus in 'MainWindow' -> 'SaveButton'
[Navigator] Modal opened: (unnamed):PopupRoot:1A02D4
```

---

### 2. Debug Overlay

**Show Navigables:** Ctrl+Shift+F12
- **Blue:** Current focus
- **Orange:** Leaf nodes
- **Gray:** Group nodes

**Show All Nodes:** Ctrl+Shift+F11

---

### 3. Consistency Validation

Runs automatically on overlay toggle (DEBUG builds):

```
========== NavNode Consistency Check ==========
Total nodes: 47
Dead visuals: 0
Parent mismatches: 0
✅ All nodes CONSISTENT
```

---

### 4. Watch Focus Selection

```
[Navigator] Finding first navigable in scope 'PopupRoot'...
  Candidate: MenuItem @ Center: 150.0,50.0 | Score: 500150.0
  ✅ WINNER: MenuItem (score: 500150.0)
```

---

### 5. Watch Context Stack

```
Context Stack (3 contexts):
  [0] RootWindow: MainWindow [focused: Button1:Button]
  [1] ModalDialog: Popup [focused: MenuItem3:MenuItem]
  [2] InteractiveControl: DoubleSlider [focused: DoubleSlider]
```

---

## 📚 Related Documentation

- **StreamDeck Integration Design.md** - Complete StreamDeck integration architecture
- **Configuration System Documentation.md** - Config file format and parser
- **Protocol.md** - Named pipe communication protocol (in NWRS AC SD Plugin)
- **CHANGELOG.md** - Chronological history of architectural changes

---

## 🎨 Design Philosophy Summary

1. **Observe, Don't Predict** - React to actual UI state
2. **Whitelist Everything** - Performance through explicit tracking
3. **Silent Discovery** - Nodes tracked internally, events for lifecycle only
4. **Complete Information** - Focus selection sees all candidates at once
5. **Weak References** - Prevent memory leaks
6. **Compute Once** - HierarchicalPath never changes after creation
7. **Path-Based Hierarchy** - String comparison for all navigation logic
8. **Parent/Child for Cleanup** - Not used for navigation
9. **Context Stack** - Three types: RootWindow, ModalDialog, InteractiveControl
10. **Persistent Resources** - Reuse windows/objects
11. **Finalizers Are Dangerous** - Always suppress in Dispose()
12. **Minimal Public API** - Only expose what's needed
13. **Clean Separation** - Observer = discovery, Navigator = navigation

---

**Document Version:** 2.0  
**Last Verified:** December 2024  
**Next Review:** After major architectural changes

---
