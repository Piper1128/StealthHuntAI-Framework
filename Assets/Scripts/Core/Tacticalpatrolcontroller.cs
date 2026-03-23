using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    /// <summary>
    /// Selects tactically interesting patrol destinations using PatrolPoints
    /// and the HuntDirector heatmap.
    ///
    /// Activated automatically when:
    ///   1. Guard has no patrolPoints assigned
    ///   2. PatrolPoints exist in the scene
    ///
    /// Guards spread naturally -- recently visited points have low scores
    /// so guards gravitate toward uncovered areas.
    /// </summary>
    public class TacticalPatrolController
    {
        private PatrolPoint _currentPoint;
        private PatrolPoint _targetPoint;
        private float _lookAroundTimer;
        private bool _lookingAround;
        private float _lookAngle;

        // ---------- Public API -----------------------------------------------

        public bool IsActive => _targetPoint != null;
        public bool IsLookingAround => _lookingAround;

        /// <summary>
        /// Tick -- call every frame from TickPatrolling when no patrolPoints set.
        /// Returns true when still active, false if no PatrolPoints in scene.
        /// </summary>
        public bool Tick(StealthHuntAI unit)
        {
            if (PatrolRegistry.Count == 0) return false;

            if (_lookingAround)
            {
                TickLookAround(unit);
                return true;
            }

            if (_targetPoint == null)
                PickNextPoint(unit);

            if (_targetPoint == null) return false;

            // Move toward target
            float dist = Vector3.Distance(
                unit.transform.position, _targetPoint.transform.position);

            if (dist > 0.8f)
            {
                unit.PatrolMoveTo(_targetPoint.transform.position);
                return true;
            }

            // Arrived -- mark visited and look around
            _targetPoint.MarkVisited(unit.squadID);
            _currentPoint = _targetPoint;
            _targetPoint = null;

            if (_currentPoint.lookAroundTime > 0f)
            {
                unit.PatrolStop();
                _lookingAround = true;
                _lookAroundTimer = 0f;
                _lookAngle = unit.transform.eulerAngles.y;
            }

            return true;
        }

        private void TickLookAround(StealthHuntAI unit)
        {
            _lookAroundTimer += Time.deltaTime;

            float duration = _currentPoint?.lookAroundTime ?? 1.5f;

            // Sweep left and right while waiting
            float t = _lookAroundTimer / duration;
            float sweep = Mathf.Sin(t * Mathf.PI * 2f) * 60f;
            float targetY = _lookAngle + sweep;

            unit.transform.rotation = Quaternion.RotateTowards(
                unit.transform.rotation,
                Quaternion.Euler(0f, targetY, 0f),
                80f * Time.deltaTime);

            if (_lookAroundTimer >= duration)
            {
                _lookingAround = false;
                PickNextPoint(unit);
            }
        }

        private void PickNextPoint(StealthHuntAI unit)
        {
            _targetPoint = PatrolRegistry.FindBest(
                unit.transform.position,
                unit.squadID,
                _currentPoint);

            if (_targetPoint != null)
                unit.PatrolMoveTo(_targetPoint.transform.position);
        }

        /// <summary>Reset when guard is alerted or re-enters patrol state.</summary>
        public void Reset()
        {
            _targetPoint = null;
            _lookingAround = false;
            _lookAroundTimer = 0f;
        }
    }
}