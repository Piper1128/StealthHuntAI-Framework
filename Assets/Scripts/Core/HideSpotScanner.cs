using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// Scans for likely player hiding spots along a flight vector using NavMesh
    /// edge analysis and cover checks. Runs as a coroutine to avoid frame spikes.
    /// Attached automatically by StealthHuntAI -- do not add manually.
    /// </summary>
    public class HideSpotScanner : MonoBehaviour
    {
        // ---------- Configuration ---------------------------------------------

        [Tooltip("How many candidate points to evaluate per scan.")]
        [Range(8, 48)] public int scanResolution = 24;

        [Tooltip("Max range to scan for hide spots along the flight vector.")]
        [Range(5f, 60f)] public float scanRange = 25f;

        [Tooltip("How wide the scan cone is ahead of the flight vector (degrees).")]
        [Range(30f, 180f)] public float scanConeAngle = 110f;

        [Tooltip("Number of rays per candidate for concavity check.")]
        [Range(4, 12)] public int concavityRays = 8;

        [Tooltip("Max concavity ray distance.")]
        [Range(0.5f, 4f)] public float concavityRadius = 1.5f;

        [Tooltip("Vertical search range when snapping candidates to NavMesh. " +
                 "0 = auto. Increase for tall multi-story levels.")]
        [Range(0f, 20f)] public float searchHeightRange = 0f;

        // ---------- Runtime output --------------------------------------------

        /// <summary>Best hide spot candidates from last scan, sorted by score.</summary>
        public List<HideSpotCandidate> Candidates { get; private set; }
            = new List<HideSpotCandidate>();

        /// <summary>True while a scan coroutine is running.</summary>
        public bool IsScanning { get; private set; }

        // ---------- Internal --------------------------------------------------

        private LayerMask _obstacleMask;

        private void Awake()
        {
            _obstacleMask = Physics.DefaultRaycastLayers;
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>
        /// Trigger a scan from a known position along a flight direction.
        /// Results available in Candidates when IsScanning becomes false.
        /// </summary>
        public void Scan(Vector3 fromPosition, Vector3 flightVector,
                          float searchRadius, LayerMask obstacleMask)
        {
            _obstacleMask = obstacleMask;

            if (IsScanning)
                StopAllCoroutines();

            StartCoroutine(ScanCoroutine(fromPosition, flightVector, searchRadius));
        }

        // ---------- Scan coroutine --------------------------------------------

        private IEnumerator ScanCoroutine(Vector3 origin, Vector3 flightDir,
                                           float searchRadius)
        {
            IsScanning = true;
            Candidates.Clear();

            bool hasFlightDir = flightDir.magnitude > 0.1f;
            Vector3 searchDir = hasFlightDir ? flightDir.normalized : Vector3.forward;

            float effectiveRange = Mathf.Min(scanRange, searchRadius * 1.5f);
            float halfCone = scanConeAngle * 0.5f;

            var rawCandidates = new List<HideSpotCandidate>();

            // ----- Pass 1: generate candidate points along flight cone --------
            int perFrame = scanResolution / 3;
            int count = 0;

            for (int i = 0; i < scanResolution; i++)
            {
                float t = (float)i / (scanResolution - 1);
                float dist = effectiveRange * (0.2f + 0.8f * t);

                // Angle biased forward along flight vector
                float angle;
                if (hasFlightDir)
                {
                    float maxAngle = Mathf.Lerp(halfCone * 0.4f, halfCone, t);
                    angle = Random.Range(-maxAngle, maxAngle);
                }
                else
                {
                    angle = Random.Range(-180f, 180f);
                }

                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * searchDir;
                Vector3 point = origin + dir * dist;

                // Snap to NavMesh with vertical awareness
                float hRange = searchHeightRange > 0f ? searchHeightRange : -1f;
                if (!NavMeshHelper.Sample(point, 3f, out Vector3 snapped, hRange))
                    continue;


                var candidate = new HideSpotCandidate
                {
                    Position = snapped,
                    FlightDot = hasFlightDir
                        ? Vector3.Dot(searchDir,
                            (snapped - origin).normalized)
                        : 0.5f,
                    DistanceScore = 1f - Mathf.Clamp01(
                        Vector3.Distance(origin, snapped) / effectiveRange)
                };

                rawCandidates.Add(candidate);

                count++;
                if (count >= perFrame)
                {
                    count = 0;
                    yield return null; // spread across frames
                }
            }

            // ----- Pass 2: concavity + cover check per candidate --------------
            count = 0;
            int concavityPerFrame = Mathf.Max(2, rawCandidates.Count / 3);

            foreach (var candidate in rawCandidates)
            {
                float concavityScore = EvaluateConcavity(candidate.Position);
                float coverScore = EvaluateCover(candidate.Position, origin);

                // Reject points with terrible scores to keep list clean
                float total = ScoreCandidate(candidate, concavityScore, coverScore);
                if (total < 0.15f)
                {
                    count++;
                    if (count >= concavityPerFrame) { count = 0; yield return null; }
                    continue;
                }

                Candidates.Add(new HideSpotCandidate
                {
                    Position = candidate.Position,
                    FlightDot = candidate.FlightDot,
                    DistanceScore = candidate.DistanceScore,
                    ConcavityScore = concavityScore,
                    CoverScore = coverScore,
                    TotalScore = total,
                    DiscoveredTime = Time.time,
                    Investigated = false
                });

                count++;
                if (count >= concavityPerFrame) { count = 0; yield return null; }
            }

            // ----- Pass 3: sort by total score --------------------------------
            Candidates.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));

            // Keep only top candidates to avoid flooding blackboard
            if (Candidates.Count > 8)
                Candidates.RemoveRange(8, Candidates.Count - 8);

            IsScanning = false;
        }

        // ---------- Evaluation helpers ----------------------------------------

        private float EvaluateConcavity(Vector3 point)
        {
            // Cast rays outward in a horizontal ring.
            // More nearby hits = more enclosed = better hiding spot.
            int hits = 0;
            float angleStep = 360f / concavityRays;
            float angle = 0f;

            for (int i = 0; i < concavityRays; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                if (Physics.Raycast(point + Vector3.up * 0.5f, dir,
                                     concavityRadius, _obstacleMask))
                    hits++;

                angle += angleStep;
            }

            // Normalize: 0 = open field, 1 = fully enclosed
            return (float)hits / concavityRays;
        }

        private float EvaluateCover(Vector3 point, Vector3 watcherPos)
        {
            // Does this point have cover from the watcher's position?
            Vector3 toWatcher = (watcherPos - point).normalized;
            float dist = Vector3.Distance(point, watcherPos);

            if (Physics.Raycast(point + Vector3.up * 0.8f, toWatcher,
                                  dist, _obstacleMask))
                return 1f; // fully concealed from watcher

            return 0f;
        }

        private float ScoreCandidate(HideSpotCandidate c,
                                      float concavity, float cover)
        {
            // Flight dot: points behind the player score 0, forward scores 1
            float directionScore = Mathf.Clamp01(c.FlightDot * 0.5f + 0.5f);

            return directionScore * 0.35f
                 + c.DistanceScore * 0.20f
                 + concavity * 0.25f
                 + cover * 0.20f;
        }

        // ---------- Gizmos ----------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            foreach (var c in Candidates)
            {
                // Color by score: red = low, yellow = mid, green = high
                Color col = Color.Lerp(Color.red, Color.green, c.TotalScore);
                col.a = c.Investigated ? 0.2f : 0.7f;
                Gizmos.color = col;
                Gizmos.DrawWireSphere(c.Position + Vector3.up * 0.3f, 0.35f);

                // Score label
                UnityEditor.Handles.color = col;
                UnityEditor.Handles.Label(
                    c.Position + Vector3.up * 0.9f,
                    c.TotalScore.ToString("F2"));
            }
        }
#endif
    }

    // ---------- Data struct ---------------------------------------------------

    [System.Serializable]
    public struct HideSpotCandidate
    {
        public Vector3 Position;
        public float FlightDot;       // dot product with flight vector
        public float DistanceScore;   // closer = higher
        public float ConcavityScore;  // more enclosed = higher
        public float CoverScore;      // has cover from watcher = higher
        public float TotalScore;      // final weighted score
        public float DiscoveredTime;  // Time.time when found
        public bool Investigated;    // has a unit visited this spot
    }
}