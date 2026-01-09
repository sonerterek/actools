using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AcManager.UiObserver
{
	#region Configuration Models

	/// <summary>
	/// Specifies what a shortcut targets when activated.
	/// </summary>
	public enum ShortcutTargetType
	{
		/// <summary>Targets a specific element (default)</summary>
		Element,
		
		/// <summary>Targets first navigable child of a group container</summary>
		Group
	}

	/// <summary>
	/// Runtime shortcut key definition for StreamDeck button execution.
	/// Contains only properties needed during shortcut execution.
	/// Bound to a NavNode at runtime when the node is discovered.
	/// Built from NavClassifier rules during Navigator initialization.
	/// </summary>
	internal class NavShortcutKey
	{
		/// <summary>Unique key name (e.g., "QD_ChangeCar")</summary>
		public string KeyName { get; set; }
		
		/// <summary>Display title on StreamDeck button (e.g., "Change Car")</summary>
		public string KeyTitle { get; set; }
		
		/// <summary>Icon specification (file path or icon name)</summary>
		public string KeyIcon { get; set; }
		
		/// <summary>Whether to skip auto-click when activated (default: false)</summary>
		public bool NoAutoClick { get; set; }
		
		/// <summary>What this shortcut targets (Element vs Group)</summary>
		public ShortcutTargetType TargetType { get; set; }
		
		/// <summary>Whether to require confirmation before executing (default: false)</summary>
		public bool RequireConfirmation { get; set; }
		
		/// <summary>Custom confirmation message (optional)</summary>
		public string ConfirmationMessage { get; set; }
		
		/// <summary>
		/// Runtime binding to discovered NavNode.
		/// Null if element not currently in scope.
		/// Set by Navigator during NodesUpdated processing.
		/// </summary>
		public NavNode BoundNode { get; set; }
		
		public override string ToString()
		{
			return $"Key:{KeyName} Title:{KeyTitle} Bound:{BoundNode != null}";
		}
	}

	/// <summary>
	/// Represents a classification rule from config file (CLASSIFY statement).
	/// Used by Observer to match and classify discovered nodes.
	/// Contains pattern matching and ALL metadata (shortcut properties, modal flags, page names).
	/// </summary>
	public class NavClassifier
	{
		/// <summary>Unique key name (e.g., "QuickChangeCar")</summary>
		public string KeyName { get; set; }

		/// <summary>Display title on StreamDeck button (e.g., "Change Car")</summary>
		public string KeyTitle { get; set; }

		/// <summary>Icon specification (file path or icon name)</summary>
		public string KeyIcon { get; set; }

		/// <summary>Hierarchical path filter with wildcard support</summary>
		public string PathFilter { get; set; }

		/// <summary>Element role (e.g., "modernbutton")</summary>
		public string Role { get; set; }
		
		/// <summary>Whether this element is a modal container</summary>
		public bool IsModal { get; set; }
		
		/// <summary>StreamDeck page name to switch to when this element gets focus or opens as modal</summary>
		public string PageName { get; set; }

		/// <summary>Whether to skip auto-click when this shortcut is activated (default: false)</summary>
		public bool NoAutoClick { get; set; }

		/// <summary>What this shortcut targets (default: Element)</summary>
		public ShortcutTargetType TargetType { get; set; }
		
		/// <summary>Whether to require confirmation before executing this shortcut (default: false)</summary>
		public bool RequireConfirmation { get; set; }
		
		/// <summary>Custom confirmation message (optional, defaults to "Execute {KeyName}")</summary>
		public string ConfirmationMessage { get; set; }

		/// <summary>Compiled regex pattern for path matching (null if no wildcards)</summary>
		public Regex PathPattern { get; set; }

		/// <summary>
		/// Checks if this shortcut matches the given element path.
		/// Supports wildcards: * matches any single segment, ** matches any number of segments.
		/// </summary>
		public bool Matches(string elementPath)
		{
			if (string.IsNullOrEmpty(elementPath) || string.IsNullOrEmpty(PathFilter))
				return false;

			// If pattern is compiled, use regex matching
			if (PathPattern != null)
			{
				return PathPattern.IsMatch(elementPath);
			}

			// Otherwise, exact match (no wildcards)
			return string.Equals(PathFilter, elementPath, StringComparison.OrdinalIgnoreCase);
		}

		public override string ToString()
		{
			return $"Key:{KeyName} Title:{KeyTitle} Path:{PathFilter} TargetType:{TargetType}";
		}
	}

	/// <summary>
	/// Represents a custom page definition.
	/// </summary>
	public class NavPageDef
	{
		/// <summary>Page name (e.g., "QuickDrive")</summary>
		public string PageName { get; set; }
		
		/// <summary>Base page name for inheritance (e.g., "Navigation"), null if no inheritance</summary>
		public string BasePageName { get; set; }

		/// <summary>5x3 grid of key names (null for empty slots)</summary>
		public string[][] KeyGrid { get; set; }

		public NavPageDef()
		{
			KeyGrid = new string[5][];
			for (int i = 0; i < 5; i++)
			{
				KeyGrid[i] = new string[3];
			}
		}
		
		/// <summary>
		/// Gets the full page name with inheritance syntax if applicable.
		/// Format: "PageName:BasePage" or just "PageName" if no base.
		/// Used for DefinePage command.
		/// </summary>
		public string GetFullPageName()
		{
			if (!string.IsNullOrEmpty(BasePageName))
			{
				return $"{PageName}:{BasePageName}";
			}
			return PageName;
		}

		public override string ToString()
		{
			if (!string.IsNullOrEmpty(BasePageName))
			{
				return $"Page:{PageName}:{BasePageName} (5x3 grid)";
			}
			return $"Page:{PageName} (5x3 grid)";
		}
	}

	#endregion
	#region Configuration Parser

	#endregion

	#region Configuration Container

	/// <summary>
	/// Container for parsed navigation configuration.
	/// </summary>
	public class NavConfiguration
	{
		/// <summary>Exclusion patterns for filtering out elements from navigation</summary>
		private readonly List<string> _exclusionPatterns = new List<string>();
		
		/// <summary>
		/// Classification rules loaded from configuration file.
		/// Includes: modal markers, page selectors, and shortcut key definitions.
		/// Used by Observer for node classification and Navigator for building runtime shortcuts.
		/// </summary>
		public List<NavClassifier> Classifications { get; set; }

		/// <summary>Custom page definitions</summary>
		public List<NavPageDef> Pages { get; set; }

		public NavConfiguration()
		{
			Classifications = new List<NavClassifier>();
			Pages = new List<NavPageDef>();
		}
		
		/// <summary>
		/// Adds an exclusion pattern to the configuration.
		/// Used during initialization to add built-in exclusion rules.
		/// </summary>
		public void AddExclusionPattern(string pattern)
		{
			if (string.IsNullOrWhiteSpace(pattern)) return;
			_exclusionPatterns.Add(pattern);
		}
		
		/// <summary>
		/// Checks if a hierarchical path should be excluded from navigation.
		/// Uses NavPathFilter for pattern matching against all exclusion patterns.
		/// </summary>
		public bool IsExcluded(string hierarchicalPath)
		{
			if (string.IsNullOrWhiteSpace(hierarchicalPath)) return false;
			
			// Check against all exclusion patterns
			foreach (var pattern in _exclusionPatterns)
			{
				if (NavPathFilter.Matches(hierarchicalPath, pattern))
				{
					return true;
				}
			}
			
			return false;
		}

		/// <summary>
		/// Finds a classifier by KeyName.
		/// </summary>
		public NavClassifier FindShortcut(string keyName)
		{
			return Classifications.FirstOrDefault(k => 
				string.Equals(k.KeyName, keyName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Finds classifications that match the given element path.
		/// </summary>
		public List<NavClassifier> FindClassificationsForPath(string elementPath)
		{
			return Classifications.Where(k => k.Matches(elementPath)).ToList();
		}
		
		/// <summary>
		/// Gets classification override for a given hierarchical path.
		/// Returns null if no matching classification found.
		/// Uses NavPathFilter for pure pattern matching.
		/// </summary>
		public NavNodeClassification GetClassification(string elementPath)
		{
			if (string.IsNullOrWhiteSpace(elementPath))
				return null;
			
			// Find FIRST matching classification (no merging!)
			var match = Classifications
				.FirstOrDefault(sk => NavPathFilter.Matches(elementPath, sk.PathFilter));
			
			if (match == null) return null;
			
			return ConvertToClassification(match);
		}
		
		/// <summary>
		/// Converts NavClassifier to NavNodeClassification.
		/// </summary>
		private static NavNodeClassification ConvertToClassification(NavClassifier rule)
		{
			return new NavNodeClassification
			{
				Role = ParseRole(rule.Role),
				IsModal = rule.IsModal,
				PageName = rule.PageName,
				KeyName = rule.KeyName,
				KeyTitle = rule.KeyTitle,
				KeyIcon = rule.KeyIcon,
				NoAutoClick = rule.NoAutoClick,
				TargetType = rule.TargetType,
				RequireConfirmation = rule.RequireConfirmation,
				ConfirmationMessage = rule.ConfirmationMessage
			};
		}
		
		/// <summary>
		/// Parses role string to NavRole enum.
		/// </summary>
		private static NavRole ParseRole(string roleString)
		{
			if (string.IsNullOrEmpty(roleString)) return NavRole.Undefined;
			if (roleString.Equals("leaf", StringComparison.OrdinalIgnoreCase)) return NavRole.Leaf;
			if (roleString.Equals("group", StringComparison.OrdinalIgnoreCase)) return NavRole.Group;
			return NavRole.Undefined;
		}
		
		/// <summary>
		/// Finds the page name to switch to when the given element gets focus.
		/// Returns just the PageName (without :BasePage suffix).
		/// </summary>
		public string FindPageForElement(string elementPath)
		{
			// Find first classification with PageName that matches this path
			var classification = Classifications
				.Where(c => !string.IsNullOrEmpty(c.PageName) && c.Matches(elementPath))
				.FirstOrDefault();
			
			if (classification == null)
				return null;
			
			// Return the PageName directly (it's already just the page name)
			return classification.PageName;
		}

		/// <summary>
		/// Finds a page definition by name (matches by PageName only).
		/// </summary>
		public NavPageDef FindPage(string pageName)
		{
			return Pages.FirstOrDefault(p => 
				string.Equals(p.PageName, pageName, StringComparison.OrdinalIgnoreCase));
		}
		
		/// <summary>
		/// Exports all classifications as CLASSIFY rule strings for unified parsing.
		/// This allows NavPathFilter to handle both built-in and config rules uniformly.
		/// </summary>
		public IEnumerable<string> ExportClassificationRules()
		{
			foreach (var rule in Classifications)
			{
				// Build properties list
				var props = new List<string>();
				
				// Navigation properties
				if (!string.IsNullOrEmpty(rule.Role))
					props.Add($"role={rule.Role}");
				if (rule.IsModal)
					props.Add("modal=true");
				
				// StreamDeck properties
				if (!string.IsNullOrEmpty(rule.PageName))
					props.Add($"PageName=\"{rule.PageName}\"");
				if (!string.IsNullOrEmpty(rule.KeyName))
					props.Add($"KeyName=\"{rule.KeyName}\"");
				if (!string.IsNullOrEmpty(rule.KeyTitle))
					props.Add($"KeyTitle=\"{rule.KeyTitle}\"");
				if (!string.IsNullOrEmpty(rule.KeyIcon))
					props.Add($"KeyIcon=\"{rule.KeyIcon}\"");
				
				// Interaction properties
				if (rule.NoAutoClick)
					props.Add("NoAutoClick=true");
				if (rule.TargetType == ShortcutTargetType.Group)
					props.Add("TargetType=Group");
				if (rule.RequireConfirmation)
					props.Add("RequireConfirmation=true");
				if (!string.IsNullOrEmpty(rule.ConfirmationMessage))
					props.Add($"ConfirmationMessage=\"{rule.ConfirmationMessage}\"");
				
				if (props.Count > 0)
				{
					yield return $"CLASSIFY: {rule.PathFilter} => {string.Join("; ", props)}";
				}
			}
		}
	}

	#endregion
}
