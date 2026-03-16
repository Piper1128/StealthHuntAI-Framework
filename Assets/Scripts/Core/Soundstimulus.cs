using UnityEngine;

namespace StealthHuntAI
{
    // ---------- Sound type presets --------------------------------------------

    public enum SoundType
    {
        Custom,         // use manual intensity and radius
        Footstep,       // soft, short range
        FootstepHard,   // loud surface, medium range
        Crouch,         // near-silent movement
        Jump,           // landing impact
        Door,           // door open/close
        DoorForced,     // kicked or broken door
        ObjectDropped,  // item knocked over
        GlassBreak,     // loud, long range
        Gunshot,        // very loud, very long range
        GunshotSilenced,// suppressed -- short range
        Explosion,      // maximum range
        Alarm,          // persistent, long range
        VoiceWhisper,   // barely audible
        VoiceShout,     // long range
        Ambient         // background noise, barely registers
    }

    // ---------- Static emission API ------------------------------------------

    /// <summary>
    /// Static utility for emitting sound stimuli to all nearby StealthHuntAI units.
    /// Call from anywhere -- no setup or references required.
    ///
    /// Usage:
    ///   SoundStimulus.Emit(transform.position, SoundType.Gunshot);
    ///   SoundStimulus.Emit(transform.position, SoundType.Custom, intensity: 0.6f, radius: 12f);
    /// </summary>
    public static class SoundStimulus
    {
        /// <summary>
        /// Emit a sound at a world position using a preset SoundType.
        /// All StealthHuntAI units within the sound's radius will respond.
        /// </summary>
        public static void Emit(Vector3 position, SoundType type)
        {
            GetPreset(type, out float intensity, out float radius);
            EmitRaw(position, intensity, radius);
        }

        /// <summary>
        /// Emit a sound at a world position with fully manual parameters.
        /// </summary>
        /// <param name="intensity">0-1. How loud the sound is at the source.</param>
        /// <param name="radius">World units. Max distance any unit can hear this sound.</param>
        public static void Emit(Vector3 position, float intensity, float radius)
        {
            EmitRaw(position, intensity, radius);
        }

        /// <summary>
        /// Emit a sound using a preset type with optional intensity and radius overrides.
        /// Pass -1 to keep the preset value for either parameter.
        /// </summary>
        public static void Emit(Vector3 position, SoundType type,
                                  float intensityOverride = -1f,
                                  float radiusOverride = -1f)
        {
            GetPreset(type, out float intensity, out float radius);

            if (intensityOverride >= 0f) intensity = intensityOverride;
            if (radiusOverride >= 0f) radius = radiusOverride;

            EmitRaw(position, intensity, radius);
        }

        // ---------- Internal --------------------------------------------------

        private static void EmitRaw(Vector3 position, float intensity, float radius)
        {
            if (intensity <= 0f || radius <= 0f) return;

            HuntDirector.BroadcastSound(position, intensity, radius);
        }

        /// <summary>Returns default intensity and radius for a given SoundType.</summary>
        public static void GetPreset(SoundType type,
                                      out float intensity, out float radius)
        {
            switch (type)
            {
                case SoundType.Footstep: intensity = 0.25f; radius = 6f; break;
                case SoundType.FootstepHard: intensity = 0.45f; radius = 10f; break;
                case SoundType.Crouch: intensity = 0.08f; radius = 3f; break;
                case SoundType.Jump: intensity = 0.50f; radius = 10f; break;
                case SoundType.Door: intensity = 0.55f; radius = 12f; break;
                case SoundType.DoorForced: intensity = 0.80f; radius = 20f; break;
                case SoundType.ObjectDropped: intensity = 0.40f; radius = 8f; break;
                case SoundType.GlassBreak: intensity = 0.75f; radius = 18f; break;
                case SoundType.Gunshot: intensity = 1.00f; radius = 50f; break;
                case SoundType.GunshotSilenced: intensity = 0.35f; radius = 8f; break;
                case SoundType.Explosion: intensity = 1.00f; radius = 80f; break;
                case SoundType.Alarm: intensity = 0.90f; radius = 40f; break;
                case SoundType.VoiceWhisper: intensity = 0.10f; radius = 4f; break;
                case SoundType.VoiceShout: intensity = 0.70f; radius = 22f; break;
                case SoundType.Ambient: intensity = 0.05f; radius = 5f; break;
                default: intensity = 0.50f; radius = 10f; break;
            }
        }
    }

    // ---------- SoundEmitter component ----------------------------------------

    /// <summary>
    /// Place on any GameObject to make it emit sounds to nearby StealthHuntAI units.
    /// Trigger via script, UnityEvent, or Animation Events.
    ///
    /// Examples: doors, alarms, props, footstep systems, gunfire.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Sound Emitter")]
    public class SoundEmitter : MonoBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Sound Type")]
        [Tooltip("Preset defines default intensity and radius. " +
                 "Use Custom to set values manually.")]
        public SoundType soundType = SoundType.Door;

        [Header("Override (ignored if SoundType is not Custom)")]
        [Tooltip("Override intensity. Set to -1 to use preset value.")]
        [Range(-1f, 1f)] public float intensityOverride = -1f;

        [Tooltip("Override radius in world units. Set to -1 to use preset value.")]
        [Range(-1f, 100f)] public float radiusOverride = -1f;

        [Header("Falloff")]
        [Tooltip("How intensity falls off with distance. " +
                 "Left = source (full intensity), Right = max radius (zero).")]
        public AnimationCurve falloffCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);

        [Header("Auto Emit")]
        [Tooltip("Emit a sound automatically when this GameObject is enabled.")]
        public bool emitOnEnable = false;

        [Tooltip("Emit sounds on a repeating interval. 0 = disabled.")]
        [Range(0f, 60f)] public float repeatInterval = 0f;

        // ---------- Runtime ---------------------------------------------------

        private float _repeatTimer;

        // ---------- Unity lifecycle -------------------------------------------

        private void OnEnable()
        {
            if (emitOnEnable)
                Emit();
        }

        private void Update()
        {
            if (repeatInterval <= 0f) return;

            _repeatTimer += Time.deltaTime;
            if (_repeatTimer >= repeatInterval)
            {
                _repeatTimer = 0f;
                Emit();
            }
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>
        /// Emit sound from this object's current position.
        /// Call from script, UnityEvent, or buttons.
        /// </summary>
        public void Emit()
        {
            EmitAt(transform.position);
        }

        /// <summary>
        /// Emit sound from a specific world position.
        /// </summary>
        public void EmitAt(Vector3 position)
        {
            SoundStimulus.GetPreset(soundType, out float intensity, out float radius);

            if (intensityOverride >= 0f) intensity = intensityOverride;
            if (radiusOverride >= 0f) radius = radiusOverride;

            // Apply falloff curve to each receiving unit via BroadcastSoundWithCurve
            HuntDirector.BroadcastSoundWithCurve(position, intensity, radius, falloffCurve);
        }

        /// <summary>
        /// Call this from an Animation Event to emit a sound mid-animation.
        /// The parameter is optional -- leave empty to use inspector settings.
        /// </summary>
        public void EmitOnAnimationEvent()
        {
            Emit();
        }

        /// <summary>
        /// Call from Animation Event with a SoundType name override.
        /// Parameter must match a SoundType enum name exactly (e.g. "Footstep").
        /// </summary>
        public void EmitOnAnimationEventWithType(string soundTypeName)
        {
            if (System.Enum.TryParse(soundTypeName, out SoundType parsed))
            {
                SoundType original = soundType;
                soundType = parsed;
                Emit();
                soundType = original;
            }
            else
            {
                Debug.LogWarning("[SoundEmitter] Unknown SoundType: " + soundTypeName +
                                  ". Valid values: " +
                                  string.Join(", ", System.Enum.GetNames(typeof(SoundType))));
                Emit();
            }
        }

        // ---------- Gizmos ----------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            SoundStimulus.GetPreset(soundType, out float intensity, out float radius);
            if (radiusOverride >= 0f) radius = radiusOverride;
            if (intensityOverride >= 0f) intensity = intensityOverride;

            Gizmos.color = new Color(1f, 0.7f, 0f, 0.12f);
            Gizmos.DrawWireSphere(transform.position, radius);
            Gizmos.color = new Color(1f, 0.7f, 0f, 0.04f);
            Gizmos.DrawSphere(transform.position, radius);

            // Inner ring at half radius for intensity reference
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.18f);
            Gizmos.DrawWireSphere(transform.position, radius * 0.5f);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * (radius * 0.1f + 0.5f),
                soundType + "  i:" + intensity.ToString("F2") +
                "  r:" + radius.ToString("F0") + "m");
#endif
        }
    }
}