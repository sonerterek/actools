using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcManager.UiObserver
{
    /// <summary>
    /// Navigation node roles - determines how a node is treated during navigation.
    /// </summary>
    internal enum NavRole
    {
        Undefined,      // Use type-based detection (fallback)
        Leaf,           // Force as navigation leaf (selectable target)
        Group,          // Force as pure group (container, not selectable)
        DualGroup       // Force as dual-role group (ComboBox-like behavior)
    }

    /// <summary>
    /// Classification override for a navigation node.
    /// Applied when hierarchical path matches a classification rule.
    /// </summary>
    internal class NavNodeClassification
    {
        /// <summary>Override the role for THIS element.</summary>
        public NavRole Role { get; set; } = NavRole.Undefined;
        
        /// <summary>Override modal behavior for THIS element.</summary>
        public bool? IsModal { get; set; }
        
        /// <summary>Priority for rule application (higher = applied first).</summary>
        public int Priority { get; set; }
        
        // Factory methods for common cases
        public static NavNodeClassification AsLeaf() => new NavNodeClassification { Role = NavRole.Leaf };
        public static NavNodeClassification AsGroup(bool? modal = null) => new NavNodeClassification { Role = NavRole.Group, IsModal = modal };
        public static NavNodeClassification AsDualGroup(bool? modal = null) => new NavNodeClassification { Role = NavRole.DualGroup, IsModal = modal };
        public static NavNodeClassification WithModality(bool isModal) => new NavNodeClassification { IsModal = isModal };
    }

    /// <summary>
    /// Filters and classifies navigation nodes based on hierarchical path patterns.
    /// 
    /// Pattern syntax:
    /// - Name:Type         - Match specific name and type
    /// - *                 - Match any single element (name or type)
    /// - **                - Match 0+ elements (any depth including current level)
    /// - ***               - Match 1+ elements (at least one ancestor away)
    /// - > separator       - Parent-child relationship
    /// 
    /// Rule types:
    /// EXCLUDE: pattern              - Skip this element from navigation
    /// CLASSIFY: pattern => props    - Override element classification
    /// 
    /// Classification properties (semicolon-separated):
    /// - role=leaf|group|dual
    /// - modal=true|false
    /// - priority=number
    /// 
    /// Examples:
    /// EXCLUDE: Window:MainWindow > ** > PART_Menu:ModernMenu
    /// CLASSIFY: ** > SettingsPanel:Border => role=group; modal=false
    /// CLASSIFY: *** > QuickFilter:ComboBox => modal=false
    /// </summary>
    internal class NavPathFilter
    {
        private readonly List<ExcludeRule> _excludeRules = new List<ExcludeRule>();
        private readonly List<ClassificationRule> _classificationRules = new List<ClassificationRule>();
        
        /// <summary>
        /// Parse and register multiple rules from string array.
        /// Each line is one rule. Comments start with #.
        /// </summary>
        public void ParseRules(string[] rules)
        {
            if (rules == null) return;
            
            int lineNumber = 0;
            foreach (var line in rules)
            {
                lineNumber++;
                try
                {
                    ParseSingleRule(line);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NavPathFilter] Error parsing rule at line {lineNumber}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[NavPathFilter]   Rule: {line}");
                }
            }
        }
        
        /// <summary>
        /// Parse a single rule line.
        /// </summary>
        private void ParseSingleRule(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            
            var trimmed = line.Trim();
            
            // Skip comments
            if (trimmed.StartsWith("#")) return;
            
            if (trimmed.StartsWith("EXCLUDE:", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = trimmed.Substring(8).Trim();
                if (!string.IsNullOrEmpty(pattern))
                {
                    AddExcludeRule(pattern);
                }
            }
            else if (trimmed.StartsWith("CLASSIFY:", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = trimmed.Substring(9).Trim();
                var parts = remainder.Split(new[] { "=>" }, 2, StringSplitOptions.None);
                
                if (parts.Length == 2)
                {
                    var pattern = parts[0].Trim();
                    var classificationStr = parts[1].Trim();
                    
                    if (!string.IsNullOrEmpty(pattern) && !string.IsNullOrEmpty(classificationStr))
                    {
                        var classification = ParseClassification(classificationStr);
                        AddClassificationRule(pattern, classification);
                    }
                }
            }
        }
        
        /// <summary>
        /// Parse classification properties string.
        /// Format: "role=group; modal=false; priority=10"
        /// </summary>
        private NavNodeClassification ParseClassification(string classificationStr)
        {
            var result = new NavNodeClassification();
            
            // Split by semicolon
            var properties = classificationStr.Split(';');
            foreach (var prop in properties)
            {
                var trimmed = prop.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                var kvp = trimmed.Split('=');
                if (kvp.Length != 2) continue;
                
                var key = kvp[0].Trim().ToLowerInvariant();
                var value = kvp[1].Trim();
                
                switch (key)
                {
                    case "role":
                        if (Enum.TryParse<NavRole>(value, true, out var role))
                        {
                            result.Role = role;
                        }
                        break;
                    
                    case "modal":
                        if (bool.TryParse(value, out var modal))
                        {
                            result.IsModal = modal;
                        }
                        break;
                    
                    case "priority":
                        if (int.TryParse(value, out var priority))
                        {
                            result.Priority = priority;
                        }
                        break;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Add a path pattern to exclude from navigation.
        /// </summary>
        public void AddExcludeRule(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            
            try
            {
                var rule = ExcludeRule.Parse(pattern);
                _excludeRules.Add(rule);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavPathFilter] Failed to parse exclude pattern '{pattern}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Add a classification rule for path overrides.
        /// </summary>
        public void AddClassificationRule(string pattern, NavNodeClassification classification)
        {
            if (string.IsNullOrWhiteSpace(pattern) || classification == null) return;
            
            try
            {
                var rule = ClassificationRule.Parse(pattern, classification);
                
                // Insert sorted by priority (higher priority first)
                int insertIndex = _classificationRules.FindIndex(r => r.Classification.Priority < classification.Priority);
                if (insertIndex < 0)
                {
                    _classificationRules.Add(rule);
                }
                else
                {
                    _classificationRules.Insert(insertIndex, rule);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NavPathFilter] Failed to parse classification pattern '{pattern}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Checks if a given hierarchical path should be excluded from navigation.
        /// </summary>
        public bool IsExcluded(string hierarchicalPath)
        {
            if (string.IsNullOrWhiteSpace(hierarchicalPath)) return false;
            
            foreach (var rule in _excludeRules)
            {
                if (rule.Matches(hierarchicalPath))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Get classification override for a given hierarchical path.
        /// Returns null if no matching classification rule found.
        /// Returns the highest-priority matching rule if multiple match.
        /// </summary>
        public NavNodeClassification GetClassification(string hierarchicalPath)
        {
            if (string.IsNullOrWhiteSpace(hierarchicalPath)) return null;
            
            // Rules are already sorted by priority
            foreach (var rule in _classificationRules)
            {
                if (rule.Matches(hierarchicalPath))
                    return rule.Classification;
            }
            
            return null;
        }
        
        /// <summary>
        /// Represents an exclusion rule with pattern matching.
        /// </summary>
        private class ExcludeRule
        {
            private readonly List<Segment> _segments;
            
            private ExcludeRule(List<Segment> segments)
            {
                _segments = segments;
            }
            
            public static ExcludeRule Parse(string pattern)
            {
                var segments = ParsePattern(pattern);
                return new ExcludeRule(segments);
            }
            
            public bool Matches(string hierarchicalPath)
            {
                return MatchesPath(hierarchicalPath, _segments);
            }
        }
        
        /// <summary>
        /// Represents a classification rule with pattern matching and classification override.
        /// </summary>
        private class ClassificationRule
        {
            private readonly List<Segment> _segments;
            public NavNodeClassification Classification { get; }
            
            private ClassificationRule(List<Segment> segments, NavNodeClassification classification)
            {
                _segments = segments;
                Classification = classification;
            }
            
            public static ClassificationRule Parse(string pattern, NavNodeClassification classification)
            {
                var segments = ParsePattern(pattern);
                return new ClassificationRule(segments, classification);
            }
            
            public bool Matches(string hierarchicalPath)
            {
                return MatchesPath(hierarchicalPath, _segments);
            }
        }
        
        /// <summary>
        /// Parse a pattern string into segments.
        /// Handles *, **, ***, and Name:Type syntax.
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
        /// Match a hierarchical path against a list of pattern segments.
        /// </summary>
        private static bool MatchesPath(string hierarchicalPath, List<Segment> segments)
        {
            if (string.IsNullOrWhiteSpace(hierarchicalPath)) return false;
            
            // Split path into nodes
            var pathNodes = hierarchicalPath.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
            
            return MatchesRecursive(pathNodes, 0, segments, 0);
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
                // Node format is "Name:Type"
                var colonIndex = node.IndexOf(':');
                if (colonIndex < 0) return false;
                
                var nodeName = node.Substring(0, colonIndex);
                var nodeType = node.Substring(colonIndex + 1);
                
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
