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
		private static NavNode FindBestCandidateInDirection(NavNode current, NavDirection dir)
		{
			var curCenter = current.GetCenterDip();
			if (!curCenter.HasValue) return null;

			var allCandidates = GetCandidatesInScope();
			if (allCandidates.Count == 0) return null;

			var dirVector = GetDirectionVector(dir);

			if (VerboseNavigationDebug) {
				Debug.WriteLine($"\n[NAV] ========== From '{current.SimpleName}' ? {dir} @ ({curCenter.Value.X:F0},{curCenter.Value.Y:F0}) | Candidates: {allCandidates.Count} ==========");
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
					Debug.WriteLine($"[NAV] ? FOUND in same group: '{sameGroupBest.SimpleName}'");
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
					Debug.WriteLine($"[NAV] ? FOUND across groups: '{acrossGroupsBest.SimpleName}'");
				} else {
				 Debug.WriteLine($"[NAV] ? NO CANDIDATE FOUND");
				}
				Debug.WriteLine($"[NAV] ============================================================\n");
			}

			return acrossGroupsBest;
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

			if (VerboseNavigationDebug && !string.IsNullOrEmpty(phase)) {
				Debug.WriteLine($"[NAV] --- {phase}: {candidates.Count} candidates ---");
			}

			var validCandidates = new List<ScoredCandidate>();

			foreach (var candidate in candidates)
			{
				// Compare by object reference, not HierarchicalPath (which may not be unique)
				if (ReferenceEquals(candidate, current)) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ? '{candidate.SimpleName}' (skipped: same as current node)");
					}
					continue;
				}
				
				var candidateCenter = candidate.GetCenterDip();
				if (!candidateCenter.HasValue) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ? '{candidate.SimpleName}' (skipped: no center point)");
					}
					continue;
				}

				var c = candidateCenter.Value;
				var v = new Point(c.X - currentCenter.X, c.Y - currentCenter.Y);
				var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
				if (len < double.Epsilon) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) (skipped: zero distance)");
					}
					continue;
				}

				var vNorm = new Point(v.X / len, v.Y / len);
				var dot = vNorm.X * dirVector.X + vNorm.Y * dirVector.Y;

				if (dot <= 0) {
					if (VerboseNavigationDebug) {
						Debug.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dot={dot:F2} (wrong direction)");
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
					Debug.WriteLine($"[NAV]   ? '{candidate.SimpleName}' @ ({c.X:F0},{c.Y:F0}) | dist={len:F0} dot={dot:F2} cost={cost:F0}{bonuses}");
				}

				validCandidates.Add(new ScoredCandidate { Node = candidate, Cost = cost });
			}

			if (VerboseNavigationDebug && validCandidates.Count > 0) {
				var sorted = validCandidates.OrderBy(sc => sc.Cost).ToList();
				Debug.WriteLine($"[NAV]   ?? WINNER: '{sorted[0].Node.SimpleName}' (cost={sorted[0].Cost:F0})");
				
				// Show runner-ups if available
				if (sorted.Count > 1) {
					Debug.WriteLine($"[NAV]   ?? Runner-up: '{sorted[1].Node.SimpleName}' (cost={sorted[1].Cost:F0})");
				}
				if (sorted.Count > 2) {
					Debug.WriteLine($"[NAV]   ?? 3rd place: '{sorted[2].Node.SimpleName}' (cost={sorted[2].Cost:F0})");
				}
			}

			return validCandidates.OrderBy(sc => sc.Cost).FirstOrDefault()?.Node;
		}

		/// <summary>
		/// Helper class to store a candidate node with its computed navigation cost.
		/// Used for sorting candidates by score during navigation.
		/// </summary>
		private class ScoredCandidate
		{
			public NavNode Node { get; set; }
			public double Cost { get; set; }
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
		/// Well-aligned nodes receive a cost bonus (×0.8) during navigation scoring.
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
