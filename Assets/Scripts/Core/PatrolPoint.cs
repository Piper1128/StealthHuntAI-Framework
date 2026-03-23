using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Place on important positions in your scene -- doorways, choke points,
    /// corridors, windows. Guards will naturally gravitate toward these positions
    /// during patrol, spreading out to cover the map.
    ///
    /// Works alongside the existing patrolPoints system:
    ///   - If a guard has patrolPoints assigned, those take priority
    ///   - If no patrolPoints, guard uses PatrolPoint registry (tactical patrol)
    /// </summary>
    public class PatrolPoint : MonoBehaviour
    {
        [Tooltip("How tactically important this position is. Higher = visited more often.")]
        [Range(0.1f, 3f)]
        public float importance = 1f;

        [Tooltip("Radius within which this point is considered 'covered'.")]
        [Range(1f, 12f)]
        public float coverageRadius = 4f;

        [Tooltip("Guard will look around for this many seconds when arriving.")]
        [Range(0f, 6f)]
        public float lookAroundTime = 1.5f;

        // ---------- Runtime state --------------------------------------------

        /// <summary>Time.time when a guard last arrived at this point.</summary>
        public float LastVisitedTime { get; private set; } = -999f;

        /// <summary>Which squad last visited this point.</summary>
        public int LastVisitedSquad { get; private set; } = -1;

        public void MarkVisited(int squadID)
        {
            LastVisitedTime = Time.time;
            LastVisitedSquad = squadID;
        }

        /// <summary>Seconds since this point was last visited.</summary>
        public float TimeSinceVisited => Time.time - LastVisitedTime;

        /// <summary>
        /// Tactical score for this point -- higher is more desirable to patrol.
        /// Combines importance, time since visited and heatmap coolness.
        /// </summary>
        public float GetScore(Vector3 guardPos, int squadID)
        {
            float dist = Vector3.Distance(guardPos, transform.position);
            if (dist < 0.5f) return -1f; // already here

            // Time bonus -- cold points are more attractive
            float timeSince = Mathf.Min(TimeSinceVisited, 120f);
            float timeScore = timeSince / 120f;

            // Heatmap -- prefer cool positions
            float heat = HuntDirector.GetHeat(transform.position);
            float heatScore = 1f - heat;

            // Distance penalty -- dont travel too far if closer options exist
            float distScore = 1f - Mathf.Clamp01(dist / 40f);

            // Same squad visited recently -- slight penalty to spread guards out
            float squadPenalty = (LastVisitedSquad == squadID
                && TimeSinceVisited < 15f) ? 0.5f : 1f;

            return importance * (timeScore * 0.5f + heatScore * 0.3f + distScore * 0.2f)
                 * squadPenalty;
        }

        // ---------- Registration ---------------------------------------------

        private void OnEnable() => PatrolRegistry.Register(this);
        private void OnDisable() => PatrolRegistry.Unregister(this);

        // ---------- Gizmos ---------------------------------------------------

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            float age = Application.isPlaying ? TimeSinceVisited : 999f;
            float hotness = Mathf.Clamp01(1f - age / 30f);

            // Color: green = cold/unvisited, red = recently visited
            Gizmos.color = Color.Lerp(
                new Color(0.2f, 0.9f, 0.3f, 0.8f),
                new Color(0.9f, 0.2f, 0.2f, 0.8f),
                hotness);

            Gizmos.DrawSphere(transform.position + Vector3.up * 0.1f, 0.25f);

            // Coverage radius
            Gizmos.color = new Color(0.3f, 0.8f, 0.3f, 0.12f);
            Gizmos.DrawSphere(transform.position, coverageRadius);

            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 0.6f,
                "P " + importance.ToString("F1"),
                UnityEditor.EditorStyles.miniLabel);
#endif
        }
    }
}