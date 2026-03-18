using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using static StealthHuntAI.Combat.CoverPoint;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Auto-detects cover positions in the scene using NavMesh sampling and raycasts.
    /// Runs as a coroutine at scene start so it doesn't block the main thread.
    ///
    /// Manual CoverPoint components placed in the scene always take priority.
    /// Add this component to the HuntDirector GameObject.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Cover Scanner")]
    public class CoverScanner : MonoBehaviour
    {
        [Header("Scanning")]
        [Tooltip("How many sample points to test per scan pass.")]
        [Range(50, 500)] public int samplesPerPass = 200;

        [Tooltip("Radius around scene center to scan.")]
        [Range(10f, 200f)] public float scanRadius = 60f;

        [Tooltip("Minimum distance between cover points.")]
        [Range(0.5f, 5f)] public float minSpacing = 2f;

        [Tooltip("Rescan interval in seconds. 0 = scan once at start only.")]
        [Range(0f, 60f)] public float rescanInterval = 0f;

        [Header("Cover Detection")]
        [Tooltip("Minimum obstacle height to count as cover.")]
        [Range(0.3f, 2f)] public float minCoverHeight = 0.6f;

        [Tooltip("Max distance to sample NavMesh from a candidate point.")]
        [Range(0.5f, 3f)] public float navMeshSampleRadius = 1.5f;

        [Tooltip("Layer mask for cover geometry raycasts.")]
        public LayerMask coverLayers = Physics.DefaultRaycastLayers;

        [Header("Debug")]
        public bool showScanGizmos = false;

        // ---------- Runtime --------------------------------------------------

        private readonly List<CoverPoint> _autoPoints = new List<CoverPoint>();
        private bool _scanning;

        public int AutoPointCount => _autoPoints.Count;
        public bool IsScanning => _scanning;

        // ---------- Unity lifecycle ------------------------------------------

        private void Start()
        {
            StartCoroutine(ScanRoutine());
        }

        private void OnDestroy()
        {
            ClearAutoPoints();
        }

        // ---------- Scanning -------------------------------------------------

        private IEnumerator ScanRoutine()
        {
            while (true)
            {
                yield return StartCoroutine(Scan());

                if (rescanInterval <= 0f) yield break;
                yield return new WaitForSeconds(rescanInterval);

                // Clear old auto-points before rescan
                ClearAutoPoints();
            }
        }

        private IEnumerator Scan()
        {
            _scanning = true;

            int generated = 0;
            int tested = 0;

            // Use scene center as scan origin
            Vector3 origin = transform.position;

            while (tested < samplesPerPass)
            {
                // Random point within scan radius
                Vector2 rand = Random.insideUnitCircle * scanRadius;
                Vector3 sample = origin + new Vector3(rand.x, 0f, rand.y);

                // Sample onto NavMesh
                if (!NavMesh.SamplePosition(sample, out NavMeshHit hit,
                    navMeshSampleRadius, NavMesh.AllAreas))
                {
                    tested++;
                    continue;
                }

                Vector3 navPos = hit.position;

                // Check minimum spacing from existing points
                if (TooClose(navPos))
                {
                    tested++;
                    continue;
                }

                // Check if there is cover geometry nearby
                CoverType? coverType = DetectCover(navPos);
                if (coverType == null)
                {
                    tested++;
                    continue;
                }

                // Generate CoverPoint
                Vector3 peekDir = DetectPeekDirection(navPos);
                CreateCoverPoint(navPos, coverType.Value, peekDir);
                generated++;

                tested++;

                // Yield every 20 tests to avoid frame spikes
                if (tested % 20 == 0)
                    yield return null;
            }

            _scanning = false;
        }

        // ---------- Detection helpers ----------------------------------------

        private CoverType? DetectCover(Vector3 pos)
        {
            // Cast horizontally in 8 directions looking for obstacles
            int wallCount = 0;
            int cornerCount = 0;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

                // Check for wall at cover height
                if (Physics.Raycast(pos + Vector3.up * 0.1f, dir, 1.2f, coverLayers))
                    wallCount++;

                // Check for wall at stand height
                if (Physics.Raycast(pos + Vector3.up * 1.6f, dir, 1.2f, coverLayers))
                    cornerCount++;
            }

            if (wallCount == 0) return null;

            // Is there an opening to peek through? (not all directions blocked)
            if (wallCount >= 6) return null; // completely surrounded

            // High cover: wall at head height
            if (cornerCount >= 2) return CoverType.High;

            // Corner: walls from two perpendicular directions
            if (wallCount == 2) return CoverType.Corner;

            return CoverType.Low;
        }

        private Vector3 DetectPeekDirection(Vector3 pos)
        {
            // Find the direction with least obstruction -- that's where to peek
            float bestDist = -1f;
            Vector3 bestDir = Vector3.forward;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

                float dist = Physics.Raycast(
                    pos + Vector3.up * 1.0f, dir, out RaycastHit hit, 10f, coverLayers)
                    ? hit.distance : 10f;

                if (dist > bestDist) { bestDist = dist; bestDir = dir; }
            }

            return bestDir;
        }

        private bool TooClose(Vector3 pos)
        {
            var points = HuntDirector.AllCoverPoints;
            for (int i = 0; i < points.Count; i++)
            {
                var cp = points[i] as CoverPoint;
                if (cp == null) continue;
                if (Vector3.Distance(pos, cp.transform.position) < minSpacing)
                    return true;
            }
            return false;
        }

        // ---------- Point management -----------------------------------------

        private void CreateCoverPoint(Vector3 pos, CoverType type, Vector3 peekDir)
        {
            var go = new GameObject("CoverPoint_Auto");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(peekDir);

            var cp = go.AddComponent<CoverPoint>();
            cp.type = type;
            cp.isAutoGenerated = true;
            cp.peekDirection = Vector3.zero; // use transform.forward

            _autoPoints.Add(cp);
        }

        private void ClearAutoPoints()
        {
            for (int i = 0; i < _autoPoints.Count; i++)
                if (_autoPoints[i] != null)
                    Destroy(_autoPoints[i].gameObject);
            _autoPoints.Clear();
        }

        // ---------- Gizmos ---------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!showScanGizmos) return;

            Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.1f);
            Gizmos.DrawWireSphere(transform.position, scanRadius);
        }
#endif
    }
}