using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Place this on the player (or any entity that should be hunted).
    /// No configuration required -- registers itself with HuntDirector automatically.
    /// Tracks velocity and flight vector automatically for AI search reasoning.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Stealth Target")]
    [DisallowMultipleComponent]
    public class StealthTarget : MonoBehaviour
    {
        // ---------- Optional overrides ----------------------------------------

        [Tooltip("How visible is this target? 1 = fully visible, 0 = invisible.")]
        [Range(0f, 1f)]
        public float visibilityMultiplier = 1f;

        [Tooltip("How much noise is this target making? 1 = full noise, 0 = silent.")]
        [Range(0f, 1f)]
        public float noiseMultiplier = 1f;

        [Tooltip("Override the perception origin point (e.g. eye/chest level). " +
                 "Leave empty to use this transform.")]
        public Transform perceptionOriginOverride;

        // ---------- Runtime properties ----------------------------------------

        /// <summary>World position used for perception checks.</summary>
        public Vector3 PerceptionOrigin =>
            perceptionOriginOverride != null
                ? perceptionOriginOverride.position
                : transform.position + Vector3.up * _heightOffset;

        /// <summary>Current world position of the target.</summary>
        public Vector3 Position => transform.position;

        /// <summary>Current velocity this frame.</summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// Smoothed movement direction over the last few frames.
        /// Used by AI to predict where the target fled to.
        /// Zero if the target has been standing still.
        /// </summary>
        public Vector3 FlightVector { get; private set; }

        /// <summary>
        /// Speed of the target this frame.
        /// </summary>
        public float Speed { get; private set; }

        /// <summary>Is this target currently active and huntable?</summary>
        public bool IsActive { get; private set; } = true;

        /// <summary>
        /// Current floor zone ID. Set automatically by FloorZone trigger volumes.
        /// -1 means no zone registered (transitioning between floors or no zones in scene).
        /// </summary>
        public int CurrentFloorID { get; set; } = -1;

        // ---------- Internal --------------------------------------------------

        private float _heightOffset = 1.4f;
        private Vector3 _lastPosition;
        private Vector3 _smoothedVelocity;

        private const float VelocitySmoothTime = 0.15f;
        private const float FlightVectorDecay = 0.95f;
        private const float MinSpeedThreshold = 0.3f;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            AutoDetectHeightOffset();
            _lastPosition = transform.position;
            HuntDirector.RegisterTarget(this);
        }

        private void OnDestroy()
        {
            HuntDirector.UnregisterTarget(this);
        }

        private void OnEnable() => IsActive = true;
        private void OnDisable() => IsActive = false;

        private void Update()
        {
            TrackVelocity();
        }

        // ---------- Velocity tracking -----------------------------------------

        private void TrackVelocity()
        {
            Vector3 rawVelocity = (transform.position - _lastPosition) / Time.deltaTime;
            rawVelocity.y = 0f; // horizontal movement only for flight vector

            // Smooth velocity to avoid jitter
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, rawVelocity,
                                              Time.deltaTime / VelocitySmoothTime);

            Velocity = _smoothedVelocity;
            Speed = _smoothedVelocity.magnitude;

            // Update flight vector when moving, decay slowly when stopped
            if (Speed > MinSpeedThreshold)
            {
                FlightVector = _smoothedVelocity.normalized;
            }
            else
            {
                // Keep last known direction but decay magnitude hint
                FlightVector = Vector3.Lerp(FlightVector, Vector3.zero,
                                             Time.deltaTime * (1f - FlightVectorDecay));
            }

            _lastPosition = transform.position;
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>Temporarily make this target undetectable (e.g. cutscene).</summary>
        public void SetActive(bool active) => IsActive = active;

        // ---------- Internal helpers ------------------------------------------

        private void AutoDetectHeightOffset()
        {
            if (TryGetComponent<CharacterController>(out var cc))
            {
                _heightOffset = cc.height * 0.85f;
                return;
            }
            if (TryGetComponent<CapsuleCollider>(out var cap))
            {
                _heightOffset = cap.height * 0.85f;
                return;
            }
            _heightOffset = 1.4f;
        }

        // ---------- Gizmos ----------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0.4f, 0.8f);
            Gizmos.DrawWireSphere(PerceptionOrigin, 0.15f);
            Gizmos.DrawLine(transform.position, PerceptionOrigin);

            if (Application.isPlaying && FlightVector.magnitude > 0.1f)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.8f);
                Gizmos.DrawRay(transform.position, FlightVector * 3f);
            }
        }
    }
}