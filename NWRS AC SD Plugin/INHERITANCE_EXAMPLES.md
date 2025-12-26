# Page Inheritance Quick Reference

## Syntax

```
DefinePage PageName:BasePage [[grid]]
```

## Grid Keywords

| Keyword | Meaning | Example |
|---------|---------|---------|
| `"base"` | Inherit key from base page | `["base","base","base"]` |
| `null` or `""` | Clear position (override with empty) | `[null,null,null]` |
| `"KeyName"` | Use specific key (override) | `["Save","Cancel","Next"]` |

## Common Patterns

### Pattern 1: Navigation Template

```
# Define base
DefinePage Navigation [["Back","Home","Next"],[null,null,null],...]

# Add context buttons, keep navigation
DefinePage EditPage:Navigation [["Save","Cancel","base"],[null,null,null],...]
# Result: Save, Cancel, Next (inherited)
```

### Pattern 2: Inherit All, Add More

```
DefinePage Base [["A","B","C"],[null,null,null],...]

# Keep everything from base, add new row
DefinePage Extended:Base [["base","base","base"],["D","E","F"],...]
# Result: Row 0 has A,B,C (inherited), Row 1 has D,E,F (new)
```

### Pattern 3: Selective Override

```
DefinePage Base [["A","B","C"],["D","E","F"],...]

# Keep most, replace one
DefinePage Modified:Base [["base","X","base"],["base","base","base"],...]
# Result: A, X (replaces B), C, D, E, F
```

### Pattern 4: Clear Positions

```
DefinePage Base [["A","B","C"],[null,null,null],...]

# Keep only middle
DefinePage Minimal:Base [[null,"base",null],[null,null,null],...]
# Result: Only B at position (0,1), A and C cleared
```

## Visual Examples

### Base Page
```
DefinePage Navigation [["Back","Home","Next"],
                        [null,null,null],
                        [null,null,null],
                        [null,null,null],
                        [null,null,null]]
```
```
[Back]  [Home]  [Next]
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
```

### Child: Add Context
```
DefinePage EditPage:Navigation [["Save","Cancel","base"],
                                 [null,null,null],
                                 [null,null,null],
                                 [null,null,null],
                                 [null,null,null]]
```
```
[Save]  [Cancel] [Next]  ? Save replaces Back, Cancel replaces Home, Next inherited
[ ]     [ ]      [ ]
[ ]     [ ]      [ ]
[ ]     [ ]      [ ]
[ ]     [ ]      [ ]
```

### Child: Full Inherit + Add
```
DefinePage ViewPage:Navigation [["base","base","base"],
                                 ["Export","Print",null],
                                 [null,null,null],
                                 [null,null,null],
                                 [null,null,null]]
```
```
[Back]  [Home]  [Next]  ? All inherited
[Export][Print] [ ]     ? New additions
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
```

### Child: Clear Most
```
DefinePage MinimalPage:Navigation [[null,null,null],
                                    [null,"base",null],
                                    [null,null,null],
                                    [null,null,null],
                                    [null,null,null]]
```
```
[ ]     [ ]     [ ]     ? Back, Home, Next all cleared
[ ]     [Home]  [ ]     ? Only Home inherited at (1,1)
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
[ ]     [ ]     [ ]
```

## Error Handling

### Base Page Not Found
```
DefinePage Child:NonExistent [["base",null,null],...]
? PageDefined Child ERROR Base page 'NonExistent' not defined
```

### Using 'base' Without Inheritance
```
DefinePage NoBase [["base",null,null],...]
? PageDefined NoBase ERROR Undefined keys: base@(0,0)-no_base_page
```

### Undefined Key in Child
```
DefinePage Base [["A",null,null],...]
DefinePage Child:Base [["UndefinedKey",null,null],...]
? PageDefined Child ERROR Undefined keys: UndefinedKey@(0,0)
```

### Empty Base Name
```
DefinePage BadChild: [["base",null,null],...]
? PageDefined BadChild ERROR Invalid inheritance syntax: base page name cannot be empty after ':'
```

## Implementation Notes

- **One-time merge**: Inheritance is resolved when page is defined
- **No dynamic updates**: Changes to base page don't affect existing children
- **Single-level only**: Multi-level inheritance (A:B:C) not supported
- **Case-sensitive**: Page names and `:BasePageName` are case-sensitive
- **Full validation**: All referenced keys must be defined or page fails
- **No circular references**: Can't create A:B and B:A

## Best Practices

1. ? **Define base pages first** - Children can't be created without parents
2. ? **Use `"base"` liberally** - Clear intent, easy to maintain
3. ? **Use `null` explicitly** - Show that you're clearing inherited keys
4. ? **Name bases clearly** - `NavigationBase`, `MenuBase`, `TemplateBase`
5. ? **Keep bases generic** - Don't put context-specific keys in base
6. ? **Don't modify bases** - Create new base if layout needs change
7. ? **Don't nest inheritance** - Avoid A:B where B:C (flat hierarchy)

## Complete Workflow Example

```
# Step 1: Define all keys
DefineKey Back "Back" C:\Icons\back.png
DefineKey Home "Home" C:\Icons\home.png
DefineKey Next "Next" C:\Icons\next.png
DefineKey Save "Save" C:\Icons\save.png
DefineKey Cancel "Cancel" C:\Icons\cancel.png
DefineKey Export "Export" C:\Icons\export.png

# Step 2: Create base navigation template
DefinePage NavBase [["Back","Home","Next"],
                    [null,null,null],
                    [null,null,null],
                    [null,null,null],
                    [null,null,null]]

# Step 3: Create context-specific pages
DefinePage EditMode:NavBase [["Save","Cancel","base"],
                             [null,null,null],
                             [null,null,null],
                             [null,null,null],
                             [null,null,null]]

DefinePage ViewMode:NavBase [["base","base","base"],
                             ["Export",null,null],
                             [null,null,null],
                             [null,null,null],
                             [null,null,null]]

# Step 4: Switch between modes
SwitchPage EditMode  # Shows: Save, Cancel, Next
SwitchPage ViewMode  # Shows: Back, Home, Next, Export
SwitchPage NavBase   # Shows: Back, Home, Next
```

Result: Clean, maintainable page hierarchy! ??
