using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    public class StealthHuntAI_Debugger : MonoBehaviour
    {
        [Header("What to log")]
        public bool logSight = true;
        public bool logMovement = true;
        public bool logLayers = true;

        [Header("Log interval (seconds)")]
        public float logInterval = 0.5f;

        private StealthHuntAI _ai;
        private AwarenessSensor _sensor;
        private NavMeshAgent _agent;
        private float _timer;
        private bool _layersLogged;

        // Use Start -- AwarenessSensor is added by StealthHuntAI.Awake
        // so it does not exist yet when Debugger.Awake runs
        private void Start()
        {
            _ai = GetComponent<StealthHuntAI>();
            _sensor = GetComponent<AwarenessSensor>();
            _agent = GetComponent<NavMeshAgent>();

            if (_sensor == null)
                Debug.LogWarning("[" + name + "] Debugger: AwarenessSensor still null in Start. Is StealthHuntAI on this object?");
        }

        private void Update()
        {
            // Retry sensor lookup in case it was added late
            if (_sensor == null)
                _sensor = GetComponent<AwarenessSensor>();

            _timer += Time.deltaTime;

            if (logLayers && !_layersLogged && _sensor != null)
            {
                _layersLogged = true;
                LogLayers();
            }

            if (_timer < logInterval) return;
            _timer = 0f;

            if (logSight) LogSight();
            if (logMovement) LogMovement();
        }

        private void LogLayers()
        {
            int blockers = (int)_sensor.sightBlockers;
            string layerNames = "";
            for (int i = 0; i < 32; i++)
            {
                if ((blockers & (1 << i)) == 0) continue;
                string ln = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(ln))
                    layerNames += ln + "(" + i + ") ";
            }

            var target = FindFirstObjectByType<StealthTarget>();
            int pLayer = target != null ? target.gameObject.layer : -1;
            string pLayerName = pLayer >= 0 ? LayerMask.LayerToName(pLayer) : "NOT FOUND";
            bool pBlocked = pLayer >= 0 && (blockers & (1 << pLayer)) != 0;

            Debug.Log("[" + name + "] LAYERS"
                + " | Blockers: " + layerNames.Trim()
                + " | PlayerLayer: " + pLayerName + "(" + pLayer + ")"
                + " | PlayerInBlockers: " + pBlocked + " (should be FALSE)");
        }

        private void LogSight()
        {
            if (_sensor == null) return;

            var target = FindFirstObjectByType<StealthTarget>();
            if (target == null)
            {
                Debug.Log("[" + name + "] SIGHT -- no StealthTarget in scene");
                return;
            }

            var anim = GetComponentInChildren<Animator>();
            Transform headBone = anim != null
                ? anim.GetBoneTransform(HumanBodyBones.Head)
                : null;
            Vector3 origin = headBone != null
                ? headBone.position
                : transform.position + Vector3.up * 1.6f;

            Vector3 toTarget = target.PerceptionOrigin - origin;
            float dist = toTarget.magnitude;
            float angle = Vector3.Angle(transform.forward, toTarget);
            bool inCone = dist <= _sensor.sightRange
                       && angle <= _sensor.sightAngle * 0.5f;

            string rawResult = "CLEAR";
            RaycastHit hit;
            if (Physics.Raycast(origin, toTarget.normalized, out hit, dist))
            {
                rawResult = "HIT " + hit.collider.name
                    + " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer);
            }

            string maskedResult = "CLEAR";
            if (Physics.Raycast(origin, toTarget.normalized, out hit,
                                 dist, _sensor.sightBlockers))
            {
                maskedResult = "BLOCKED by " + hit.collider.name
                    + " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer);
            }

            // Also cast from actual sensor origin to catch differences
            string sensorResult = "CLEAR";
            if (_sensor.SightOrigin != null)
            {
                Vector3 sensorOrigin = _sensor.SightOrigin.position;
                Vector3 sensorToTarget = target.PerceptionOrigin - sensorOrigin;
                if (Physics.Raycast(sensorOrigin, sensorToTarget.normalized, out hit,
                                    sensorToTarget.magnitude, _sensor.sightBlockers))
                {
                    sensorResult = "BLOCKED by " + hit.collider.name
                        + " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer);
                }
            }

            bool originNull = _sensor.SightOrigin == null;
            bool hasTarget = _sensor.HasTarget;
            bool targetActive = hasTarget && _sensor.TargetIsActive;
            string originInfo = originNull
                ? "NULL (CheckSight will skip!)"
                : _sensor.SightOrigin.name + " @ " + _sensor.SightOrigin.position.ToString("F1");

            Debug.Log("[" + name + "] SIGHT"
                + " | Aware: " + _sensor.AwarenessLevel.ToString("F2")
                + " | CanSee: " + _sensor.CanSeeTarget
                + " | Acc: " + _sensor.SightAccumulator.ToString("F3")
                + " | Detected: " + _sensor.SightDetected
                + " | IsPassive: " + _sensor._isPassive
                + " | AccMult: " + _sensor.sightAccumulatorMultiplier.ToString("F1")
                + " | Conf: " + _sensor.StimulusConfidence.ToString("F2")
                + " | InCone: " + inCone
                + " | Dist: " + dist.ToString("F1") + "/" + _sensor.sightRange
                + " | Angle: " + angle.ToString("F0") + "/" + (_sensor.sightAngle * 0.5f).ToString("F0")
                + " | Raw: " + rawResult
                + " | Masked: " + maskedResult
                + " | Origin: " + originInfo
                + " | HasTarget: " + hasTarget
                + " | TargetActive: " + targetActive
                + " | SensorRay: " + sensorResult);
        }

        private void LogMovement()
        {
            if (_agent == null || _ai == null) return;

            string dest = _agent.hasPath
                ? _agent.destination.ToString("F0")
                  + " rem=" + _agent.remainingDistance.ToString("F1") + "m"
                : "NO PATH";

            var target = FindFirstObjectByType<StealthTarget>();
            string targetDist = target != null
                ? Vector3.Distance(transform.position, target.Position).ToString("F1") + "m from player"
                : "";

            string destMatchesPlayer = "?";
            if (target != null && _agent.hasPath)
            {
                float distDestToPlayer = Vector3.Distance(_agent.destination, target.Position);
                destMatchesPlayer = distDestToPlayer < 3f ? "DEST=PLAYER POS!" : "ok";
            }

            Debug.Log("[" + name + "] MOVEMENT"
                + " | Sub: " + _ai.CurrentSubState
                + " | Dest: " + dest
                + " | Speed: " + _agent.speed.ToString("F1")
                + " | " + targetDist
                + " | Check: " + destMatchesPlayer);
        }
    }
}