using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Filters navigation nodes based on hierarchical path patterns.
    /// 
    /// Pattern syntax:
    /// - Name:Type         - Match specific name and type
    /// - *:Type            - Match any name with specific type
    /// - Name:*            - Match specific name with any type
    /// - *                 - Match any name and type (equivalent to *:*)
    /// - Name >> Child     - Match Child at any depth under Name
    /// - Name > Child      - Match Child as direct child of Name
    /// - * >> Target       - Match Target at any depth
    /// 
    /// Examples:
    /// - "QuickDrive:QuickDrive >> *:CheckBox"          -> All CheckBoxes anywhere under QuickDrive
    /// - "QuickDrive:QuickDrive > *:CheckBox"           -> Only direct CheckBox children of QuickDrive
    /// - "*:Expander > *:Button"                        -> Buttons directly inside any Expander
    /// - "Settings:Grid >> SaveButton:Button"           -> SaveButton at any depth under Settings
    /// - "* >> UserPresetsControl:* >> *:CheckBox"      -> CheckBoxes inside UserPresetsControl (at any depth on both sides)
    /// </summary>
    internal class NavPathFilter
    {
        private readonly List<FilterRule> _excludeRules = new List<FilterRule>();
        
        /// <summary>
        /// Add a path pattern to exclude from navigation.
        /// Nodes matching this pattern will not be navigable.
        /// </summary>
        public void AddExcludeRule(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            
            try
            {
                var rule = FilterRule.Parse(pattern);
                _excludeRules.Add(rule);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavPathFilter] Failed to parse pattern '{pattern}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Checks if a given visual tree path should be excluded from navigation.
        /// </summary>
        public bool IsExcluded(string visualTreePath)
        {
            if (string.IsNullOrWhiteSpace(visualTreePath)) return false;
            
            foreach (var rule in _excludeRules)
            {
                if (rule.Matches(visualTreePath))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Represents a parsed filter rule with segments and depth operators.
        /// </summary>
        private class FilterRule
        {
            private readonly List<Segment> _segments;
            
            private FilterRule(List<Segment> segments)
            {
                _segments = segments;
            }
            
            public static FilterRule Parse(string pattern)
            {
                var segments = new List<Segment>();
                
                // Split by >> (any depth) or > (direct child)
                // We need to preserve the separator type
                var parts = Regex.Split(pattern, @"(\s*>>\s*|\s*>\s*)");
                
                bool expectSegment = true;
                DepthMode nextDepth = DepthMode.Direct;
                
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    
                    if (trimmed == ">>")
                    {
                        nextDepth = DepthMode.AnyDepth;
                        expectSegment = true;
                    }
                    else if (trimmed == ">")
                    {
                        nextDepth = DepthMode.Direct;
                        expectSegment = true;
                    }
                    else if (expectSegment)
                    {
                        segments.Add(new Segment(trimmed, nextDepth));
                        expectSegment = false;
                        nextDepth = DepthMode.Direct; // Reset to direct for next segment
                    }
                }
                
                if (segments.Count == 0)
                    throw new ArgumentException("Pattern must contain at least one segment");
                
                return new FilterRule(segments);
            }
            
            public bool Matches(string visualTreePath)
            {
                if (string.IsNullOrWhiteSpace(visualTreePath)) return false;
                
                // Split the path into node segments
                var pathNodes = visualTreePath.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
                
                return MatchesRecursive(pathNodes, 0, 0);
            }
            
            private bool MatchesRecursive(string[] pathNodes, int pathIndex, int segmentIndex)
            {
                // If we've matched all segments, check if we've also consumed all path nodes
                if (segmentIndex >= _segments.Count)
                {
                    // Success only if we've consumed the entire path (exact match)
                    return pathIndex >= pathNodes.Length;
                }
                
                // If we've run out of path nodes but still have segments to match, fail
                if (pathIndex >= pathNodes.Length)
                    return false;
                
                var segment = _segments[segmentIndex];
                var currentNode = pathNodes[pathIndex];
                
                // Check if current node matches the segment
                bool nodeMatches = segment.MatchesNode(currentNode);
                
                if (segment.Depth == DepthMode.AnyDepth)
                {
                    // Try matching at current position
                    if (nodeMatches && MatchesRecursive(pathNodes, pathIndex + 1, segmentIndex + 1))
                        return true;
                    
                    // Try skipping this node (any depth means we can skip multiple levels)
                    if (MatchesRecursive(pathNodes, pathIndex + 1, segmentIndex))
                        return true;
                    
                    return false;
                }
                else // Direct child
                {
                    // Must match at this exact position
                    if (!nodeMatches)
                        return false;
                    
                    return MatchesRecursive(pathNodes, pathIndex + 1, segmentIndex + 1);
                }
            }
        }
        
        /// <summary>
        /// Represents a single segment in a filter pattern (Name:Type).
        /// </summary>
        private class Segment
        {
            private readonly string _namePattern;
            private readonly string _typePattern;
            public DepthMode Depth { get; }
            
            public Segment(string pattern, DepthMode depth)
            {
                Depth = depth;
                
                // Parse Name:Type or just Type or just Name
                var colonIndex = pattern.IndexOf(':');
                if (colonIndex >= 0)
                {
                    _namePattern = pattern.Substring(0, colonIndex).Trim();
                    _typePattern = pattern.Substring(colonIndex + 1).Trim();
                }
                else
                {
                    // If no colon, treat as name pattern with wildcard type
                    _namePattern = pattern.Trim();
                    _typePattern = "*";
                }
                
                // Empty patterns become wildcards
                if (string.IsNullOrEmpty(_namePattern)) _namePattern = "*";
                if (string.IsNullOrEmpty(_typePattern)) _typePattern = "*";
            }
            
            public bool MatchesNode(string node)
            {
                // Node format is "Name:Type"
                var colonIndex = node.IndexOf(':');
                if (colonIndex < 0) return false;
                
                var nodeName = node.Substring(0, colonIndex);
                var nodeType = node.Substring(colonIndex + 1);
                
                bool nameMatches = MatchesPattern(_namePattern, nodeName);
                bool typeMatches = MatchesPattern(_typePattern, nodeType);
                
                return nameMatches && typeMatches;
            }
            
            private bool MatchesPattern(string pattern, string value)
            {
                if (pattern == "*") return true;
                
                // For now, simple exact match (case-insensitive)
                // Could be extended to support glob patterns like "Button*" etc.
                return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            }
        }
        
        private enum DepthMode
        {
            Direct,      // > (immediate child)
            AnyDepth     // >> (any descendant)
        }
    }
}
