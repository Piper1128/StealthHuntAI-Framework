using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// Utility methods for vertical-aware NavMesh sampling.
    /// Used throughout StealthHuntAI to correctly place points on
    /// multi-story NavMeshes with stairs and ramps.
    /// </summary>
    public static class NavMeshHelper
    {
        // Default vertical search range when no override is given
        public const float DefaultHeightRange = 3f;

        /// <summary>
        /// Sample a position on the NavMesh with vertical awareness.
        /// First tries the point as-is, then expands the vertical search
        /// upward and downward by heightRange to find the correct floor.
        ///
        /// Auto mode: heightRange is computed from the agent's step height.
        /// Manual override: pass a positive heightRange to fix the search band.
        /// </summary>
        public static bool Sample(Vector3 point, float horizontalRadius,
                                   out Vector3 result,
                                   float heightRange = -1f,
                                   int areaMask = NavMesh.AllAreas)
        {
            float vRange = heightRange > 0f ? heightRange : DefaultHeightRange;

            // Try the point directly first
            if (NavMesh.SamplePosition(point, out NavMeshHit hit,
                                        horizontalRadius, areaMask))
            {
                result = hit.position;
                return true;
            }

            // Search upward and downward in steps
            int steps = 4;
            float stepSize = vRange / steps;

            for (int i = 1; i <= steps; i++)
            {
                // Try above
                Vector3 above = point + Vector3.up * stepSize * i;
                if (NavMesh.SamplePosition(above, out hit, horizontalRadius, areaMask))
                {
                    result = hit.position;
                    return true;
                }

                // Try below
                Vector3 below = point - Vector3.up * stepSize * i;
                if (NavMesh.SamplePosition(below, out hit, horizontalRadius, areaMask))
                {
                    result = hit.position;
                    return true;
                }
            }

            result = point;
            return false;
        }

        /// <summary>
        /// Generate a horizontal offset vector from a center point and snap
        /// it vertically to the NavMesh. The y component of center is preserved
        /// as the starting search height.
        ///
        /// This replaces the common pattern:
        ///   center + new Vector3(cos * dist, 0f, sin * dist)
        /// with a version that works correctly on multi-story NavMeshes.
        /// </summary>
        public static bool SampleOffset(Vector3 center, float angle, float dist,
                                          float horizontalRadius,
                                          out Vector3 result,
                                          float heightRange = -1f,
                                          int areaMask = NavMesh.AllAreas)
        {
            float rad = angle * Mathf.Deg2Rad;
            Vector3 flatOffset = new Vector3(Mathf.Cos(rad) * dist, 0f,
                                              Mathf.Sin(rad) * dist);
            Vector3 candidate = center + flatOffset;

            return Sample(candidate, horizontalRadius, out result,
                          heightRange, areaMask);
        }

        /// <summary>
        /// Snap an arbitrary world position down onto the nearest NavMesh surface.
        /// Uses a generous vertical search so it works on ramps and stairs.
        /// </summary>
        public static bool SnapToNavMesh(Vector3 worldPos, out Vector3 result,
                                          float heightRange = -1f,
                                          int areaMask = NavMesh.AllAreas)
        {
            return Sample(worldPos, 1f, out result, heightRange, areaMask);
        }

        /// <summary>
        /// Returns true if two NavMesh positions are on the same floor
        /// (within a given vertical tolerance). Useful to avoid sending
        /// units to points on the wrong floor via a stairwell.
        /// </summary>
        public static bool OnSameFloor(Vector3 a, Vector3 b,
                                        float floorTolerance = 1.5f)
        {
            return Mathf.Abs(a.y - b.y) <= floorTolerance;
        }
    }
}