using FirstFloor.ModernUI.Windows.Media;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Focus Management
	/// 
	/// Responsibilities:
	/// - Focus state management (SetFocus, FocusChanged event)
	/// - Visual feedback (overlay rectangle, mouse tracking)
	/// - Focus initialization (TryInitializeFocusIfNeeded)
	/// - Keyboard focus synchronization
	/// - Scroll-into-view handling
	/// </summary>
	internal static partial class Navigator
	{
		#region Mouse Position Restoration
		
		// ? NEW: Mouse position restoration timer
		// Checks every 500ms if mouse was moved externally and restores it to focused element
		// Stops after 3 seconds (6 checks total) or when focus changes
		private static DispatcherTimer _mouseRestoreTimer;
		private static Point? _expectedMousePosition;
		private static DateTime _mouseRestoreStartTime;
		private static readonly TimeSpan _mouseRestoreMaxDuration = TimeSpan.FromSeconds(3);
		private static readonly TimeSpan _mouseRestoreInterval = TimeSpan.FromMilliseconds(500);
		
		private static void StartMouseRestoreTimer(NavNode node)
		{
			if (!_enableMouseTracking) return;
			
			// Stop any existing timer
			StopMouseRestoreTimer();
			
			// Create new timer
			_mouseRestoreTimer = new DispatcherTimer
			{
				Interval = _mouseRestoreInterval
			};
			_mouseRestoreTimer.Tick += (s, e) => OnMouseRestoreTimerTick(node);
			_mouseRestoreStartTime = DateTime.Now;
			_mouseRestoreTimer.Start();
			
			if (VerboseNavigationDebug)
			{
				Debug.WriteLine($"[Navigator] Started mouse restore timer for '{node.SimpleName}' (3s duration, 500ms interval)");
			}
		}
		
		private static void StopMouseRestoreTimer()
		{
			if (_mouseRestoreTimer != null)
			{
				_mouseRestoreTimer.Stop();
				_mouseRestoreTimer = null;
				_expectedMousePosition = null;
				
				if (VerboseNavigationDebug)
				{
					Debug.WriteLine($"[Navigator] Stopped mouse restore timer");
				}
			}
		}
		
		private static void OnMouseRestoreTimerTick(NavNode node)
		{
			// Check if we've exceeded the max duration
			if (DateTime.Now - _mouseRestoreStartTime > _mouseRestoreMaxDuration)
			{
				if (VerboseNavigationDebug)
				{
					Debug.WriteLine($"[Navigator] Mouse restore timer expired (3s elapsed)");
				}
				StopMouseRestoreTimer();
				return;
			}
			
			// Check if focus has changed
			if (CurrentContext?.FocusedNode != node)
			{
				if (VerboseNavigationDebug)
				{
					Debug.WriteLine($"[Navigator] Mouse restore timer stopped (focus changed)");
				}
				StopMouseRestoreTimer();
				return;
			}
			
			// Get current mouse position
			var currentMousePos = GetCurrentMousePosition();
			if (!currentMousePos.HasValue)
			{
				return; // Can't get current position, skip this check
			}
			
			// Check if mouse was moved away from expected position
			if (_expectedMousePosition.HasValue)
			{
				var distance = Math.Sqrt(
					Math.Pow(currentMousePos.Value.X - _expectedMousePosition.Value.X, 2) +
					Math.Pow(currentMousePos.Value.Y - _expectedMousePosition.Value.Y, 2)
				);
				
				// Allow small tolerance (5 pixels) for rounding/DPI differences
				if (distance > 5.0)
				{
					if (VerboseNavigationDebug)
					{
						Debug.WriteLine($"[Navigator] Mouse moved away (distance: {distance:F1}px), restoring to '{node.SimpleName}'");
					}
					
					// Mouse was moved externally, restore it
					MoveMouseToFocusedNode(node);
				}
			}
		}
		
		/// <summary>
		/// Gets the current mouse position in device pixels.
		/// Returns null if the position cannot be determined.
		/// </summary>
		private static Point? GetCurrentMousePosition()
		{
			try
			{
				var mousePos = System.Windows.Forms.Control.MousePosition;
				return new Point(mousePos.X, mousePos.Y);
			}
			catch
			{
				return null;
			}
		}
		
		#endregion

		#region Focus Management

		/// <summary>
		/// Sets focus to a new node, updates visual feedback, and triggers focus changed event.
		/// Handles scroll-into-view and mouse tracking with proper timing (deferred after layout).
		/// </summary>
		/// <param name="newNode">The node to focus</param>
		/// <returns>True if focus was set successfully</returns>
		private static bool SetFocus(NavNode newNode)
		{
			if (CurrentContext == null) return false;
			if (ReferenceEquals(CurrentContext.FocusedNode, newNode)) return true;

			var oldNode = CurrentContext.FocusedNode;
			
			// ? Stop mouse restore timer when focus changes
			StopMouseRestoreTimer();
			
			if (oldNode != null) {
				oldNode.HasFocus = false;
			}

			if (newNode != null) {
				newNode.HasFocus = true;
				CurrentContext.FocusedNode = newNode;
				
				SetFocusVisuals(newNode);
				
				// ? NEW: Ensure the item is scrolled into view if in a virtualized container
				EnsureScrolledIntoView(newNode);
				
				// ? FIX: Defer mouse movement until after layout completes
				// EnsureScrolledIntoView() triggers a layout pass, but returns immediately.
				// If we move the mouse now, PointToScreen() returns the OLD position (before scroll).
				// Schedule mouse movement on Dispatcher with Render priority (after layout, before render).
				if (_enableMouseTracking) {
					if (newNode.TryGetVisual(out var element)) {
						element.Dispatcher.BeginInvoke(
							DispatcherPriority.Render,  // After layout updates element position
							new Action(() => {
								// Re-check that focus hasn't changed while waiting
								if (CurrentContext?.FocusedNode == newNode) {
									MoveMouseToFocusedNode(newNode);
									// ? NEW: Start mouse restore timer after initial positioning
									StartMouseRestoreTimer(newNode);
								}
							})
						);
					} else {
						// Fallback if visual reference is dead (shouldn't happen for newly focused node)
						MoveMouseToFocusedNode(newNode);
						StartMouseRestoreTimer(newNode);
					}
				}
				
				try { FocusChanged?.Invoke(oldNode, newNode); } catch { }
				return true;
			}

			CurrentContext.FocusedNode = null;
			return false;
		}

		/// <summary>
		/// Initialize focus in the current context if:
		/// 1. Current context exists
		/// 2. Current context has no focus
		/// 3. There are navigable candidates available in scope
		/// 
		/// This is called when modal lifecycle events fire (ModalGroupOpened/Closed).
		/// All nodes within the modal scope are already discovered when this executes,
		/// ensuring complete information for optimal focus selection.
		/// </summary>
		private static void TryInitializeFocusIfNeeded()
		{
			if (CurrentContext == null) {
				Debug.WriteLine($"[Navigator] TryInitializeFocusIfNeeded: CurrentContext is null!");
				return;
			}
			
			if (CurrentContext.FocusedNode != null) {
				Debug.WriteLine($"[Navigator] TryInitializeFocusIfNeeded: Already has focus: {CurrentContext.FocusedNode.SimpleName}");
				return;
			}
			
			Debug.WriteLine($"[Navigator] Finding first navigable in scope '{CurrentContext.ScopeNode.SimpleName}'...");
			
			// Log scope node details
			if (CurrentContext.ScopeNode.TryGetVisual(out var scopeVisual)) {
				Debug.WriteLine($"[Navigator] ScopeNode type: {scopeVisual.GetType().Name}");
			} else {
				Debug.WriteLine($"[Navigator] ScopeNode type: (dead reference)");
			}
			Debug.WriteLine($"[Navigator] ScopeNode path: {CurrentContext.ScopeNode.HierarchicalPath}");
			
			// ? NEW (Phase 5): Use efficient scoped query instead of get-all-then-filter
			var scopePath = CurrentContext.ScopeNode.HierarchicalPath;
			var allNodesInScope = Observer.GetNodesUnderPath(scopePath);
			Debug.WriteLine($"[Navigator] GetNodesUnderPath returned {allNodesInScope.Count} nodes for path: {scopePath}");

			var navigableNodes = allNodesInScope.Where(n => IsNavigableForSelection(n)).ToList();
			Debug.WriteLine($"[Navigator] Navigable nodes (IsGroup=false, IsNavigable=true): {navigableNodes.Count}");
			
			var allCandidates = GetCandidatesInScope();
			Debug.WriteLine($"[Navigator] GetCandidatesInScope() returned {allCandidates.Count} candidates");
			
			if (allCandidates.Count == 0) {
				// Enhanced diagnostics
				Debug.WriteLine($"[Navigator] ? NO CANDIDATES FOUND - Detailed Analysis:");
				
				var inScopeNodes = navigableNodes.Where(n => IsInActiveModalScope(n)).ToList();
				Debug.WriteLine($"[Navigator]   Nodes passing IsInActiveModalScope: {inScopeNodes.Count}");
				
				if (navigableNodes.Count > 0 && inScopeNodes.Count == 0) {
					Debug.WriteLine($"[Navigator]   ? Scope filtering removed ALL candidates!");
					
					// Check first few navigable nodes
					var samplesToCheck = Math.Min(5, navigableNodes.Count);
					for (int i = 0; i < samplesToCheck; i++) {
						var sample = navigableNodes[i];
						Debug.WriteLine($"[Navigator]   Sample #{i}: {sample.SimpleName}");
						
						if (sample.TryGetVisual(out var sampleVisual)) {
							Debug.WriteLine($"[Navigator]     Type: {sampleVisual.GetType().Name}");
						} else {
							Debug.WriteLine($"[Navigator]     Type: (dead reference)");
						}
						
						Debug.WriteLine($"[Navigator]     Path: {sample.HierarchicalPath}");
						Debug.WriteLine($"[Navigator]     IsDescendantOf(ScopeNode): {IsDescendantOf(sample, CurrentContext.ScopeNode)}");
						
						// Walk parent chain
						var current = sample.Parent;
						int depth = 0;
						Debug.WriteLine($"[Navigator]     Parent chain:");
						while (current != null && current.TryGetTarget(out var parentNode) && depth < 8) {
							var isScope = ReferenceEquals(parentNode, CurrentContext.ScopeNode);
							Debug.WriteLine($"[Navigator]       [{depth}] {parentNode.SimpleName} {(isScope ? "? SCOPE ROOT" : "")}");
							current = parentNode.Parent;
							depth++;
							if (isScope) break;
						}
						if (depth >= 8) Debug.WriteLine($"[Navigator]       [...] (chain continues)");
					}
				}
				
				return;
			}
			
			var candidates = allCandidates
				.Select(n => {
					var center = n.GetCenterDip();
					var score = center.HasValue ? center.Value.X + center.Value.Y * 10000.0 : double.MaxValue;
					
					Debug.WriteLine($"  Candidate: {n.SimpleName} @ {n.HierarchicalPath}");
					Debug.WriteLine($"    Center: {center?.X:F1},{center?.Y:F1} | Score: {score:F1}");
					
					return new { Node = n, Score = score };
				})
				.OrderBy(x => x.Score)
				.ToList();
	
			// ? FIX: Skip if all candidates have invalid positions
			if (candidates.Count == 0 || candidates[0].Score == double.MaxValue) {
				Debug.WriteLine($"[Navigator] No valid candidates found (all have invalid positions)");
				return; // Don't try to set focus if nothing is positioned yet
			}
			
			var firstNode = candidates.FirstOrDefault()?.Node;
			if (firstNode != null) {
				Debug.WriteLine($"  ? WINNER: {firstNode.SimpleName} (score: {candidates[0].Score:F1})");
				CurrentContext.FocusedNode = firstNode;
				
				// ? FIX: Defer visual update until after layout is complete
				// SetFocusVisuals() calls UpdateFocusRect() which needs final bounds.
				// During ModalGroupOpened, visual tree may not be fully rendered yet,
				// so we schedule the visual update on Dispatcher with Loaded priority.
				if (Application.Current != null) {
					Application.Current.Dispatcher.BeginInvoke(new Action(() => {
						// Re-check that focus hasn't changed while waiting
						if (CurrentContext?.FocusedNode == firstNode) {
							SetFocusVisuals(firstNode);
						}
					}), DispatcherPriority.Loaded);
				} else {
					// Fallback if no dispatcher available
					SetFocusVisuals(firstNode);
				}
				
				Debug.WriteLine($"[Navigator] Initialized focus in '{CurrentContext.ScopeNode.SimpleName}' -> '{firstNode.SimpleName}'");
				try { FocusChanged?.Invoke(null, firstNode); } catch { }
			} else {
				Debug.WriteLine($"[Navigator] No valid candidate found after filtering!");
			}
		}

		/// <summary>
		/// Updates visual feedback (overlay) for focused node.
		/// Separated from focus state management for clarity.
		/// Also moves mouse to the focused node AND sets WPF keyboard focus.
		/// 
		/// ? FIXED: Now always uses MoveMouseToFocusedNode() which correctly transforms
		/// DIP ? device pixels. Previously used SetCursorPos() directly with DIP coordinates,
		/// causing mouse position errors at non-100% DPI scales (e.g., 150% DPI).
		/// 
		/// ? NEW: Also sets WPF keyboard focus to sync with Navigator's focus tracking.
		/// This prevents WPF from stealing focus to excluded elements like TextBox.
		/// </summary>
		private static void SetFocusVisuals(NavNode node)
		{
			if (node == null) {
#if DEBUG
				_overlay?.HideFocusRect();
#endif
				return;
			}

#if DEBUG
			UpdateFocusRect(node);
#endif

			// ? NEW: Only set WPF keyboard focus for interactive controls, not list items
			// ListBoxItems should not receive keyboard focus during navigation because
			// WPF's ListBox automatically selects items when they receive keyboard focus.
			// We track Navigator focus separately from WPF keyboard focus.
			if (node.TryGetVisual(out var element)) {
				try {
					// Check if this is a list item (ListBoxItem, ComboBoxItem, etc.)
					var isListItem = element is System.Windows.Controls.ListBoxItem ||
									 element is System.Windows.Controls.ComboBoxItem ||
									 element.GetType().Name.Contains("ListBoxItem");
					
					if (!isListItem) {
						// Only set keyboard focus for non-list items (buttons, textboxes, etc.)
						Keyboard.Focus(element);
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Set keyboard focus to '{node.SimpleName}'");
						}
					} else {
						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Skipped keyboard focus for list item '{node.SimpleName}' (would trigger selection)");
						}
					}
				} catch (Exception ex) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Failed to set keyboard focus: {ex.Message}");
					}
				}
			}

			// ? FIXED: Always use MoveMouseToFocusedNode() which handles DIP ? device pixel conversion
			// Removed modal stack depth check - mouse tracking should work consistently everywhere
			if (_enableMouseTracking) {
				MoveMouseToFocusedNode(node);
			}
		}

		/// <summary>
		/// Ensures that the given node's container is scrolled into view if it's inside a virtualized list.
		/// Uses ListBox.ScrollIntoView() which works correctly with virtualization - it will generate
		/// the container if needed and scroll it into view.
		/// 
		/// This is called after setting focus to ensure off-screen items become visible.
		/// </summary>
		private static void EnsureScrolledIntoView(NavNode node)
		{
			if (node == null) return;
			if (!node.TryGetVisual(out var element)) return;

			try {
				// Find parent ListBox (most common virtualized container)
				var listBox = element.GetParent<ListBox>();
				if (listBox != null) {
					// Get the data item from the container
					var item = listBox.ItemContainerGenerator.ItemFromContainer(element);
					if (item != null && item != DependencyProperty.UnsetValue) {
						// Scroll the item into view
						listBox.ScrollIntoView(item);

						if (VerboseNavigationDebug) {
							Debug.WriteLine($"[Navigator] Scrolled '{node.SimpleName}' into view in ListBox");
						}
						return;
					}
				}

				// Fallback: Try ItemsControl (base class for ListBox, ListView, etc.)
				var itemsControl = element.GetParent<ItemsControl>();
				if (itemsControl != null) {
					// ItemsControl doesn't have ScrollIntoView, but we can try BringIntoView on the element
					element.BringIntoView();

					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Called BringIntoView on '{node.SimpleName}' in ItemsControl");
					}
					return;
				}

				// Last resort: Try BringIntoView on any scrollable parent
				var scrollViewer = element.GetParent<ScrollViewer>();
				if (scrollViewer != null) {
					element.BringIntoView();

					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[Navigator] Called BringIntoView on '{node.SimpleName}' in ScrollViewer");
					}
				}
			} catch (Exception ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Failed to scroll '{node.SimpleName}' into view: {ex.Message}");
				}
			}
		}

		/// <summary>
		/// Updates the visual overlay rectangle to highlight the focused node.
		/// Called on layout changes and focus changes.
		/// DEBUG-only since HighlightOverlay is DEBUG-only.
		/// </summary>
		/// <param name="node">The node to highlight</param>
		private static void UpdateFocusRect(NavNode node)
		{
#if DEBUG
			if (_overlay == null || node == null) {
				if (VerboseNavigationDebug && _overlay == null) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: overlay is null");
				}
				return;
			}
			
			if (!node.TryGetVisual(out var fe)) { 
				Debug.WriteLine($"[Navigator] UpdateFocusRect: Visual DEAD for {node.SimpleName}");
				_overlay.HideFocusRect(); 
				return; 
			}

			// Check if element is in visual tree (connected to window)
			if (PresentationSource.FromVisual(fe) == null) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: NO PRESENTATION SOURCE - {node.SimpleName}");
				}
				return;
			}

			// Try to get bounds - handles async image loading gracefully
			Point? centerDip = null;
			try {
				centerDip = node.GetCenterDip();
			} catch (InvalidOperationException ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: BOUNDS ERROR - {node.SimpleName}: {ex.Message}");
				}
				return;
			}

			if (!centerDip.HasValue) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: NO BOUNDS - {node.SimpleName}");
				}
				return;
			}

			if (!fe.IsVisible) {
				Debug.WriteLine($"[Navigator] UpdateFocusRect: NOT VISIBLE - {node.SimpleName}");
				_overlay.HideFocusRect(); 
				return;
			}

			try {
				var topLeft = fe.PointToScreen(new Point(0, 0));
				var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: {node.SimpleName} @ screen({topLeft.X:F1}, {topLeft.Y:F1})");
				}

				var ps = PresentationSource.FromVisual(fe);
				if (ps?.CompositionTarget != null) {
					var transform = ps.CompositionTarget.TransformFromDevice;
					topLeft = transform.Transform(topLeft);
					bottomRight = transform.Transform(bottomRight);
				}

				var rect = new Rect(
					new Point(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y)),
					new Point(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y))
				);

				if (rect.Width >= 1.0 && rect.Height >= 1.0) {
					_overlay.ShowFocusRect(rect);
					
					// DO NOT move mouse here - this gets called on resize/layout changes
					// Mouse should only move on explicit focus changes (in SetFocus)
				} else {
					Debug.WriteLine($"[Navigator] UpdateFocusRect: Rectangle too small ({rect.Width:F1}x{rect.Height:F1}) - {node.SimpleName}");
					_overlay.HideFocusRect();
				}
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] UpdateFocusRect EXCEPTION for {node.SimpleName}: {ex.Message}");
				_overlay.HideFocusRect();
			}
#endif
		}

		/// <summary>
		/// Moves the mouse cursor to the center of the focused NavNode.
		/// This provides visual feedback during keyboard navigation and prepares
		/// the mouse position for potential click-based activation.
		/// 
		/// Tooltips are already disabled globally, so no popup interference.
		/// 
		/// ? NEW: Updates expected mouse position for restoration timer.
		/// </summary>
		private static void MoveMouseToFocusedNode(NavNode node)
		{
			if (node == null) return;

			try {
				var centerDip = node.GetCenterDip();
				if (!centerDip.HasValue) return;

				// Get the visual element to access PresentationSource
				if (!node.TryGetVisual(out var fe)) return;

				// Convert DIP to screen coordinates (device pixels)
				var ps = PresentationSource.FromVisual(fe);
				if (ps?.CompositionTarget == null) return;

				var transformToDevice = ps.CompositionTarget.TransformToDevice;
				var centerDevice = transformToDevice.Transform(centerDip.Value);

				// Get screen size for absolute positioning (SendInput uses 0-65535 range)
				var screenWidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
				var screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

				// Calculate absolute position in SendInput's coordinate space (0-65536)
				var absoluteX = (int)(centerDevice.X * 65536.0 / screenWidth);
				var absoluteY = (int)(centerDevice.Y * 65536.0 / screenHeight);

				// Move mouse to focused element
				var mouse = new AcTools.Windows.Input.MouseSimulator();
				mouse.MoveMouseTo(absoluteX, absoluteY);
				
				// ? NEW: Save expected position for restoration timer
				_expectedMousePosition = new Point(centerDevice.X, centerDevice.Y);

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Mouse moved to '{node.SimpleName}' @ DIP({centerDip.Value.X:F0},{centerDip.Value.Y:F0}) Device({centerDevice.X:F0},{centerDevice.Y:F0})");
				}
			} catch (Exception ex) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[Navigator] Failed to move mouse to '{node?.SimpleName}': {ex.Message}");
				}
			}
		}

		#endregion
	}
}
