using System;
using System.Collections.Generic;
using System.Windows;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Base interface for all navigation nodes (both leaf elements and groups).
    /// </summary>
    internal interface INavNode
    {
        /// <summary>
        /// Unique identifier for this navigation node (based on Name, AutomationId, or fallback).
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Parent node in the navigation hierarchy. Null for root-level nodes.
        /// </summary>
        INavNode Parent { get; set; }

        /// <summary>
        /// Whether this node currently has focus in the navigation system.
        /// </summary>
        bool HasFocus { get; set; }

        /// <summary>
        /// Whether this node can currently be navigated to.
        /// For leaf elements: checks if element is alive, visible, and parent is navigable.
        /// For groups: false when open (children are navigable instead), true when closed.
        /// </summary>
        bool IsNavigable { get; }

        /// <summary>
        /// Gets the center point of this node in device-independent pixels (DIP).
        /// Returns null if the node is not currently navigable or has invalid bounds.
        /// </summary>
        Point? GetCenterDip();

        /// <summary>
        /// Attempts to get the underlying FrameworkElement.
        /// Returns false if the element has been garbage collected.
        /// </summary>
        bool TryGetVisual(out FrameworkElement fe);

        /// <summary>
        /// Activates this navigation node (performs its default action).
        /// For leaf nodes: clicks buttons, toggles checkboxes, selects items, etc.
        /// For closed dual-role groups: opens the group (ComboBox, ContextMenu).
        /// For open dual-role groups: no-op (children handle activation).
        /// Returns true if activation succeeded, false otherwise.
        /// </summary>
        bool Activate();
    }

    /// <summary>
    /// Marker interface for leaf navigation elements (buttons, textboxes, etc.).
    /// These are the actual targets of navigation - elements the user can "land on".
    /// </summary>
    internal interface INavLeaf : INavNode
    {
        // Leaf elements have no additional members beyond INavNode.
        // This interface serves as a type marker for distinguishing leaves from groups.
    }

    /// <summary>
    /// Interface for navigation groups (containers that can hold child navigation nodes).
    /// Examples: ComboBox dropdown, ContextMenu, Popup, MenuItem submenu.
    /// 
    /// Groups have dual behavior:
    /// - When closed: the group itself is navigable (user can navigate TO it)
    /// - When open: only children are navigable (user navigates WITHIN it)
    /// </summary>
    internal interface INavGroup : INavNode
    {
        /// <summary>
        /// Whether this group is currently open (active context).
        /// When true, children are navigable; when false, the group itself is navigable.
        /// </summary>
        bool IsOpen { get; set; }

        /// <summary>
        /// Gets all navigable children of this group.
        /// Returns empty if the group is closed or has no alive children.
        /// </summary>
        IEnumerable<INavNode> GetNavigableChildren();

        /// <summary>
        /// Adds a child node to this group.
        /// Sets the child's Parent property to this group.
        /// </summary>
        void AddChild(INavNode child);

        /// <summary>
        /// Removes all dead children (where WeakReference no longer resolves).
        /// Used for periodic cleanup to prevent memory leaks.
        /// </summary>
        void PruneDeadChildren();

        /// <summary>
        /// Closes this group if it's a dual-role group (ComboBox, ContextMenu).
        /// For pure container groups, this is a no-op.
        /// Returns true if the group was closed successfully, false otherwise.
        /// </summary>
        bool Close();
    }
}
