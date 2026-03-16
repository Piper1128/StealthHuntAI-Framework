using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    public partial class StealthHuntAI
    {
        // ---------- Passive sub-states ----------------------------------------

        private void TickIdle()
        {
            StopMoving();
            ApplySpeedToMovement(_baseAgentSpeed * patrolSpeedMultiplier);

            switch (behaviourMode)
            {
                case BehaviourMode.Patrol:
                    if (patrolPoints != null && patrolPoints.Length > 0)
                        TransitionSubState(SubState.Patrolling);
                    break;

                case BehaviourMode.GuardZone:
                    if (guardZoneWaypointCount > 0 && _guardZonePoints.Count > 0)
                        TransitionSubState(SubState.GuardZonePatrol);
                    break;

                default:
                    break;
            }
        }

        // ---------- Patrol ----------------------------------------------------

        private void TickPatrolling()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;

            if (_waitingAtWaypoint)
            {
                _waypointWaitTimer += Time.deltaTime;
                if (_waypointWaitTimer >= waypointWaitTime)
                {
                    _waitingAtWaypoint = false;
                    _waypointWaitTimer = 0f;
                    AdvancePatrolIndex();
                    MoveTo(patrolPoints[_patrolIndex].position);
                }
                return;
            }

            if (!_movement.HasPath || _movement.RemainingDistance < 0.5f)
            {
                // Apply pending nudge when waypoint is reached -- never mid-patrol
                if (ConsumePendingNudge()) return;

                if (waypointWaitTime > 0f)
                {
                    _waitingAtWaypoint = true;
                    StopMoving();
                }
                else
                {
                    AdvancePatrolIndex();
                    MoveTo(patrolPoints[_patrolIndex].position);
                }
            }
        }

        private void AdvancePatrolIndex()
        {
            if (patrolPattern == PatrolPattern.Loop)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                return;
            }

            // PingPong
            if (_pingPongForward)
            {
                _patrolIndex++;
                if (_patrolIndex >= patrolPoints.Length - 1)
                {
                    _patrolIndex = patrolPoints.Length - 1;
                    _pingPongForward = false;
                }
            }
            else
            {
                _patrolIndex--;
                if (_patrolIndex <= 0)
                {
                    _patrolIndex = 0;
                    _pingPongForward = true;
                }
            }
        }

        // ---------- Guard Zone ------------------------------------------------

        private void GenerateGuardZonePoints()
        {
            _guardZonePoints.Clear();
            _guardZoneIndex = 0;

            Vector3 center = guardZoneCenter != null
                ? guardZoneCenter.position
                : _spawnPosition;

            float angleStep = 360f / guardZoneWaypointCount;
            float angle = Random.Range(0f, 360f);
            float hRange = searchHeightRange > 0f ? searchHeightRange : -1f;

            for (int i = 0; i < guardZoneWaypointCount; i++)
            {
                float dist = guardZoneRadius * Random.Range(0.35f, 0.85f);

                if (NavMeshHelper.SampleOffset(center, angle, dist,
                                                guardZoneRadius, out Vector3 pt,
                                                hRange))
                    _guardZonePoints.Add(pt);

                angle += angleStep + Random.Range(-20f, 20f);
            }
        }

        private void TickGuardZonePatrol()
        {
            if (_guardZonePoints.Count == 0)
            {
                GenerateGuardZonePoints();
                return;
            }

            if (_guardZoneWaiting)
            {
                _guardZoneWaitTimer += Time.deltaTime;
                if (_guardZoneWaitTimer >= guardZoneWaitTime)
                {
                    _guardZoneWaiting = false;
                    _guardZoneWaitTimer = 0f;
                    _guardZoneIndex = (_guardZoneIndex + 1) % _guardZonePoints.Count;
                    MoveTo(_guardZonePoints[_guardZoneIndex]);
                }
                return;
            }

            if (!_movement.HasPath || _movement.RemainingDistance < 0.6f)
            {
                if (guardZoneWaitTime > 0f)
                {
                    _guardZoneWaiting = true;
                    StopMoving();
                }
                else
                {
                    _guardZoneIndex = (_guardZoneIndex + 1) % _guardZonePoints.Count;
                    MoveTo(_guardZonePoints[_guardZoneIndex]);
                }
            }
        }

        // ---------- Returning -------------------------------------------------

        private void TickReturning()
        {
            ApplySpeedToMovement(_baseAgentSpeed * patrolSpeedMultiplier);
            MoveTo(GetHomePosition());

            if (_movement.RemainingDistance < 0.8f)
            {
                switch (behaviourMode)
                {
                    case BehaviourMode.Patrol:
                        TransitionSubState(SubState.Patrolling);
                        break;
                    case BehaviourMode.GuardZone:
                        TransitionSubState(guardZoneWaypointCount > 0
                            ? SubState.GuardZonePatrol
                            : SubState.Idle);
                        break;
                    default:
                        TransitionSubState(SubState.Idle);
                        break;
                }
            }
        }

        private Vector3 GetHomePosition()
        {
            if (behaviourMode == BehaviourMode.GuardZone)
                return guardZoneCenter != null ? guardZoneCenter.position : _spawnPosition;

            if (behaviourMode == BehaviourMode.Patrol &&
                patrolPoints != null && patrolPoints.Length > 0)
                return patrolPoints[_patrolIndex].position;

            return _spawnPosition;
        }

    }
}