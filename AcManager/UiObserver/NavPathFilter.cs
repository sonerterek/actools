using System;
using System.Collections.Generic;
using System.Linq;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Navigation node roles - determines how a node is treated during navigation.
    /// </summary>
    public enum NavRole
    {
        Undefined,      // Use type-based detection (fallback)
        Leaf,           // Force as navigation leaf (selectable target)
        Group           // Force as pure group (container, not selectable)
    }

    /// <summary>
    /// Classification override for a navigation node.
    /// Applied when hierarchical path matches a classification rule.
    /// This is the SINGLE source of truth for all node metadata - no special cases.
    /// </summary>
    public class NavNodeClassification
    {
        // ========== Navigation Properties =========
        
        /// <summary>Override the role for THIS element.</summary>
        public NavRole Role { get; set; } = NavRole.Undefined;
        
        /// <summary>Override modal behavior for THIS element.</summary>
        public bool IsModal { get; set; }
        
        // ========== StreamDeck Properties =========
        
        /// <summary>StreamDeck page to switch to when this node appears or opens (non-modal) or as modal.</summary>
        public string PageName { get; set; }
        
        /// <summary>Shortcut key name for StreamDeck button mapping (e.g., "QuickChangeCar").</summary>
        public string KeyName { get; set; }
        
        /// <summary>Display title for StreamDeck button (e.g., "Change Car").</summary>
        public string KeyTitle { get; set; }
        
        /// <summary>Icon specification for StreamDeck button (file path or icon name).</summary>
        public string KeyIcon { get; set; }
        
        // ========== Interaction Properties =========
        
        /// <summary>Whether to skip auto-click when shortcut is activated (default: false).</summary>
        public bool NoAutoClick { get; set; }
        
        /// <summary>What this shortcut targets (Element or Group). Type defined in NavConfig.cs.</summary>
        public ShortcutTargetType TargetType { get; set; }
        
        /// <summary>Whether to require confirmation before executing (default: false).</summary>
        public bool RequireConfirmation { get; set; }
        
        /// <summary>Custom confirmation message (optional).</summary>
        public string ConfirmationMessage { get; set; }
        
        // Factory methods for common cases (backward compatibility)
        public static NavNodeClassification AsLeaf() => new NavNodeClassification { Role = NavRole.Leaf };
        public static NavNodeClassification AsGroup(bool? modal = null) => new NavNodeClassification { Role = NavRole.Group, IsModal = modal ?? false };
        public static NavNodeClassification WithModality(bool isModal) => new NavNodeClassification { IsModal = isModal };
    }
    
    /// <summary>
    /// Pure pattern matching utility for navigation paths.
    /// Stateless - contains no rule storage, only matching algorithms.
    /// 
    /// Pattern syntax:
    /// - Name:Type         - Match specific name and type
    /// - *                 - Match any single element (name or type)
    /// - **                - Match 0+ elements (any depth including current level)
    /// - ***               - Match 1+ elements (at least one ancestor away)
    /// - > separator       - Parent-child relationship
    /// 
    /// Examples:
    /// Window:MainWindow > ** > PART_Menu:ModernMenu
    /// ** > SettingsPanel:Border
    /// *** > QuickFilter:ComboBox
    /// </summary>
    internal static class NavPathFilter
    {
        /// <summary>
        /// Checks if a hierarchical path matches a pattern string.
        /// </summary>
        /// <param name="hierarchicalPath">Full hierarchical path (e.g., "Window:MainWindow > Content:Border > Button:Button")</param>
        /// <param name="pattern">Pattern with wildcards (e.g., "** > Button:Button")</param>
        /// <returns>True if pattern matches path</returns>
        public static bool Matches(string hierarchicalPath, string pattern)
        {
            if (string.IsNullOrWhiteSpace(hierarchicalPath) || string.IsNullOrWhiteSpace(pattern))
                return false;
            
            try
            {
                var segments = ParsePattern(pattern);
                var pathNodes = hierarchicalPath.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
                
                return MatchesRecursive(pathNodes, 0, segments, 0);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse a pattern string into segments.
        /// Handles *, **, *** and Name:Type syntax.
        /// </summary>
        private static List<Segment> ParsePattern(string pattern)
        {
            var segments = new List<Segment>();
            
            // Split by '>' separator
            var parts = pattern.Split(new[] { '>' }, StringSplitOptions.None);
            
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                // Check for depth wildcards
                DepthMode depth;
                if (trimmed == "***")
                {
                    depth = DepthMode.OneOrMore;
                    segments.Add(new Segment("*", "*", depth));
                }
                else if (trimmed == "**")
                {
                    depth = DepthMode.ZeroOrMore;
                    segments.Add(new Segment("*", "*", depth));
                }
                else
                {
                    depth = DepthMode.Exact;
                    
                    // Parse Name:Type
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        var name = trimmed.Substring(0, colonIndex).Trim();
                        var type = trimmed.Substring(colonIndex + 1).Trim();
                        segments.Add(new Segment(
                            string.IsNullOrEmpty(name) ? "*" : name,
                            string.IsNullOrEmpty(type) ? "*" : type,
                            depth
                        ));
                    }
                    else
                    {
                        // No colon - treat as wildcard match for both
                        if (trimmed == "*")
                        {
                            segments.Add(new Segment("*", "*", depth));
                        }
                        else
                        {
                            // Treat as name with wildcard type
                            segments.Add(new Segment(trimmed, "*", depth));
                        }
                    }
                }
            }
            
            if (segments.Count == 0)
                throw new ArgumentException("Pattern must contain at least one segment");
            
            return segments;
        }
        
        /// <summary>
        /// Recursive pattern matching algorithm.
        /// Handles **, *** depth wildcards and exact last-segment matching.
        /// </summary>
        private static bool MatchesRecursive(string[] pathNodes, int pathIndex, List<Segment> segments, int segmentIndex)
        {
            // All segments consumed?
            if (segmentIndex >= segments.Count)
            {
                // Success: all path nodes must also be consumed (exact match)
                return pathIndex >= pathNodes.Length;
            }
            
            // Path exhausted but segments remain?
            if (pathIndex >= pathNodes.Length)
            {
                // Only succeed if remaining segments are all ** (zero-or-more)
                for (int i = segmentIndex; i < segments.Count; i++)
                {
                    if (segments[i].Depth != DepthMode.ZeroOrMore)
                        return false;
                }
                return true;
            }
            
            var segment = segments[segmentIndex];
            var currentNode = pathNodes[pathIndex];
            bool isLastSegment = (segmentIndex == segments.Count - 1);
            
            switch (segment.Depth)
            {
                case DepthMode.Exact:
                    // Must match at this exact position
                    if (!segment.MatchesNode(currentNode))
                        return false;
                    
                    // If this is the last segment, we must also be at the last path node
                    if (isLastSegment && pathIndex != pathNodes.Length - 1)
                        return false;
                    
                    return MatchesRecursive(pathNodes, pathIndex + 1, segments, segmentIndex + 1);
                
                case DepthMode.ZeroOrMore: // **
                    // Try matching at current position
                    if (segment.MatchesNode(currentNode))
                    {
                        // Try consuming this segment (greedy match)
                        if (MatchesRecursive(pathNodes, pathIndex + 1, segments, segmentIndex + 1))
                            return true;
                    }
                    
                    // Try skipping current path node (zero-or-more allows skipping)
                    if (MatchesRecursive(pathNodes, pathIndex + 1, segments, segmentIndex))
                        return true;
                    
                    // Try moving to next segment without consuming path node
                    // (this handles ** matching zero elements)
                    if (MatchesRecursive(pathNodes, pathIndex, segments, segmentIndex + 1))
                        return true;
                    
                    return false;
                
                case DepthMode.OneOrMore: // ***
                    // Must skip at least one node before trying to match
                    // Try skipping current node
                    if (MatchesRecursive(pathNodes, pathIndex + 1, segments, segmentIndex))
                        return true;
                    
                    // After skipping at least one, try to match (convert to ** behavior)
                    var modifiedSegments = new List<Segment>(segments);
                    modifiedSegments[segmentIndex] = new Segment(segment.NamePattern, segment.TypePattern, DepthMode.ZeroOrMore);
                    
                    // Must skip at least one node
                    if (pathIndex + 1 < pathNodes.Length)
                    {
                        if (MatchesRecursive(pathNodes, pathIndex + 1, modifiedSegments, segmentIndex))
                            return true;
                    }
                    
                    return false;
                
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// Represents a single segment in a pattern (Name:Type with depth mode).
        /// </summary>
        private class Segment
        {
            public string NamePattern { get; }
            public string TypePattern { get; }
            public DepthMode Depth { get; }
            
            public Segment(string namePattern, string typePattern, DepthMode depth)
            {
                NamePattern = namePattern ?? "*";
                TypePattern = typePattern ?? "*";
                Depth = depth;
            }
            
            public bool MatchesNode(string node)
            {
                // Valid node formats:
                // - "Name:Type"
                // - "Name:Type:HWND"
                // - "Name:Type[Content]"
                
                // Pattern formats:
                // - "Name:Type" - matches both "Name:Type" and "Name:Type[Content]"
                // - "Name:Type[Content]" - only matches "Name:Type[Content]"
                
                // Split by colons
                var parts = node.Split(':');
                if (parts.Length < 2) return false;
                
                string nodeName = parts[0];
                string nodeType = parts[1];
                // parts[2] is HWND (ignored)
                
                // Check if patterns have [Content] attribute
                bool typePatternHasContent = TypePattern.IndexOf('[') >= 0;
                
                // If pattern doesn't have [Content], strip it from node for matching
                if (!typePatternHasContent)
                {
                    int bracketIndex = nodeType.IndexOf('[');
                    if (bracketIndex >= 0)
                    {
                        nodeType = nodeType.Substring(0, bracketIndex);
                    }
                }
                // If pattern HAS [Content], keep nodeType as-is for exact match
                
                // Match against patterns
                bool nameMatches = MatchesPattern(NamePattern, nodeName);
                bool typeMatches = MatchesPattern(TypePattern, nodeType);
                
                return nameMatches && typeMatches;
            }
            
            private bool MatchesPattern(string pattern, string value)
            {
                if (pattern == "*") return true;
                
                // Exact match (case-insensitive)
                return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            }
        }
        
        private enum DepthMode
        {
            Exact,        // No wildcard - must match at this position
            ZeroOrMore,   // ** - match 0+ elements (any depth including current)
            OneOrMore     // *** - match 1+ elements (at least one ancestor away)
        }
    }
}
