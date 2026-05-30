using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Built-in StreamDeck Definitions
	/// 
	/// Responsibilities:
	/// - Define built-in navigation keys (Up, Down, Left, Right, etc.)
	/// - Define built-in slider adjustment keys (SliderDecrease, SliderIncrease, etc.)
	/// - Define built-in page layouts (Navigation, Slider, DoubleSlider, etc.)
	/// - Provide icon path resolution for built-in keys
	/// </summary>
	internal static partial class Navigator
	{
		/// <summary>
		/// Built-in page names
		/// </summary>
		private const string PageNavigation = "Navigation";
		private const string PageUpDown = "UpDown";
		private const string PageSlider = "Slider";
		private const string PageDoubleSlider = "DoubleSlider";
		private const string PageRoundSlider = "RoundSlider";
		private const string PageConfirm = "Confirm";

		/// <summary>
		/// Defines all built-in StreamDeck keys (navigation, slider adjustment, discovery, confirmation).
		/// Also defines configured shortcut keys from NavConfiguration.
		/// Called during StreamDeck initialization.
		/// </summary>
		/// <param name="icons">Icon path mapping (icon name -> full path)</param>
		private static void DefineStreamDeckKeys(Dictionary<string, string> icons)
		{
			// Define built-in navigation keys
			_streamDeckClient.DefineKey("Back", null, GetIconPath(icons, "Back"));
			_streamDeckClient.DefineKey("Esc", null, GetIconPath(icons, "Back"));
			_streamDeckClient.DefineKey("Up", null, GetIconPath(icons, "Up"));
			_streamDeckClient.DefineKey("Down", null, GetIconPath(icons, "Down"));
			_streamDeckClient.DefineKey("Left", null, GetIconPath(icons, "Left"));
			_streamDeckClient.DefineKey("Right", null, GetIconPath(icons, "Right"));
			_streamDeckClient.DefineKey("MouseLeft", null, GetIconPath(icons, "Mouse Left"));
			_streamDeckClient.DefineKey("Select", null, GetIconPath(icons, "Mouse Left"));
			
			// ✅ Slider value adjustment keys (use Left/Right icons for now)
			_streamDeckClient.DefineKey("SliderDecrease", null, GetIconPath(icons, "Left"));
			_streamDeckClient.DefineKey("SliderIncrease", null, GetIconPath(icons, "Right"));

			// ✅ Slider range adjustment keys (use Up/Down icons for now)
			_streamDeckClient.DefineKey("SliderRangeDecrease", null, GetIconPath(icons, "Down"));
			_streamDeckClient.DefineKey("SliderRangeIncrease", null, GetIconPath(icons, "Up"));

			// ✅ Round Slider adjustment keys (Called TurnCCW and TurnCW, but use Left/Right icons for now)
			_streamDeckClient.DefineKey("SliderTurnCCW", null, GetIconPath(icons, "Turn CCW"));
			_streamDeckClient.DefineKey("SliderTurnCW", null, GetIconPath(icons, "Turn CW"));

			// ✅ Confirmation keys
			_streamDeckClient.DefineKey("Yes", "YES", GetIconPath(icons, "confirm_yes"));
			_streamDeckClient.DefineKey("No", "NO", GetIconPath(icons, "confirm_no"));

			// ✅ Define configured shortcut keys
			foreach (var shortcut in _navConfig.Classifications)
			{
				// Skip classifications without KeyName (modals, page mappings)
				if (string.IsNullOrEmpty(shortcut.KeyName))
				{
					Debug.WriteLine($"[Navigator] Skipping classification without KeyName: {shortcut.PathFilter}");
					continue;
				}
				
				// Get icon path (check if it's a file path or icon name)
				string iconSpec = null;
				if (!string.IsNullOrEmpty(shortcut.KeyIcon))
				{
					// Try to get icon from discovered icons first
					iconSpec = GetIconPath(icons, shortcut.KeyIcon);
					
					// If not found, check if it's a file path
					if (iconSpec == null && File.Exists(shortcut.KeyIcon))
					{
						iconSpec = shortcut.KeyIcon;
					}
				}
				
				_streamDeckClient.DefineKey(shortcut.KeyName, shortcut.KeyTitle, null);
				
				Debug.WriteLine($"[Navigator] Defined StreamDeck key: {shortcut.KeyName} → {shortcut.PathFilter}");
			}
		}

		/// <summary>
		/// Defines all built-in StreamDeck page layouts.
		/// Called during StreamDeck initialization.
		/// </summary>
		private static void DefineBuiltInPages()
		{
			Debug.WriteLine("[Navigator] DefineBuiltInPages() START");
			
			// Navigation page (full 6-direction navigation)
			Debug.WriteLine($"[Navigator] Defining page: {PageNavigation}");
			_streamDeckClient.DefinePage(PageNavigation, new[] {
				new[] { "Back", "", "" },
				new[] { "","",""},
				new[] { "", "Up", "" },
				new[] { "Left", "MouseLeft", "Right" },
				new[] { "", "Down", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageNavigation}");
			
			// UpDown page (vertical navigation only, for menus)
			Debug.WriteLine($"[Navigator] Defining page: {PageUpDown}");
			_streamDeckClient.DefinePage(PageUpDown, new[] {
				new[] { "Esc", "", "" },
				new[] { "", "", "" },
				new[] { "", "Up", "" },
				new[] { "", "Select", "" },
				new[] { "", "Down", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageUpDown}");
			
			// ✅ Slider page (value adjustment only, no range)
			Debug.WriteLine($"[Navigator] Defining page: {PageSlider}");
			_streamDeckClient.DefinePage(PageSlider, new[] {
				new[] { "Back", "", "" },
				new[] { "", "", "" },
				new[] { "", "", "" },
				new[] { "SliderDecrease", "", "SliderIncrease" },
				new[] { "", "", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageSlider}");
			
			// ✅ DoubleSlider page (value + range adjustment)
			Debug.WriteLine($"[Navigator] Defining page: {PageDoubleSlider}");
			_streamDeckClient.DefinePage(PageDoubleSlider, new[] {
				new[] { "Back", "", "" },
				new[] { "", "", "" },
				new[] { "", "SliderRangeIncrease", "" },
				new[] { "SliderDecrease", "", "SliderIncrease" },
				new[] { "", "SliderRangeDecrease", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageDoubleSlider}");
			
			// ✅ RoundSlider page (value adjustment only, circular slider doesn't have range)
			Debug.WriteLine($"[Navigator] Defining page: {PageRoundSlider}");
			_streamDeckClient.DefinePage(PageRoundSlider, new[] {
				new[] { "Back", "", "" },
				new[] { "", "", "" },
				new[] { "", "", "" },
				new[] { "SliderTurnCW", "", "SliderTurnCCW" },
				new[] { "", "", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageRoundSlider}");
			
			// ✅ Confirm page (Yes/No confirmation dialog)
			Debug.WriteLine($"[Navigator] Defining page: {PageConfirm}");
			_streamDeckClient.DefinePage(PageConfirm, new[] {
				new[] { "", "", "" },
				new[] { "", "", "" },
				new[] { "Yes", "", "No" },
				new[] { "", "", "" },
				new[] { "", "", "" }
			});
			Debug.WriteLine($"[Navigator] ✅ Defined built-in page: {PageConfirm}");
			
			Debug.WriteLine("[Navigator] DefineBuiltInPages() END");
			Debug.WriteLine($"[Navigator] SDPClient page count: {_streamDeckClient.PageCount}");
		}

		/// <summary>
		/// Gets the icon path for a built-in icon name, with fallback to null if not found.
		/// </summary>
		/// <param name="icons">Icon path mapping (icon name -> full path)</param>
		/// <param name="iconName">The icon name to look up</param>
		/// <returns>Full path to icon file, or null if not found</returns>
		private static string GetIconPath(Dictionary<string, string> icons, string iconName)
		{
			if (icons == null || string.IsNullOrEmpty(iconName))
				return null;
			
			return icons.TryGetValue(iconName, out var path) ? path : null;
		}
	}
}
