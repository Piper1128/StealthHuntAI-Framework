using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// Simple geometric spiral search strategy.
    /// Fast and lightweight -- used as fallback when ReachabilitySearch
    /// is still computing or unavailable.
    /// Can also be used directly for simple single-floor scenarios.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Search Strategy/Spiral Search")]
    public class SpiralSearch : MonoBehaviour, ISearchStrategy
    {
        // ---------- Inspector -------------------------------------------------

        [Tooltip("Angle variation per step to avoid perfectly uniform spirals.")]
        [Range(0f, 30f)] public float angleVariation = 15f;

        // ---------- ISearchStrategy -------------------------------------------

        public bool IsReady { get; private set; }
        public bool IsExhausted => _index >= _points.Count;

        // ---------- Internal --------------------------------------------------

        private List<Vector3> _points = new List<Vector3>();
        private int _index;
        private SearchContext _ctx;

        // ---------- ISearchStrategy implementation ----------------------------

        public void Initialize(SearchContext context)
        {
            _ctx = context;
            IsReady = false;
            _points.Clear();
            _index = 0;

            GeneratePoints();
            IsReady = true;
        }

        public Vector3? GetNextPoint()
        {
            if (!IsReady || IsExhausted) return null;
            return _points[_index];
        }

        public void OnPointReached(Vector3 point)
        {
            _index++;

            // Mark cell as visited
            if (_ctx.VisitedCells != null)
                _ctx.VisitedCells.Add(GetCellKey(point, _ctx.CellSize));
        }

        public void Reset()
        {
            _points.Clear();
            _index = 0;
            IsReady = false;
        }

        // ---------- Point generation ------------------------------------------

        private void GeneratePoints()
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

                if (!NavMeshHelper.SampleOffset(center, angle, dist,
                                                 radius, out Vector3 pt, hRange))
                {
                    angle += angleStep + Random.Range(-angleVariation, angleVariation);
                    continue;
                }

                // Skip heavily visited cells
                int cellKey = GetCellKey(pt, _ctx.CellSize);
                if (_ctx.VisitedCells != null && _ctx.VisitedCells.Contains(cellKey))
                {
                    angle += angleStep + Random.Range(-angleVariation, angleVariation);
                    continue;
                }

                _points.Add(pt);
                angle += angleStep + Random.Range(-angleVariation, angleVariation);
            }
        }

        // ---------- Utility ---------------------------------------------------

        public static int GetCellKey(Vector3 point, float cellSize)
        {
            int cx = Mathf.RoundToInt(point.x / cellSize);
            int cz = Mathf.RoundToInt(point.z / cellSize);
            return cx * 100000 + cz;
        }
    }
}