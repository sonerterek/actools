using AcManager.Pages.Dialogs;
using AcTools.Windows.Input;
using FirstFloor.ModernUI.Windows.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Navigation node data structure with type-specific behaviors.
    /// 
    /// Each NavNode represents a navigable UI element (leaf) or container (group).
    /// NavForest discovers nodes and builds the hierarchy via HierarchicalId.
    /// NavMapper handles navigation logic and modal scope management.
    /// 
    /// Leaf elements: buttons, menu items, etc. - actual navigation targets.
    /// Group elements: containers like ListBox, TabControl - hold navigable children.
    /// Dual-role groups: ComboBox, ContextMenu - navigable when closed, container when open.
    /// </summary>
    internal class NavNode
    {
        #region Debug Configuration

        /// <summary>
        /// Enable verbose debug output for NavNode creation and evaluation.
        /// Now controlled by Observer.ToggleVerboseDebug() via Navigator hotkey.
        /// </summary>
        internal static bool VerboseDebug { get; set; } = false;

        #endregion

        #region Type Classification

        // Leaf elements - actual navigation targets (buttons, inputs, items, etc.)
        private static readonly HashSet<Type> _leafTypes = new HashSet<Type>
        {
            // Buttons
            typeof(Button),
            typeof(RepeatButton),
            typeof(ToggleButton),
            typeof(CheckBox),
            typeof(RadioButton),
            
            // Selection controls
            typeof(ListBoxItem),
            typeof(ListViewItem),
            typeof(ComboBoxItem),
            typeof(TreeViewItem),
            
            // Menu items
            typeof(MenuItem),
            
            // ? Menu controls (WPF Menu is a leaf that triggers dropdown, not a dual-role group)
            typeof(Menu),
            typeof(ContextMenu),
            typeof(ComboBox),
            
            // Other interactive controls
            typeof(Slider),
            typeof(DoubleSlider),
            typeof(ScrollBar),
            typeof(TabItem),
            typeof(Expander),
            typeof(GroupBox),
		};

        // Group elements - containers that can hold navigable children
        private static readonly HashSet<Type> _groupTypes = new HashSet<Type>
        {
            typeof(Window),        // Root modal: application windows (MainWindow, dialogs, etc.)
            typeof(Popup),         // Pure container: never directly navigable
            typeof(ToolBar),       // Pure container: never directly navigable
            typeof(StatusBar),     // Pure container: never directly navigable
            typeof(TabControl),    // Pure container: never directly navigable
            typeof(TreeView),      // Pure container: never directly navigable
            typeof(ListBox),       // Pure container: never directly navigable
            typeof(ListView),      // Pure container: never directly navigable
            typeof(DataGrid),      // Pure container: never directly navigable
		};

        #endregion

        #region Filtering

        /// <summary>
        /// Global filter for excluding specific navigation paths.
        /// Populate this with patterns to exclude certain controls from navigation.
        /// </summary>
        public static readonly NavPathFilter PathFilter = new NavPathFilter();

        #endregion

        #region Factory Method

        /// <summary>
        /// Creates a NavNode for the given FrameworkElement, determining its type (leaf/group)
        /// based on the element's runtime type. Returns null if the element should not be tracked.
        /// </summary>
        /// <param name="fe">The FrameworkElement to wrap</param>
        /// <param name="computeId">Optional function to compute the ID. If null, uses default ID generation.</param>
        /// <returns>A new NavNode, or null if the element type should be ignored</returns>
        public static NavNode CreateNavNode(FrameworkElement fe, Func<FrameworkElement, string> computeId = null)
        {
            if (fe == null) return null;

            var feType = fe.GetType();

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode] Evaluating: {feType.Name} '{(string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name)}'");
            }

            // Step 1: Perform type-based detection (baseline detection)
            bool isGroup = IsGroupType(feType);
            bool isLeaf = IsLeafType(feType);

            // ? NEW: PopupRoot is a modal group (WPF's internal container for Menu/ContextMenu dropdowns)
            // PopupRoot IS the Popup's child - it's what actually blocks input to background elements
            // We detect it by type name since it's an internal WPF type
            if (feType.Name == "PopupRoot") {
                isGroup = true;
                isLeaf = false;
                
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> PopupRoot detected - treating as modal group");
                }
            }

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode]   -> Type-based: IsGroup={isGroup}, IsLeaf={isLeaf}");
            }

            // Step 2: Compute hierarchical path early (needed for both exclusion and classification)
            string hierarchicalPath = GetHierarchicalPath(fe);

            // Step 3: Check for classification overrides BEFORE whitelist check
            // This allows CLASSIFY rules to bring non-whitelisted types into the system
            var classification = PathFilter.GetClassification(hierarchicalPath);
            if (classification != null) {
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> Classification rule matched: Role={classification.Role}, Modal={classification.IsModal}");
                }

                // Apply role overrides
                if (classification.Role != NavRole.Undefined) {
                    switch (classification.Role) {
                        case NavRole.Leaf:
                            isGroup = false;
                            isLeaf = true;
                            break;

                        case NavRole.Group:
                            isGroup = true;
                            isLeaf = false;
                            break;
                    }

                    if (VerboseDebug) {
                        Debug.WriteLine($"[NavNode]   -> After classification: IsGroup={isGroup}, IsLeaf={isLeaf}");
                    }
                }
            }

            // Step 4: Whitelist check
            if (!isGroup && !isLeaf) {
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> Not in whitelist (and no classification override), rejected");
                }
                return null;
            }

            // Step 5: Check for nested leaf constraint
            if (!isGroup && HasLeafAncestor(fe, out var leafAncestor)) {
                if (VerboseDebug) {
                    try {
                        var skippedTypeName = feType.Name;
                        var ancestorTypeName = leafAncestor?.GetType().Name ?? "Unknown";
                        var skippedName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
                        var ancestorName = string.IsNullOrEmpty(leafAncestor?.Name) ? "(unnamed)" : leafAncestor.Name;

                        Debug.WriteLine($"[NavNode] SKIPPED: {skippedTypeName} '{skippedName}' - has leaf ancestor: {ancestorTypeName} '{ancestorName}'");
                    } catch { }
                }

                return null;
            }

            // Step 6: Compute ID
            string id = computeId != null ? computeId(fe) : ComputeDefaultId(fe);

            // Step 7: Check exclusion rules (uses hierarchicalPath computed earlier)
            if (PathFilter.IsExcluded(hierarchicalPath)) {
                Debug.WriteLine($"[NavNode]   -> FILTERED: Path matches exclusion rule [{hierarchicalPath}]");
                return null;
            }

            // Step 8: Determine modal behavior (type-based baseline)
            bool isModal = IsModalType(feType);
            
            // ? NEW: PopupRoot is always modal (it blocks background input)
            if (feType.Name == "PopupRoot") {
                isModal = true;
                
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> PopupRoot is MODAL (blocks background navigation)");
                }
            }

            // Step 9: Apply modal override from classification (if specified)
            if (classification?.IsModal.HasValue == true) {
                isModal = classification.IsModal.Value;

                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> Modal overridden by classification: {isModal}");
                }
            }

            if (VerboseDebug && isModal) {
                Debug.WriteLine($"[NavNode]   -> MODAL TYPE (blocks background navigation)");
            }

            // Step 10: Validation - Check for non-modal group nesting
            if (isGroup && !isModal) {
                var nonModalParent = FindNonModalGroupAncestorNode(fe, out var modalBlocker);

                if (nonModalParent != null) {
                    ReportNonModalNesting(fe, feType, id, hierarchicalPath, nonModalParent, modalBlocker);
                }
            }

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode]   -> CREATED: {feType.Name} Id={id}");
                Debug.WriteLine($"[NavNode]   -> Final: IsGroup={isGroup}, IsModal={isModal}");
                Debug.WriteLine($"[NavNode]   -> Path: {hierarchicalPath}");
            }

            // Step 11: Create the node with final values
            // ? Pass hierarchicalPath to constructor so it's stored immediately
            return new NavNode(fe, id, hierarchicalPath, isGroup, isModal);
        }

        private static bool IsLeafType(Type type)
        {
            // Check exact type match
            if (_leafTypes.Contains(type)) return true;

            // Check if derives from any leaf base type
            foreach (var leafType in _leafTypes) {
                if (leafType.IsAssignableFrom(type)) return true;
            }

            // Check for custom ModernUI controls (by namespace)
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI.Windows.Controls")) {
                var typeName = type.Name;
                if (typeName.Contains("Button") || typeName.Contains("Link")) {
                    return true;
                }
            }

            return false;
        }

        private static bool IsGroupType(Type type)
        {
            // Check exact type match
            if (_groupTypes.Contains(type)) return true;

            // Check if derives from any group base type
            foreach (var groupType in _groupTypes) {
                if (groupType.IsAssignableFrom(type)) return true;
            }

            // Check for custom ModernUI controls that act as groups
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI.Windows.Controls")) {
                var typeName = type.Name;
                if (typeName.Contains("Tab") || typeName.Contains("Frame")) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if this element has a leaf-type control as an ancestor in the visual tree.
        /// This prevents nested leaves (e.g., buttons inside sliders, toggles inside comboboxes).
        /// </summary>
        /// <param name="fe">The element to check</param>
        /// <param name="leafAncestor">The leaf ancestor that was found, if any</param>
        /// <returns>True if a leaf ancestor exists</returns>
        private static bool HasLeafAncestor(FrameworkElement fe, out FrameworkElement leafAncestor)
        {
            leafAncestor = null;

            try {
                DependencyObject current = fe;
                while (current != null) {
                    // Move up the visual tree
                    current = VisualTreeHelper.GetParent(current);

                    if (current is FrameworkElement parent) {
                        var parentType = parent.GetType();

                        // Check if parent is a leaf - groups and unknown types are safe
                        if (!IsGroupType(parentType) && IsLeafType(parentType)) {
                            // Found a leaf ancestor - this element should not be a separate leaf
                            leafAncestor = parent;
                            return true;
                        }
                    }
                }
            } catch { }

            return false;
        }

        /// <summary>
        /// Determines if this element type creates a modal navigation context.
        /// Modal elements block access to parent/background elements during navigation.
        /// 
        /// In our "observe and react" model:
        /// - Window creates a modal scope (separate navigation context)
        /// - Popup creates a modal scope (temporary overlay)
        /// - PopupRoot (internal WPF type) creates a modal scope for Menu/ComboBox dropdowns
        /// - ComboBox, ContextMenu, Menu are just LEAVES that trigger popups (not modals themselves!)
        /// </summary>
        /// <param name="feType">The type of the element to check</param>
        /// <returns>True if element creates a modal navigation context</returns>
        private static bool IsModalType(Type feType)
        {
            // Popups are modal (they block background input)
            if (typeof(Popup).IsAssignableFrom(feType))
                return true;

            // Windows are modal (separate navigation contexts)
            if (typeof(Window).IsAssignableFrom(feType))
                return true;

            // ? NEW: ComboBox, ContextMenu, Menu are NOT modal!
            // They are just leaves that happen to open popups.
            // The PopupRoot that appears is the actual modal.

            return false;
        }

        /// <summary>
        /// Walks up the visual tree to find if there's a non-modal NavGroup ancestor.
        /// Uses Observer's tracking dictionary to avoid re-evaluating types.
        /// </summary>
        private static NavNode FindNonModalGroupAncestorNode(FrameworkElement fe, out NavNode modalBlocker)
        {
            modalBlocker = null;

            try {
                DependencyObject current = fe;
                while (current != null) {
                    try {
                        current = VisualTreeHelper.GetParent(current);
                    } catch {
                        break;
                    }

                    if (current is FrameworkElement parent) {
                        if (Observer.TryGetNavNode(parent, out var parentNode)) {
                            if (parentNode.IsGroup) {
                                if (parentNode.IsModal) {
                                    modalBlocker = parentNode;
                                    return null;
                                } else {
                                    return parentNode;
                                }
                            }
                        }
                    }
                }
            } catch { }

            return null;
        }

        /// <summary>
        /// Outputs detailed debug information about non-modal group nesting violation.
        /// </summary>
        private static void ReportNonModalNesting(
            FrameworkElement childFe,
            Type childType,
            string childId,
            string childPath,
            NavNode parentNode,
            NavNode modalBlocker)
        {
            Debug.WriteLine("");
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            Debug.WriteLine("? ??  NON-MODAL GROUP NESTING DETECTED");
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");

            // Child information
            try {
                var childTypeName = childType.Name;
                var childName = string.IsNullOrEmpty(childFe.Name) ? "(unnamed)" : childFe.Name;

                Debug.WriteLine("? CHILD (Non-Modal Group):");
                Debug.WriteLine($"?   Type: {childTypeName}");
                Debug.WriteLine($"?   Name: {childName}");
                Debug.WriteLine($"?   Would-be NavNode ID: {childId}");
                Debug.WriteLine($"?   Path: {childPath}");
            } catch { }

            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");

            // Parent information (from already-discovered NavNode)
            try {
                if (parentNode.TryGetVisual(out var parentFe)) {
                    var parentType = parentFe.GetType();
                    var parentTypeName = parentType.Name;
                    var parentName = string.IsNullOrEmpty(parentFe.Name) ? "(unnamed)" : parentFe.Name;
                    var parentPath = GetHierarchicalPath(parentFe);

                    Debug.WriteLine("? PARENT (Non-Modal Group - Already Discovered):");
                    Debug.WriteLine($"?   Type: {parentTypeName}");
                    Debug.WriteLine($"?   Name: {parentName}");
                    Debug.WriteLine($"?   NavNode SimpleName: {parentNode.SimpleName}");
                    Debug.WriteLine($"?   Path: {parentPath}");
                }
            } catch { }

            // Modal blocker information (if any - shouldn't be in violation case)
            if (modalBlocker != null) {
                Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
                try {
                    if (modalBlocker.TryGetVisual(out var blockerFe)) {
                        var blockerType = blockerFe.GetType();
                        var blockerTypeName = blockerType.Name;
                        var blockerName = string.IsNullOrEmpty(blockerFe.Name) ? "(unnamed)" : blockerFe.Name;

                        Debug.WriteLine("? NOTE: Modal Blocker Found (shouldn't see this in violation):");
                        Debug.WriteLine($"?   Type: {blockerTypeName}");
                        Debug.WriteLine($"?   Name: {blockerName}");
                        Debug.WriteLine($"?   NavNode SimpleName: {modalBlocker.SimpleName}");
                    }
                } catch { }
            }

            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            Debug.WriteLine("? RECOMMENDATION:");
            Debug.WriteLine("?   • Remove CHILD from _groupTypes if it shouldn't be a group");
            Debug.WriteLine("?   • Remove PARENT from _groupTypes if it shouldn't be a group");
            Debug.WriteLine("?   • Add to PathFilter.AddExcludeRule() if one shouldn't be navigable");
            Debug.WriteLine("?   • Make one of them modal if the nesting is intentional");
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            Debug.WriteLine("");
        }

        private static string ComputeDefaultId(FrameworkElement fe)
        {
            var typeName = fe.GetType().Name;
            var elementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
            
            // Start with simple Name:Type format
            string baseId = $"{elementName}:{typeName}";
            
            // ? For top-level elements (Window, PopupRoot), append WindowHandle as third component
            // This makes each popup window unique: (unnamed):PopupRoot:12345
            bool isTopLevel = fe is Window || typeName == "PopupRoot";
            
            if (isTopLevel) {
                try {
                    var hwndSource = PresentationSource.FromVisual(fe) as System.Windows.Interop.HwndSource;
                    if (hwndSource != null) {
                        var hwnd = hwndSource.Handle.ToInt32();
                        baseId = $"{baseId}:{hwnd:X}";
                        
                        if (VerboseDebug) {
                            Debug.WriteLine($"[NavNode] ComputeDefaultId: Top-level {typeName} has HWND, ID={baseId}");
                        }
                    }
                } catch {
                    // If we can't get HWND, just use the baseId without it
                    // This can happen if element isn't fully initialized yet
                }
            }
            
            return baseId;
        }

        /// <summary>
        /// Gets the hierarchical path of an element in the visual tree.
        /// Format: Name:Type[:HWND] > ChildName:ChildType > ...
        /// 
        /// ? FIXED: Includes WindowHandle in SimpleName for top-level elements (Window, PopupRoot).
        /// Each WPF Popup creates its own OS-level window (HWND), so PopupRoots for main menu
        /// and submenu will have different window handles in their SimpleName, making paths unique.
        /// 
        /// Example paths with WindowHandles in SimpleName:
        /// - Main menu: "(unnamed):PopupRoot:3F4A21B > ... > (unnamed):MenuItem"
        /// - Submenu: "(unnamed):PopupRoot:5C8D943 > ... > (unnamed):HierarchicalItem"
        /// 
        /// This ensures different PopupRoots have unique paths for scope filtering.
        /// Handles both NavNode elements (uses their SimpleName) and non-NavNode intermediate elements.
        /// </summary>
        public static string GetHierarchicalPath(FrameworkElement fe)
        {
            var path = new List<string>();

            try {
                DependencyObject current = fe;
                
                while (current != null) {
                    if (current is FrameworkElement parent) {
                        string pathSegment;
                        
                        // Check if this element is a NavNode (has been discovered)
                        if (Observer.TryGetNavNode(parent, out var existingNode)) {
                            // Use the pre-computed SimpleName from the NavNode
                            pathSegment = existingNode.SimpleName;
                        } else {
                            // Not a NavNode - compute path segment manually
                            // This handles intermediate elements in the visual tree
                            var typeName = parent.GetType().Name;
                            var elementName = string.IsNullOrEmpty(parent.Name) ? "(unnamed)" : parent.Name;
                            pathSegment = $"{elementName}:{typeName}";
                            
                            // For top-level elements, include HWND even if not yet a NavNode
                            bool isTopLevel = parent is Window || typeName == "PopupRoot";
                            if (isTopLevel) {
                                try {
                                    var hwndSource = PresentationSource.FromVisual(parent) as System.Windows.Interop.HwndSource;
                                    if (hwndSource != null) {
                                        var hwnd = hwndSource.Handle.ToInt32();
                                        pathSegment = $"{pathSegment}:{hwnd:X}";
                                    }
                                } catch {
                                    // HWND not available yet, use base format
                                }
                            }
                        }
                        
                        path.Insert(0, pathSegment);
                    }

                    try {
                        // Walk up the visual tree
                        current = VisualTreeHelper.GetParent(current);
                    } catch {
                        break;
                    }
                }
            } catch { }

            return string.Join(" > ", path);
        }

        #endregion

        #region Properties

        public WeakReference<FrameworkElement> VisualRef { get; }
        
        /// <summary>
        /// Simple human-readable name for this node (NOT unique!).
        /// Derived from AutomationId, Name, or Type+Hash.
        /// Use HierarchicalPath for unique identification.
        /// </summary>
        public string SimpleName { get; }

        /// <summary>
        /// Full hierarchical path from root to this element.
        /// Format: "WindowName:Window > PanelName:StackPanel > ButtonName:Button"
        /// 
        /// Used by PathFilter for pattern matching and as unique identifier.
        /// Computed once during creation from the full visual tree.
        /// </summary>
        public string HierarchicalPath { get; internal set; }

        /// <summary>
        /// Parent node in the visual tree.
        /// Null if this is a root node (Window/PresentationSource root).
        /// Set by NavForest during discovery.
        /// 
        /// For elements inside Popups, this correctly points to ancestors via PlacementTarget bridging,
        /// so the Parent chain always represents the logical navigation structure.
        /// </summary>
        public WeakReference<NavNode> Parent { get; internal set; }

        /// <summary>
        /// Child nodes in the visual tree.
        /// Empty list if this is a leaf node.
        /// Updated by NavForest as children are discovered.
        /// </summary>
        public List<WeakReference<NavNode>> Children { get; } = new List<WeakReference<NavNode>>();

        /// <summary>
        /// Captures the intermediate visual tree path from this element to its parent NavNode.
        /// Stored during LinkToParent() for diagnostic purposes.
        /// 
        /// Format: List of weak references to FrameworkElements between this node and its parent.
        /// - Does NOT include this node's element (accessible via VisualRef)
        /// - Does NOT include parent NavNode's element (accessible via Parent.VisualRef)
        /// - Only contains intermediate non-NavNode FrameworkElements
        /// 
        /// Index 0 = immediate visual parent (non-NavNode)
        /// Index 1 = parent's visual parent (non-NavNode)
        /// ...
        /// Last index = last intermediate element before parent NavNode
        /// 
        /// NULL or empty if this node is a direct child of its parent NavNode (no intermediates).
        /// NULL if this is a root node (no parent).
        /// 
        /// Used for diagnosing visual tree disconnections by comparing captured path
        /// with current visual tree state.
        /// </summary>
        public List<WeakReference<FrameworkElement>> VisualTreePath { get; set; }

        /// <summary>
        /// Whether this node is a group (can contain children) or a leaf (navigation target).
        /// Groups are not navigable themselves; leaves are navigable.
        /// </summary>
        public bool IsGroup { get; }

        /// <summary>
        /// Whether this node creates a modal navigation context.
        /// Modal nodes (Window, Popup, PopupRoot) block access to background elements during navigation.
        /// 
        /// In our "observe and react" model:
        /// - Popup and PopupRoot are modals (they block input to background)
        /// - We don't predict behavior, we observe what blocks input
        /// - Menu and ComboBox are just leaves that trigger popups when activated
        /// </summary>
        public bool IsModal { get; }

        /// <summary>
        /// Whether this node currently has focus in the navigation system.
        /// Set by NavMapper during navigation.
        /// </summary>
        public bool HasFocus { get; set; }

        /// <summary>
        /// Whether this node is navigable (can receive keyboard focus).
        /// Leaves are navigable; groups are not.
        /// </summary>
        public bool IsNavigable => !IsGroup;

        #endregion

        public NavNode(FrameworkElement fe, string simpleName, string hierarchicalPath, bool isGroup = false, bool isModal = false)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));
            if (string.IsNullOrEmpty(simpleName)) throw new ArgumentException("SimpleName cannot be null or empty", nameof(simpleName));

            VisualRef = new WeakReference<FrameworkElement>(fe);
            SimpleName = simpleName;
            HierarchicalPath = hierarchicalPath ?? simpleName; // Use provided path, fallback to simpleName
            IsGroup = isGroup;
            IsModal = isModal;
        }

        public bool TryGetVisual(out FrameworkElement fe)
        {
            return VisualRef.TryGetTarget(out fe);
        }

        /// <summary>
        /// Computes the center point of this node in device-independent pixels (DIP), screen-absolute.
        /// Returns null if bounds cannot be computed.
        /// 
        /// Note: Returns value even for groups (for debug visualization purposes).
        /// Navigation logic should check IsNavigable separately.
        /// 
        /// ? CHANGED: Removed IsVisible check to support elements in non-active tabs.
        /// WPF's IsVisible returns false for tab content that's loaded but not currently selected,
        /// even though the elements are rendered and have valid screen coordinates.
        /// 
        /// ? SEMANTIC: Returns DIP (Device Independent Pixels) in screen-absolute coordinate space.
        /// This is the correct coordinate system for WPF UI elements and overlay positioning.
        /// Mouse input code should convert DIP ? device pixels as needed.
        /// 
        /// ? ADDED: IsArrangeValid check to wait for WPF layout completion.
        /// This prevents calculating coordinates before popup/dropdown positioning is finalized.
        /// 
        /// ? COORDINATE FLOW:
        /// 1. PointToScreen() returns device pixels (screen-absolute)
        /// 2. TransformFromDevice converts device pixels ? DIP
        /// 3. Return DIP (screen-absolute) for UI overlay positioning
        /// </summary>
        public Point? GetCenterDip()
        {
            if (!TryGetVisual(out var fe))
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Visual reference dead for {SimpleName}");
                return null;
            }

            // ? CHANGED: Only check IsLoaded, not IsVisible
            // Elements in inactive tabs have IsVisible=false but are still navigable
            if (!fe.IsLoaded)
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Not loaded - {SimpleName}");
                return null;
            }

            // ? NEW: Check if layout is complete before calculating coordinates
            // This prevents wrong coordinates during popup/dropdown initial positioning
            if (!fe.IsArrangeValid)
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Layout not ready (IsArrangeValid=false) - {SimpleName}");
                return null;
            }

            // Verify we have a presentation source for coordinate transformation
            PresentationSource ps;
            try {
                ps = PresentationSource.FromVisual(fe);
                if (ps?.CompositionTarget == null)
                {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: No PresentationSource - {SimpleName}");
                    return null;
                }
            } catch {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: PresentationSource exception - {SimpleName}");
                return null;
            }

            if (fe.ActualWidth < 1.0 || fe.ActualHeight < 1.0)
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Zero size ({fe.ActualWidth}x{fe.ActualHeight}) - {SimpleName}");
                return null;
            }

            try {
                // ? Step 1: Get element corners in device pixels (screen-absolute)
                var topLeftDevice = fe.PointToScreen(new Point(0, 0));
                var bottomRightDevice = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

                // ? Step 2: Transform device pixels ? DIP (screen-absolute)
                var transform = ps.CompositionTarget.TransformFromDevice;
                var topLeftDip = transform.Transform(topLeftDevice);
                var bottomRightDip = transform.Transform(bottomRightDevice);

                // ? Step 3: Calculate center in DIP
                var centerX = (topLeftDip.X + bottomRightDip.X) / 2.0;
                var centerY = (topLeftDip.Y + bottomRightDip.Y) / 2.0;

                // ? Viewport bounds check in DIP coordinates
                // Reject items scrolled off-screen or far outside viewport
                // Note: These bounds are approximate and in DIP space
                if (centerY < -100 || centerY > 2000) {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip rejected: off-screen Y={centerY:F1} DIP - {SimpleName}");
                    return null;
                }

                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip success: {SimpleName} @ ({centerX:F1},{centerY:F1}) DIP [screen-absolute]");
                return new Point(centerX, centerY);
            } catch (Exception ex) {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Exception - {SimpleName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Activates this navigation node by performing its default action.
        /// Behavior depends on the control type and current state.
        /// 
        /// For MenuItem specifically, uses mouse-click simulation to let WPF handle
        /// all internal state management (_userInitiatedPress, IsPressed, timers, etc.)
        /// 
        /// Leaf controls:
        ///   - Button/RepeatButton: raises Click event
        ///   - ToggleButton/CheckBox/RadioButton: toggles IsChecked
        ///   - MenuItem: simulates mouse click at element center
        ///   - ComboBox: opens dropdown
        ///   - Menu: simulates mouse click to open dropdown (not submenu!)
        ///   - ContextMenu: opens menu
        ///   - ListBoxItem/ComboBoxItem/TreeViewItem: selects the item
        ///   - TabItem: selects the tab
        ///   - Expander: toggles IsExpanded
        ///   - Default: tries to set WPF focus
        /// </summary>
        public bool Activate()
        {
            if (!TryGetVisual(out var fe)) {
                return false;
            }

            try {
                // Buttons
                if (fe is Button btn) {
                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return true;
                }

                if (fe is RepeatButton repeatBtn) {
                    repeatBtn.RaiseEvent(new RoutedEventArgs(RepeatButton.ClickEvent));
                    return true;
                }

                if (fe is ToggleButton toggle) {
                    toggle.IsChecked = !toggle.IsChecked;
                    return true;
                }

                // Menu items - use mouse simulation for maximum reliability
                if (fe is MenuItem menuItem) {
                    return SimulateMouseClick(fe);
                }

                // ? Menu control - should open its own dropdown, not try to open submenu
                // The Menu may not have MenuItem children (could have HierarchicalItem or other types)
                // Use mouse click to trigger the dropdown
                if (fe is Menu menu) {
                    return SimulateMouseClick(fe);
                }

                // ? NEW: ComboBox, ContextMenu are leaves - open them when activated
                if (fe is ComboBox comboBox) {
                    comboBox.IsDropDownOpen = true;
                    return true;
                }

                if (fe is ContextMenu contextMenu) {
                    contextMenu.IsOpen = true;
                    return true;
                }

                // Selection items
                if (fe is ListBoxItem listBoxItem) {
                    listBoxItem.IsSelected = true;
                    return true;
                }

                if (fe is ComboBoxItem comboBoxItem) {
                    comboBoxItem.IsSelected = true;
                    return true;
                }

                if (fe is TreeViewItem treeViewItem) {
                    treeViewItem.IsSelected = true;
                    return true;
                }

                if (fe is TabItem tabItem) {
                    tabItem.IsSelected = true;
                    return true;
                }

                // Expander
                if (fe is Expander expander) {
                    expander.IsExpanded = !expander.IsExpanded;
                    return true;
                }

                // Default: try to focus
                return fe.Focus();
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Simulates a mouse click at the center of the element.
        /// This is the most robust way to activate complex controls like MenuItem,
        /// as it lets WPF's internal machinery handle all state management.
        /// 
        /// ? COORDINATE FLOW:
        /// 1. GetCenterDip() returns DIP (screen-absolute)
        /// 2. TransformToDevice converts DIP ? device pixels
        /// 3. Device pixels used for mouse input (SendInput API)
        /// </summary>
        private bool SimulateMouseClick(FrameworkElement fe)
        {
            try {
                // ? Step 1: Get center in DIP (screen-absolute coordinate space)
                var centerDip = GetCenterDip();
                if (!centerDip.HasValue) {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] SimulateMouseClick failed: Could not get center point for {SimpleName}");
                    return false;
                }

                // ? Step 2: Get presentation source for DIP ? device pixel conversion
                var ps = PresentationSource.FromVisual(fe);
                if (ps?.CompositionTarget == null) {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] SimulateMouseClick failed: No PresentationSource for {SimpleName}");
                    return false;
                }

                // ? Step 3: Convert DIP ? device pixels (screen-absolute)
                var transformToDevice = ps.CompositionTarget.TransformToDevice;
                var centerDevice = transformToDevice.Transform(centerDip.Value);

                // ? Step 4: Get screen dimensions in device pixels for SendInput normalization
                // SendInput uses 0-65535 normalized coordinate space
                var screenWidthDevice = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                var screenHeightDevice = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

                // ? Step 5: Normalize to SendInput's 0-65535 coordinate space
                var absoluteX = (int)(centerDevice.X * 65536.0 / screenWidthDevice);
                var absoluteY = (int)(centerDevice.Y * 65536.0 / screenHeightDevice);

                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode] SimulateMouseClick: {SimpleName}");
                    Debug.WriteLine($"  DIP (screen-absolute): ({centerDip.Value.X:F1}, {centerDip.Value.Y:F1})");
                    Debug.WriteLine($"  Device pixels: ({centerDevice.X:F1}, {centerDevice.Y:F1})");
                    Debug.WriteLine($"  SendInput normalized: ({absoluteX}, {absoluteY})");
                }

                // ? Step 6: Move mouse and click
                var mouse = new MouseSimulator();
                mouse.MoveMouseTo(absoluteX, absoluteY);
                mouse.LeftButtonClick();

                return true;
            } catch (Exception ex) {
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode] SimulateMouseClick exception for {SimpleName}: {ex.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// Closes this node if it's a modal group (Popup, Window).
        /// For leaves (ComboBox, Menu, ContextMenu), closes their dropdown/menu.
        /// Returns true if successfully closed, false otherwise.
        /// </summary>
        public bool Close()
        {
            if (!TryGetVisual(out var fe)) {
                return false;
            }

            try {
                // Modal groups: close them
                if (fe is Popup popup) {
                    popup.IsOpen = false;
                    return true;
                }

                if (fe is Window window) {
                    window.Close();
                    return true;
                }

                // Leaves that open dropdowns: close their dropdowns
                if (fe is ComboBox comboBox) {
                    comboBox.IsDropDownOpen = false;
                    return true;
                }

                if (fe is ContextMenu contextMenu) {
                    contextMenu.IsOpen = false;
                    return true;
                }

                if (fe is Menu menu) {
                    foreach (MenuItem item in menu.Items.OfType<MenuItem>()) {
                        item.IsSubmenuOpen = false;
                    }
                    return true;
                }
            } catch { }

            return false;
        }

        public override string ToString()
        {
            var type = IsGroup ? "Group" : "Leaf";
            var modal = IsModal ? "+Modal" : "";
            var focus = HasFocus ? "Focus" : "";
            return $"{type}{modal} {SimpleName} [{(IsNavigable ? "Nav" : "!Nav")}] {focus}".Trim();
        }
    }
}
