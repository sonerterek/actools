using AcManager.Pages.Dialogs;
using AcTools.Windows.Input;
using FirstFloor.ModernUI.Windows.Controls;
using SlimDX.Direct2D;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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

        #region Mouse Simulation

        /// <summary>
        /// Cached MouseSimulator instance to avoid creating new instances for every click.
        /// Thread-safe singleton pattern.
        /// </summary>
        private static readonly MouseSimulator _mouseSimulator = new MouseSimulator();

        /// <summary>
        /// Delay in milliseconds after moving mouse before sending click.
        /// WPF needs time to process the mouse position update before the click event.
        /// 
        /// Set to 0 for immediate click (no delay).
        /// Testing showed 10ms was insufficient for some scenarios.
        /// Can be increased if clicks are being missed.
        /// </summary>
        private const int MousePositionSettleDelayMs = 0;

        /// <summary>
        /// Delay in milliseconds between button down and button up events.
        /// This mimics a real human click where there's a small delay between pressing and releasing.
        /// Real mouse clicks typically have 10-50ms between down and up events.
        /// </summary>
        private const int MouseButtonPressDelayMs = 10;

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
            
            // ✓ Menu controls (WPF Menu is a leaf that triggers dropdown, not a dual-role group)
            typeof(Menu),
            typeof(ContextMenu),
            typeof(ComboBox),
            
            // Other interactive controls
            typeof(Slider),
            typeof(DoubleSlider),
			typeof(RoundSlider),
			typeof(ScrollBar),
            typeof(TabItem),
            typeof(Expander),
            typeof(GroupBox),
            typeof(ContextMenuButton),
		};

        // Group elements - containers that can hold navigable children
        private static readonly HashSet<Type> _groupTypes = new HashSet<Type>
        {
            typeof(Window),        // Root modal: application windows (MainWindow, dialogs, etc.)
            // typeof(Popup),         // Pure container: never directly navigable
            typeof(ToolBar),       // Pure container: never directly navigable
            typeof(StatusBar),     // Pure container: never directly navigable
            typeof(TabControl),    // Pure container: never directly navigable
            typeof(TreeView),      // Pure container: never directly navigable
            typeof(ListBox),       // Pure container: never directly navigable
            typeof(ListView),      // Pure container: never directly navigable
            typeof(DataGrid),      // Pure container: never directly navigable
            typeof(ReferenceSwitch)// Content-swapping container for GameDialog post-game UI
		};

        #endregion

        #region Factory Method

        /// <summary>
        /// Force-creates a NavNode for ANY element, bypassing all type and exclusion checks.
        /// Used when element matches a CLASSIFY rule (classification overrides everything).
        /// 
        /// This method:
        /// - Does NOT check if type is in sanctioned lists
        /// - Does NOT check exclusion rules
        /// - Always creates a node (unless fe is null)
        /// - Determines IsGroup/IsModal based on type UNLESS classification overrides them
        /// </summary>
        /// <param name="fe">The FrameworkElement to wrap</param>
        /// <param name="hierarchicalPath">Pre-computed hierarchical path</param>
        /// <param name="classification">Classification rule to apply (optional)</param>
        /// <returns>A new NavNode (never null if fe is not null)</returns>
        public static NavNode CreateNavNode(
            FrameworkElement fe, 
            string hierarchicalPath,
            NavNodeClassification classification = null)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));

            var feType = fe.GetType();

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode] CreateNavNode: {feType.Name} '{(string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name)}'");
            }

            bool isRoot = VisualTreeHelper.GetParent(fe) is null;
            bool isGroup = isRoot || IsGroupType(feType);
            bool isModal = isRoot || IsModalType(feType);
            
            if (VerboseDebug && isRoot) {
                Debug.WriteLine($"[NavNode]   -> VisualRoot detected - type-based: ");
            }
            
            // Apply classification overrides
            if (classification != null) {
                // Override Role (Group vs Leaf)
                if (classification.Role == NavRole.Group) {
                    isGroup = true;
                    if (VerboseDebug) Debug.WriteLine($"[NavNode]   -> Classification override: isGroup=true (role=group)");
                } else if (classification.Role == NavRole.Leaf) {
                    isGroup = false;
                    if (VerboseDebug) Debug.WriteLine($"[NavNode]   -> Classification override: isGroup=false (role=leaf)");
                }
                // If Role == Undefined, keep type-based default
                
                // Override IsModal
                if (classification.IsModal) {
                    isModal = true;
                    if (VerboseDebug) Debug.WriteLine($"[NavNode]   -> Classification override: isModal=true");
                }
            }

            // Compute ID
            string simpleName = ComputeSimpleName(fe);

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode]   -> CREATED: SimpleName={simpleName}");
                Debug.WriteLine($"[NavNode]   -> IsGroup={isGroup}, IsModal={isModal}");
                Debug.WriteLine($"[NavNode]   -> Path: {hierarchicalPath}");
            }

            // Create node with determined characteristics
            return new NavNode(fe, simpleName, hierarchicalPath, isGroup, isModal, isRoot);
        }

        /// <summary>
        /// Attempts to create a NavNode using standard type-based rules.
        /// 
        /// Checks:
        /// 1. Element type must be in sanctioned lists (leaf or group)
        /// 2. Exclusion rules must not apply
        /// 3. No nested leaf constraint
        /// 
        /// If all checks pass, delegates to CreateNavNode for actual creation.
        /// 
        /// Returns null if element shouldn't be tracked.
        /// </summary>
        /// <param name="fe">The FrameworkElement to wrap</param>
        /// <param name="hierarchicalPath">Pre-computed hierarchical path</param>
        /// <param name="navConfig">Configuration for exclusion checking (can be null)</param>
        /// <returns>A new NavNode, or null if the element should not be tracked</returns>
        public static NavNode TryCreateNavNode(
            FrameworkElement fe, 
            string hierarchicalPath, 
            NavConfiguration navConfig)
        {
            if (fe == null) return null;

            var feType = fe.GetType();

            if (VerboseDebug) {
                Debug.WriteLine($"[NavNode] TryCreateNavNode: {hierarchicalPath}'");
            }

            var feTypeName = feType.Name;

            // STEP 1: Check if type is sanctioned
            bool isGroup = IsGroupType(feType);
            bool isLeaf = IsLeafType(feType);
            
            if (!isGroup && !isLeaf) {
                if (VerboseDebug) {
					Debug.WriteLine($"[NavNode] -> Not in whitelist, rejected {hierarchicalPath}");
                }
                return null;
            }

            // STEP 2: Check exclusions (only if config provided)
            if (navConfig != null && navConfig.IsExcluded(hierarchicalPath)) {
                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode]   -> Excluded by rule, rejected {hierarchicalPath}");
                }
                return null;
            }

            // STEP 3: Check for nested leaf constraint (only for non-groups)
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

			// STEP 4: All checks passed - delegate to CreateNavNode for actual creation
			// This ensures we reuse the same node construction logic
			if (VerboseDebug) {
                Debug.WriteLine($"[NavNode]   -> Type rules passed, delegating to CreateNavNode");
            }
            
            return CreateNavNode(fe, hierarchicalPath, null);
        }

        static List<string> ModernUITypes = new List<string>();

		private static bool IsLeafType(Type type)
        {
            // Check exact type match
            if (_leafTypes.Contains(type)) return true;

            // Check if derives from any leaf base type
            foreach (var leafType in _leafTypes) {
                if (leafType.IsAssignableFrom(type)) return true;
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

                        // Check if currentFe is a leaf - groups and unknown types are safe
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
        /// Modal elements block access to currentFe/background elements during navigation.
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
        /// Extracts display content from a FrameworkElement for disambiguation.
        /// Returns null if no meaningful content is found.
        /// 
        /// Priority:
        /// 1. ContentControl.Content (Button, Label, CheckBox, etc.) - if it's a string or TextBlock with text
        /// 2. TextBlock.Text - if it has actual text
        /// 3. HeaderedContentControl.Header (GroupBox, Expander) - if it's a string
        /// 4. ToolTip - HIGH-PRIORITY BACKUP for icon-only buttons (ModernButton, etc.)
        /// 5. First TextBlock child in visual tree - LAST RESORT, only if it has text
        /// </summary>
        private static string ExtractElementContent(FrameworkElement fe)
        {
            if (fe == null) return null;

            try {
                // ✅ PRIORITY 1: ContentControl.Content (Button, Label, CheckBox, etc.)
                if (fe is ContentControl cc && cc.Content != null) {
                    // If Content is a string, use it directly
                    if (cc.Content is string str && !string.IsNullOrWhiteSpace(str)) {
                        return str.Trim();
                    }
                    
                    // If Content is a TextBlock, get its text
                    if (cc.Content is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) {
                        return tb.Text.Trim();
                    }
                }

                // ✅ PRIORITY 2: TextBlock.Text (direct TextBlock element)
                if (fe is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text)) {
                    return textBlock.Text.Trim();
                }

                // ✅ PRIORITY 3: HeaderedContentControl.Header (GroupBox, Expander)
                if (fe is HeaderedContentControl hcc && hcc.Header is string headerStr && !string.IsNullOrWhiteSpace(headerStr)) {
                    return headerStr.Trim();
                }

                // ✅ PRIORITY 4: ToolTip (HIGH-PRIORITY BACKUP for ModernButton, icon-only buttons, etc.)
                // This is PERFECT for elements where Content is null but ToolTip describes the action
                if (fe.ToolTip != null) {
                    // Case 1: ToolTip is a direct string (simple case)
                    if (fe.ToolTip is string tooltipStr && !string.IsNullOrWhiteSpace(tooltipStr)) {
                        return tooltipStr.Trim();
                    }
                    
                    // Case 2: ToolTip is a ToolTip object with Content property (common case)
                    // When you set ToolTip="text" in XAML, WPF wraps it in a ToolTip object
                    if (fe.ToolTip is System.Windows.Controls.ToolTip tooltipObj && tooltipObj.Content is string tooltipContent && !string.IsNullOrWhiteSpace(tooltipContent)) {
                        return tooltipContent.Trim();
                    }
                }

                // ✅ PRIORITY 5: Visual tree search (LAST RESORT fallback)
                // Search for first TextBlock child - but ONLY if it has actual text
                var childTextBlock = FindVisualChild<TextBlock>(fe);
                if (childTextBlock != null && !string.IsNullOrWhiteSpace(childTextBlock.Text)) {
                    return childTextBlock.Text.Trim();
                }
            } catch {
                // Content extraction failed, return null
            }

            return null;
        }

        /// <summary>
        /// Finds the first child of a given type in the visual tree.
        /// </summary>
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            try {
                int childCount = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < childCount; i++) {
                    var child = VisualTreeHelper.GetChild(parent, i);

                    if (child is T typedChild) {
                        return typedChild;
                    }

                    var foundChild = FindVisualChild<T>(child);
                    if (foundChild != null) {
                        return foundChild;
                    }
                }
            } catch {
                // Visual tree traversal failed
            }

            return null;
        }

        /// <summary>
        /// Sanitizes content string for use in hierarchical path.
        /// Truncates long content and escapes special characters.
        /// </summary>
        private static string SanitizeContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            content = content.Trim();

            // Skip if content looks like dynamic data (pure numbers, dates, etc.)
            if (System.Text.RegularExpressions.Regex.IsMatch(content, @"^\d+$")) {
                return null; // Skip pure numbers
            }

            // Truncate long content
            const int maxLength = 30;
            if (content.Length > maxLength) {
                content = content.Substring(0, maxLength - 3) + "...";
            }

            // Escape special characters that conflict with path format
            content = content.Replace('"', '\'');   // Replace quotes with single quotes
            content = content.Replace('>', '›');    // Replace path separator
            content = content.Replace('\r', ' ');   // Replace line breaks
            content = content.Replace('\n', ' ');
            content = content.Replace('\t', ' ');

            // Collapse multiple spaces
            while (content.Contains("  ")) {
                content = content.Replace("  ", " ");
            }

            return content;
        }

		/// <summary>
		/// Computes the SimpleName for a FrameworkElement.
		/// SimpleName format: "Type:Name:HWND" for roots (Window, PopupRoot).
		/// </summary>
		/// <param name="fe">The fe FrameworkElement</param>
		/// <returns>SimpleName string (e.g., "Window:MainWindow", "PopupRoot:(unnamed):3E0A44")</returns>
		internal static string ComputeSimpleName(FrameworkElement fe)
		{
			if (fe == null) return null;

			var typeName = fe.GetType().Name;
			var elementName = string.IsNullOrEmpty(fe.Name) ? "(unnamed)" : fe.Name;

			// Start with simple Name:Type format
			string simpleName = $"{elementName}:{typeName}";

			// ✓ For top-level elements (Window, PopupRoot), append WindowHandle as third component
			// This makes each window unique: (unnamed):PopupRoot:12345
			bool isTopLevel = VisualTreeHelper.GetParent(fe) == null;

            // Add WHWND for top level elements
			if (isTopLevel) {
				try {
					var hwndSource = PresentationSource.FromVisual(fe) as System.Windows.Interop.HwndSource;
					if (hwndSource != null) {
						var hwnd = hwndSource.Handle.ToInt32();
						simpleName = $"{simpleName}:{hwnd:X}";

						if (VerboseDebug) {
							Debug.WriteLine($"[NavNode] ComputeDefaultId: Top-level {typeName} has HWND, ID={simpleName}");
						}
					}
				} catch {
					// If we can't get HWND, just use the baseId without it
					// This can happen if element isn't fully initialized yet
				}
			}

			// Add content for unnamed LEAF elements for further disambiguation
			if (string.IsNullOrEmpty(fe.Name) && !isTopLevel && IsLeafType(fe.GetType())) {
				var content = ExtractElementContent(fe);
				if (!string.IsNullOrWhiteSpace(content)) {
					content = SanitizeContent(content);
					if (!string.IsNullOrWhiteSpace(content)) {
						simpleName = $"{simpleName}[\"{content}\"]";
					}
				}
			}

			return simpleName;
		}

		/// <summary>
		/// Gets the hierarchical path of an element in the visual tree.
		/// Format: Name:Type[:HWND]["Content"] > ChildName:ChildType["Content"] > ...
		/// 
		/// ✓ CONTENT DISAMBIGUATION: For unnamed leaf nodes, appends ["Content"] to make paths unique.
		/// This allows distinguishing between buttons like "OK" vs "Cancel" or list items like "Germany" vs "Italy".
		/// 
		/// ✓ HWND FOR TOP-LEVEL: Includes WindowHandle for top-level elements (Window, PopupRoot).
		/// Each WPF Popup creates its own OS-level window (HWND), ensuring unique paths for different popups.
		/// 
		/// Example paths:
		/// - Named button: "OkButton:Button" (no content needed - name is unique)
		/// - Unnamed button: "(unnamed):Button["OK"]" (content distinguishes from Cancel)
		/// - Main menu: "(unnamed):PopupRoot:3F4A21B > (unnamed):MenuItem["File"]"
		/// - Submenu: "(unnamed):PopupRoot:5C8D943 > (unnamed):MenuItem["Open"]"
		/// 
		/// Handles both NavNode elements (uses their SimpleName) and non-NavNode intermediate elements.
		/// </summary>
		public static string GetHierarchicalPath(FrameworkElement fe, FrameworkElement rootFe = null)
        {
            var path = new List<string>();

            try {
                DependencyObject current = fe;
                
                while (current != null) {
                    if (current is FrameworkElement currentFe) {
                        // Check if this element is a NavNode (has been discovered)
                        if (Observer.TryGetNavNode(currentFe, out var existingNode, rootFe)) {
							// Great! Use its pre-computed hierarchical path. We are DONE...
							path.Insert(0, existingNode.HierarchicalPath);
                            break;
                        } else {
                            // Not a NavNode - compute path segment manually
                            // This handles intermediate elements in the visual tree
							path.Insert(0, ComputeSimpleName(currentFe));
                        }
                        
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
        /// Full hierarchical path from fe to this element.
        /// Format: "WindowName:Window > PanelName:StackPanel > ButtonName:Button"
        /// 
        /// Used by PathFilter for pattern matching and as unique identifier.
        /// Computed once during creation from the full visual tree.
        /// </summary>
        public string HierarchicalPath { get; internal set; }

        /// <summary>
        /// Parent node in the visual tree.
        /// Null if this is a fe node (Window/PresentationSource fe).
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
        /// Captures the intermediate visual tree path from this element to its currentFe NavNode.
        /// Stored during LinkToParent() for diagnostic purposes.
        /// 
        /// Format: List of weak references to FrameworkElements between this node and its currentFe.
        /// - Does NOT include this node's element (accessible via VisualRef)
        /// - Does NOT include currentFe NavNode's element (accessible via Parent.VisualRef)
        /// - Only contains intermediate non-NavNode FrameworkElements
        /// 
        /// Index 0 = immediate visual currentFe (non-NavNode)
        /// Index 1 = currentFe's visual currentFe (non-NavNode)
        /// ...
        /// Last index = last intermediate element before currentFe NavNode
        /// 
        /// NULL or empty if this node is a direct child of its currentFe NavNode (no intermediates).
        /// NULL if this is a fe node (no currentFe).
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
        /// True if this node is a modal container that creates a new navigation scope.
        /// Modal nodes get their own context in the navigation stack.
        /// </summary>
        public bool IsModal { get; private set; }

        /// <summary>
        /// True if this node is a root in Observer. It implies it does not have a
        /// parent in the VisualTree, meaning that it is a PresentationSource root.
        /// </summary>
        public bool IsRoot { get; private set; }

        /// <summary>
        /// True if this node is a PageSelector (non-modal with PageName).
        /// PageSelector nodes trigger StreamDeck page switches when they become visible.
        /// </summary>
        public bool IsPageSelector => !IsModal && !string.IsNullOrEmpty(PageName);

        // ========== StreamDeck Properties (from NavNodeClassification) =========="

        /// <summary>
        /// The StreamDeck page name to switch to when this node becomes active.
        /// - For modals: Used by SwitchStreamDeckPageForModal()
        /// - For PageSelectors: Used by OnPageSelectorActivated()
        /// </summary>
        public string PageName { get; internal set; }
        
        /// <summary>
        /// Shortcut key name for StreamDeck button mapping (e.g., "QuickChangeCar").
        /// </summary>
        public string KeyName { get; internal set; }
        
        /// <summary>
        /// Display title for StreamDeck button (e.g., "Change Car").
        /// </summary>
        public string KeyTitle { get; internal set; }
        
        /// <summary>
        /// Icon specification for StreamDeck button (file path or icon name).
        /// </summary>
        public string KeyIcon { get; internal set; }
        
        // ========== Interaction Properties (from NavNodeClassification) =========="
        
        /// <summary>
        /// Whether to skip auto-click when shortcut is activated (default: false).
        /// </summary>
        public bool NoAutoClick { get; internal set; }
        
        /// <summary>
        /// What this shortcut targets (Element or Group).
        /// </summary>
        public ShortcutTargetType TargetType { get; internal set; }
        
        /// <summary>
        /// Whether to require confirmation before executing (default: false).
        /// </summary>
        public bool RequireConfirmation { get; internal set; }
        
        /// <summary>
        /// Custom confirmation message (optional).
        /// </summary>
        public string ConfirmationMessage { get; internal set; }

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

        public NavNode(FrameworkElement fe, string simpleName, string hierarchicalPath, bool isGroup = false, bool isModal = false, bool isRoot = false)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));
            if (string.IsNullOrEmpty(simpleName)) throw new ArgumentException($"SimpleName cannot be null or empty");
            if (string.IsNullOrEmpty(hierarchicalPath)) throw new ArgumentException($"HierarchicalPath cannot be null or empty");

            VisualRef = new WeakReference<FrameworkElement>(fe);
            SimpleName = simpleName;
            HierarchicalPath = hierarchicalPath;
            IsGroup = isGroup;
            IsModal = isModal;
            IsRoot = isRoot;
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
        /// ✓ CHANGED: Removed IsVisible check to support elements in non-active tabs.
        /// WPF's IsVisible returns false for tab content that's loaded but not currently selected,
        /// even though the elements are rendered and have valid screen coordinates.
        /// 
        /// ✓ SEMANTIC: Returns DIP (Device Independent Pixels) in screen-absolute coordinate space.
        /// This is the correct coordinate system for WPF UI elements and overlay positioning.
        /// Mouse input code should convert DIP → device pixels as needed.
        /// 
        /// ✓ ADDED: IsArrangeValid check to wait for WPF layout completion.
        /// This prevents calculating coordinates before popup/dropdown positioning is finalized.
        /// 
        /// ✓ COORDINATE FLOW:
        /// 1. PointToScreen() returns device pixels (screen-absolute)
        /// 2. TransformFromDevice converts device pixels → DIP
        /// 3. Return DIP (screen-absolute) for UI overlay positioning
        /// </summary>
        public Point? GetCenterDip()
        {
            if (!TryGetVisual(out var fe))
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Visual reference dead for {SimpleName}");
                return null;
            }

            // ✓ CHANGED: Only check IsLoaded, not IsVisible
            // Elements in inactive tabs have IsVisible=false but are still navigable
            if (!fe.IsLoaded)
            {
                if (VerboseDebug) Debug.WriteLine($"[NavNode] GetCenterDip failed: Not loaded - {SimpleName}");
                return null;
            }

            // ✓ NEW: Check if layout is complete before calculating coordinates
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
                // ✓ Step 1: Get element corners in device pixels (screen-absolute)
                var topLeftDevice = fe.PointToScreen(new Point(0, 0));
                var bottomRightDevice = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

                // ✓ Step 2: Transform device pixels → DIP (screen-absolute)
                var transform = ps.CompositionTarget.TransformFromDevice;
                var topLeftDip = transform.Transform(topLeftDevice);
                var bottomRightDip = transform.Transform(bottomRightDevice);

                // ✓ Step 3: Calculate center in DIP
                var centerX = (topLeftDip.X + bottomRightDip.X) / 2.0;
                var centerY = (topLeftDip.Y + bottomRightDip.Y) / 2.0;

                // ✓ Viewport bounds check in DIP coordinates
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
        /// 
        /// Uses mouse-click simulation for all single-action controls to provide
        /// the most reliable activation by letting WPF handle the full event chain:
        ///   - Button/RepeatButton: Click event with full state management
        ///   - ToggleButton/CheckBox/RadioButton: Toggle with proper visual states
        ///   - MenuItem/Menu: Full WPF internal state (_userInitiatedPress, IsPressed, timers)
        ///   - ListBoxItem/ComboBoxItem/TreeViewItem/TabItem: Selection + any attached behaviors
        /// 
        /// Direct property manipulation only for controls that don't benefit from click simulation:
        ///   - ComboBox/ContextMenu: Simple open/close (no complex state)
        ///   - Expander: Simple toggle (expand/collapse header click is complex)
        ///   - Slider/DoubleSlider/RoundSlider: Enter interaction mode for value adjustment
        /// </summary>
        public bool Activate()
        {
            if (!TryGetVisual(out var fe)) {
                return false;
            }

            try {
                // ✅ Interactive controls - enter interaction mode
                // Check by type name FIRST to handle derived types correctly
                var typeName = fe.GetType().Name;
                
                if (typeName == "RoundSlider")
                {
                    return Navigator.EnterInteractionMode(this);
                }
                
                if (typeName == "DoubleSlider")
                {
                    return Navigator.EnterInteractionMode(this);
                }
                
                // Check base Slider type last (after derived types)
                if (fe is Slider)
                {
                    return Navigator.EnterInteractionMode(this);
                }
                
                // ✅ Single-action controls - use mouse click simulation
                // This triggers the full WPF event chain (PreviewMouseDown → MouseDown → Click)
                // which ensures all behaviors, animations, and handlers are invoked properly
                
                if (fe is Button
                        || fe is RepeatButton
                        || fe is ToggleButton  // Includes CheckBox, RadioButton
                        || fe is MenuItem
                        || fe is Menu
                        || fe is ListBoxItem
                        || fe is ComboBoxItem
                        || fe is TreeViewItem
                        || fe is TabItem
                        || fe is ContextMenuButton) {
                    return SimulateMouseClick(fe);
                }

                // Direct property manipulation for simple open/close controls
                if (fe is ComboBox comboBox) {
                    comboBox.IsDropDownOpen = true;
                    return true;
                }

                if (fe is ContextMenu contextMenu) {
                    contextMenu.IsOpen = true;
                    return true;
                }

                // Expander - direct toggle (clicking the header is more complex than we need)
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
        /// ✓ COORDINATE FLOW:
        /// 1. GetCenterDip() returns DIP (screen-absolute)
        /// 2. TransformToDevice converts DIP → device pixels
        /// 3. Device pixels used for mouse input (SendInput API)
        /// 
        /// ✓ NO DELAY: Mouse position and click sent immediately (0ms delay).
        /// If clicks are missed, increase MousePositionSettleDelayMs constant.
        /// </summary>
        private bool SimulateMouseClick(FrameworkElement fe)
        {
            try {
				// ✓ Step 1: Force-release modifier keys before clicking
				// This ensures WPF doesn't interpret the click as Shift+Click (range selection)
				// even if the user is still physically holding down modifier keys from the debug hotkey

				var modifierKeysToRelease = new List<AcTools.Windows.User32.KeyboardInput>();

				// Check and release Shift keys
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.LShiftKey)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.LShiftKey,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp
					});
				}
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.RShiftKey)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.RShiftKey,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp
					});
				}

				// Check and release Ctrl keys
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.LControlKey)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.LControlKey,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp | AcTools.Windows.User32.KeyboardFlag.ExtendedKey
					});
				}
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.RControlKey)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.RControlKey,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp | AcTools.Windows.User32.KeyboardFlag.ExtendedKey
					});
				}

				// Check and release Alt keys
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.LMenu)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.LMenu,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp | AcTools.Windows.User32.KeyboardFlag.ExtendedKey
					});
				}
				if (AcTools.Windows.User32.IsAsyncKeyPressed(System.Windows.Forms.Keys.RMenu)) {
					modifierKeysToRelease.Add(new AcTools.Windows.User32.KeyboardInput
					{
						VirtualKeyCode = (ushort)System.Windows.Forms.Keys.RMenu,
						Flags = AcTools.Windows.User32.KeyboardFlag.KeyUp | AcTools.Windows.User32.KeyboardFlag.ExtendedKey
					});
				}

				// Send the key-up events if any modifiers were pressed
				if (modifierKeysToRelease.Count > 0) {
					AcTools.Windows.User32.SendInput(modifierKeysToRelease);

					// Small delay to let the key-up events be processed by Windows/WPF
					System.Threading.Thread.Sleep(20);

					if (VerboseDebug) {
						Debug.WriteLine($"[NavNode] Released {modifierKeysToRelease.Count} modifier keys before click");
					}
				}

				// ✓ Step 2: Get center in DIP (screen-absolute coordinate space)
				var centerDip = GetCenterDip();
                if (!centerDip.HasValue) {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] SimulateMouseClick failed: Could not get center point for {SimpleName}");
                    return false;
                }

                // ✓ Step 3: Get presentation source for DIP → device pixel conversion
                var ps = PresentationSource.FromVisual(fe);
                if (ps?.CompositionTarget == null) {
                    if (VerboseDebug) Debug.WriteLine($"[NavNode] SimulateMouseClick failed: No PresentationSource for {SimpleName}");
                    return false;
                }

                // ✓ Step 4: Convert DIP → device pixels (screen-absolute)
                var transformToDevice = ps.CompositionTarget.TransformToDevice;
                var centerDevice = transformToDevice.Transform(centerDip.Value);

                // ✓ Step 5: Get screen dimensions in device pixels for SendInput normalization
                // SendInput uses 0-65535 normalized coordinate space
                var screenWidthDevice = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                var screenHeightDevice = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

                // ✓ Step 6: Normalize to SendInput's 0-65535 coordinate space
                var absoluteX = (int)(centerDevice.X * 65536.0 / screenWidthDevice);
                var absoluteY = (int)(centerDevice.Y * 65536.0 / screenHeightDevice);

                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode] SimulateMouseClick: {SimpleName}");
                    Debug.WriteLine($"  DIP (screen-absolute): ({centerDip.Value.X:F1}, {centerDip.Value.Y:F1})");
                    Debug.WriteLine($"  Device pixels: ({centerDevice.X:F1}, {centerDevice.Y:F1})");
                    Debug.WriteLine($"  SendInput normalized: ({absoluteX}, {absoluteY})");
                }

                // ✓ Step 7: Simulate left mouse button press with realistic timing
                // Split into separate ButtonDown and ButtonUp with delay to mimic real human click
                _mouseSimulator.LeftButtonDown();
                
                // Delay between button down and up (mimics real human click)
                if (MouseButtonPressDelayMs > 0) {
                    System.Threading.Thread.Sleep(MouseButtonPressDelayMs);
                }
                
                _mouseSimulator.LeftButtonUp();

                if (VerboseDebug) {
                    Debug.WriteLine($"[NavNode] Click sent successfully to '{SimpleName}' (focus cleared, no move)");
                }

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
