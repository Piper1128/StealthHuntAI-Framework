using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Static registry of all active lights in the scene.
    /// Eliminates per-frame FindObjectsByType calls in AwarenessSensor.
    /// Lights self-register via OnEnable/OnDisable -- no manual setup needed.
    ///
    /// Usage: LightRegistry.All gives a read-only list of all active lights.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Light Registry")]
    [DisallowMultipleComponent]
    public class LightRegistry : MonoBehaviour
    {
        private static readonly List<Light> _lights = new List<Light>();
        public static IReadOnlyList<Light> All => _lights;

        private Light _light;

        private void Awake()
        {
            _light = GetComponent<Light>();
        }

        private void OnEnable()
        {
            if (_light != null && !_lights.Contains(_light))
                _lights.Add(_light);
        }

        private void OnDisable()
        {
            if (_light != null)
                _lights.Remove(_light);
        }

        // ---------- Scene-wide auto-registration ------------------------------

        /// <summary>
        /// Automatically adds LightRegistry to all Light components in the scene.
        /// Called by StealthHuntAI AutoConfigure -- no manual setup needed.
        /// </summary>
        public static void AutoRegisterSceneLights()
        {
            var sceneLights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var light in sceneLights)
            {
                if (light.GetComponent<LightRegistry>() == null)
                    light.gameObject.AddComponent<LightRegistry>();
            }
        }

        private void OnDestroy()
        {
            if (_light != null)
                _lights.Remove(_light);
        }
    }
}