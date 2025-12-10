using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;

namespace AcManager.UiObserver
{
	internal enum NavDirection { Up, Down, Left, Right }

	/// <summary>
	/// Represents a navigation context within the modal hierarchy.
	/// Each context bundles a modal scope (the root node defining the context)
	/// with the currently focused node within that scope.
	/// </summary>
	internal class NavContext
	{
		/// <summary>
		/// The modal node that defines this context's scope.
		/// For root context, this is the MainWindow.
		/// For modal contexts, this is the Popup/Window/PopupRoot node.
		/// Never null.
		/// </summary>
		public NavNode ModalNode { get; }
		
		/// <summary>
		/// Currently focused node within this modal context.
		/// Null if no focus has been established yet in this context.
		/// </summary>
		public NavNode FocusedNode { get; set; }
		
		public NavContext(NavNode modalNode, NavNode focusedNode = null)
		{
			ModalNode = modalNode ?? throw new ArgumentNullException(nameof(modalNode));
			FocusedNode = focusedNode;
		}
	}

	/// <summary>
	/// Navigator - Navigation coordinator with event-driven architecture.
	/// 
	/// Responsibilities:
	/// - Subscribe to Observer modal lifecycle events (ModalGroupOpened, ModalGroupClosed)
	/// - Manage modal context stack (each context = modal scope + focused node)
	/// - Handle keyboard input (Ctrl+Shift+Arrow keys for navigation)
	/// - Filter navigable candidates by modal scope
	/// - Find best candidate in direction using spatial algorithm
	/// - Manage focus highlighting overlay
	/// 
	/// Architecture:
	/// - NavNode: Data + type-specific behaviors (CreateNavNode, Activate, Close)
	/// - Observer: Discovery engine (scans visual trees silently, emits modal lifecycle events)
	/// - Navigator: Navigation logic (subscribes to modal events only, manages modal stack, handles input)
	/// 
	/// Note: Individual node discovery is SILENT. Navigator only reacts to modal lifecycle changes,
	/// which provides complete information about all nodes in the modal scope at once.
	/// </summary>
	internal static partial class Navigator
	{
		#region Fields and Properties

		private static bool _initialized = false;

		// Modal context stack - each entry bundles modal scope + focused node
		// Invariant: _modalContextStack.Count >= 1 after initialization (root context always present)
		internal static readonly List<NavContext> _modalContextStack = new List<NavContext>();
		
		// Helper property for current context (never null after initialization)
		internal static NavContext CurrentContext => _modalContextStack.Count > 0 
			? _modalContextStack[_modalContextStack.Count - 1] 
			: null;

		// Events
		internal static event Action<NavNode, NavNode> FocusChanged; // (oldNode, newNode)

		// Highlighting overlay (used by both production focus and debug visualization)
		internal static HighlightOverlay _overlay;

		/// <summary>
		/// Gets whether verbose navigation debug output is enabled.
		/// Controlled by Ctrl+Shift+F9 hotkey in debug builds.
		/// Field defined in Navigator.Debug.cs partial class.
		/// </summary>
		internal static bool VerboseNavigationDebug =>
#if DEBUG
			_verboseNavigationDebug;
#else
			false;
#endif

		#endregion

		#region Initialization

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;

			// Initialize navigation rules
			InitializeNavigationRules();

			// Subscribe to Observer events
			Observer.ModalGroupOpened += OnModalGroupOpened;
			Observer.ModalGroupClosed += OnModalGroupClosed;
			Observer.WindowLayoutChanged += OnWindowLayoutChanged;
			
			// ? FIX: Create overlay BEFORE starting Observer to avoid race condition
			// When Observer discovers MainWindow and fires ModalGroupOpened, the overlay
			// must already exist so focus initialization can show the blue rectangle
			EnsureOverlay();
			
			// Startup Observer (it will hook windows itself and fire ModalGroupOpened)
			Observer.Initialize();

			// Register keyboard handler
			try {
				EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, 
					new KeyEventHandler(OnPreviewKeyDown), true);
			} catch { }
		}
		
		private static void InitializeNavigationRules()
		{
			var rules = new[] {
				// Exclude scrollbars in popups/menus
				"EXCLUDE: *:PopupRoot > ** > *:ScrollBar",
				"EXCLUDE: *:PopupRoot > ** > *:BetterScrollBar",

				// Exclude text or fancy menu items
				"EXCLUDE: ** > *:HistoricalTextBox > **",
				"EXCLUDE: ** > *:LazyMenuItem > **",

				// CRITICAL: Exclude debug overlay from navigation tracking to prevent feedback loop
				"EXCLUDE: *:HighlightOverlay > **",

				// Exclude main menu and content frame from navigation
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > (unnamed):DockPanel > PART_Menu:ModernMenu",
				"EXCLUDE: Window:MainWindow > WindowBorder:Border > (unnamed):AdornerDecorator > (unnamed):Cell > (unnamed):Cell > (unnamed):AdornerDecorator > LayoutRoot:DockPanel > ContentFrame:ModernFrame",

				"CLASSIFY: ** > *:SelectCarDialog => role=group; modal=true",
				"CLASSIFY: ** > *:SelectTrackDialog => role=group; modal=true",
				"CLASSIFY: ** > PART_SystemButtonsPanel:StackPanel => role=group; modal=false",
				"CLASSIFY: ** > PART_TitleBar:DockPanel > *:ItemsControl => role=group; modal=false",
			};

			try {
				NavNode.PathFilter.ParseRules(rules);
			} catch (Exception ex) {
				Debug.WriteLine($"[Navigator] Failed to initialize navigation rules: {ex}");
			}
		}

		internal static void EnsureOverlay()
		{
			if (_overlay == null) {
				try {
					_overlay = new HighlightOverlay();
				} catch (Exception ex) {
					Debug.WriteLine($"[Navigator] Failed to create overlay: {ex.Message}");
				}
			}
		}

		#endregion

		#region Event Handlers

		private static void OnModalGroupOpened(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[Navigator] Modal opened: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			// Validate linear chain (except for root context AND nodes with no parent yet)
			if (_modalContextStack.Count > 0) {
				var currentTop = CurrentContext.ModalNode;
				
				// Optimistic validation: Only reject if HAS parent but WRONG parent
				// Accept modals with no parent yet (will be linked by Observer after this event)
				if (modalNode.Parent != null && !IsDescendantOf(modalNode, currentTop)) {
					Debug.WriteLine($"[Navigator] ERROR: Modal {modalNode.SimpleName} not descendant of {currentTop.SimpleName}!");
					return;
				}
			}
			
			// Push new context onto stack
			var newContext = new NavContext(modalNode, focusedNode: null);
			_modalContextStack.Add(newContext);
			
			Debug.WriteLine($"[Navigator] Modal stack depth: {_modalContextStack.Count}");
			
			// Clear old focus visuals (new context has no focus yet)
			_overlay?.HideFocusRect();
			
			// Initialize focus in new context (all nodes within scope are already discovered!)
			TryInitializeFocusIfNeeded();
		}

		private static void OnModalGroupClosed(NavNode modalNode)
		{
			if (modalNode == null) return;
			
			Debug.WriteLine($"[Navigator] Modal closed: {modalNode.SimpleName} @ {modalNode.HierarchicalPath}");
			
			// Pop context from stack
			if (_modalContextStack.Count > 0 
				&& CurrentContext.ModalNode.HierarchicalPath == modalNode.HierarchicalPath) {
				_modalContextStack.RemoveAt(_modalContextStack.Count - 1);
			} else {
				Debug.WriteLine($"[Navigator] WARNING: Closed modal not at top");
				_modalContextStack.RemoveAll(ctx => ctx.ModalNode.HierarchicalPath == modalNode.HierarchicalPath);
			}
			
			// Restore focus from previous context
			if (CurrentContext != null && CurrentContext.FocusedNode != null) {
				SetFocusVisuals(CurrentContext.FocusedNode);
				Debug.WriteLine($"[Navigator] Restored focus to '{CurrentContext.FocusedNode.SimpleName}'");
			} else {
				// No previous focus - try to initialize
				TryInitializeFocusIfNeeded();
			}
		}

		private static void OnWindowLayoutChanged(object sender, EventArgs e)
		{
			if (CurrentContext?.FocusedNode != null) {
				UpdateFocusRect(CurrentContext.FocusedNode);
			}
		}

		#endregion

		#region Focus Initialization

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
			
			Debug.WriteLine($"[Navigator] Finding first navigable in scope '{CurrentContext.ModalNode.SimpleName}'...");
			
			// Get all candidates and log details
			var allCandidates = GetCandidatesInScope();
			Debug.WriteLine($"[Navigator] GetCandidatesInScope() returned {allCandidates.Count} candidates");
			
			if (allCandidates.Count == 0) {
				// Diagnose why no candidates
				var allNodes = Observer.GetAllNavNodes().ToList();
				Debug.WriteLine($"[Navigator] Total nodes from Observer: {allNodes.Count}");
				
				var navigableNodes = allNodes.Where(n => IsNavigableForSelection(n)).ToList();
				Debug.WriteLine($"[Navigator] Navigable nodes: {navigableNodes.Count}");
				
				var inScopeNodes = navigableNodes.Where(n => IsInActiveModalScope(n)).ToList();
				Debug.WriteLine($"[Navigator] In active modal scope: {inScopeNodes.Count}");
				
				if (navigableNodes.Count > 0 && inScopeNodes.Count == 0) {
					Debug.WriteLine($"[Navigator] Modal scope filtering removed all candidates!");
					Debug.WriteLine($"[Navigator] CurrentContext.ModalNode: {CurrentContext.ModalNode.HierarchicalPath}");
					
					// Check a sample node
					var sample = navigableNodes.First();
					Debug.WriteLine($"[Navigator] Sample navigable node: {sample.SimpleName} @ {sample.HierarchicalPath}");
					Debug.WriteLine($"[Navigator] Sample is descendant: {IsDescendantOf(sample, CurrentContext.ModalNode)}");
					
					// Walk up parent chain
					var current = sample.Parent;
					int depth = 0;
					while (current != null && current.TryGetTarget(out var parentNode) && depth < 10) {
						Debug.WriteLine($"[Navigator]   Parent {depth}: {parentNode.SimpleName} @ {parentNode.HierarchicalPath}");
						if (ReferenceEquals(parentNode, CurrentContext.ModalNode)) {
							Debug.WriteLine($"[Navigator]   ? Found modal node at depth {depth}!");
						}
						current = parentNode.Parent;
						depth++;
					}
				}
				
				return;
			}
			
			var candidates = allCandidates
				.Where(n => CurrentContext.ModalNode == null || IsDescendantOf(n, CurrentContext.ModalNode))
				.Select(n => {
					var center = n.GetCenterDip();
					var score = center.HasValue ? center.Value.X + center.Value.Y * 10000.0 : double.MaxValue;
					
					Debug.WriteLine($"  Candidate: {n.SimpleName} @ {n.HierarchicalPath}");
					Debug.WriteLine($"    Center: {center?.X:F1},{center?.Y:F1} | Score: {score:F1}");
					
					return new { Node = n, Score = score };
				})
				.OrderBy(x => x.Score)
				.ToList();
			
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
				
				Debug.WriteLine($"[Navigator] Initialized focus in '{CurrentContext.ModalNode.SimpleName}' -> '{firstNode.SimpleName}'");
				try { FocusChanged?.Invoke(null, firstNode); } catch { }
			} else {
				Debug.WriteLine($"[Navigator] No valid candidate found after filtering!");
			}
		}

		/// <summary>
		/// Updates visual feedback (overlay) for focused node.
		/// Separated from focus state management for clarity.
		/// </summary>
		private static void SetFocusVisuals(NavNode node)
		{
			if (node != null)
			{
				node.HasFocus = true;
				UpdateFocusRect(node);
			}
			else
			{
				_overlay?.HideFocusRect();
			}
		}

		#endregion

		#region Public API

		public static bool MoveInDirection(NavDirection dir)
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			var best = FindBestCandidateInDirection(CurrentContext.FocusedNode, dir);
			if (best == null) return false;

			return SetFocus(best);
		}

		public static bool ActivateFocusedNode()
		{
			if (CurrentContext == null || CurrentContext.FocusedNode == null) return false;

			return CurrentContext.FocusedNode.Activate();
		}

		public static bool ExitGroup()
		{
			if (CurrentContext == null) return false;
			return CurrentContext.ModalNode.Close();
		}

		#endregion

		#region Focus Management

		private static bool SetFocus(NavNode newNode)
		{
			if (CurrentContext == null) return false;
			if (ReferenceEquals(CurrentContext.FocusedNode, newNode)) return true;

			var oldNode = CurrentContext.FocusedNode;
			
			if (oldNode != null) {
				oldNode.HasFocus = false;
			}

			if (newNode != null) {
				newNode.HasFocus = true;
				CurrentContext.FocusedNode = newNode;
				UpdateFocusRect(newNode);
				try { FocusChanged?.Invoke(oldNode, newNode); } catch { }
				return true;
			}

			CurrentContext.FocusedNode = null;
			return false;
		}

		private static void UpdateFocusRect(NavNode node)
		{
			if (_overlay == null || node == null) return;
			if (!node.TryGetVisual(out var fe)) { _overlay.HideFocusRect(); return; }
			if (!fe.IsLoaded || !fe.IsVisible) { _overlay.HideFocusRect(); return; }

			try {
				var topLeft = fe.PointToScreen(new Point(0, 0));
				var bottomRight = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));

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
				} else {
					_overlay.HideFocusRect();
				}
			} catch {
				_overlay.HideFocusRect();
			}
		}

		#endregion

		#region Navigation Algorithm

		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir)
		{
			var curCenter = current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			var allCandidates = GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;

			var dirVector = GetDirectionVector(dir);

			if (VerboseNavigationDebug) {
				Debug.WriteLine($"\n[NAV] ========== From '{current.SimpleName}' → {dir} @ ({curCenter.Value.X:F0},{curCenter.Value.Y:F0}) | Candidates: {allCandidates.Count} ==========");
			}

			// Try same group first
			var sameGroupCandidates = allCandidates.Where(c => AreInSameNonModalGroup(current, c)).ToList();
			
			var sameGroupBest = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector,
				sameGroupCandidates,
				"SAME GROUP"
			);

			if (sameGroupBest != null) {
				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[NAV] ✅ FOUND in same group: '{sameGroupBest.SimpleName}'");
					Debug.WriteLine($"[NAV] ============================================================\n");
				}
				return sameGroupBest;
			}

			if (VerboseNavigationDebug) {
				Debug.WriteLine($"[NAV] No match in same group, trying across groups...");
			}

			// Try across groups
			var acrossGroupsBest = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector, 
				allCandidates,
				"ACROSS GROUPS"
			);

			if (VerboseNavigationDebug) {
				if (acrossGroupsBest != null) {
					Debug.WriteLine($"[NAV] ✅ FOUND across groups: '{acrossGroupsBest.SimpleName}'");
				} else {
					Debug.WriteLine($"[NAV] ❌ NO CANDIDATE FOUND");
				}
				Debug.WriteLine($"[NAV] ============================================================\n");
			}

			return acrossGroupsBest;
		}

		private static NavNode FindBestInCandidates(
			NavNode current, Point currentCenter, NavDirection dir, Point dirVector, List<NavNode> candidates,
			string phase = "")
		{
			if (candidates.Count == 0) return null;

			if (VerboseNavigationDebug && !string.IsNullOrEmpty(phase)) {
				Debug.WriteLine($"[NAV] --- {phase}: {candidates.Count} candidates ---");
			}

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				// Compare by object reference, not HierarchicalPath (which may not be unique)
				if (ReferenceEquals(candidate, current)) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' (skipped: same as current node)");
					}
					continue;
				}
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' (skipped: no center point)");
					}
					continue;
				}

				var c = candidateCenter.Value;
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ⊘ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (skipped: zero distance)");
					}
					continue;
				}

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				if (dot <= 0) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ❌ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dot={dot:F2} (wrong direction)");
					}
					continue;
				}

				var cost = len / Math.Max(1e-7, dot);
				var bonuses = "";

				if (HaveSameImmediateParent(current, candidate)) {
					cost *= 0.7;
					bonuses += " parent×0.7";
				}
				
				if (IsWellAligned(currentCenter, c, dir)) {
					cost *= 0.8;
					bonuses += " align×0.8";
				}

				if (VerboseNavigationDebug) {
					Debug.WriteLine($"[NAV]   ✅ '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dist={len:F0} dot={dot:F2} cost={cost:F0}{bonuses}");
				}

				validCandidates.Add(new ScoredCandidate { Node = candidate, Cost = cost });
			}

			if (VerboseNavigationDebug && validCandidates.Count > 0) {
				var sorted = validCandidates.OrderBy(sc => sc.Cost).ToList();
				Debug.WriteLine($"[NAV]   🥇 WINNER: '{sorted[0].Node.SimpleName}' (cost={sorted[0].Cost:F0})");
				
				// Show runner-ups if available
				if (sorted.Count > 1) {
					Debug.WriteLine($"[NAV]   🥈 Runner-up: '{sorted[1].Node.SimpleName}' (cost={sorted[1].Cost:F0})");
				}
				if (sorted.Count > 2) {
					Debug.WriteLine($"[NAV]   🥉 3rd place: '{sorted[2].Node.SimpleName}' (cost={sorted[2].Cost:F0})");
				}
			}

			return validCandidates.OrderBy(sc => sc.Cost).FirstOrDefault()?.Node;
		}

		private class ScoredCandidate
		{
			public NavNode Node { get; set; }
			public double Cost { get; set; }
		}

		private static Point GetDirectionVector(NavDirection dir)
		{
			switch (dir) {
				case NavDirection.Up: return new Point(0, -1);
				case NavDirection.Down: return new Point(0, 1);
				case NavDirection.Left: return new Point(-1, 0);
				case NavDirection.Right: return new Point(1, 0);
				default: return new Point(0, 0);
			}
		}

		private static bool IsWellAligned(Point from, Point to, NavDirection dir)
		{
			const double threshold = 20.0;
			switch (dir) {
				case NavDirection.Up:
				case NavDirection.Down:
					return Math.Abs(from.X - to.X) < threshold;
				case NavDirection.Left:
				case NavDirection.Right:
					return Math.Abs(from.Y - to.Y) < threshold;
				default:
					return false;
			}
		}

		#endregion

		#region Helper Methods

		internal static bool IsDescendantOf(NavNode child, NavNode ancestor)
		{
			if (child == null || ancestor == null) return false;
			
			// Walk up the Parent chain (which includes PlacementTarget bridges!)
			var current = child.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (ReferenceEquals(parentNode, ancestor)) return true;
				current = parentNode.Parent;
			}
			
			return false;
		}

		internal static bool IsInActiveModalScope(NavNode node)
		{
			if (CurrentContext == null) return true;
			return IsDescendantOf(node, CurrentContext.ModalNode);
		}

		internal static List<NavNode> GetCandidatesInScope()
		{
			return Observer.GetAllNavNodes()
				.Where(n => IsNavigableForSelection(n) && IsInActiveModalScope(n))
				.ToList();
		}

		private static bool IsNavigableForSelection(NavNode node)
		{
			if (!node.IsNavigable) return false;
			return !node.IsGroup;
		}

		private static bool AreInSameNonModalGroup(NavNode a, NavNode b)
		{
			var groupA = FindClosestNonModalGroup(a);
			var groupB = FindClosestNonModalGroup(b);
			if (groupA == null || groupB == null) return false;
			return groupA.HierarchicalPath == groupB.HierarchicalPath;
		}

		private static NavNode FindClosestNonModalGroup(NavNode node)
		{
			if (node == null) return null;

			var current = node.Parent;
			while (current != null && current.TryGetTarget(out var parentNode))
			{
				if (parentNode.IsGroup)
				{
					if (parentNode.IsModal) return null;
					return parentNode;
				}
				current = parentNode.Parent;
			}
			
			return null;
		}

		private static bool HaveSameImmediateParent(NavNode a, NavNode b)
		{
			if (a == null || b == null) return false;
			if (a.Parent == null || b.Parent == null) return false;
			
			if (!a.Parent.TryGetTarget(out var aParent)) return false;
			if (!b.Parent.TryGetTarget(out var bParent)) return false;
			
			return ReferenceEquals(aParent, bParent);
		}

		#endregion

		#region Keyboard Input

		private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e == null) return;
			
			// Debug hotkeys (F9/F11/F12) handled in Navigator.Debug.cs partial class
			
			// Ctrl+Shift+Arrow keys: Navigation (ensure ONLY Ctrl+Shift, no other modifiers)
			if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
				bool handled = false;
				
				switch (e.Key) {
					case Key.Up: handled = MoveInDirection(NavDirection.Up); break;
					case Key.Down: handled = MoveInDirection(NavDirection.Down); break;
					case Key.Left: handled = MoveInDirection(NavDirection.Left); break;
					case Key.Right: handled = MoveInDirection(NavDirection.Right); break;
					case Key.Return: handled = ActivateFocusedNode(); break;
					case Key.Escape: handled = ExitGroup(); break;
					case Key.F9:  // Debug hotkey - handled in Navigator.Debug.cs
					case Key.F11: // Debug hotkey - handled in Navigator.Debug.cs
					case Key.F12: // Debug hotkey - handled in Navigator.Debug.cs
						OnDebugHotkey(e);
						return;
				}
				
				if (handled) e.Handled = true;
			}
		}

		// Partial method declaration - implemented in Navigator.Debug.cs
		static partial void OnDebugHotkey(KeyEventArgs e);

		#endregion
	}
}
