using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Navigator partial class - Navigation Algorithm
	/// 
	/// Responsibilities:
	/// - Direction-based navigation logic
	/// - Candidate scoring and selection
	/// - Spatial distance calculations
	/// - Alignment checks and bonuses
	/// </summary>
	internal static partial class Navigator
	{
		#region Navigation Algorithm

		/// <summary>
		/// Finds the best candidate node to navigate to from the current node in the specified direction.
		/// Uses a two-phase approach: first tries to find candidates within the same non-modal group,
		/// then falls back to searching across all groups if no match is found.
		/// </summary>
		/// <param name="current">The currently focused node</param>
		/// <param name="dir">The direction to navigate (Up, Down, Left, Right)</param>
		/// <returns>The best candidate node, or null if none found</returns>
		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir, List<NavNode> candidates = null, bool useAdjacency = true)
		{
			var currentBounds = current.GetBoundsDip();
			var curCenter = currentBounds.HasValue
					? new Point(currentBounds.Value.Left + currentBounds.Value.Width / 2.0, currentBounds.Value.Top + currentBounds.Value.Height / 2.0)
					: current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			var allCandidates = candidates ?? GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;
			var adjacency = useAdjacency ? _navConfig?.FindAdjacency(current.HierarchicalPath, dir) : null;
			if (adjacency != null) {
				if (adjacency.IsRemoved) {
					if (VerboseNavigationDebug) DebugLog.WriteLine($"[NAV] Adjacency removed: '{current.SimpleName}' ? {dir}");
				} else {
				var matches = allCandidates.Where(x => NavPathFilter.Matches(NavConfiguration.NormalizePath(x.HierarchicalPath), adjacency.TargetFilter)).ToList();
					if (matches.Count == 1) {
						if (VerboseNavigationDebug) DebugLog.WriteLine($"[NAV] Adjacency override: '{current.SimpleName}' ? {dir} ? '{matches[0].SimpleName}'");
						return matches[0];
					}
					if (VerboseNavigationDebug) DebugLog.WriteLine($"[NAV] Adjacency override ignored: '{current.SimpleName}' ? {dir} matched {matches.Count} targets.");
				}
			}

			var dirVector = GetDirectionVector(dir);

			if (VerboseNavigationDebug) {
				DebugLog.WriteLine($"\n[NAV] ========== From '{current.SimpleName}' ? {dir} @ ({curCenter.Value.X:F0},{curCenter.Value.Y:F0}) | Candidates: {allCandidates.Count} ==========");
			}

			var bestCandidate = FindBestInCandidates(
				current, curCenter.Value, dir, dirVector, 
				allCandidates,
				"ALL CANDIDATES"
			);

			if (VerboseNavigationDebug) {
				if (bestCandidate != null) {
					DebugLog.WriteLine($"[NAV] ? FOUND: '{bestCandidate.SimpleName}'");
				} else {
				 DebugLog.WriteLine($"[NAV] ? NO CANDIDATE FOUND");
				}
				DebugLog.WriteLine($"[NAV] ============================================================\n");
			}

			return bestCandidate;
		}

		private static NavNode FindGeometryCandidateInDirection(NavNode current, NavDirection dir, List<NavNode> candidates)
		{
			return FindBestCandidateInDirection(current, dir, candidates, false);
		}

		private static NavNode FindAdjacencyCandidate(NavNode current, NavDirection direction, List<NavNode> candidates)
		{
			var rule = _navConfig?.FindAdjacency(current.HierarchicalPath, direction);
			if (rule == null || rule.IsRemoved) return null;
			var matches = candidates.Where(x => NavPathFilter.Matches(NavConfiguration.NormalizePath(x.HierarchicalPath), rule.TargetFilter)).ToList();
			return matches.Count == 1 ? matches[0] : null;
		}

		[Conditional("DEBUG")]
		private static void AnalyzeNavigationReachability(NavNode initialNode, List<NavNode> candidates)
		{
			if (initialNode == null || candidates == null || candidates.Count == 0) return;
			candidates = candidates.Where(IsVisibleForNavigationAnalysis).ToList();
			if (!candidates.Contains(initialNode) || candidates.Count == 0) return;

			var transitionsByNode = new Dictionary<NavNode, Dictionary<NavDirection, NavNode>>();
			foreach (var source in candidates) {
				var transitions = new Dictionary<NavDirection, NavNode>();
				foreach (NavDirection direction in Enum.GetValues(typeof(NavDirection))) {
					var target = FindBestCandidateInDirection(source, direction, candidates);
					if (target != null && candidates.Contains(target)) transitions[direction] = target;
				}
				transitionsByNode[source] = transitions;
			}

			var reachable = new HashSet<NavNode> { initialNode };
			var pending = new Queue<NavNode>();
			pending.Enqueue(initialNode);
			var reachableTransitions = 0;

			while (pending.Count > 0) {
				var source = pending.Dequeue();
				foreach (var target in transitionsByNode[source].Values) {
					reachableTransitions++;
					if (reachable.Add(target)) pending.Enqueue(target);
				}
			}

			var unreachable = candidates.Where(x => !reachable.Contains(x)).OrderBy(x => x.SimpleName).ToList();
			var symmetryFailures = new List<string>();
			foreach (var source in candidates) {
				foreach (var transition in transitionsByNode[source]) {
					var reverse = GetOppositeDirection(transition.Key);
					NavNode returned;
					if (!transitionsByNode[transition.Value].TryGetValue(reverse, out returned) || !ReferenceEquals(returned, source)) {
						symmetryFailures.Add($"{DescribeNavigationAnalysisNode(source)} ? {transition.Key} ? {DescribeNavigationAnalysisNode(transition.Value)}, but {reverse} returns {DescribeNavigationAnalysisNode(returned)}");
					}
				}
			}

			DebugLog.WriteLine($"[NAV-REACHABILITY] Scope '{CurrentContext?.PageName ?? CurrentContext?.ScopeNode?.SimpleName ?? "(none)"}': {reachable.Count}/{candidates.Count} visible nodes reachable from {DescribeNavigationAnalysisNode(initialNode)} using {reachableTransitions} directional transitions.");
			if (unreachable.Count == 0) {
				DebugLog.WriteLine("[NAV-REACHABILITY] All candidates are reachable.");
			} else {
				foreach (var node in unreachable) {
					DebugLog.WriteLine($"[NAV-REACHABILITY] UNREACHABLE {DescribeNavigationAnalysisNode(node)} @ {node.HierarchicalPath}");
				}
			}

			DebugLog.WriteLine($"[NAV-REACHABILITY] Reverse-direction symmetry: {symmetryFailures.Count} mismatches across {transitionsByNode.Sum(x => x.Value.Count)} transitions.");
			foreach (var failure in symmetryFailures) {
				DebugLog.WriteLine($"[NAV-REACHABILITY] ASYMMETRIC {failure}");
			}
		}

		private static bool IsVisibleForNavigationAnalysis(NavNode node)
		{
			FrameworkElement element;
			return node != null && node.TryGetVisual(out element) && element.IsVisible && element.IsArrangeValid;
		}

		private static string DescribeNavigationAnalysisNode(NavNode node)
		{
			if (node == null) return "none";
			var bounds = node.GetBoundsDip();
			return bounds.HasValue
					? $"'{node.SimpleName}'[{bounds.Value.Left:F0},{bounds.Value.Top:F0},{bounds.Value.Width:F0}x{bounds.Value.Height:F0}]"
					: $"'{node.SimpleName}'[no bounds]";
		}

		private static NavDirection GetOppositeDirection(NavDirection direction)
		{
			switch (direction) {
				case NavDirection.Up: return NavDirection.Down;
				case NavDirection.Down: return NavDirection.Up;
				case NavDirection.Left: return NavDirection.Right;
				case NavDirection.Right: return NavDirection.Left;
				default: throw new ArgumentOutOfRangeException(nameof(direction));
			}
		}

		private static bool IsWithinDirectionalCone(Rect? current, Rect? candidate, NavDirection direction)
		{
			if (!current.HasValue || !candidate.HasValue) return true;
			var source = current.Value;
			var target = candidate.Value;
			double forwardDistance;
			double perpendicularDistance;
			switch (direction) {
				case NavDirection.Left:
					forwardDistance = Math.Max(0.0, source.Left - target.Right);
					perpendicularDistance = Math.Max(0.0, Math.Max(source.Top, target.Top) - Math.Min(source.Bottom, target.Bottom));
					break;
				case NavDirection.Right:
					forwardDistance = Math.Max(0.0, target.Left - source.Right);
					perpendicularDistance = Math.Max(0.0, Math.Max(source.Top, target.Top) - Math.Min(source.Bottom, target.Bottom));
					break;
				case NavDirection.Up:
					forwardDistance = Math.Max(0.0, source.Top - target.Bottom);
					perpendicularDistance = Math.Max(0.0, Math.Max(source.Left, target.Left) - Math.Min(source.Right, target.Right));
					break;
				case NavDirection.Down:
					forwardDistance = Math.Max(0.0, target.Top - source.Bottom);
					perpendicularDistance = Math.Max(0.0, Math.Max(source.Left, target.Left) - Math.Min(source.Right, target.Right));
					break;
				default:
					return false;
			}

			return perpendicularDistance <= forwardDistance;
		}

		/// <summary>
		/// Evaluates and scores a list of candidate nodes to find the best match in the specified direction.
		/// Uses dot product for direction validation and applies bonuses for parent/alignment relationships.
		/// </summary>
		/// <param name="current">The currently focused node</param>
		/// <param name="currentCenter">The center point of the current node (DIP coordinates)</param>
		/// <param name="dir">The navigation direction</param>
		/// <param name="dirVector">The unit vector representing the direction</param>
		/// <param name="candidates">List of candidate nodes to evaluate</param>
		/// <param name="phase">Debug label for the search phase (e.g., "SAME GROUP", "ACROSS GROUPS")</param>
		/// <returns>The best candidate node, or null if none valid</returns>
		private static NavNode FindBestInCandidates(
			NavNode current, Point currentCenter, NavDirection dir, Point dirVector, List<NavNode> candidates,
			String phase = "")
		{
			if (candidates.Count == 0) return null;
			var currentBounds = current.GetBoundsDip();

			if (VerboseNavigationDebug && !string.IsNullOrEmpty(phase)) {
				DebugLog.WriteLine($"[NAV] --- {phase}: {candidates.Count} candidates ---");
			}

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				// Compare by object reference, not HierarchicalPath (which may not be unique)
				if (ReferenceEquals(candidate, current)) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' (skipped: same as current node)");
					}
					continue;
				}
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' (skipped: no center point)");
					}
					continue;
				}

				var candidateBounds = candidate.GetBoundsDip();
				var c = candidateBounds.HasValue
						? new Point(candidateBounds.Value.Left + candidateBounds.Value.Width / 2.0, candidateBounds.Value.Top + candidateBounds.Value.Height / 2.0)
						: candidateCenter.Value;
				if (!IsBeyondDirectionalBoundary(currentBounds, candidateBounds, dir)) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (not beyond {dir} boundary)");
					}
					continue;
				}
				if (!IsWithinDirectionalCone(currentBounds, candidateBounds, dir)) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (outside 45-degree {dir} cone)");
					}
					continue;
				}
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (skipped: zero distance)");
					}

					continue;
				}

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				if (dot <= 0) {
					if (VerboseNavigationDebug) {
						DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dot={dot:F2} (wrong direction)");
					}
					continue;
				}

				var forwardGap = GetForwardGap(currentBounds, candidateBounds, currentCenter, c, dir);
				var alignment = GetPerpendicularAlignment(currentBounds, candidateBounds, currentCenter, c, dir);
				var alignmentPenalty = (1.0 - alignment) * 64.0;
				var visualScore = forwardGap + alignmentPenalty;
				var parentRank = HaveSameImmediateParent(current, candidate) ? 0 : 1;

				if (VerboseNavigationDebug) {
					DebugLog.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | forward={forwardGap:F0} alignment={alignment:F2} penalty={alignmentPenalty:F0} score={visualScore:F0} dist={len:F0} dot={dot:F2} parent={parentRank}");
				}

				validCandidates.Add(new ScoredCandidate {
					Node = candidate,
					ForwardGap = forwardGap,
					Alignment = alignment,
					VisualScore = visualScore,
					ParentRank = parentRank,
					Distance = len
				});
			}

			if (VerboseNavigationDebug && validCandidates.Count > 0) {
				var sorted = SortCandidates(validCandidates).ToList();
				DebugLog.WriteLine($"[NAV]   ?? WINNER: '{sorted[0].Node.SimpleName}' (score={sorted[0].VisualScore:F0}, forward={sorted[0].ForwardGap:F0}, alignment={sorted[0].Alignment:F2})");
				
				// Show runner-ups if available
				if (sorted.Count > 1) {
					DebugLog.WriteLine($"[NAV]   ?? Runner-up: '{sorted[1].Node.SimpleName}' (score={sorted[1].VisualScore:F0}, forward={sorted[1].ForwardGap:F0}, alignment={sorted[1].Alignment:F2})");
				}
				if (sorted.Count > 2) {
					DebugLog.WriteLine($"[NAV]   ?? 3rd place: '{sorted[2].Node.SimpleName}' (score={sorted[2].VisualScore:F0}, forward={sorted[2].ForwardGap:F0}, alignment={sorted[2].Alignment:F2})");
				}
			}

			return SortCandidates(validCandidates).FirstOrDefault()?.Node;
		}

		private static IOrderedEnumerable<ScoredCandidate> SortCandidates(IEnumerable<ScoredCandidate> candidates)
		{
			return candidates.OrderBy(x => x.VisualScore).ThenBy(x => x.ParentRank).ThenBy(x => x.Distance);
		}

		private static double GetPerpendicularAlignment(Rect? current, Rect? candidate, Point currentCenter, Point candidateCenter, NavDirection direction)
		{
			if (!current.HasValue || !candidate.HasValue) return IsWellAligned(currentCenter, candidateCenter, direction) ? 1.0 : 0.0;
			var currentStart = IsVertical(direction) ? current.Value.Left : current.Value.Top;
			var currentEnd = IsVertical(direction) ? current.Value.Right : current.Value.Bottom;
			var candidateStart = IsVertical(direction) ? candidate.Value.Left : candidate.Value.Top;
			var candidateEnd = IsVertical(direction) ? candidate.Value.Right : candidate.Value.Bottom;
			var overlap = Math.Max(0.0, Math.Min(currentEnd, candidateEnd) - Math.Max(currentStart, candidateStart));
			var smallerSpan = Math.Min(currentEnd - currentStart, candidateEnd - candidateStart);
			return smallerSpan > 0.0 ? overlap / smallerSpan : 0.0;
		}

		private static double GetForwardGap(Rect? current, Rect? candidate, Point currentCenter, Point candidateCenter, NavDirection direction)
		{
			if (!current.HasValue || !candidate.HasValue) return IsVertical(direction) ? Math.Abs(candidateCenter.Y - currentCenter.Y) : Math.Abs(candidateCenter.X - currentCenter.X);
			switch (direction) {
				case NavDirection.Up: return Math.Max(0.0, current.Value.Top - candidate.Value.Bottom);
				case NavDirection.Down: return Math.Max(0.0, candidate.Value.Top - current.Value.Bottom);
				case NavDirection.Left: return Math.Max(0.0, current.Value.Left - candidate.Value.Right);
				case NavDirection.Right: return Math.Max(0.0, candidate.Value.Left - current.Value.Right);
				default: return 0.0;
			}
		}

		private static double GetPerpendicularOffset(Rect? current, Rect? candidate, Point currentCenter, Point candidateCenter, NavDirection direction)
		{
			if (!current.HasValue || !candidate.HasValue) return IsVertical(direction) ? Math.Abs(candidateCenter.X - currentCenter.X) : Math.Abs(candidateCenter.Y - currentCenter.Y);
			return IsVertical(direction)
					? Math.Max(0.0, Math.Max(current.Value.Left, candidate.Value.Left) - Math.Min(current.Value.Right, candidate.Value.Right))
					: Math.Max(0.0, Math.Max(current.Value.Top, candidate.Value.Top) - Math.Min(current.Value.Bottom, candidate.Value.Bottom));
		}

		private static bool IsVertical(NavDirection direction)
		{
			return direction == NavDirection.Up || direction == NavDirection.Down;
		}

		private static bool IsBeyondDirectionalBoundary(Rect? current, Rect? candidate, NavDirection direction)
		{
			if (!current.HasValue || !candidate.HasValue) return true;
			switch (direction) {
				case NavDirection.Up: return candidate.Value.Top < current.Value.Top;
				case NavDirection.Down: return candidate.Value.Bottom > current.Value.Bottom;
				case NavDirection.Left: return candidate.Value.Left < current.Value.Left;
				case NavDirection.Right: return candidate.Value.Right > current.Value.Right;
				default: return false;
			}
		}

		/// <summary>
		/// Helper class to store a candidate node with its geometry ranking factors.
		/// Used for lexicographic sorting during navigation.
		/// </summary>
		private class ScoredCandidate
		{
			public NavNode Node { get; set; }
			public double ForwardGap { get; set; }
			public double Alignment { get; set; }
			public double VisualScore { get; set; }
			public int ParentRank { get; set; }
			public double Distance { get; set; }
		}

		/// <summary>
		/// Converts a navigation direction enum to a unit vector.
		/// Used for dot product calculations in candidate scoring.
		/// </summary>
		/// <param name="dir">The navigation direction</param>
		/// <returns>A Point representing the direction vector (magnitude 1.0)</returns>
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

		/// <summary>
		/// Checks if two nodes are well-aligned in the specified direction.
		/// Well-aligned nodes receive a cost bonus (�0.8) during navigation scoring.
		/// </summary>
		/// <param name="from">Starting node center point</param>
		/// <param name="to">Candidate node center point</param>
		/// <param name="dir">Navigation direction</param>
		/// <returns>True if nodes are aligned within threshold (20 DIP)</returns>
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
	}
}
