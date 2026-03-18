using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// ScriptableObject preset for StealthHuntAI configuration.
    /// Drag a preset onto StealthHuntAI to apply all settings at once.
    ///
    /// Create presets via:
    /// Assets  Create  StealthHuntAI  AI Preset
    /// </summary>
    [CreateAssetMenu(
        fileName = "New StealthAI Preset",
        menuName = "StealthHuntAI/AI Preset",
        order = 1)]
    public class StealthAIPreset : ScriptableObject
    {
        [Header("Detection")]
        [Range(1f, 60f)] public float sightRange = 15f;
        [Range(10f, 360f)] public float sightAngle = 90f;
        [Range(1f, 40f)] public float hearingRange = 10f;
        [Range(0.1f, 10f)] public float sightDetectionSpeed = 1.5f;
        [Range(0.05f, 3f)] public float sightDecaySpeed = 0.3f;
        [Range(0f, 1f)] public float reactionTime = 0.15f;

        [Header("Light Sensitivity")]
        public bool useLightDetection = true;
        [Range(0f, 2f)] public float darknessThreshold = 0.3f;
        [Range(0.01f, 1f)] public float darknessFactor = 0.2f;

        [Header("Awareness Thresholds")]
        [Range(0f, 1f)] public float suspicionThreshold = 0.30f;
        [Range(0f, 1f)] public float hostileThreshold = 0.70f;

        [Header("Movement")]
        [Range(0.1f, 1f)] public float patrolSpeedMultiplier = 0.55f;
        [Range(0.5f, 2f)] public float chaseSpeedMultiplier = 1.0f;

        [Header("Search")]
        [Range(5f, 60f)] public float searchDuration = 15f;
        [Range(3f, 30f)] public float searchRadius = 8f;

        [Header("Sound")]
        [Range(0.1f, 1f)] public float soundSuspicionThreshold = 0.4f;
        [Range(0.1f, 1f)] public float soundHostileThreshold = 0.8f;

        [Header("Morale")]
        [Range(0f, 1f)] public float startingMorale = 1f;

        // ---------- Built-in presets ------------------------------------------

        /// <summary>Apply this preset to a StealthHuntAI component.</summary>
        public void ApplyTo(StealthHuntAI ai)
        {
            ai.sightDetectionSpeed = sightDetectionSpeed;
            ai.sightDecaySpeed = sightDecaySpeed;
            ai.suspicionThreshold = suspicionThreshold;
            ai.hostileThreshold = hostileThreshold;
            ai.patrolSpeedMultiplier = patrolSpeedMultiplier;
            ai.chaseSpeedMultiplier = chaseSpeedMultiplier;
            ai.searchDuration = searchDuration;
            ai.searchRadius = searchRadius;
            ai.startingMorale = startingMorale;

            var sensor = ai.GetComponent<AwarenessSensor>();
            if (sensor == null) return;

            sensor.sightRange = sightRange;
            sensor.sightAngle = sightAngle;
            sensor.hearingRange = hearingRange;
            sensor.sightRiseSpeed = sightDetectionSpeed;
            sensor.sightDecaySpeed = sightDecaySpeed;
            sensor.reactionTime = reactionTime;
            sensor.useLightDetection = useLightDetection;
            sensor.darknessThreshold = darknessThreshold;
            sensor.darknessFactor = darknessFactor;
            sensor.soundSuspicionThreshold = soundSuspicionThreshold;
            sensor.soundHostileThreshold = soundHostileThreshold;
        }
    }
}