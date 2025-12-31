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
			
			// ✅ Check if already in interaction mode for this control
			if (CurrentContext != null && 
				CurrentContext.ContextType == NavContextType.InteractiveControl &&
				ReferenceEquals(CurrentContext.ScopeNode, control))
			{
				Debug.WriteLine($"[Navigator] Already in interaction mode for {control.SimpleName} - ignoring duplicate activation");
				return true;  // Return true since we're already in the desired state
			}
			
			// ✅ Capture original value for Cancel functionality
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
			
			// ✅ Revert value if requested (Cancel)
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
					Debug.WriteLine($"[Navigator] Reverted slider value: {oldValue:F2} → {slider.Value:F2}");
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
						Debug.WriteLine($"[Navigator] Reverted {typeName} value: {oldValue} → {originalValue}");
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
		/// <param name="adjustment">The adjustment operation to perform</param>
		private static void AdjustSliderValue(SliderAdjustment adjustment)
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
			
			Debug.WriteLine($"[Navigator] AdjustSliderValue: Adjusting {fe.GetType().Name} with {adjustment}");
			
			try
			{
				// Handle standard WPF Slider
				if (fe is Slider slider)
				{
					AdjustStandardSlider(slider, adjustment);
					return;
				}
				
				// Handle custom slider types via reflection
				var typeName = fe.GetType().Name;
				
				if (typeName == "DoubleSlider")
				{
					AdjustDoubleSliderValue(fe, adjustment);
					return;
				}
				
				if (typeName == "RoundSlider")
				{
					AdjustRoundSlider(fe, adjustment);
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
		/// Adjusts the range (min/max bounds) of a DoubleSlider control.
		/// Only works when in InteractiveControl context with a DoubleSlider.
		/// </summary>
		/// <param name="adjustment">The adjustment operation to perform</param>
		private static void AdjustSliderRange(SliderAdjustment adjustment)
		{
			if (CurrentContext?.ContextType != NavContextType.InteractiveControl)
			{
				Debug.WriteLine("[Navigator] AdjustSliderRange: Not in interaction mode");
				return;
			}
			
			var control = CurrentContext.ScopeNode;
			if (!control.TryGetVisual(out var fe))
			{
				Debug.WriteLine("[Navigator] AdjustSliderRange: Visual reference dead");
				return;
			}
			
			var typeName = fe.GetType().Name;
			if (typeName != "DoubleSlider")
			{
				Debug.WriteLine($"[Navigator] AdjustSliderRange: Only DoubleSlider supports range adjustment (got '{typeName}')");
				return;
			}
			
			Debug.WriteLine($"[Navigator] AdjustSliderRange: Adjusting DoubleSlider range with {adjustment}");
			
			try
			{
				AdjustDoubleSliderRange(fe, adjustment);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] AdjustSliderRange ERROR: {ex.Message}");
			}
		}

		/// <summary>
		/// Adjusts a standard WPF Slider control.
		/// Uses proportional adjustment: 2% of the slider's total range.
		/// </summary>
		private static void AdjustStandardSlider(Slider slider, SliderAdjustment adjustment)
		{
			var totalRange = Math.Abs(slider.Maximum - slider.Minimum);
			var adjustmentStep = totalRange * 0.02; // 2% of total range
			var oldValue = slider.Value;
			
			switch (adjustment)
			{
				case SliderAdjustment.SmallIncrement:
					slider.Value = Math.Min(slider.Maximum, slider.Value + adjustmentStep);
					break;
				case SliderAdjustment.SmallDecrement:
					slider.Value = Math.Max(slider.Minimum, slider.Value - adjustmentStep);
					break;
			}
			
			Debug.WriteLine($"[Navigator] Slider adjusted: {oldValue:F2} → {slider.Value:F2} (step={adjustmentStep:F2} [2% of {totalRange:F2}], {adjustment})");
		}

		/// <summary>
		/// Adjusts a DoubleSlider's value (within current From/To range).
		/// Uses proportional adjustment: 2% of the slider's TOTAL range (Maximum - Minimum).
		/// </summary>
		private static void AdjustDoubleSliderValue(FrameworkElement element, SliderAdjustment adjustment)
		{
			var valueProperty = element.GetType().GetProperty("Value");
			var fromProperty = element.GetType().GetProperty("From");
			var toProperty = element.GetType().GetProperty("To");
			var minProperty = element.GetType().GetProperty("Minimum");
			var maxProperty = element.GetType().GetProperty("Maximum");
			
			if (valueProperty == null || fromProperty == null || toProperty == null || minProperty == null || maxProperty == null)
				return;
			
			var currentValue = (double)valueProperty.GetValue(element);
			var currentFrom = (double)fromProperty.GetValue(element);
			var currentTo = (double)toProperty.GetValue(element);
			var absoluteMin = (double)minProperty.GetValue(element);
			var absoluteMax = (double)maxProperty.GetValue(element);
			
			// ✅ Calculate adjustment as 2% of TOTAL possible range (Maximum - Minimum)
			var totalRange = Math.Abs(absoluteMax - absoluteMin);
			var adjustmentStep = Math.Max(0.1, totalRange * 0.02);
			
			double newValue = currentValue;
			switch (adjustment)
			{
				case SliderAdjustment.SmallIncrement:
					newValue = Math.Min(currentTo, currentValue + adjustmentStep);
					break;
				case SliderAdjustment.SmallDecrement:
					newValue = Math.Max(currentFrom, currentValue - adjustmentStep);
					break;
			}
			
			valueProperty.SetValue(element, newValue);
			Debug.WriteLine($"[Navigator] DoubleSlider value adjusted: {currentValue:F2} → {newValue:F2} (step={adjustmentStep:F2} [2% of total range {totalRange:F2}], current from/to: [{currentFrom:F2}, {currentTo:F2}], {adjustment})");
		}

		/// <summary>
		/// Adjusts a DoubleSlider's range (From/To bounds).
		/// Uses proportional adjustment: 1% of the slider's TOTAL range (Maximum - Minimum).
		/// 
		/// SIMPLE BOUNDARY HANDLING:
		/// - Expand: Increase To by rangeStep, decrease From by rangeStep independently
		///   - If one hits limit, it stops but the other continues normally
		/// - Contract: Move boundaries toward Value by rangeStep
		///   - If asymmetric (one at limit), only move the side that's further away
		/// 
		/// ✅ USES FROM/TO DIRECTLY: Sets From and To properties directly.
		///    In FromToFixed mode, this will cause Value to auto-center between From and To.
		///    This is the INTENDED BEHAVIOR of FromToFixed mode.
		/// </summary>
		private static void AdjustDoubleSliderRange(FrameworkElement element, SliderAdjustment adjustment)
		{
			var valueProperty = element.GetType().GetProperty("Value");
			var fromProperty = element.GetType().GetProperty("From");
			var toProperty = element.GetType().GetProperty("To");
			var minProperty = element.GetType().GetProperty("Minimum");
			var maxProperty = element.GetType().GetProperty("Maximum");
			
			if (valueProperty == null || fromProperty == null || toProperty == null || 
			    minProperty == null || maxProperty == null)
			{
				Debug.WriteLine("[Navigator] AdjustDoubleSliderRange: Required properties not found");
				return;
			}
			
			var currentValue = (double)valueProperty.GetValue(element);
			var currentFrom = (double)fromProperty.GetValue(element);
			var currentTo = (double)toProperty.GetValue(element);
			var absoluteMin = (double)minProperty.GetValue(element);
			var absoluteMax = (double)maxProperty.GetValue(element);
			
			// Calculate adjustment as 1% of TOTAL possible range
			var totalRange = Math.Abs(absoluteMax - absoluteMin);
			var rangeStep = Math.Max(0.1, totalRange * 0.01);
			
			double newFrom = currentFrom;
			double newTo = currentTo;
			
			switch (adjustment)
			{
				case SliderAdjustment.SmallIncrement:
					// ✅ EXPAND: Move both boundaries independently
					// From moves down by rangeStep (or until it hits absoluteMin)
					newFrom = Math.Max(absoluteMin, currentFrom - rangeStep);
					
					// To moves up by rangeStep (or until it hits absoluteMax)
					newTo = Math.Min(absoluteMax, currentTo + rangeStep);
					
					Debug.WriteLine($"[Navigator] DoubleSlider range EXPANDED: " +
						$"From {currentFrom:F2}→{newFrom:F2}, To {currentTo:F2}→{newTo:F2} " +
						$"(step={rangeStep:F2}, bounds=[{absoluteMin:F2}, {absoluteMax:F2}])");
					break;
					
				case SliderAdjustment.SmallDecrement:
					// ✅ CONTRACT: Move boundaries toward Value
					
					// Check if we're in an asymmetric state (one boundary at limit)
					bool fromAtLimit = (currentFrom <= absoluteMin + 0.01);
					bool toAtLimit = (currentTo >= absoluteMax - 0.01);
					
					if (fromAtLimit && !toAtLimit)
					{
						// From is at minimum limit, only contract To
						newTo = Math.Max(currentValue + rangeStep, currentTo - rangeStep);
						Debug.WriteLine($"[Navigator] From at limit, only contracting To: {currentTo:F2}→{newTo:F2}");
					}
					else if (toAtLimit && !fromAtLimit)
					{
						// To is at maximum limit, only contract From
						newFrom = Math.Min(currentValue - rangeStep, currentFrom + rangeStep);
						Debug.WriteLine($"[Navigator] To at limit, only contracting From: {currentFrom:F2}→{newFrom:F2}");
					}
					else
					{
						// Symmetric state: move both toward Value by rangeStep
						// From moves up (toward Value)
						newFrom = Math.Min(currentValue - rangeStep, currentFrom + rangeStep);
						
						// To moves down (toward Value)
						newTo = Math.Max(currentValue + rangeStep, currentTo - rangeStep);
						
						Debug.WriteLine($"[Navigator] Symmetric contraction: " +
							$"From {currentFrom:F2}→{newFrom:F2}, To {currentTo:F2}→{newTo:F2}");
					}
					
					// ✅ Enforce minimum range (prevent From and To from getting too close)
					double minAllowedRange = rangeStep * 2;
					if (newTo - newFrom < minAllowedRange)
					{
						Debug.WriteLine($"[Navigator] Range too small ({newTo - newFrom:F2} < {minAllowedRange:F2}), " +
							$"keeping current range");
						newFrom = currentFrom;
						newTo = currentTo;
					}
					
					Debug.WriteLine($"[Navigator] DoubleSlider range CONTRACTED: " +
						$"[{currentFrom:F2}, {currentTo:F2}] → [{newFrom:F2}, {newTo:F2}] " +
						$"(value={currentValue:F2})");
					break;
			}
			
			// ✅ Apply changes using From/To properties directly
			// In FromToFixed mode, Value will auto-center between From and To (by design)
			bool changed = false;
			
			if (Math.Abs(newFrom - currentFrom) > 0.01)
			{
				fromProperty.SetValue(element, newFrom);
				changed = true;
				Debug.WriteLine($"[Navigator] Set From: {currentFrom:F2} → {newFrom:F2}");
			}
			
			if (Math.Abs(newTo - currentTo) > 0.01)
			{
				toProperty.SetValue(element, newTo);
				changed = true;
				Debug.WriteLine($"[Navigator] Set To: {currentTo:F2} → {newTo:F2}");
			}
			
			if (changed)
			{
				// Read back the actual values (Value may have auto-centered)
				var actualFrom = (double)fromProperty.GetValue(element);
				var actualTo = (double)toProperty.GetValue(element);
				var actualValue = (double)valueProperty.GetValue(element);
				
				Debug.WriteLine($"[Navigator] Applied range: [{actualFrom:F2}, {actualTo:F2}], Value={actualValue:F2}");
			}
			else
			{
				Debug.WriteLine($"[Navigator] DoubleSlider range unchanged");
			}
		}

		/// <summary>
		/// Adjusts a RoundSlider control (custom circular slider).
		/// Uses proportional adjustment: 10% of the slider's total range.
		/// ✅ Supports wrap-around at boundaries (0° ↔ 360°).
		/// </summary>
		private static void AdjustRoundSlider(FrameworkElement element, SliderAdjustment adjustment)
		{
			var valueProperty = element.GetType().GetProperty("Value");
			var minProperty = element.GetType().GetProperty("Minimum");
			var maxProperty = element.GetType().GetProperty("Maximum");
			
			if (valueProperty == null || minProperty == null || maxProperty == null)
				return;
			
			var currentValue = (double)valueProperty.GetValue(element);
			var min = (double)minProperty.GetValue(element);
			var max = (double)maxProperty.GetValue(element);
			
			// Calculate adjustment as 2% of total range
			var totalRange = Math.Abs(max - min);
			var adjustmentStep = totalRange * 0.02;
			
			double newValue = currentValue;
			switch (adjustment)
			{
				case SliderAdjustment.SmallIncrement:
					newValue = currentValue + adjustmentStep;
					// ✅ Wrap around if exceeds max
					if (newValue > max)
					{
						newValue = min + (newValue - max);
					}
					break;
					
				case SliderAdjustment.SmallDecrement:
					newValue = currentValue - adjustmentStep;
					// ✅ Wrap around if goes below min
					if (newValue < min)
					{
						newValue = max - (min - newValue);
					}
					break;
			}
			
			// ✅ Clamp to range (safety check for floating point errors)
			newValue = Math.Max(min, Math.Min(max, newValue));
			
			valueProperty.SetValue(element, newValue);
			Debug.WriteLine($"[Navigator] RoundSlider adjusted: {currentValue:F2} → {newValue:F2} (step={adjustmentStep:F2} [2% of {totalRange:F2}], {adjustment})");
		}

		#endregion

		#region StreamDeck Page Mapping

		/// <summary>
		/// Gets the appropriate built-in StreamDeck page for a control type.
		/// Returns null if no specific page mapping exists (caller should use default).
		/// </summary>
		private static string GetBuiltInPageForControl(FrameworkElement element)
		{
			var typeName = element.GetType().Name;
			
			Debug.WriteLine($"[Navigator] GetBuiltInPageForControl: typeName='{typeName}'");
			
			if (typeName == "DoubleSlider")
			{
				Debug.WriteLine($"[Navigator] GetBuiltInPageForControl: Returning 'DoubleSlider' page");
				return "DoubleSlider";
			}
			
			if (typeName == "RoundSlider")
			{
				Debug.WriteLine($"[Navigator] GetBuiltInPageForControl: Returning 'RoundSlider' page");
				return "RoundSlider";
			}
			
			if (typeName == "Slider" || typeName == "FormattedSlider")
			{
				Debug.WriteLine($"[Navigator] GetBuiltInPageForControl: Returning 'Slider' page");
				return "Slider";
			}
			
			Debug.WriteLine($"[Navigator] GetBuiltInPageForControl: No specific page for '{typeName}', returning null");
			return null;
		}

		#endregion
	}
}
