# Unified Rule System

## Overview

The navigation system now uses a **single, unified rule processing pipeline** where all node metadata comes from one source: `NavPathFilter`. There are no special cases or separate systems for different types of classifications.

## Core Principles

### 1. Everything is Classification
```
EXCLUDE rules = "Don't create NavNode"
CLASSIFY rules = "Create NavNode with properties"
```

- **EXCLUDE** rules filter out elements
- **CLASSIFY** rules define node properties
- **Classifications ALWAYS override exclusions** (if a path has a classification, it's never excluded)

### 2. Single Source of Truth
```
NavPathFilter
  ?
NavNodeClassification (all properties)
  ?
NavNode (populated from classification)
```

**NavNodeClassification** contains ALL possible node metadata:
- Navigation: `Role`, `IsModal`, `Priority`
- StreamDeck: `PageName`, `KeyName`, `KeyTitle`, `KeyIcon`
- Interaction: `NoAutoClick`, `TargetType`, `RequireConfirmation`, `ConfirmationMessage`

### 3. No Special Cases
- `PageSelector` is just: `!IsModal && !string.IsNullOrEmpty(PageName)`
- Shortcuts are just: `!string.IsNullOrEmpty(KeyName)`
- Modals are just: `IsModal == true`

Everything flows through the same classification system.

## Rule Sources

### Built-In Rules (Navigator.cs)
```csharp
var allRules = new List<string>();

// Exclusions
allRules.AddRange(new[] {
    "EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
    "EXCLUDE: ** > *:HistoricalTextBox > **",
    // ... more exclusions
});

// Classifications
allRules.AddRange(new[] {
    "CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true",
    // ... more classifications
});
```

### Config File Rules (.cfg)
```
# PageSelector (non-modal with PageName)
CLASSIFY: Window:MainWindow >> ** >> This:QuickDrive => PageName="QuickDrive"

# Shortcut (has KeyName)
CLASSIFY: Window:MainWindow >> ** >> This:QuickDrive >> ** >> (unnamed):ModernButton["Change car"] => \
    KeyName="QuickChangeCar"; \
    KeyTitle="Change Car"; \
    KeyIcon="car.png"

# Modal with page
CLASSIFY: (unnamed):SelectCarDialog => role=group; modal=true; PageName="SelectCarDialog"

# Multiple properties
CLASSIFY: ** > *:DangerButton => KeyName="Delete"; RequireConfirmation=true; ConfirmationMessage="Are you sure?"
```

### Combination
```csharp
// In Navigator.InitializeNavigationRules()
var allRules = new List<string>();
allRules.AddRange(GetBuiltInRules());

if (_navConfig != null) {
    allRules.AddRange(_navConfig.ExportClassificationRules());
}

NavNode.PathFilter.ParseRules(allRules.ToArray());
```

## Rule Processing Flow

### 1. Initialization (Navigator.InitializeNavigationRules)
```
Built-in EXCLUDE rules
  +
Built-in CLASSIFY rules
  +
Config CLASSIFY rules (from .cfg file)
  ?
NavPathFilter.ParseRules()
  ?
Stored in unified lists:
  - _excludeRules
  - _classificationRules
```

### 2. Node Creation (Observer.ScanVisualTree ? NavNode.CreateNavNode)
```
1. Compute hierarchical path
2. Check NavPathFilter.GetClassification(path)
   - If classification exists ? apply role overrides
3. Check whitelist (type-based)
4. Check NavPathFilter.IsExcluded(path)
   - But classifications override exclusions!
5. Apply modal from classification
6. Create NavNode
```

### 3. Property Application (Observer.ScanVisualTree)
```
After NavNode created:
  ?
Get classification = NavPathFilter.GetClassification(pathWithoutHwnd)
  ?
If classification != null:
  Apply PageName ? navNode.PageName
  Apply KeyName ? navNode.KeyName
  Apply KeyTitle ? navNode.KeyTitle
  Apply KeyIcon ? navNode.KeyIcon
  Apply NoAutoClick ? navNode.NoAutoClick
  Apply TargetType ? navNode.TargetType
  Apply RequireConfirmation ? navNode.RequireConfirmation
  Apply ConfirmationMessage ? navNode.ConfirmationMessage
```

## Classification Priority

When multiple CLASSIFY rules match the same path, they are **merged** by priority:

```csharp
// In NavPathFilter.GetClassification()
var result = new NavNodeClassification();
foreach (var rule in matches.OrderBy(r => r.Classification.Priority)) {
    result.MergeFrom(rule.Classification);  // Later rules override earlier
}
```

**Example:**
```
# Built-in rule (priority=0, implicit)
CLASSIFY: ** > *:Button => role=leaf

# Config rule (priority=0, implicit, loaded later)
CLASSIFY: ** > *:DangerButton => KeyName="Delete"; RequireConfirmation=true

# Explicit priority
CLASSIFY: ** > *:CriticalButton => priority=10; ConfirmationMessage="This cannot be undone!"
```

Result for `*:CriticalButton`:
- `Role=leaf` (from first rule)
- `KeyName="Delete"` (from second rule)
- `RequireConfirmation=true` (from second rule)
- `ConfirmationMessage="This cannot be undone!"` (from third rule, highest priority wins)

## Override Semantics

### Classifications Override Exclusions
```csharp
public bool IsExcluded(string hierarchicalPath)
{
    // Check if path has a classification - if so, it's NOT excluded
    var classification = GetClassification(hierarchicalPath);
    if (classification != null) {
        return false;  // Has classification = not excluded
    }
    
    // No classification - check exclusion rules
    return _excludeRules.Any(r => r.Matches(hierarchicalPath));
}
```

**Example:**
```
# Exclude all ModernFrames by default
EXCLUDE: ** > *:ModernFrame

# But allow QuickDrive's frame (classification overrides exclusion)
CLASSIFY: Window:MainWindow >> ** >> This:QuickDrive => PageName="QuickDrive"
```

The `This:QuickDrive` element will be created despite matching the EXCLUDE rule, because it has a classification.

## Usage Examples

### PageSelector (Non-Modal Page Switch)
```
# When this element appears, switch StreamDeck to "QuickDrive" page
CLASSIFY: Window:MainWindow >> ** >> This:QuickDrive => PageName="QuickDrive"
```

Result:
- NavNode created with `PageName="QuickDrive"`
- `IsPageSelector = true` (because `!IsModal && PageName != null`)
- When node appears ? Navigator switches StreamDeck page

### Modal with Page
```
# When this modal opens, switch StreamDeck to "SelectCarDialog" page
CLASSIFY: (unnamed):SelectCarDialog => role=group; modal=true; PageName="SelectCarDialog"
```

Result:
- NavNode created with `IsModal=true` and `PageName="SelectCarDialog"`
- `IsPageSelector = false` (because modal)
- When modal opens ? Navigator switches StreamDeck page

### Shortcut Key
```
# Map StreamDeck key to this button
CLASSIFY: Window:MainWindow >> ** >> (unnamed):ModernButton["Change car"] => \
    KeyName="QuickChangeCar"; \
    KeyTitle="Change Car"; \
    KeyIcon="car.png"
```

Result:
- NavNode created with `KeyName="QuickChangeCar"`
- StreamDeck can execute shortcut to activate this button
- StreamDeck displays "Change Car" with car icon

### Confirmation Required
```
# Dangerous action requires confirmation
CLASSIFY: ** > *:DeleteButton => \
    KeyName="Delete"; \
    RequireConfirmation=true; \
    ConfirmationMessage="Delete this item? This cannot be undone."
```

Result:
- NavNode created with `RequireConfirmation=true`
- When activated ? Navigator shows confirmation dialog
- Custom message displayed

## Benefits

? **Conceptually simpler** - Only two rule types, everything else is properties  
? **No special cases** - PageSelector, shortcuts, modals all use same system  
? **Rule composition** - Multiple rules can contribute to same node  
? **Override semantics** - Clear precedence (classification > exclusion)  
? **Single source of truth** - NavPathFilter knows everything  
? **Easier testing** - One parser, one filter, one classification system  
? **Config overrides built-ins** - Later rules refine earlier ones  
? **Extensible** - New properties added in one place (NavNodeClassification)  

## Migration Notes

### What Changed
- **Before:** Three separate systems (NavPathFilter, NavConfig, Observer PageSelector detection)
- **After:** One unified system (NavPathFilter handles everything)

### Backwards Compatibility
- Config file format unchanged
- Built-in rules work the same way
- NavNode properties have same meaning
- Only internal plumbing changed

### New Capabilities
- Config can now override ANY property (not just shortcuts)
- Multiple rules can contribute to same node
- Explicit priority control
- Classifications override exclusions (allow whitelisting excluded paths)
