using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Interaction Mode
	/// 
	/// Responsibilities:
	/// - Enter/exit interaction mode for controls (Sliders, etc.)
	/// - Capture and revert control values (Cancel functionality)
	/// - Adjust slider values in all directions
	/// - Determine appropriate StreamDeck pages for controls
	/// </summary>
	internal static partial class Navigator
	{
		#region Interaction Mode

		/// <summary>
		/// Enters interaction mode for a non-modal control (Slider, DoubleSlider, etc.).
		/// Creates a new navigation context scoped to the single control.
		/// 
		/// This allows controls to create focused "modes" without requiring a popup/dialog.
		/// Example: Slider activation switches to Slider page, locks navigation to the slider.
		/// </summary>
		/// <param name="control">The control to enter interaction mode for</param>
		/// <param name="pageName">Optional StreamDeck page name (auto-detected if null)</param>
		/// <returns>True if interaction mode was entered successfully</returns>
		public static bool EnterInteractionMode(NavNode control, string pageName = null)
		{
			if (control == null)
			{
				Debug.WriteLine("[Navigator] EnterInteractionMode failed: control is null");
				return false;
			}
			
			if (!control.IsNavigable)
			{
				Debug.WriteLine($"[Navigator] EnterInteractionMode failed: {control.SimpleName} is not navigable");
				return false;
			}
			
			// ? Check if already in interaction mode for this control
			if (CurrentContext != null && 
				CurrentContext.ContextType == NavContextType.InteractiveControl &&
				ReferenceEquals(CurrentContext.ScopeNode, control))
			{
				Debug.WriteLine($"[Navigator] Already in interaction mode for {control.SimpleName} - ignoring duplicate activation");
				return true;  // Return true since we're already in the desired state
			}
			
			// ? Capture original value for Cancel functionality
			object originalValue = CaptureControlValue(control);
			
			// Create interaction context (focus set to control itself)
			var context = new NavContext(control, NavContextType.InteractiveControl, focusedNode: control)
			{
				OriginalValue = originalValue
			};
			
			_contextStack.Add(context);
			var depth = _contextStack.Count;
			
			Debug.WriteLine($"[Navigator] Entered interaction mode: {control.SimpleName}");
			Debug.WriteLine($"[Navigator] Context stack depth: {depth}");
			Debug.WriteLine($"[Navigator] Context type: {NavContextType.InteractiveControl}");
			
			// Switch StreamDeck page
			if (string.IsNullOrEmpty(pageName) && control.TryGetVisual(out var element))
			{
				pageName = GetBuiltInPageForControl(element);
			}
			
			if (!string.IsNullOrEmpty(pageName) && _streamDeckClient != null)
			{
				Debug.WriteLine($"[Navigator] Switching to page: {pageName}");
				_streamDeckClient.SwitchPage(pageName);
			}
			
			// Show focus visuals (blue highlight on the control)
			SetFocusVisuals(control);
			
			return true;
		}

		/// <summary>
		/// Exits interaction mode and restores previous navigation context.
		/// Called when user presses Back/Escape (cancel) or MouseLeft (confirm).
		/// </summary>
		/// <param name="revertChanges">If true, reverts control to original value (Cancel). If false, keeps current value (Confirm).</param>
		/// <returns>True if interaction mode was exited successfully</returns>
		public static bool ExitInteractionMode(bool revertChanges = false)
		{
			if (CurrentContext == null)
			{
				Debug.WriteLine("[Navigator] ExitInteractionMode failed: no current context");
				return false;
			}
			
			if (CurrentContext.ContextType != NavContextType.InteractiveControl)
			{
				Debug.WriteLine($"[Navigator] ExitInteractionMode failed: current context is {CurrentContext.ContextType}, not InteractiveControl");
				return false;
			}
			
			var control = CurrentContext.ScopeNode;
			
			// ? Revert value if requested (Cancel)
			if (revertChanges && CurrentContext.OriginalValue != null)
			{
				RestoreControlValue(control, CurrentContext.OriginalValue);
			}
			else if (!revertChanges)
			{
				Debug.WriteLine($"[Navigator] Confirmed changes (keeping current value)");
			}
			
			// Pop the interaction context
			_contextStack.RemoveAt(_contextStack.Count - 1);
			var depth = _contextStack.Count;
			
			Debug.WriteLine($"[Navigator] Exited interaction mode: {control.SimpleName} (revert={revertChanges})");
			Debug.WriteLine($"[Navigator] Context stack depth: {depth}");
			
			// Restore parent context focus
			if (CurrentContext != null)
			{
				// Try to restore focus to the control we just exited
				// (user is still "at" that location, just not in interaction mode)
				if (CurrentContext.FocusedNode == null || !ReferenceEquals(CurrentContext.FocusedNode, control))
				{
					SetFocus(control);
				}
				else
				{
					// Focus already on control, just update visuals/page
					SetFocusVisuals(control);
				}
				
				// Switch StreamDeck page for parent context
				SwitchStreamDeckPageForModal(CurrentContext.ScopeNode);
			}
			else
			{
				// No parent context - clear visuals
				_overlay?.HideFocusRect();
			}
			
			return true;
		}

		#endregion

		#region Control Value Management

		/// <summary>
		/// Captures the current value of a control for potential revert (Cancel).
		/// Supports Slider, DoubleSlider, and RoundSlider.
		/// </summary>
		/// <param name="control">The control to capture value from</param>
		/// <returns>The captured value, or null if capture failed</returns>
		private static object CaptureControlValue(NavNode control)
		{
			if (!control.TryGetVisual(out var fe))
				return null;
			
			try
			{
				if (fe is Slider slider)
				{
					var value = slider.Value;
					Debug.WriteLine($"[Navigator] Captured slider value: {value}");
					return value;
				}
				
				var typeName = fe.GetType().Name;
				if (typeName == "DoubleSlider" || typeName == "RoundSlider")
				{
					var valueProperty = fe.GetType().GetProperty("Value");
					if (valueProperty != null)
					{
						var value = valueProperty.GetValue(fe);
						Debug.WriteLine($"[Navigator] Captured {typeName} value: {value}");
						return value;
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] Failed to capture control value: {ex.Message}");
			}
			
			return null;
		}

		/// <summary>
		/// Restores a previously captured control value (Cancel operation).
		/// Supports Slider, DoubleSlider, and RoundSlider.
		/// </summary>
		/// <param name="control">The control to restore value to</param>
		/// <param name="originalValue">The value to restore</param>
		private static void RestoreControlValue(NavNode control, object originalValue)
		{
			if (!control.TryGetVisual(out var fe))
				return;
			
			try
			{
				if (fe is Slider slider)
				{
					var oldValue = slider.Value;
					slider.Value = (double)originalValue;
					Debug.WriteLine($"[Navigator] Reverted slider value: {oldValue:F2} ? {slider.Value:F2}");
					return;
				}
				
				var typeName = fe.GetType().Name;
				if (typeName == "DoubleSlider" || typeName == "RoundSlider")
				{
					var valueProperty = fe.GetType().GetProperty("Value");
					if (valueProperty != null)
					{
						var oldValue = valueProperty.GetValue(fe);
						valueProperty.SetValue(fe, originalValue);
						Debug.WriteLine($"[Navigator] Reverted {typeName} value: {oldValue} ? {originalValue}");
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] Failed to revert value: {ex.Message}");
			}
		}

		#endregion

		#region Slider Value Adjustment

		/// <summary>
		/// Adjusts the value of the currently focused slider control.
		/// Only works when in InteractiveControl context (slider interaction mode).
		/// Supports Slider, DoubleSlider, and RoundSlider types.
		/// </summary>
		/// <param name="direction">Direction to adjust (Left/Right for horizontal, Up/Down for vertical)</param>
		private static void AdjustSliderValue(NavDirection direction)
		{
			if (CurrentContext?.ContextType != NavContextType.InteractiveControl)
			{
				Debug.WriteLine("[Navigator] AdjustSliderValue: Not in interaction mode");
				return;
			}
			
			var control = CurrentContext.ScopeNode;
			if (!control.TryGetVisual(out var fe))
			{
				Debug.WriteLine("[Navigator] AdjustSliderValue: Visual reference dead");
				return;
			}
			
			Debug.WriteLine($"[Navigator] AdjustSliderValue: Adjusting {fe.GetType().Name} in direction {direction}");
			
			try
			{
				// Handle standard WPF Slider
				if (fe is Slider slider)
				{
					AdjustStandardSlider(slider, direction);
					return;
				}
				
				// Handle custom slider types via reflection
				var typeName = fe.GetType().Name;
				
				if (typeName == "DoubleSlider")
				{
					AdjustDoubleSlider(fe, direction);
					return;
				}
				
				if (typeName == "RoundSlider")
				{
					AdjustRoundSlider(fe, direction);
					return;
				}
				
				Debug.WriteLine($"[Navigator] AdjustSliderValue: Unsupported control type '{typeName}'");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] AdjustSliderValue ERROR: {ex.Message}");
			}
		}

		/// <summary>
		/// Adjusts a standard WPF Slider control.
		/// Supports Left/Right directions only (horizontal slider).
		/// </summary>
		private static void AdjustStandardSlider(Slider slider, NavDirection direction)
		{
			var step = slider.LargeChange > 0 ? slider.LargeChange : slider.SmallChange;
			var oldValue = slider.Value;
			
			if (direction == NavDirection.Left)
				slider.Value = Math.Max(slider.Minimum, slider.Value - step);
			else if (direction == NavDirection.Right)
				slider.Value = Math.Min(slider.Maximum, slider.Value + step);
			
			Debug.WriteLine($"[Navigator] Slider adjusted: {oldValue:F2} ? {slider.Value:F2} (step={step:F2})");
		}

		/// <summary>
		/// Adjusts a DoubleSlider control (custom control with dual adjustment modes).
		/// Up/Down = coarse adjustment (LargeChange)
		/// Left/Right = fine adjustment (SmallChange)
		/// </summary>
		private static void AdjustDoubleSlider(FrameworkElement element, NavDirection direction)
		{
			var valueProperty = element.GetType().GetProperty("Value");
			var minProperty = element.GetType().GetProperty("Minimum");
			var maxProperty = element.GetType().GetProperty("Maximum");
			var largeChangeProperty = element.GetType().GetProperty("LargeChange");
			var smallChangeProperty = element.GetType().GetProperty("SmallChange");
			
			if (valueProperty == null || minProperty == null || maxProperty == null)
				return;
			
			var currentValue = (double)valueProperty.GetValue(element);
			var min = (double)minProperty.GetValue(element);
			var max = (double)maxProperty.GetValue(element);
			
			// Use LargeChange for Up/Down (coarse), SmallChange for Left/Right (fine)
			double step;
			if (direction == NavDirection.Up || direction == NavDirection.Down)
			{
				// Coarse adjustment
				step = largeChangeProperty != null ? (double)largeChangeProperty.GetValue(element) : 1.0;
			}
			else
			{
				// Fine adjustment
				step = smallChangeProperty != null ? (double)smallChangeProperty.GetValue(element) : 0.1;
			}
			
			// Calculate new value
			double newValue = currentValue;
			if (direction == NavDirection.Left || direction == NavDirection.Down)
				newValue = Math.Max(min, currentValue - step);
			else if (direction == NavDirection.Right || direction == NavDirection.Up)
				newValue = Math.Min(max, currentValue + step);
			
			valueProperty.SetValue(element, newValue);
			Debug.WriteLine($"[Navigator] DoubleSlider adjusted: {currentValue:F2} ? {newValue:F2} (step={step:F2}, mode={((direction == NavDirection.Up || direction == NavDirection.Down) ? "coarse" : "fine")})");
		}

		/// <summary>
		/// Adjusts a RoundSlider control (custom circular slider).
		/// All 4 directions supported: Right/Up = increase, Left/Down = decrease
		/// Up/Down = LargeChange, Left/Right = SmallChange
		/// </summary>
		private static void AdjustRoundSlider(FrameworkElement element, NavDirection direction)
		{
			var valueProperty = element.GetType().GetProperty("Value");
			var minProperty = element.GetType().GetProperty("Minimum");
			var maxProperty = element.GetType().GetProperty("Maximum");
			var largeChangeProperty = element.GetType().GetProperty("LargeChange");
			var smallChangeProperty = element.GetType().GetProperty("SmallChange");
			
			if (valueProperty == null || minProperty == null || maxProperty == null)
				return;
			
			var currentValue = (double)valueProperty.GetValue(element);
			var min = (double)minProperty.GetValue(element);
			var max = (double)maxProperty.GetValue(element);
			
			// Use LargeChange for Up/Down, SmallChange for Left/Right
			double step;
			if (direction == NavDirection.Up || direction == NavDirection.Down)
			{
				step = largeChangeProperty != null ? (double)largeChangeProperty.GetValue(element) : 1.0;
			}
			else
			{
				step = smallChangeProperty != null ? (double)smallChangeProperty.GetValue(element) : 0.1;
			}
			
			// RoundSlider semantics: Right/Up = increase, Left/Down = decrease
			double newValue = currentValue;
			if (direction == NavDirection.Left || direction == NavDirection.Down)
				newValue = Math.Max(min, currentValue - step);
			else if (direction == NavDirection.Right || direction == NavDirection.Up)
				newValue = Math.Min(max, currentValue + step);
			
			valueProperty.SetValue(element, newValue);
			Debug.WriteLine($"[Navigator] RoundSlider adjusted: {currentValue:F2} ? {newValue:F2} (step={step:F2})");
		}

		#endregion

		#region StreamDeck Page Mapping

		/// <summary>
		/// Determines the optimal built-in StreamDeck page for a given control type.
		/// Returns null if no special page is needed (use default Navigation page).
		/// 
		/// This is separate from config-based page mapping (which has higher priority).
		/// </summary>
		private static string GetBuiltInPageForControl(FrameworkElement element)
		{
			if (element == null) return null;
			
			var typeName = element.GetType().Name;
			
			// ? Slider family - optimize for value adjustment
			if (element is Slider)
				return "Slider";  // Left/Right only
			
			if (typeName == "DoubleSlider")
				return "DoubleSlider";  // Vertical coarse + Horizontal fine
			
			if (typeName == "RoundSlider")
				return "RoundSlider";  // All 4 directions (circular)
			
			// ? Menu items - optimize for vertical navigation
			if (element is System.Windows.Controls.MenuItem)
				return "UpDown";  // Up/Down only
			
			// ? List items - context-dependent
			// (You might want to use UpDown for long lists, Navigation for grids)
			// For now, return null and use default Navigation page
			if (element is System.Windows.Controls.ListBoxItem)
				return null;
			
			// ? Future control types:
			// if (element is ComboBox comboBox && comboBox.IsDropDownOpen)
			//     return "UpDown";  // Dropdown is vertical
			//
			// if (element is TreeViewItem)
			//     return "TreeNav";  // Custom tree navigation page
			//
			// if (element is DataGridCell)
			//     return "GridNav";  // Custom grid navigation page
			
			return null;  // Use default Navigation page
		}

		#endregion
	}
}
