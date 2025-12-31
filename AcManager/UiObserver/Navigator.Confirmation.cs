using System;
using System.Diagnostics;
using System.Windows;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Confirmation System
	/// 
	/// Responsibilities:
	/// - Request user confirmation for critical operations
	/// - Show Confirm page on StreamDeck with Yes/No options
	/// - Execute or cancel pending actions based on user response
	/// - Restore previous StreamDeck page after confirmation/cancellation
	/// </summary>
	internal static partial class Navigator
	{
		#region Confirmation State

		/// <summary>
		/// The action to execute if user confirms (Yes)
		/// </summary>
		private static Action _pendingConfirmAction;

		/// <summary>
		/// The action to execute if user cancels (No) - optional
		/// </summary>
		private static Action _pendingCancelAction;

		/// <summary>
		/// Description of the action being confirmed (for logging/debugging)
		/// </summary>
		private static string _confirmationDescription;

		/// <summary>
		/// Whether we're currently in confirmation mode
		/// </summary>
		private static bool IsAwaitingConfirmation => _pendingConfirmAction != null;

		#endregion

		#region Request Confirmation

		/// <summary>
		/// Requests user confirmation for a critical operation.
		/// Switches StreamDeck to Confirm page with Yes/No buttons.
		/// </summary>
		/// <param name="description">Description of the action (for logging)</param>
		/// <param name="onConfirm">Action to execute if user confirms (Yes)</param>
		/// <param name="onCancel">Optional action to execute if user cancels (No)</param>
		public static void RequestConfirmation(string description, Action onConfirm, Action onCancel = null)
		{
			if (IsAwaitingConfirmation)
			{
				Debug.WriteLine($"[Navigator] RequestConfirmation: Already awaiting confirmation, ignoring new request");
				return;
			}

			_confirmationDescription = description;
			_pendingConfirmAction = onConfirm;
			_pendingCancelAction = onCancel;

			Debug.WriteLine($"[Navigator] RequestConfirmation: '{description}'");

			// Switch to Confirm page
			if (_streamDeckClient != null)
			{
				Debug.WriteLine($"[Navigator] Switching to Confirm page");
				_streamDeckClient.SwitchPage("Confirm");
			}
		}

		/// <summary>
		/// User confirmed the action (pressed Yes).
		/// Executes the pending action and restores previous page.
		/// Called by StreamDeck command handler.
		/// </summary>
		private static void ConfirmAction()
		{
			if (!IsAwaitingConfirmation)
			{
				Debug.WriteLine($"[Navigator] ConfirmAction: No pending confirmation");
				return;
			}

			Debug.WriteLine($"[Navigator] ? User CONFIRMED: '{_confirmationDescription}'");

			var action = _pendingConfirmAction;
			ClearConfirmationState();

			// Restore previous page
			RestorePreviousPage();

			// Execute the confirmed action
			try
			{
				action?.Invoke();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] ConfirmAction ERROR: {ex.Message}");
			}
		}

		/// <summary>
		/// User cancelled the action (pressed No).
		/// Executes optional cancel callback and restores previous page.
		/// Called by StreamDeck command handler.
		/// </summary>
		private static void CancelAction()
		{
			if (!IsAwaitingConfirmation)
			{
				Debug.WriteLine($"[Navigator] CancelAction: No pending confirmation");
				return;
			}

			Debug.WriteLine($"[Navigator] ? User CANCELLED: '{_confirmationDescription}'");

			var cancelAction = _pendingCancelAction;
			ClearConfirmationState();

			// Restore previous page
			RestorePreviousPage();

			// Execute optional cancel callback
			try
			{
				cancelAction?.Invoke();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Navigator] CancelAction ERROR: {ex.Message}");
			}
		}

		/// <summary>
		/// Clears confirmation state variables.
		/// </summary>
		private static void ClearConfirmationState()
		{
			_pendingConfirmAction = null;
			_pendingCancelAction = null;
			_confirmationDescription = null;
		}

		#endregion
	}
}
