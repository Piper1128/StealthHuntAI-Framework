using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// NavMesh reachability based search strategy.
    /// Generates search waypoints that respect actual NavMesh topology --
    /// including multi-floor layouts with ramps, stairs and level changes.
    ///
    /// How it works:
    ///   1. Computes a reachability budget from time elapsed and estimated player speed
    ///   2. Generates 32 candidate points around last known position
    ///   3. Evaluates each candidate using NavMeshPath -- keeps only reachable ones
    ///   4. Scores survivors by flight vector alignment, concealment, and novelty
    ///   5. Returns sorted list -- unit visits highest scored points first
    ///
    /// This mirrors Alien Isolation's approach where the Xenomorph reasons
    /// about where Amanda physically COULD be, not just where she was seen.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Search Strategy/Reachability Search")]
    public class ReachabilitySearch : MonoBehaviour, ISearchStrategy
    {
        // ---------- Inspector -------------------------------------------------

        [Tooltip("How many candidate points to evaluate per search pass.")]
        [Range(8, 64)] public int candidateCount = 32;

        [Tooltip("Assumed player movement speed in m/s for reachability budget.")]
        [Range(1f, 10f)] public float assumedPlayerSpeed = 4f;

        [Tooltip("Extra budget multiplier to account for uncertainty. " +
                 "1.5 means player could have gone 50% further than minimum estimate.")]
        [Range(1f, 3f)] public float budgetMultiplier = 1.5f;

        [Tooltip("Minimum reachability budget in meters. " +
                 "Prevents zero-budget searches at moment of contact loss.")]
        [Range(2f, 20f)] public float minBudget = 8f;

        [Tooltip("Weight of flight vector alignment in point scoring.")]
        [Range(0f, 1f)] public float flightAlignmentWeight = 0.40f;

        [Tooltip("Weight of concealment potential in point scoring.")]
        [Range(0f, 1f)] public float concealmentWeight = 0.25f;

        [Tooltip("Weight of stimulus direction alignment in scoring.")]
        [Range(0f, 1f)] public float stimulusDirectionWeight = 0.20f;

        [Tooltip("Weight of novelty (not previously visited) in scoring.")]
        [Range(0f, 1f)] public float noveltyWeight = 0.15f;

        [Tooltip("Concavity check rays for concealment scoring.")]
        [Range(4, 12)] public int concavityRays = 6;

        [Tooltip("Concavity ray distance.")]
        [Range(0.5f, 3f)] public float concavityRadius = 1.5f;

        // ---------- ISearchStrategy -------------------------------------------

        public bool IsReady { get; private set; }
        public bool IsExhausted => _index >= _scored.Count;

        // ---------- Internal --------------------------------------------------

        private List<ScoredPoint> _scored = new List<ScoredPoint>();
        private int _index;
        private SearchContext _ctx;
        private Coroutine _coroutine;

        // Pre-allocated NavMeshPath -- initialized in Awake, reused across calls
        private NavMeshPath _navPath;

        private struct ScoredPoint
        {
            public Vector3 Position;
            public float Score;
            public float PathLength;
        }

        // ---------- ISearchStrategy implementation ----------------------------

        private void Awake()
        {
            _navPath = new NavMeshPath();
        }

        public void Initialize(SearchContext context)
        {
            _ctx = context;
            IsReady = false;
            _scored.Clear();
            _index = 0;

            if (_navPath == null)
                _navPath = new NavMeshPath();

            if (_coroutine != null)
                StopCoroutine(_coroutine);

            _coroutine = StartCoroutine(EvaluateCandidates());
        }

        public Vector3? GetNextPoint()
        {
            if (!IsReady || IsExhausted) return null;
            return _scored[_index].Position;
        }

        public void OnPointReached(Vector3 point)
        {
            _index++;

            if (_ctx.VisitedCells != null)
                _ctx.VisitedCells.Add(SpiralSearch.GetCellKey(point, _ctx.CellSize));
        }

        public void Reset()
        {
            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
            _scored.Clear();
            _index = 0;
            IsReady = false;
        }

        // ---------- Coroutine -------------------------------------------------

        private IEnumerator EvaluateCandidates()
        {
            // Compute reachability budget
            float speed = _ctx.EstimatedTargetSpeed > 0.5f
                ? _ctx.EstimatedTargetSpeed
                : assumedPlayerSpeed;

            float budget = Mathf.Max(
                _ctx.TimeSinceLostTarget * speed * budgetMultiplier,
                minBudget);

            // Extend budget by search radius so we always have enough range
            budget = Mathf.Max(budget, _ctx.SearchRadius * 0.6f);

            Vector3 origin = _ctx.LastKnownPosition;
            float hRange = _ctx.HeightRange > 0f ? _ctx.HeightRange : -1f;

            // Generate candidate ring points at expanding distances
            var candidates = new List<Vector3>();
            int rings = 4;
            int perRing = candidateCount / rings;

            for (int ring = 0; ring < rings; ring++)
            {
                float ringFraction = (float)(ring + 1) / rings;
                float ringRadius = _ctx.SearchRadius * ringFraction;
                float angleStep = 360f / perRing;
                float startAngle = Random.Range(0f, 360f);

                for (int i = 0; i < perRing; i++)
                {
                    float angle = startAngle + angleStep * i;
                    if (NavMeshHelper.SampleOffset(origin, angle, ringRadius,
                                                    3f, out Vector3 pt, hRange))
                        candidates.Add(pt);
                }

                yield return null; // spread across frames
            }

            // Evaluate each candidate
            int perFrame = Mathf.Max(4, candidates.Count / 3);
            int count = 0;

            foreach (var candidate in candidates)
            {
                // Skip visited cells early
                int cellKey = SpiralSearch.GetCellKey(candidate, _ctx.CellSize);
                if (_ctx.VisitedCells != null && _ctx.VisitedCells.Contains(cellKey))
                {
                    count++;
                    if (count >= perFrame) { count = 0; yield return null; }
                    continue;
                }

                // Reuse cached NavMeshPath -- avoids per-candidate allocation
                _navPath.ClearCorners();
                bool pathFound = NavMesh.CalculatePath(
                    origin, candidate, NavMesh.AllAreas, _navPath);

                if (!pathFound || _navPath.status == NavMeshPathStatus.PathInvalid)
                {
                    count++;
                    if (count >= perFrame) { count = 0; yield return null; }
                    continue;
                }

                // Measure actual path length
                float pathLength = GetPathLength(_navPath);

                // Reject unreachable points (outside budget)
                if (pathLength > budget)
                {
                    count++;
                    if (count >= perFrame) { count = 0; yield return null; }
                    continue;
                }

                // Score the candidate
                float score = ScoreCandidate(candidate, pathLength, budget);

                _scored.Add(new ScoredPoint
                {
                    Position = candidate,
                    Score = score,
                    PathLength = pathLength
                });

                count++;
                if (count >= perFrame) { count = 0; yield return null; }
            }

            // Sort by score descending
            _scored.Sort((a, b) => b.Score.CompareTo(a.Score));

            // Fallback -- if reachability found nothing use spiral points
            if (_scored.Count == 0)
            {
                FallbackToSpiral();
            }

            IsReady = true;
        }

        // ---------- Scoring ---------------------------------------------------

        private float ScoreCandidate(Vector3 point, float pathLength, float budget)
        {
            float score = 0f;

            // ---- Flight alignment -- actual observed flight vector ----
            if (_ctx.FlightVector.magnitude > 0.1f)
            {
                Vector3 toPoint = (point - _ctx.LastKnownPosition).normalized;
                float dot = Vector3.Dot(_ctx.FlightVector.normalized, toPoint);
                score += Mathf.Clamp01(dot * 0.5f + 0.5f) * flightAlignmentWeight;
            }
            else
            {
                score += 0.5f * flightAlignmentWeight;
            }

            // ---- Predicted flight direction -- historical pattern ----
            if (_ctx.PredictedFlightDir.magnitude > 0.1f)
            {
                Vector3 toPoint = (point - _ctx.LastKnownPosition).normalized;
                float dot = Vector3.Dot(_ctx.PredictedFlightDir, toPoint);
                float predScore = Mathf.Clamp01(dot * 0.5f + 0.5f);
                // Blend with flight alignment -- prediction adds 15% extra bias
                score += predScore * 0.15f;
            }

            // ---- Stimulus direction -- sound direction bias ----
            if (_ctx.PrimaryStimulus.Type == StimulusType.Sound
             && _ctx.PrimaryStimulus.Direction.magnitude > 0.1f
             && _ctx.PrimaryStimulus.DirectionCertainty > 0.3f)
            {
                Vector3 toPoint = (point - _ctx.UnitPosition).normalized;
                float dot = Vector3.Dot(_ctx.PrimaryStimulus.Direction, toPoint);
                score += Mathf.Clamp01(dot * 0.5f + 0.5f)
                                * stimulusDirectionWeight
                                * _ctx.PrimaryStimulus.DirectionCertainty;
            }
            else
            {
                score += 0.5f * stimulusDirectionWeight;
            }

            // ---- Known hide spots -- bias toward places player has hidden ----
            if (_ctx.KnownHideSpots != null && _ctx.KnownHideSpots.Count > 0)
            {
                float hideScore = 0f;
                foreach (var spot in _ctx.KnownHideSpots)
                {
                    float dist = Vector3.Distance(point, spot.Position);
                    if (dist < 4f)
                    {
                        // Recency weighted by found count
                        float recency = Mathf.Clamp01(
                            1f - (Time.time - spot.LastFoundTime) / 120f);
                        float freq = Mathf.Clamp01(spot.FoundCount / 5f);
                        float proximity = 1f - Mathf.Clamp01(dist / 4f);
                        hideScore = Mathf.Max(hideScore,
                            proximity * recency * freq);
                    }
                }
                score += hideScore * 0.25f;
            }

            // ---- Concealment -- enclosed niches score higher ----
            float concealment = EvaluateConcavity(point);
            score += concealment * concealmentWeight;

            // ---- Novelty -- unvisited cells score higher ----
            int cellKey = SpiralSearch.GetCellKey(point, _ctx.CellSize);
            bool visited = _ctx.VisitedCells != null
                        && _ctx.VisitedCells.Contains(cellKey);
            score += (visited ? 0f : 1f) * noveltyWeight;

            return score;
        }

        private float EvaluateConcavity(Vector3 point)
        {
            int hits = 0;
            float angleStep = 360f / concavityRays;

            for (int i = 0; i < concavityRays; i++)
            {
                float angle = angleStep * i;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

                if (Physics.Raycast(point + Vector3.up * 0.5f, dir,
                                     concavityRadius, _ctx.ObstacleMask))
                    hits++;
            }

            return (float)hits / concavityRays;
        }

        // ---------- Helpers ---------------------------------------------------

        private float GetPathLength(NavMeshPath path)
        {
            float length = 0f;
            Vector3[] corners = path.corners;

            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);

            return length;
        }

        private void FallbackToSpiral()
        {
            Vector3 center = _ctx.LastKnownPosition;
            float radius = _ctx.SearchRadius;
            int count = _ctx.SearchPointCount;
            float hRange = _ctx.HeightRange > 0f ? _ctx.HeightRange : -1f;
            float angleStep = 360f / count;
            float angle = Random.Range(0f, 360f);

            for (int i = 0; i < count; i++)
            {
                float dist = radius * (0.4f + 0.6f * ((float)i / count));

                if (NavMeshHelper.SampleOffset(center, angle, dist,
                                                radius, out Vector3 pt, hRange))
                {
                    _scored.Add(new ScoredPoint
                    {
                        Position = pt,
                        Score = 0.5f,
                        PathLength = dist
                    });
                }

                angle += angleStep + Random.Range(-15f, 15f);
            }
        }

        // ---------- Gizmos ----------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || !IsReady) return;

            for (int i = 0; i < _scored.Count; i++)
            {
                var pt = _scored[i];
                bool visited = i < _index;

                Color col = visited
                    ? new Color(0.4f, 0.4f, 0.4f, 0.2f)
                    : Color.Lerp(Color.red, Color.green, pt.Score);

                col.a = visited ? 0.2f : 0.7f;
                Gizmos.color = col;
                Gizmos.DrawWireSphere(pt.Position + Vector3.up * 0.4f, 0.3f);

                UnityEditor.Handles.color = col;
                UnityEditor.Handles.Label(
                    pt.Position + Vector3.up * 0.9f,
                    pt.Score.ToString("F2") +
                    " / " + pt.PathLength.ToString("F0") + "m");
            }
        }
#endif
    }
}