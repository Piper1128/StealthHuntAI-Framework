using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Static registry of all PatrolPoints in the scene.
    /// Used by TacticalPatrolController to find the best next destination.
    /// </summary>
    public static class PatrolRegistry
    {
        private static readonly List<PatrolPoint> _points = new List<PatrolPoint>();

        public static void Register(PatrolPoint p)
        {
            if (!_points.Contains(p))
                _points.Add(p);
        }

        public static void Unregister(PatrolPoint p)
            => _points.Remove(p);

        public static IReadOnlyList<PatrolPoint> All => _points;

        public static int Count => _points.Count;

        /// <summary>
        /// Find the best next patrol destination for a guard.
        /// Returns null if no points registered.
        /// </summary>
        public static PatrolPoint FindBest(Vector3 guardPos, int squadID,
                                            PatrolPoint currentPoint = null)
        {
            PatrolPoint best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < _points.Count; i++)
            {
                var p = _points[i];
                if (p == currentPoint) continue; // dont pick current point
                if (!p.gameObject.activeInHierarchy) continue;

                float score = p.GetScore(guardPos, squadID);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            return best;
        }

        /// <summary>
        /// Find the nearest PatrolPoint to a position.
        /// </summary>
        public static PatrolPoint FindNearest(Vector3 pos)
        {
            PatrolPoint nearest = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < _points.Count; i++)
            {
                float d = Vector3.Distance(pos, _points[i].transform.position);
                if (d < bestDist) { bestDist = d; nearest = _points[i]; }
            }

            return nearest;
        }
    }
}