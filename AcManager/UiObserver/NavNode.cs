using AcManager.Pages.Dialogs;
using FirstFloor.ModernUI.Windows.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Unified navigation node implementation that can represent both leaf elements and groups.
    /// Role is determined at construction time via the isGroup parameter.
    /// 
    /// Leaf elements: buttons, textboxes, menu items, etc. - actual navigation targets.
    /// Groups: containers like ComboBox dropdowns, context menus, popups - hold children.
    /// 
    /// Groups exhibit dual behavior:
    /// - When closed: the group itself is navigable
    /// - When open: only children are navigable (group itself is not)
    /// </summary>
    internal class NavNode : INavNode, INavLeaf, INavGroup
    {
        #region Debug Configuration
        
        /// <summary>
        /// Enable verbose debug output for NavNode creation and evaluation.
        /// Set to false in production to reduce debug spam.
        /// </summary>
        private const bool VERBOSE_DEBUG = false;
        
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
            
            // Other interactive controls
            typeof(Slider),
            typeof(DoubleSlider),
            typeof(ScrollBar),
            typeof(TabItem),
            typeof(Expander),
            typeof(GroupBox),

            // Custom controls (FirstFloor.ModernUI)
            // Note: These will be checked by name/namespace since types may not be available
        };
        
        // Group elements - containers that can hold navigable children
        private static readonly HashSet<Type> _groupTypes = new HashSet<Type>
        {
            typeof(ComboBox),      // Dual-role: navigable when closed
            typeof(ContextMenu),   // Dual-role: navigable when closed
            typeof(Menu),          // Dual-role: navigable when closed
            typeof(Popup),         // Pure container: never directly navigable
            typeof(ToolBar),       // Pure container: never directly navigable
            typeof(StatusBar),     // Pure container: never directly navigable
            typeof(TabControl),    // Pure container: never directly navigable
            typeof(TreeView),      // Pure container: never directly navigable
            typeof(ListBox),       // Pure container: never directly navigable
            typeof(ListView),      // Pure container: never directly navigable
            typeof(DataGrid),      // Pure container: never directly navigable
		};
        
        // Dual-role groups - can be navigated TO when closed, act as containers when open
        private static readonly HashSet<Type> _dualRoleGroupTypes = new HashSet<Type>
        {
            typeof(ComboBox),
            typeof(ContextMenu),
            typeof(Menu),
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
            
            if (VERBOSE_DEBUG)
            {
                Debug.WriteLine($"[NavNode] Evaluating: {feType.Name} '{(string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name)}'");
            }
            
            // Step 1: Perform type-based detection (baseline detection)
            bool isGroup = IsGroupType(feType);
            bool isDualRoleGroup = isGroup && IsDualRoleGroupType(feType);
            bool isLeaf = IsLeafType(feType);
            
            if (VERBOSE_DEBUG)
            {
                Debug.WriteLine($"[NavNode]   -> Type-based: IsGroup={isGroup}, IsDualRole={isDualRoleGroup}, IsLeaf={isLeaf}");
            }
            
            // Step 2: Compute hierarchical path early (needed for both exclusion and classification)
            string hierarchicalPath = GetHierarchicalPath(fe);
            
            // Step 3: Check for classification overrides BEFORE whitelist check
            // This allows CLASSIFY rules to bring non-whitelisted types into the system
            var classification = PathFilter.GetClassification(hierarchicalPath);
            if (classification != null)
            {
                if (VERBOSE_DEBUG)
                {
                    Debug.WriteLine($"[NavNode]   -> Classification rule matched: Role={classification.Role}, Modal={classification.IsModal}");
                }
                
                // Apply role overrides
                if (classification.Role != NavRole.Undefined)
                {
                    switch (classification.Role)
                    {
                        case NavRole.Leaf:
                            isGroup = false;
                            isDualRoleGroup = false;
                            isLeaf = true;
                            break;
                        
                        case NavRole.Group:
                            isGroup = true;
                            isDualRoleGroup = false;
                            isLeaf = false;
                            break;
                        
                        case NavRole.DualGroup:
                            isGroup = true;
                            isDualRoleGroup = true;
                            isLeaf = false;
                            break;
                    }
                    
                    if (VERBOSE_DEBUG)
                    {
                        Debug.WriteLine($"[NavNode]   -> After classification: IsGroup={isGroup}, IsDualRole={isDualRoleGroup}, IsLeaf={isLeaf}");
                    }
                }
            }
            
            // Step 4: Whitelist check (now uses potentially overridden values)
            if (!isGroup && !isLeaf)
            {
                if (VERBOSE_DEBUG)
                {
                    Debug.WriteLine($"[NavNode]   -> Not in whitelist (and no classification override), rejected");
                }
                return null;
            }
            
            // Step 5: Check for nested leaf constraint
            if (!isGroup && HasLeafAncestor(fe, out var leafAncestor))
            {
                if (VERBOSE_DEBUG)
                {
                    try
                    {
                        var skippedTypeName = feType.Name;
                        var ancestorTypeName = leafAncestor?.GetType().Name ?? "Unknown";
                        var skippedName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;
                        var ancestorName = string.IsNullOrEmpty(leafAncestor?.Name) ? "(unnamed)" : leafAncestor.Name;
                        
                        Debug.WriteLine($"[NavNode] SKIPPED: {skippedTypeName} '{skippedName}' - has leaf ancestor: {ancestorTypeName} '{ancestorName}'");
                    }
                    catch { }
                }
                
                return null;
            }
            
            // Step 6: Compute ID
            string id = computeId != null ? computeId(fe) : ComputeDefaultId(fe);
            
            // Step 7: Check exclusion rules
            if (PathFilter.IsExcluded(hierarchicalPath))
            {
                if (VERBOSE_DEBUG)
                {
                    Debug.WriteLine($"[NavNode]   -> FILTERED: Path matches exclusion rule");
                    Debug.WriteLine($"[NavNode]   -> Path: {hierarchicalPath}");
                }
                return null;
            }
            
            // Step 8: Determine modal behavior (type-based baseline)
            bool isModal = IsModalType(feType);
            
            // Step 9: Apply modal override from classification (if specified)
            if (classification?.IsModal.HasValue == true)
            {
                isModal = classification.IsModal.Value;
                
                if (VERBOSE_DEBUG)
                {
                    Debug.WriteLine($"[NavNode]   -> Modal overridden by classification: {isModal}");
                }
            }
            
            if (VERBOSE_DEBUG && isModal)
            {
                Debug.WriteLine($"[NavNode]   -> MODAL TYPE (blocks background navigation)");
            }
            
            // Step 10: Validation - Check for non-modal group nesting
            if (isGroup && !isModal)
            {
                var nonModalParent = FindNonModalGroupAncestorNode(fe, out var modalBlocker);
                
                if (nonModalParent != null)
                {
                    ReportNonModalNesting(fe, feType, id, hierarchicalPath, nonModalParent, modalBlocker);
                }
            }
            
            if (VERBOSE_DEBUG)
            {
                Debug.WriteLine($"[NavNode]   -> CREATED: {feType.Name} Id={id}");
                Debug.WriteLine($"[NavNode]   -> Final: IsGroup={isGroup}, IsDualRole={isDualRoleGroup}, IsModal={isModal}");
                Debug.WriteLine($"[NavNode]   -> Path: {hierarchicalPath}");
            }
            
            // Step 11: Create the node with final values
            return new NavNode(fe, id, isGroup, isDualRoleGroup, isModal);
        }
        
        private static bool IsLeafType(Type type)
        {
            // Check exact type match
            if (_leafTypes.Contains(type)) return true;
            
            // Check if derives from any leaf base type
            foreach (var leafType in _leafTypes)
            {
                if (leafType.IsAssignableFrom(type)) return true;
            }
            
            // Check for custom ModernUI controls (by namespace)
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI_WINDOWS.Controls"))
            {
                var typeName = type.Name;
                // Only detect actual interactive controls, not layout containers
                if (typeName.Contains("Button") || typeName.Contains("Link"))
                {
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
            foreach (var groupType in _groupTypes)
            {
                if (groupType.IsAssignableFrom(type)) return true;
            }
            
            // Check for custom ModernUI controls that act as groups
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI_WINDOWS.Controls"))
            {
                var typeName = type.Name;
                if (typeName.Contains("Menu") || typeName.Contains("Tab") || typeName.Contains("Frame"))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private static bool IsDualRoleGroupType(Type type)
        {
            // Check exact type match
            if (_dualRoleGroupTypes.Contains(type)) return true;
            
            // Check if derives from any dual-role group base type
            foreach (var dualType in _dualRoleGroupTypes)
            {
                if (dualType.IsAssignableFrom(type)) return true;
            }
            
            // Check for custom ModernUI controls that act as dual-role groups
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI_WINDOWS.Controls"))
            {
                var typeName = type.Name;
                // ModernMenu can be navigated to when closed
                if (typeName.Contains("Menu"))
                {
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
            
            try
            {
                DependencyObject current = fe;
                while (current != null)
                {
                    // Move up the visual tree
                    current = VisualTreeHelper.GetParent(current);
                    
                    if (current is FrameworkElement parent)
                    {
                        var parentType = parent.GetType();
                        
                        // Check if parent is a leaf - groups and unknown types are safe
                        if (!IsGroupType(parentType) && IsLeafType(parentType))
                        {
                            // Found a leaf ancestor - this element should not be a separate leaf
                            leafAncestor = parent;
                            return true;
                        }
                    }
                }
            }
            catch { }
            
            return false;
        }
        
        /// <summary>
        /// Determines if this element type creates a modal navigation context.
        /// Modal elements block access to parent/background elements during navigation.
        /// This is based on element type and navigation behavior, not WPF's ShowDialog.
        /// </summary>
        /// <param name="feType">The type of the element to check</param>
        /// <returns>True if element creates a modal navigation context</returns>
        private static bool IsModalType(Type feType)
        {
            // Modal types: elements that create exclusive navigation contexts
            // When open, these block access to background/parent elements
            
            // Popups and their contents are always modal
            if (typeof(Popup).IsAssignableFrom(feType))
                return true;
            
            // ContextMenu creates modal context when open
            if (typeof(ContextMenu).IsAssignableFrom(feType))
                return true;
            
            // ComboBox dropdown creates modal context when open (dual-role + modal)
            if (typeof(ComboBox).IsAssignableFrom(feType))
                return true;
            
            // Menu creates modal context for its items
            if (typeof(Menu).IsAssignableFrom(feType))
                return true;
            
            // Separate Windows are inherently modal for navigation purposes
            // (each window is its own navigation scope)
            if (typeof(Window).IsAssignableFrom(feType))
                return true;
            
            // Non-modal types (even though they're groups):
            // - ListBox, ListView, TreeView: freely navigate through items
            // - TabControl: tabs don't block background navigation
            // - ToolBar, StatusBar: always accessible
            // - DataGrid: cell navigation doesn't create modal context
            
            return false;
        }
        
        /// <summary>
        /// Computes the center point of this node in device-independent pixels (DIP).
        /// Returns null if element is not navigable or bounds cannot be computed.
        /// This is computed on-demand (not cached) to ensure accuracy.
        /// </summary>
        public Point? GetCenterDip()
        {
            if (!IsNavigable)
                return null;

            if (!TryGetVisual(out var fe))
                return null;

            try
            {
                // PointToScreen returns coordinates in DEVICE PIXELS, not DIP
                // We need to transform them to DIP using the PresentationSource
                var topLeftDevice = fe.PointToScreen(new Point(0, 0));
                var bottomRightDevice = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

                // Get the transform from device pixels to DIP
                var ps = PresentationSource.FromVisual(fe);
                if (ps?.CompositionTarget != null)
                {
                    var transform = ps.CompositionTarget.TransformFromDevice;
                    var topLeftDip = transform.Transform(topLeftDevice);
                    var bottomRightDip = transform.Transform(bottomRightDevice);

                    var centerX = (topLeftDip.X + bottomRightDip.X) / 2.0;
                    var centerY = (topLeftDip.Y + bottomRightDip.Y) / 2.0;

                    return new Point(centerX, centerY);
                }
                else
                {
                    // Fallback: no transform available, use device pixels as-is
                    // (This shouldn't happen if IsNavigable check passed)
                    var centerX = (topLeftDevice.X + bottomRightDevice.X) / 2.0;
                    var centerY = (topLeftDevice.Y + bottomRightDevice.Y) / 2.0;
                    return new Point(centerX, centerY);
                }
            }
            catch
            {
                return null;
            }
        }

        #region INavGroup Implementation

        /// <summary>
        /// Whether this group is currently open.
        /// Throws InvalidOperationException if called on a non-group node.
        /// </summary>
        public bool IsOpen
        {
            get
            {
                if (!IsGroup)
                    throw new InvalidOperationException($"Cannot access IsOpen on non-group node {Id}");
                return _isOpen;
            }
            set
            {
                if (!IsGroup)
                    throw new InvalidOperationException($"Cannot set IsOpen on non-group node {Id}");
                _isOpen = value;
            }
        }

        /// <summary>
        /// Gets all currently navigable children.
        /// Returns empty enumerable if:
        /// - This is not a group
        /// - Group is closed
        /// - No children are alive/navigable
        /// </summary>
        public IEnumerable<INavNode> GetNavigableChildren()
        {
            if (!IsGroup)
                return Enumerable.Empty<INavNode>();

            if (!_isOpen)
                return Enumerable.Empty<INavNode>();

            return _children
                .Where(wr => wr.TryGetTarget(out var _))
                .Select(wr =>
                {
                    wr.TryGetTarget(out var child);
                    return child;
                })
                .Where(child => child != null && child.IsNavigable);
        }

        /// <summary>
        /// Adds a child node to this group.
        /// Throws InvalidOperationException if called on a non-group node.
        /// </summary>
        public void AddChild(INavNode child)
        {
            if (!IsGroup)
                throw new InvalidOperationException($"Cannot add child to non-group node {Id}");

            if (child == null)
                throw new ArgumentNullException(nameof(child));

            _children.Add(new WeakReference<INavNode>(child));
            child.Parent = this;
        }

        /// <summary>
        /// Removes all children whose WeakReferences are no longer alive.
        /// Safe to call on non-group nodes (no-op).
        /// </summary>
        public void PruneDeadChildren()
        {
            if (!IsGroup)
                return;

            _children = _children
                .Where(wr => wr.TryGetTarget(out var _))
                .ToList();
        }

        #endregion

        public WeakReference<FrameworkElement> VisualRef { get; }
        public string Id { get; }
        public INavNode Parent { get; set; }
        
        /// <summary>
        /// Whether this node currently has focus in the navigation system.
        /// Only one node should have focus at a time within a navigation context.
        /// </summary>
        public bool HasFocus { get; set; }

        // Group-specific state (only used if IsGroup = true)
        private List<WeakReference<INavNode>> _children;
        private bool _isOpen;

        /// <summary>
        /// Determines whether this node is a group (can contain children) or a leaf (navigation target).
        /// </summary>
        public bool IsGroup { get; }
        
        /// <summary>
        /// Determines whether this group can act as both a leaf (when closed) and a group (when open).
        /// Only relevant if IsGroup is true.
        /// Examples: ComboBox, ContextMenu (dual-role) vs ListBox, TabControl (pure containers).
        /// </summary>
        public bool IsDualRoleGroup { get; }
        
        /// <summary>
        /// Determines whether this node creates a modal navigation context.
        /// Modal nodes block access to their parent/background elements during navigation.
        /// Examples: ComboBox dropdown (when open), Popup, ContextMenu, separate Window.
        /// Non-modal: ListBox, TabControl, TreeView (navigation flows freely through them).
        /// </summary>
        public bool IsModal { get; }

        public NavNode(FrameworkElement fe, string id, bool isGroup = false, bool isDualRoleGroup = false, bool isModal = false)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("ID cannot be null or empty", nameof(id));

            VisualRef = new WeakReference<FrameworkElement>(fe);
            Id = id;
            IsGroup = isGroup;
            IsDualRoleGroup = isDualRoleGroup;
            IsModal = isModal;

            if (isGroup)
            {
                _children = new List<WeakReference<INavNode>>();
                _isOpen = false;
            }
        }
        
        private static string ComputeDefaultId(FrameworkElement fe)
        {
            // Try AutomationId first (most stable)
            var automationId = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrEmpty(automationId))
                return $"A:{automationId}";
            
            // Try Name (stable if set)
            if (!string.IsNullOrEmpty(fe.Name))
                return $"N:{fe.Name}";
            
            // Fallback to type + hash (not stable across sessions, but unique)
            return $"G:{fe.GetType().Name}:{fe.GetHashCode():X8}";
        }
        
        /// <summary>
        /// Gets the hierarchical path of an element in the visual tree.
        /// Format: Name:Type > ChildName:ChildType > ...
        /// This matches the format used by NavMapper for consistency.
        /// </summary>
        public static string GetHierarchicalPath(FrameworkElement fe)
        {
            var path = new List<string>();
            
            try {
                DependencyObject current = fe;
                while (current != null) {
                    if (current is FrameworkElement parent) {
                        var typeName = parent.GetType().Name;
                        var elementName = string.IsNullOrEmpty(parent.Name) ? "(unnamed)" : parent.Name;
                        path.Insert(0, $"{elementName}:{typeName}");
                    }
                    
                    try {
                        current = VisualTreeHelper.GetParent(current);
                    } catch {
                        break;
                    }
                }
            } catch { }
            
            return string.Join(" > ", path);
        }
        
        #endregion

        public bool TryGetVisual(out FrameworkElement fe)
        {
            return VisualRef.TryGetTarget(out fe);
        }

        /// <summary>
        /// Checks if this node is currently navigable.
        /// 
        /// This only checks basic validity:
        /// - Element is alive (WeakReference valid)
        /// - Element is loaded and visible
        /// - Element has valid presentation source
        /// - Element has reasonable size
        /// - Parent (if any) is navigable
        /// 
        /// Note: For groups, additional logic in NavMapper determines actual navigability
        /// based on IsOpen state and dual-role behavior.
        /// </summary>
        public bool IsNavigable
        {
            get
            {
                // Parent must be navigable (hierarchical check)
                if (Parent != null && !Parent.IsNavigable)
                    return false;

                // Element must be alive
                if (!TryGetVisual(out var fe))
                    return false;

                // Element must be loaded and visible
                if (!fe.IsLoaded || !fe.IsVisible)
                    return false;

                // Element must be connected to a presentation source
                try
                {
                    var ps = PresentationSource.FromVisual(fe);
                    if (ps == null)
                        return false;
                }
                catch
                {
                    return false;
                }

                // Element should have reasonable size
                if (fe.ActualWidth < 1.0 || fe.ActualHeight < 1.0)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Activates this navigation node by performing its default action.
        /// Behavior depends on the control type and current state:
        /// 
        /// Dual-role groups (ComboBox, ContextMenu):
        ///   - When closed: opens the group
        ///   - When open: no-op (children handle their own activation)
        /// 
        /// Leaf controls:
        ///   - Button/RepeatButton: raises Click event
        ///   - ToggleButton/CheckBox/RadioButton: toggles IsChecked
        ///   - MenuItem: raises Click event
        ///   - ListBoxItem/ComboBoxItem/TreeViewItem: selects the item
        ///   - TabItem: selects the tab
        ///   - Expander: toggles IsExpanded
        ///   - Default: tries to set WPF focus
        /// </summary>
        /// <returns>True if activation succeeded, false if node is not navigable or action failed</returns>
        public bool Activate()
        {
            // Get the underlying visual element
            if (!TryGetVisual(out var fe)) {
                return false;
            }

            // Dual-role groups: open if closed
            if (IsDualRoleGroup && IsGroup) {
                if (!IsOpen) {
                    return TryOpenGroup(fe);
                }
                // Group is open - children handle activation, not the group itself
                return false;
            }

            // Leaf controls: perform type-specific activation
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
                
                // Toggle controls
                if (fe is ToggleButton toggle) {
                    toggle.IsChecked = !toggle.IsChecked;
                    return true;
                }
                
                // Menu items
                if (fe is MenuItem menuItem) {
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    return true;
                }
                
                // Selectable items
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
                
                // Tab selection
                if (fe is TabItem tabItem) {
                    tabItem.IsSelected = true;
                    return true;
                }
                
                // Expander toggle
                if (fe is Expander expander) {
                    expander.IsExpanded = !expander.IsExpanded;
                    return true;
                }
                
                // Fallback: try to focus the element
                return fe.Focus();
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Attempts to open a dual-role group (ComboBox, ContextMenu).
        /// </summary>
        private bool TryOpenGroup(FrameworkElement fe)
        {
            try {
                if (fe is ComboBox comboBox) {
                    comboBox.IsDropDownOpen = true;
                    return true;
                }
                
                if (fe is ContextMenu contextMenu) {
                    contextMenu.IsOpen = true;
                    return true;
                }
                
                // Menu is a bit more complex - need to open the first MenuItem
                if (fe is Menu menu) {
                    // Try to find and open the first menu item
                    var firstItem = menu.Items.OfType<MenuItem>().FirstOrDefault();
                    if (firstItem != null) {
                        firstItem.IsSubmenuOpen = true;
                        return true;
                    }
                }
            } catch { }
            
            return false;
        }

        /// <summary>
        /// Closes this group if it's a dual-role group.
        /// Returns true if successfully closed, false otherwise.
        /// </summary>
        public bool Close()
        {
            if (!IsGroup || !IsDualRoleGroup) {
                return false; // Not a closeable group
            }

            if (!TryGetVisual(out var fe)) {
                return false;
            }

            try {
                if (fe is ComboBox comboBox) {
                    comboBox.IsDropDownOpen = false;
                    return true;
                }
                
                if (fe is ContextMenu contextMenu) {
                    contextMenu.IsOpen = false;
                    return true;
                }
                
                if (fe is Menu menu) {
                    // Close all open menu items
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
            var state = IsGroup ? (_isOpen ? "Open" : "Closed") : "";
            var navigable = IsNavigable ? "Navigable" : "NotNavigable";
            var focus = HasFocus ? "Focused" : "";
            return $"{type} {Id} {state} {navigable} {focus}".Trim();
        }
        
        /// <summary>
        /// Walks up the visual tree to find if there's a non-modal NavGroup ancestor
        /// that has already been discovered, before encountering a modal NavGroup.
        /// Uses NavForest's tracking dictionary to avoid re-evaluating types.
        /// </summary>
        /// <param name="fe">The element to check</param>
        /// <param name="modalBlocker">The first modal group NavNode encountered (if any)</param>
        /// <returns>The non-modal group NavNode ancestor if found, null otherwise</returns>
        private static NavNode FindNonModalGroupAncestorNode(FrameworkElement fe, out NavNode modalBlocker)
        {
            modalBlocker = null;
            
            try
            {
                DependencyObject current = fe;
                while (current != null)
                {
                    // Move up the visual tree
                    try
                    {
                        current = VisualTreeHelper.GetParent(current);
                    }
                    catch
                    {
                        break;
                    }
                    
                    if (current is FrameworkElement parent)
                    {
                        // Check if this parent has an already-discovered NavNode
                        // Use NavForest's tracking dictionary (consistent with _nodesByElement, _nodesById)
                        if (NavForest.TryGetNavNode(parent, out var parentNode))
                        {
                            // Found a NavNode - check if it's a group
                            if (parentNode.IsGroup)
                            {
                                if (parentNode.IsModal)
                                {
                                    // Found a modal group - this acts as a barrier
                                    // Non-modal groups CAN nest under modal groups
                                    modalBlocker = parentNode;
                                    return null; // No violation
                                }
                                else
                                {
                                    // Found a non-modal group ancestor - this is a violation!
                                    return parentNode;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            
            return null; // No non-modal group ancestor found
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
            try
            {
                var childTypeName = childType.Name;
                var childName = string.IsNullOrEmpty(childFe.Name) ? "(unnamed)" : childFe.Name;
                
                Debug.WriteLine("? CHILD (Non-Modal Group):");
                Debug.WriteLine($"?   Type: {childTypeName}");
                Debug.WriteLine($"?   Name: {childName}");
                Debug.WriteLine($"?   Would-be NavNode ID: {childId}");
                Debug.WriteLine($"?   Path: {childPath}");
            }
            catch { }
            
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            
            // Parent information (from already-discovered NavNode)
            try
            {
                if (parentNode.TryGetVisual(out var parentFe))
                {
                    var parentType = parentFe.GetType();
                    var parentTypeName = parentType.Name;
                    var parentName = string.IsNullOrEmpty(parentFe.Name) ? "(unnamed)" : parentFe.Name;
                    var parentPath = GetHierarchicalPath(parentFe);
                    
                    Debug.WriteLine("? PARENT (Non-Modal Group - Already Discovered):");
                    Debug.WriteLine($"?   Type: {parentTypeName}");
                    Debug.WriteLine($"?   Name: {parentName}");
                    Debug.WriteLine($"?   NavNode ID: {parentNode.Id}");
                    Debug.WriteLine($"?   IsDualRoleGroup: {parentNode.IsDualRoleGroup}");
                    Debug.WriteLine($"?   Path: {parentPath}");
                }
            }
            catch { }
            
            // Modal blocker information (if any - shouldn't be in violation case)
            if (modalBlocker != null)
            {
                Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
                try
                {
                    if (modalBlocker.TryGetVisual(out var blockerFe))
                    {
                        var blockerType = blockerFe.GetType();
                        var blockerTypeName = blockerType.Name;
                        var blockerName = string.IsNullOrEmpty(blockerFe.Name) ? "(unnamed)" : blockerFe.Name;
                        
                        Debug.WriteLine("? NOTE: Modal Blocker Found (shouldn't see this in violation):");
                        Debug.WriteLine($"?   Type: {blockerTypeName}");
                        Debug.WriteLine($"?   Name: {blockerName}");
                        Debug.WriteLine($"?   NavNode ID: {modalBlocker.Id}");
                    }
                }
                catch { }
            }
            
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            Debug.WriteLine("? RECOMMENDATION:");
            Debug.WriteLine("?   One of these should be added to an exception list or reclassified:");
            Debug.WriteLine("?   • Remove CHILD from _groupTypes if it shouldn't be a group");
            Debug.WriteLine("?   • Remove PARENT from _groupTypes if it shouldn't be a group");
            Debug.WriteLine("?   • Add to PathFilter.AddExcludeRule() if one shouldn't be navigable");
            Debug.WriteLine("?   • Make one of them modal if the nesting is intentional");
            Debug.WriteLine("???????????????????????????????????????????????????????????????????????????????");
            Debug.WriteLine("");
        }
    }
}
