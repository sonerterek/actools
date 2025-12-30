using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Focus Guard (Experimental)
	/// 
	/// Responsibilities:
	/// - Monitor WPF keyboard focus changes globally
	/// - Restore focus to Navigator-tracked elements when WPF steals it
	/// - Prevent focus from moving to excluded elements (TextBox, etc.)
	/// 
	/// Status: DISABLED
	/// This functionality is currently disabled but preserved for potential future use.
	/// It was causing issues with menu navigation, and the original problem it was meant
	/// to solve (SelectCarDialog selection issues) was actually caused by held-down modifier keys,
	/// not focus stealing.
	/// </summary>
	internal static partial class Navigator
	{
		#region Focus Guard

		/// <summary>
		/// Installs a global focus monitor to prevent WPF from stealing focus to non-navigable elements.
		/// 
		/// Problem:
		/// WPF automatically moves keyboard focus to text inputs (TextBox, etc.) when they load or become visible.
		/// These elements are excluded from our navigation system (not NavNodes), so we lose track of focus.
		/// 
		/// Solution:
		/// Monitor WPF's focus changes globally. If focus moves to a non-tracked element, restore it
		/// to our last known focused NavNode.
		/// 
		/// This acts as a "focus guard" that keeps keyboard focus aligned with our navigation system.
		/// 
		/// ? DISABLED: Focus guard is currently disabled as it was causing issues with menu navigation.
		/// The original issue (SelectCarDialog selection problems) was due to Shift+Ctrl keys being held down,
		/// not focus stealing. Keeping code for potential future use.
		/// </summary>
		private static void InstallFocusGuard()
		{
			// ? DISABLED: Focus guard functionality disabled
			// Uncomment below to re-enable
			/*
			try {
				// Subscribe to global keyboard focus changes (tunneling event - fires before Loaded/GotFocus)
				EventManager.RegisterClassHandler(
					typeof(UIElement),
					Keyboard.GotKeyboardFocusEvent,
					new KeyboardFocusChangedEventHandler(OnGlobalKeyboardFocusChanged),
					handledEventsToo: true  // ? Monitor even if event is marked as handled
				);

				Debug.WriteLine("[Navigator] Focus guard installed");
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to install focus guard: {ex.Message}");
			}
			*/
			
			Debug.WriteLine("[Navigator] Focus guard DISABLED (keeping code for future use)");
		}

		/// <summary>
		/// Global keyboard focus change handler.
		/// Restores focus to our tracked NavNode if WPF tries to focus a non-navigable element.
		/// 
		/// ? DISABLED: This handler is not currently hooked up (see InstallFocusGuard).
		/// </summary>
		private static void OnGlobalKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
		{
			// ? DISABLED: Focus guard functionality disabled
			// This method is kept for future use but not currently subscribed to events
			return;
			
			#pragma warning disable CS0162 // Unreachable code detected
			
			// Get the newly focused element
			if (!(e.NewFocus is FrameworkElement newFocusedElement)) {
				// Focus moved to non-FrameworkElement (or null) - ignore
				return;
			}

			// Check if this element is a tracked NavNode
			if (Observer.TryGetNavNode(newFocusedElement, out var navNode)) {
				// Focus moved to a tracked NavNode - this is expected behavior
				// Update our focus tracking to match WPF's focus
				if (CurrentContext != null && !ReferenceEquals(CurrentContext.FocusedNode, navNode)) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Focus moved to tracked NavNode: {navNode.SimpleName}");
					}

					// Sync our tracking with WPF's focus (don't trigger visual update - already focused)
					var oldNode = CurrentContext.FocusedNode;
					if (oldNode != null) oldNode.HasFocus = false;

					navNode.HasFocus = true;
					CurrentContext.FocusedNode = navNode;

					try { FocusChanged?.Invoke(oldNode, navNode); } catch { }
				}
				return;
			}

			// ? Focus moved to a NON-tracked element (e.g., excluded TextBox)

			if (CurrentContext?.FocusedNode == null) {
				// No previous focus to restore - let WPF do its thing
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Focus moved to non-tracked element '{newFocusedElement.GetType().Name}' (no previous focus to restore)");
				}
				return;
			}

			// CHECK IF OUR FOCUSED NAVNODE IS STILL VALID
			if (!CurrentContext.FocusedNode.TryGetVisual(out var ourFocusedElement)) {
				// Our focused element is DEAD - OnNodeUnloaded should have already handled this
				Debug.WriteLine($"[Navigator] Focus stolen but our focused NavNode is dead (element unloaded) - allowing focus to move");

				CurrentContext.FocusedNode.HasFocus = false;
				CurrentContext.FocusedNode = null;
				return;
			}

			// CHECK IF ELEMENT IS STILL IN VISUAL TREE
			if (PresentationSource.FromVisual(ourFocusedElement) == null) {
				Debug.WriteLine($"[Navigator] Focus stolen but our focused element is no longer in visual tree - allowing focus to move");

				CurrentContext.FocusedNode.HasFocus = false;
				CurrentContext.FocusedNode = null;
				return;
			}

			// Element is alive and in visual tree - restore focus to it
			Debug.WriteLine($"[Navigator] ? Focus stolen by non-tracked '{newFocusedElement.GetType().Name}' - restoring to '{CurrentContext.FocusedNode.SimpleName}'");

			// Use Dispatcher to avoid re-entrancy issues (focus change during focus change)
			ourFocusedElement.Dispatcher.BeginInvoke(
				DispatcherPriority.Input,  // High priority - restore focus ASAP
				new Action(() => {
					try {
						// ? UPDATED: Re-check that our focused node is STILL valid
						if (CurrentContext?.FocusedNode == null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - focused node cleared");
							}
							return;
						}

						if (!CurrentContext.FocusedNode.TryGetVisual(out var elementToFocus)) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - element died");
							}
							CurrentContext.FocusedNode.HasFocus = false;
							CurrentContext.FocusedNode = null;
							return;
						}

						if (PresentationSource.FromVisual(elementToFocus) == null) {
							if (VerboseNavigationDebug) {
								Debug.WriteLine($"[Navigator] Focus restore cancelled - element removed from tree");
							}
							CurrentContext.FocusedNode.HasFocus = false;
							CurrentContext.FocusedNode = null;
							return;
						}

						// Restore keyboard focus
						Keyboard.Focus(elementToFocus);

						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Focus restored to '{CurrentContext.FocusedNode.SimpleName}'");
						}
					} catch (Exception ex) {
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Failed to restore focus: {ex.Message}");
						}
					}
				})
			);
			
			#pragma warning restore CS0162 // Unreachable code detected
		}

		#endregion
	}
}
