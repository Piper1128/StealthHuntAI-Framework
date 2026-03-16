using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    // ---------- Stimulus types ------------------------------------------------

    public enum StimulusType
    {
        Sight,       // direct visual -- high position certainty, low direction certainty
        Sound,       // heard noise   -- low position certainty, high direction certainty
        SquadIntel,  // from blackboard broadcast
        Director     // awareness bump from HuntDirector
    }

    // ---------- Stimulus record -----------------------------------------------

    /// <summary>
    /// A single sensory event recorded by AwarenessSensor.
    /// Stored in a circular history buffer for use by search strategies.
    /// </summary>
    public struct StimulusRecord
    {
        /// <summary>Type of stimulus that generated this record.</summary>
        public StimulusType Type;

        /// <summary>World position where stimulus was detected.</summary>
        public Vector3 Position;

        /// <summary>
        /// Direction the stimulus came from.
        /// Meaningful for Sound (direction of noise source).
        /// Zero for Sight (position is already precise).
        /// </summary>
        public Vector3 Direction;

        /// <summary>0-1. How confident are we in Position accuracy.</summary>
        public float PositionCertainty;

        /// <summary>0-1. How confident are we in Direction accuracy.</summary>
        public float DirectionCertainty;

        /// <summary>Overall confidence in this record. Decays over time.</summary>
        public float Confidence;

        /// <summary>Time.time when this record was created.</summary>
        public float Timestamp;

        /// <summary>Age of this record in seconds.</summary>
        public float Age => Time.time - Timestamp;

        // Factory methods for common stimulus types
        public static StimulusRecord FromSight(Vector3 position, float confidence = 1f)
        {
            return new StimulusRecord
            {
                Type = StimulusType.Sight,
                Position = position,
                Direction = Vector3.zero,
                PositionCertainty = 1.0f,
                DirectionCertainty = 0.3f,
                Confidence = confidence,
                Timestamp = Time.time
            };
        }

        public static StimulusRecord FromSound(Vector3 position, Vector3 direction,
                                                float confidence)
        {
            return new StimulusRecord
            {
                Type = StimulusType.Sound,
                Position = position,
                Direction = direction.normalized,
                PositionCertainty = 0.4f,
                DirectionCertainty = 0.8f,
                Confidence = confidence,
                Timestamp = Time.time
            };
        }

        public static StimulusRecord FromSquadIntel(Vector3 position, float confidence)
        {
            return new StimulusRecord
            {
                Type = StimulusType.SquadIntel,
                Position = position,
                Direction = Vector3.zero,
                PositionCertainty = confidence * 0.7f,
                DirectionCertainty = 0.2f,
                Confidence = confidence,
                Timestamp = Time.time
            };
        }
    }

    // ---------- Search context ------------------------------------------------

    /// <summary>
    /// All information a search strategy needs to generate waypoints.
    /// Passed to ISearchStrategy.Initialize() at the start of a search.
    /// </summary>
    public struct SearchContext
    {
        /// <summary>Last confirmed world position of the target.</summary>
        public Vector3 LastKnownPosition;

        /// <summary>The most recent and relevant stimulus record.</summary>
        public StimulusRecord PrimaryStimulus;

        /// <summary>Full stimulus history from AwarenessSensor.</summary>
        public List<StimulusRecord> StimulusHistory;

        /// <summary>
        /// Seconds elapsed since target was lost.
        /// Used to compute reachability budget.
        /// </summary>
        public float TimeSinceLostTarget;

        /// <summary>Estimated target movement speed (from FlightVector magnitude).</summary>
        public float EstimatedTargetSpeed;

        /// <summary>
        /// Flight direction of the target at last contact.
        /// Used to bias search forward along escape route.
        /// </summary>
        public Vector3 FlightVector;

        /// <summary>Maximum search radius in world units.</summary>
        public float SearchRadius;

        /// <summary>Preferred number of search waypoints to generate.</summary>
        public int SearchPointCount;

        /// <summary>Vertical sampling range for NavMesh queries.</summary>
        public float HeightRange;

        /// <summary>Layer mask for obstacle detection.</summary>
        public LayerMask ObstacleMask;

        /// <summary>
        /// Cells already visited this search session.
        /// Strategies should avoid generating points in these cells.
        /// </summary>
        public HashSet<int> VisitedCells;

        /// <summary>Cell size in meters for visited cell hashing.</summary>
        public float CellSize;

        /// <summary>World position of the searching unit.</summary>
        public Vector3 UnitPosition;

        /// <summary>
        /// Historical predicted flight direction from HuntDirector.FlightPatternMemory.
        /// Vector3.zero if no pattern has been established yet.
        /// </summary>
        public Vector3 PredictedFlightDir;

        /// <summary>
        /// Snapshot of known player hide spots from HuntDirector.HideSpotMemory.
        /// Null-safe -- always check for null before iterating.
        /// </summary>
        public List<HuntDirector.HideSpotRecord> KnownHideSpots;

        /// <summary>
        /// PatrolRegion the last known position falls within, if any.
        /// Null if position is outside all regions.
        /// </summary>
        public PatrolRegion LastKnownRegion;
    }

    // ---------- Interface -----------------------------------------------------

    /// <summary>
    /// Strategy pattern for AI search behaviour.
    /// Implement this to create custom search algorithms.
    ///
    /// Built-in implementations:
    ///   ReachabilitySearch  -- NavMesh reachability based (default, recommended)
    ///   SpiralSearch        -- simple geometric spiral (fast fallback)
    ///
    /// Usage:
    ///   Assign a MonoBehaviour implementing ISearchStrategy to the
    ///   "Search Strategy Override" field on StealthHuntAI.
    ///   Leave empty to use ReachabilitySearch automatically.
    /// </summary>
    public interface ISearchStrategy
    {
        /// <summary>
        /// Called once at the start of a search pass.
        /// Strategy should generate its internal waypoint list here.
        /// Returns immediately -- use IsReady to poll for completion
        /// if generation is async (coroutine-based).
        /// </summary>
        void Initialize(SearchContext context);

        /// <summary>True when Initialize has finished and points are ready.</summary>
        bool IsReady { get; }

        /// <summary>True when all generated points have been visited.</summary>
        bool IsExhausted { get; }

        /// <summary>
        /// Returns the next waypoint, or null if none available yet.
        /// Called by StealthHuntAI each time a new destination is needed.
        /// </summary>
        Vector3? GetNextPoint();

        /// <summary>
        /// Called by StealthHuntAI when unit arrives at a waypoint.
        /// Strategy can use this to mark points as visited or replan.
        /// </summary>
        void OnPointReached(Vector3 point);

        /// <summary>Reset all internal state for reuse in next search pass.</summary>
        void Reset();
    }
}