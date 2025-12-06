using System;
using System.Collections.Generic;
using System.Linq;
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
            
            // Input controls
            typeof(TextBox),
            typeof(PasswordBox),
            typeof(RichTextBox),
            
            // Selection controls
            typeof(ListBoxItem),
            typeof(ListViewItem),
            typeof(ComboBoxItem),
            typeof(TreeViewItem),
            
            // Menu items
            typeof(MenuItem),
            
            // Other interactive controls
            typeof(Slider),
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
            typeof(ComboBox),
            typeof(ContextMenu),
            typeof(Menu),
            typeof(Popup),
            typeof(ToolBar),
            typeof(StatusBar),
            typeof(TabControl),
            typeof(TreeView),
            typeof(ListBox),
            typeof(ListView),
            typeof(DataGrid),
        };
        
        // Elements that should be ignored (not navigable)
        private static readonly HashSet<Type> _ignoredTypes = new HashSet<Type>
        {
            typeof(ScrollViewer),
            typeof(Border),
            typeof(Grid),
            typeof(StackPanel),
            typeof(WrapPanel),
            typeof(DockPanel),
            typeof(Canvas),
            typeof(ContentPresenter),
            typeof(ItemsPresenter),
            typeof(Decorator),
            typeof(Panel),
        };
        
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
            
            // Check if this type should be ignored
            if (IsIgnoredType(feType)) return null;
            
            // Determine if this is a group or leaf
            bool isGroup = IsGroupType(feType);
            
            // If not explicitly a group or leaf, check if it's navigable
            if (!isGroup && !IsLeafType(feType))
            {
                // Not a known navigable type - check if it might be a custom control
                if (!IsLikelyNavigable(fe)) return null;
            }
            
            // Compute ID
            string id = computeId != null ? computeId(fe) : ComputeDefaultId(fe);
            
            // Create the node
            return new NavNode(fe, id, isGroup);
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
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI.Windows.Controls"))
            {
                var typeName = type.Name;
                if (typeName.Contains("Button") || typeName.Contains("Link") || 
                    typeName.Contains("Switch") || typeName.Contains("Cell"))
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
            if (type.Namespace != null && type.Namespace.StartsWith("FirstFloor.ModernUI.Windows.Controls"))
            {
                var typeName = type.Name;
                if (typeName.Contains("Menu") || typeName.Contains("Tab") || typeName.Contains("Frame"))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private static bool IsIgnoredType(Type type)
        {
            // Check exact type match
            if (_ignoredTypes.Contains(type)) return true;
            
            // Check if derives from any ignored base type
            foreach (var ignoredType in _ignoredTypes)
            {
                if (ignoredType.IsAssignableFrom(type)) return true;
            }
            
            return false;
        }
        
        private static bool IsLikelyNavigable(FrameworkElement fe)
        {
            // Heuristics for determining if an unknown element is likely navigable
            
            // Has a name - might be important
            if (!string.IsNullOrEmpty(fe.Name)) return true;
            
            // Has automation ID - designed for accessibility/automation
            var automationId = System.Windows.Automation.AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrEmpty(automationId)) return true;
            
            // Is focusable - can receive keyboard focus
            if (fe.Focusable) return true;
            
            // Has click handler (check if it's a Control with Command)
            if (fe is Control control && control.IsEnabled)
            {
                // Many custom controls might not be in our type lists but are interactive
                return true;
            }
            
            return false;
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

        public NavNode(FrameworkElement fe, string id, bool isGroup = false)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("ID cannot be null or empty", nameof(id));

            VisualRef = new WeakReference<FrameworkElement>(fe);
            Id = id;
            IsGroup = isGroup;

            if (isGroup)
            {
                _children = new List<WeakReference<INavNode>>();
                _isOpen = false;
            }
        }

        public bool TryGetVisual(out FrameworkElement fe)
        {
            return VisualRef.TryGetTarget(out fe);
        }

        /// <summary>
        /// Checks if this node is currently navigable.
        /// 
        /// For groups: not navigable when open (children are navigable instead).
        /// For all nodes: must have alive element, be loaded, visible, and have valid presentation source.
        /// Parent (if any) must also be navigable.
        /// </summary>
        public bool IsNavigable
        {
            get
            {
                // Groups are not navigable when open (their children are)
                if (IsGroup && _isOpen)
                    return false;

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
                // PointToScreen returns coordinates in DIP
                var topLeft = fe.PointToScreen(new Point(0, 0));
                var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

                var centerX = (topLeft.X + bottomRight.X) / 2.0;
                var centerY = (topLeft.Y + bottomRight.Y) / 2.0;

                return new Point(centerX, centerY);
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
            internal set
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

        public override string ToString()
        {
            var type = IsGroup ? "Group" : "Leaf";
            var state = IsGroup ? (_isOpen ? "Open" : "Closed") : "";
            var navigable = IsNavigable ? "Navigable" : "NotNavigable";
            var focus = HasFocus ? "Focused" : "";
            return $"{type} {Id} {state} {navigable} {focus}".Trim();
        }
    }
}
