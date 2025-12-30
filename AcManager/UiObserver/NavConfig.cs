using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

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
	/// Represents a shortcut key definition from CLASSIFY rule.
	/// </summary>
	public class NavShortcutKey
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

	/// <summary>
	/// Parser for NWRS Navigation configuration file.
	/// Supports CLASSIFY rules with shortcut properties and PAGE definitions.
	/// </summary>
	public class NavConfigParser
	{
		private const string ConfigFileName = "NWRS Navigation.cfg";

		/// <summary>
		/// Loads configuration from the standard location.
		/// Returns empty config if file doesn't exist or has errors.
		/// </summary>
		public static NavConfiguration Load()
		{
			try
			{
				var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				var configPath = Path.Combine(appDataPath, "AcTools Content Manager", ConfigFileName);

				if (!File.Exists(configPath))
				{
					Debug.WriteLine($"[NavConfig] Config file not found: {configPath}");
					return new NavConfiguration();
				}

				Debug.WriteLine($"[NavConfig] Loading config from: {configPath}");
				var content = File.ReadAllText(configPath);

				return Parse(content);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[NavConfig] Load failed: {ex.Message}");
				return new NavConfiguration();
			}
		}

		/// <summary>
		/// Parses configuration content and returns structured configuration.
		/// </summary>
		public static NavConfiguration Parse(string content)
		{
			var config = new NavConfiguration();

			if (string.IsNullOrWhiteSpace(content))
			{
				Debug.WriteLine("[NavConfig] Empty config content");
				return config;
			}

			var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			
			for (int i = 0; i < lines.Length; i++)
			{
				var line = lines[i].Trim();

				// Skip comments and empty lines
				if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
					continue;

				try
				{
					// ? FIX: Handle multi-line rules (lines ending with \)
					// This applies to BOTH CLASSIFY and PAGE rules
					while (line.EndsWith("\\") && i + 1 < lines.Length)
					{
						// Remove trailing backslash and trim
						line = line.Substring(0, line.Length - 1).Trim();
						i++;
						var nextLine = lines[i].Trim();
						
						// Skip comment lines but continue processing
						if (nextLine.StartsWith("#"))
							continue;
						
						// Concatenate with space separator
						line += " " + nextLine;
					}

					if (line.StartsWith("CLASSIFY:", StringComparison.OrdinalIgnoreCase))
					{
						var classification = ParseClassifyRule(line);
						if (classification != null)
						{
							// ? NEW: Always add classification (not just shortcuts)
							config.ShortcutKeys.Add(classification);
							
							// Log what was loaded
							var features = new List<string>();
							if (classification.IsModal) features.Add("modal");
							if (!string.IsNullOrEmpty(classification.PageName)) features.Add($"page={classification.PageName}");
							if (!string.IsNullOrEmpty(classification.KeyName)) features.Add($"key={classification.KeyName}");
							
							if (features.Count > 0)
							{
								Debug.WriteLine($"[NavConfig] Loaded classification: {classification.PathFilter} ({string.Join(", ", features)})");
							}
						}
					}
					else if (line.StartsWith("PAGE:", StringComparison.OrdinalIgnoreCase))
					{
						var page = ParsePageDefinition(line);
						if (page != null)
						{
							config.Pages.Add(page);
							Debug.WriteLine($"[NavConfig] Loaded page: {page}");
						}
					}
				}
				catch (Exception ex)
				{
					var errorMsg = $"[NavConfig] Parse error on line {i + 1}: {ex.Message}\nLine: {line}";
					Debug.WriteLine(errorMsg);
					
#if DEBUG
					// ? NEW: Fatal error in DEBUG builds for faster discovery
					throw new InvalidOperationException(errorMsg, ex);
#endif
				}
			}

			// ? NEW: Count classifications by type
			var modalCount = config.ShortcutKeys.Count(c => c.IsModal);
			var pageCount = config.ShortcutKeys.Count(c => !string.IsNullOrEmpty(c.PageName));
			var shortcutCount = config.ShortcutKeys.Count(c => !string.IsNullOrEmpty(c.KeyName));
			
			Debug.WriteLine($"[NavConfig] Loaded {config.ShortcutKeys.Count} classifications: {modalCount} modals, {pageCount} page mappings, {shortcutCount} shortcuts; {config.Pages.Count} page definitions");
			return config;
		}

		/// <summary>
		/// Parses a CLASSIFY rule with shortcut properties.
		/// Format: CLASSIFY: <path> => <properties>
		/// </summary>
		private static NavShortcutKey ParseClassifyRule(string line)
		{
			// Remove "CLASSIFY:" prefix
			var content = line.Substring("CLASSIFY:".Length).Trim();

			// Split by "=>"
			var parts = content.Split(new[] { "=>" }, StringSplitOptions.None);
			if (parts.Length != 2)
			{
				var errorMsg = $"[NavConfig] Invalid CLASSIFY format (missing '=>'): {line}";
				Debug.WriteLine(errorMsg);
#if DEBUG
				throw new FormatException(errorMsg);
#endif
				return null;
			}

			var path = parts[0].Trim();
			var propertiesStr = parts[1].Trim();

			// Parse properties (semicolon-separated key=value pairs)
			var properties = ParseProperties(propertiesStr);

			// ? UPDATED: CLASSIFY rules are now always processed (not just shortcuts)
			// They can define: modal behavior, page switching, shortcuts, or any combination
			
			var classification = new NavShortcutKey
			{
				PathFilter = path,
				Role = properties.ContainsKey("role") ? properties["role"] : null,
				IsModal = properties.ContainsKey("modal") && 
				          (properties["modal"].Equals("true", StringComparison.OrdinalIgnoreCase) || 
				           properties["modal"] == "1"),
				PageName = properties.ContainsKey("PageName") ? properties["PageName"] : null,
				KeyName = properties.ContainsKey("KeyName") ? properties["KeyName"] : null,
				KeyTitle = properties.ContainsKey("KeyTitle") ? properties["KeyTitle"] : null,
				KeyIcon = properties.ContainsKey("KeyIcon") ? properties["KeyIcon"] : null,
				NoAutoClick = properties.ContainsKey("NoAutoClick") && 
				              (properties["NoAutoClick"].Equals("true", StringComparison.OrdinalIgnoreCase) || 
				               properties["NoAutoClick"] == "1"),
				TargetType = ParseTargetType(properties.ContainsKey("TargetType") 
				              ? properties["TargetType"] 
				              : "Element")
			};

			// Compile path pattern if it contains wildcards
			if (path.Contains("*"))
			{
				classification.PathPattern = CompilePathPattern(path);
			}

			return classification;
		}

		/// <summary>
		/// Parses TargetType property value.
		/// </summary>
		private static ShortcutTargetType ParseTargetType(string value)
		{
			if (string.IsNullOrEmpty(value))
				return ShortcutTargetType.Element;
			
			if (string.Equals(value, "Group", StringComparison.OrdinalIgnoreCase))
				return ShortcutTargetType.Group;
			
			return ShortcutTargetType.Element; // Default
		}

		/// <summary>
		/// Parses a PAGE definition.
		/// Format: PAGE: <name>[:<basePage>] => <json array>
		/// ? UPDATED: Page name can include inheritance syntax with BasePage.
		/// </summary>
		private static NavPageDef ParsePageDefinition(string line)
		{
			// Remove "PAGE:" prefix
			var content = line.Substring("PAGE:".Length).Trim();

			// Split by "=>"
			var parts = content.Split(new[] { "=>" }, StringSplitOptions.None);
			if (parts.Length != 2)
			{
				var errorMsg = $"[NavConfig] Invalid PAGE format (missing '=>'): {line}";
				Debug.WriteLine(errorMsg);
#if DEBUG
				throw new FormatException(errorMsg);
#endif
				return null;
			}

			var pageNameRaw = parts[0].Trim();
			var gridJson = parts[1].Trim();

			// ? NEW: Validate that page name is quoted
			if (!pageNameRaw.StartsWith("\"") || !pageNameRaw.EndsWith("\"") || pageNameRaw.Length < 2)
			{
				var errorMsg = $"[NavConfig] PAGE name must be a quoted string (found: {pageNameRaw})";
				Debug.WriteLine(errorMsg);
#if DEBUG
				throw new FormatException(errorMsg);
#endif
				return null;
			}

			// Remove quotes from page name
			var pageNameFull = pageNameRaw.Substring(1, pageNameRaw.Length - 2);
			
			// ? NEW: Parse inheritance syntax: "PageName:BasePage"
			string pageName;
			string basePageName = null;
			
			int colonIndex = pageNameFull.IndexOf(':');
			if (colonIndex > 0)
			{
				pageName = pageNameFull.Substring(0, colonIndex);
				basePageName = pageNameFull.Substring(colonIndex + 1);
				
				if (string.IsNullOrWhiteSpace(basePageName))
				{
					var errorMsg = $"[NavConfig] Invalid PAGE inheritance syntax - base page name cannot be empty after ':' (found: {pageNameFull})";
					Debug.WriteLine(errorMsg);
#if DEBUG
					throw new FormatException(errorMsg);
#endif
					return null;
				}
			}
			else
			{
				pageName = pageNameFull;
			}

			try
			{
				// Parse JSON array
				var grid = JsonConvert.DeserializeObject<string[][]>(gridJson);

				if (grid == null || grid.Length != 5)
				{
					var errorMsg = $"[NavConfig] PAGE grid must have 5 rows (found {grid?.Length ?? 0}): {pageName}";
					Debug.WriteLine(errorMsg);
#if DEBUG
					throw new FormatException(errorMsg);
#endif
					return null;
				}

				for (int i = 0; i < 5; i++)
				{
					if (grid[i] == null || grid[i].Length != 3)
					{
						var errorMsg = $"[NavConfig] PAGE row {i} must have 3 columns (found {grid[i]?.Length ?? 0}): {pageName}";
						Debug.WriteLine(errorMsg);
#if DEBUG
						throw new FormatException(errorMsg);
#endif
						return null;
					}
				}

				return new NavPageDef
				{
					PageName = pageName,
					BasePageName = basePageName,
					KeyGrid = grid
				};
			}
			catch (JsonException ex)
			{
				var errorMsg = $"[NavConfig] PAGE JSON parse error: {ex.Message}\nJSON: {gridJson}";
				Debug.WriteLine(errorMsg);
#if DEBUG
				throw new FormatException(errorMsg, ex);
#endif
				return null;
			}
		}

		/// <summary>
		/// Parses properties string (semicolon-separated key=value pairs).
		/// Handles quoted values: KeyTitle="Change Car"
		/// ? UPDATED: KeyName and PageName MUST be quoted strings.
		/// </summary>
		private static Dictionary<string, string> ParseProperties(string propertiesStr)
		{
			var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			// Split by semicolon, but respect quotes
			var parts = SplitRespectingQuotes(propertiesStr, ';');

			foreach (var part in parts)
			{
				var trimmed = part.Trim();
				if (string.IsNullOrEmpty(trimmed))
					continue;

				var keyValue = SplitRespectingQuotes(trimmed, '=', 2);
				if (keyValue.Length == 2)
				{
					var key = keyValue[0].Trim();
					var value = keyValue[1].Trim();

					// ? NEW: Validate that KeyName and PageName are quoted
					if ((key.Equals("KeyName", StringComparison.OrdinalIgnoreCase) ||
					     key.Equals("PageName", StringComparison.OrdinalIgnoreCase)))
					{
						if (!value.StartsWith("\"") || !value.EndsWith("\"") || value.Length < 2)
						{
							var errorMsg = $"[NavConfig] {key} must be a quoted string (found: {value})";
							Debug.WriteLine(errorMsg);
#if DEBUG
							throw new FormatException(errorMsg);
#endif
							// In release, skip this property
							continue;
						}
					}

					// Remove quotes from value if present
					if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
					{
						value = value.Substring(1, value.Length - 2);
					}

					properties[key] = value;
				}
			}

			return properties;
		}

		/// <summary>
		/// Splits a string by delimiter while respecting quoted sections.
		/// </summary>
		private static string[] SplitRespectingQuotes(string input, char delimiter, int maxSplits = -1)
		{
			var parts = new List<string>();
			var current = "";
			bool inQuotes = false;
			int splitCount = 0;

			for (int i = 0; i < input.Length; i++)
			{
				char c = input[i];

				if (c == '"')
				{
					inQuotes = !inQuotes;
					current += c;
				}
				else if (c == delimiter && !inQuotes && (maxSplits == -1 || splitCount < maxSplits))
				{
					parts.Add(current);
					current = "";
					splitCount++;
				}
				else
				{
					current += c;
				}
			}

			// Add remaining
			if (!string.IsNullOrEmpty(current) || splitCount > 0)
			{
				parts.Add(current);
			}

			return parts.ToArray();
		}

		/// <summary>
		/// Compiles a path pattern with wildcards into a Regex.
		/// Wildcards:
		/// - * matches any single path segment
		/// - ** matches any number of path segments (greedy)
		/// </summary>
		private static Regex CompilePathPattern(string pathFilter)
		{
			// Escape special regex characters except * and >
			var pattern = Regex.Escape(pathFilter);

			// Replace escaped wildcards with regex patterns
			pattern = pattern.Replace(@"\*\*", @".*?");  // ** ? .*? (any segments, non-greedy)
			pattern = pattern.Replace(@"\*", @"[^>]+");  // * ? [^>]+ (any single segment)

			// Anchor pattern (must match entire path)
			pattern = "^" + pattern + "$";

			return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
		}
	}

	#endregion

	#region Configuration Container

	/// <summary>
	/// Container for parsed navigation configuration.
	/// </summary>
	public class NavConfiguration
	{
		/// <summary>Classifications defined in configuration (includes shortcuts, modals, page mappings)</summary>
		public List<NavShortcutKey> ShortcutKeys { get; set; }

		/// <summary>Custom page definitions</summary>
		public List<NavPageDef> Pages { get; set; }

		public NavConfiguration()
		{
			ShortcutKeys = new List<NavShortcutKey>();
			Pages = new List<NavPageDef>();
		}

		/// <summary>
		/// Finds a shortcut key by name.
		/// </summary>
		public NavShortcutKey FindShortcut(string keyName)
		{
			return ShortcutKeys.FirstOrDefault(k => 
				string.Equals(k.KeyName, keyName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Finds classifications that match the given element path.
		/// </summary>
		public List<NavShortcutKey> FindClassificationsForPath(string elementPath)
		{
			return ShortcutKeys.Where(k => k.Matches(elementPath)).ToList();
		}
		
		/// <summary>
		/// Finds the page name to switch to when the given element gets focus.
		/// Returns just the PageName (without :BasePage suffix).
		/// </summary>
		public string FindPageForElement(string elementPath)
		{
			// Find first classification with PageName that matches this path
			var classification = ShortcutKeys
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
	}

	#endregion
}
