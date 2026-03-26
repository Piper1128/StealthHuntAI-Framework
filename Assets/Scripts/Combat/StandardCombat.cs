using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using StealthHuntAI.Combat.CQB;

namespace StealthHuntAI.Combat
{
    public class StandardCombat : MonoBehaviour, ICombatBehaviour
    {
        [Range(0.1f, 4f)] public float RoleInterval = 3f;
        [Range(0.5f, 2f)] public float SpeedMultiplier = 1.4f;

        public bool WantsControl { get; private set; }
        public string CurrentStateName => _s.Role.ToString();
        public string CurrentPlanName => _s.Role + " t=" + _s.Timer.ToString("F1") + "s";
        public string CurrentStrategy => _brain?.Strategy.ToString() ?? "None";

        public enum Goal { Idle, AdvanceTo, Flank, Suppress, HoldAndFire, Search, Withdraw }
        public Goal CurrentGoal { get; private set; }
        public bool IsInCover => _s.AtCover;

        public enum CombatRole
        {
            Advance, Flank, Suppress, Cover, Cautious,
            Reposition, Search, Overwatch, RearSecurity,
            Breach, Follow, Withdraw, CQB, Idle
        }

        private StealthHuntAI _ai;
        private TacticalBrain _brain;
        private CombatEventBus _events;
        private ThreatModel _threat => _brain?.Intel?.Threat;
        private readonly CombatAgentState _s = new CombatAgentState();
        private static bool _tacticianRunnerChecked;
        private static bool _tacticianRunnerPresent;
        private IShootable _shootable;
        private NavMeshAgent _agent;

        // ---------- ICombatBehaviour -----------------------------------------

        public void OnEnterCombat(StealthHuntAI ai)
        {
            WantsControl = true;
            _ai = ai;
            if (_shootable == null) _shootable = ai.GetComponent<IShootable>();
            if (_agent == null) _agent = ai.GetComponent<NavMeshAgent>();

            // Fast re-entry after YieldToCore
            if (_brain != null) { _brain.RegisterMember(ai); return; }

            _brain = TacticalBrain.GetOrCreate(ai.squadID);
            _brain.RegisterMember(ai);
            _events = CombatEventBus.Get(ai);

            var sharedThreat = _brain.Intel.Threat;
            var board = SquadBlackboard.Get(ai.squadID);
            if (board != null && board.SharedConfidence > 0.05f)
                sharedThreat.ReceiveIntel(board.SharedLastKnown, Vector3.zero,
                                          board.SharedConfidence);
            var target = ai.GetTarget();
            if (target != null && ai.Sensor != null && ai.Sensor.CanSeeTarget)
                sharedThreat.UpdateWithSight(target.Position, target.Velocity);
            if (sharedThreat.EstimatedPosition == Vector3.zero)
                sharedThreat.ReceiveIntel(
                    ai.transform.position + ai.transform.forward * 10f,
                    Vector3.zero, 0.1f);

            // Force fresh start on combat entry
            _s.ResetMovement();
            _s.LastScenario = (SquadTactician.TacticianScenario)(-1);
            SetRole(CombatRole.Search);
        }

        public void OnExitCombat(StealthHuntAI ai)
        {
            WantsControl = false;
            _brain?.UnregisterMember(ai);
            ReleaseCoverSpot();
            ai.CombatRestoreRotation();
        }

        public void OnNearShot(Vector3 shotOrigin)
        {
            if (_threat == null) return; // not yet in combat
            _threat.RegisterShotFrom(shotOrigin,
                (shotOrigin - _ai.transform.position).normalized);
            bool isMoving = _s.Role == CombatRole.Advance
                         || _s.Role == CombatRole.Flank
                         || _s.Role == CombatRole.Cautious
                         || _s.Role == CombatRole.Reposition;
            if (!isMoving)
                ForceRole(_threat.HasLOS ? CombatRole.Cover : CombatRole.Reposition);
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            UpdateThreat();
            ProcessEvents();
            CheckStuck();

            // Self-driving: only when TacticianRunner is NOT in scene
            if (_brain != null && IsSquadLeader()
             && !HasTacticianRunner())
            {
                _brain.UpdateSquadAnchor(HuntDirector.AllUnits, _ai.squadID);
                _brain.Tactician.Tick(Time.deltaTime, _brain,
                    HuntDirector.AllUnits, _ai.squadID);
                _brain.TickCommittedGoal();
                _brain.CQB.Tick(Time.deltaTime);
                var ws = WorldState.Build(_ai, _brain.Intel.Threat, _brain);
                _brain.Strategy.Update(Time.deltaTime, ws, _brain);
            }

            TickCombatLoop();

            // Only yield when no longer Hostile -- combat pack owns all Hostile states
            if (_ai.CurrentAlertState != AlertState.Hostile)
            { YieldToCore(); return; }

            _s.Timer += Time.deltaTime;

            // Apply role only when Tactician scenario changes or guard has no role
            // Guards keep their role until next Tactician evaluation -- no per-frame churn
            if (_brain != null)
            {
                var scenario = _brain.Tactician.CurrentScenario;
                // Re-claim if: scenario changed, no role, or Search with no sector
                bool noSector = _s.Role == CombatRole.Search
                    && !_brain.Tactician.HasSearchSector(_ai);
                if (scenario != _s.LastScenario || _s.Role == CombatRole.Idle || noSector)
                {
                    _s.LastScenario = scenario;
                    var board = SquadBlackboard.Get(_ai.squadID);
                    var slot = board?.ClaimBestSlot(_ai);
                    SquadBlackboard.TacticalRole tacRole = slot != null
                        ? slot.Role
                        : _brain.Tactician.GetAssignedRole(_ai);
                    var assigned = FromTactical(tacRole);
                    if (assigned != _s.Role) ForceRole(assigned);
                }
            }

            // Search: reset dest periodically to pick new sector
            // Idle: trigger a fresh claim
            if (_s.Role == CombatRole.Search && _s.Timer >= _s.MaxTime)
            { _s.DestinationSet = false; _s.Timer = 0f; }
            else if (_s.Role == CombatRole.Idle)
            { _s.LastScenario = (SquadTactician.TacticianScenario)(-1); }

            TickRole();

            HuntDirector.RegisterHeat(ai.transform.position, 0.04f * Time.deltaTime);
        }

        // ---------- Combat loop ----------------------------------------------

        private void TickCombatLoop()
        {
            if (_threat == null || !_threat.HasLOS) return;
            // Face and shoot -- IShootable owns fire rate and animation
            _ai.CombatFaceToward(_threat.EstimatedPosition, 300f);
            ShootAt(_threat.EstimatedPosition);
        }

        // ---------- Role selection -------------------------------------------

        private void SelectRole()
        {
            if (_brain == null) return;
            // Try to claim a role slot from the blackboard
            var board = SquadBlackboard.Get(_ai.squadID);
            if (board != null)
            {
                var slot = board.ClaimBestSlot(_ai);
                if (slot != null)
                {
                    SetRole(FromTactical(slot.Role));
                    return;
                }
            }
            // Fallback to Tactician assignment
            SetRole(FromTactical(_brain.Tactician.GetAssignedRole(_ai)));
        }

        public void ForceRole(CombatRole role)
        {
            if (_s.Role == role) return;
            // Release blackboard slot and destination on role change
            var board = _ai != null ? SquadBlackboard.Get(_ai.squadID) : null;
            board?.UnregisterDestination(_ai);
            board?.ReleaseSlot(_ai);
            ReleaseCoverSpot();

            float maxTime = role switch
            {
                CombatRole.Suppress => 4f,
                CombatRole.Cover => 3f,
                CombatRole.Advance => 12f,
                CombatRole.Flank => 15f,
                CombatRole.Cautious => 10f,
                CombatRole.Reposition => 8f,
                CombatRole.CQB => 30f,
                CombatRole.Search => 20f,
                CombatRole.Withdraw => 15f,
                CombatRole.Overwatch => 10f,
                CombatRole.RearSecurity => 12f,
                _ => 8f,
            };

            _s.ResetRole(role, maxTime);
            _s.RepositionWindow = UnityEngine.Random.Range(1.8f, 3.5f);

            CurrentGoal = role switch
            {
                CombatRole.Advance => Goal.AdvanceTo,
                CombatRole.Flank => Goal.Flank,
                CombatRole.Suppress => Goal.Suppress,
                CombatRole.Reposition => Goal.AdvanceTo,
                CombatRole.Cover => Goal.HoldAndFire,
                CombatRole.Cautious => Goal.AdvanceTo,
                CombatRole.Search => Goal.Search,
                CombatRole.Overwatch => Goal.HoldAndFire,
                CombatRole.RearSecurity => Goal.HoldAndFire,
                CombatRole.Breach => Goal.AdvanceTo,
                CombatRole.Follow => Goal.AdvanceTo,
                CombatRole.Withdraw => Goal.Withdraw,
                _ => Goal.Idle,
            };
        }


        private void SetRole(CombatRole role)
        {
            if (_s.Role == role && _s.Timer < _s.MaxTime * 0.8f) return;
            ForceRole(role);
        }

        // ---------- Role execution -------------------------------------------

        private void TickRole()
        {
            switch (_s.Role)
            {
                case CombatRole.Advance: TickAdvance(); break;
                case CombatRole.Flank: TickFlank(); break;
                case CombatRole.Suppress: TickSuppress(); break;
                case CombatRole.Cover: TickCover(); break;
                case CombatRole.Cautious: TickCautious(); break;
                case CombatRole.Reposition: TickReposition(); break;
                case CombatRole.Search: TickSearch(); break;
                case CombatRole.Overwatch: TickOverwatch(); break;
                case CombatRole.RearSecurity: TickRearSecurity(); break;
                case CombatRole.Breach: TickCQB(); break;
                case CombatRole.Follow: TickCQB(); break;
                case CombatRole.Withdraw: TickWithdraw(); break;
                case CombatRole.CQB: TickCQB(); break;
                case CombatRole.Idle: SelectRole(); break;
            }
        }

        // --- Advance ---------------------------------------------------------

        private void TickAdvance()
        {
            // Stale intel -- only fallback after 30s with no update
            if (_threat.TimeSinceSeen > 30f && !_threat.HasLOS
             && _threat.Confidence < 0.2f)
            { ForceRole(CombatRole.Cautious); return; }

            Vector3 dest = GetRoutingDest();
            if (!_s.DestinationSet || Vector3.Distance(_s.Destination, dest) > 6f)
            {
                int idx = GetSquadIndex();
                int count = GetSquadCount();
                Vector3 toT = (dest - _ai.transform.position);
                toT.y = 0f;
                Vector3 perp = Vector3.Cross(toT.normalized, Vector3.up);
                float spread = Mathf.Approximately(toT.magnitude, 0f) ? 0f
                               : (idx - count * 0.5f) * 3f;
                Vector3 rawDest = dest + perp * spread;
                // Sample onto NavMesh -- prevents destination inside walls
                _s.Destination = NavMesh.SamplePosition(rawDest, out var navHit, 4f,
                    NavMesh.AllAreas) ? navHit.position : dest;
                // Pass actual threat pos so cover-to-cover routing is correct
                Vector3 threatForRoute = _threat?.EstimatedPosition ?? _s.Destination;
                _s.Waypoints = TacticalPathfinder.BuildAdvanceRoute(_ai, threatForRoute);
                if (_s.Waypoints == null || _s.Waypoints.Count == 0)
                    _s.Waypoints = BuildDirectPath(_s.Destination);
                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
                // Register destination so other guards avoid same spot
                SquadBlackboard.Get(_ai.squadID)
                    ?.RegisterDestination(_ai, _s.Destination);
            }

            // // Coherency: guards outside radius move toward anchor
            if (_brain != null && _brain.SquadAnchor != Vector3.zero)
            {
                float distFromAnchor = Vector3.Distance(
                    _ai.transform.position, _brain.SquadAnchor);
                if (distFromAnchor > _brain.CoherencyRadius)
                {
                    _ai.CombatMoveTo(_brain.SquadAnchor, SpeedMultiplier);
                    if (_threat != null && _threat.HasLOS)
                        _shootable?.TryShoot(_threat.EstimatedPosition);
                    return;
                }
            }

            if (_s.Waypoints != null && _s.Waypoints.Count > 0)
            {
                if (_s.WaypointIdx < _s.Waypoints.Count)
                    _ai.CombatMoveTo(_s.Waypoints[_s.WaypointIdx], SpeedMultiplier);
                bool done = TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints,
                    ref _s.WaypointIdx);
                if (done) _s.Timer = _s.MaxTime;
            }
            else
            {
                // No waypoints -- direct move, reset dest if blocked
                if (IsDestinationBlocked(_s.Destination))
                    _s.DestinationSet = false;
                else
                    _ai.CombatMoveTo(_s.Destination, SpeedMultiplier);
            }

            // POINT 5: cohesion -- dont advance too far from squad
            if (_brain != null && Vector3.Distance(_ai.transform.position, _brain.SquadAnchor) > _brain.CoherencyRadius * 1.5f)
            { _s.DestinationSet = false; } // recalculate closer dest

            float distToDest2D = Vector3.Distance(
                new Vector3(_ai.transform.position.x, 0, _ai.transform.position.z),
                new Vector3(_s.Destination.x, 0, _s.Destination.z));
            if (distToDest2D < 4f)
            {
                if (!_threat.HasLOS)
                {
                    // Arrived at last known position -- player not here
                    // Switch to Search to clear the area
                    ForceRole(CombatRole.Search);
                    return;
                }
                _s.Timer = _s.MaxTime; // arrived with LOS -- Tactician will reassign
            }
        }

        // --- Flank -----------------------------------------------------------

        private void TickFlank()
        {
            if (!_s.DestinationSet)
            {
                Vector3 threatPos = GetRoutingDest();
                var ep = EntryPointRegistry.FindBest(
                                        _ai.transform.position, threatPos);

                if (ep != null && Vector3.Distance(ep.transform.position, threatPos) < 20f)
                {
                    int idx = GetSquadIndex();
                    Vector3 stackPos = idx % 2 == 0 ? ep.StackLeftPos : ep.StackRightPos;
                    _s.Destination = stackPos;
                    _s.Waypoints = TacticalPathfinder.BuildAdvanceRoute(_ai, stackPos);
                }
                else
                {
                    _s.Waypoints = TacticalPathfinder.BuildFlankRoute(_ai, threatPos);
                    _s.Destination = _s.Waypoints != null && _s.Waypoints.Count > 0
                        ? _s.Waypoints[_s.Waypoints.Count - 1] : threatPos;
                }
                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
                SquadBlackboard.Get(_ai.squadID)?.RegisterDestination(_ai, _s.Destination);
            }

            if (_s.Waypoints != null && _s.Waypoints.Count > 0)
            {
                if (_s.WaypointIdx < _s.Waypoints.Count)
                    _ai.CombatMoveTo(_s.Waypoints[_s.WaypointIdx], SpeedMultiplier);
                TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints, ref _s.WaypointIdx);
            }
            else
            {
                if (IsDestinationBlocked(_s.Destination))
                { _s.DestinationSet = false; return; }
                _ai.CombatMoveTo(_s.Destination, SpeedMultiplier);
            }

            if (Vector3.Distance(_ai.transform.position, _s.Destination) < 2f)
                ForceRole(CombatRole.Cover);
        }

        // --- Suppress --------------------------------------------------------

        private void TickSuppress()
        {
            // Bounding overwatch -- suppress while squad advances
            // After 3s hand off to Advance so we leapfrog forward
            _s.AtCover = false;
            Vector3 target = GetBestKnownPos();
            _ai.CombatFaceToward(target, 200f);

            Vector3 toT = target - _ai.transform.position;
            bool blocked = Physics.Raycast(
                _ai.transform.position + Vector3.up * 1.5f,
                toT.normalized, toT.magnitude * 0.6f,
                LayerMask.GetMask("Default", "Environment"));

            if (!blocked)
            {
                _ai.CombatStop();
                _s.Timer += Time.deltaTime;
                _s.SuppressBurstTimer += Time.deltaTime;

                // Shoot burst every 0.3s
                if (_s.SuppressBurstTimer > 0.3f)
                {
                    _s.SuppressBurstTimer = 0f;
                    ShootAt(target);
                    _s.SuppressBurst++;
                }

                // After 3s suppressing -- advance and let others suppress
                if (_s.Timer >= 3f)
                {
                    _s.SuppressBurst = 0;
                    ForceRole(CombatRole.Advance);
                }
            }
            else
            {
                // No angle -- move to get one
                ForceRole(CombatRole.Reposition);
            }
        }

        // --- Cover -----------------------------------------------------------
        // Situation-based: reposition when exposed too long, lose LOS, or take damage.

        private void TickCover()
        {
            if (!_s.AtCover)
            {
                if (!_s.DestinationSet)
                {
                    var spot = FindCoverSpot();
                    if (spot != null)
                    {
                        _s.CoverPos = spot.Position;
                        _s.ReservedSpot = spot;
                        _s.DestinationSet = true;
                        _s.RepositionWindow = Random.Range(1.8f, 3.5f);
                        _s.ExposedTimer = 0f;
                        SquadBlackboard.Get(_ai.squadID)
                            ?.RegisterDestination(_ai, _s.CoverPos);
                    }
                    else
                    { ForceRole(CombatRole.Advance); return; }
                }

                _ai.CombatMoveTo(_s.CoverPos, SpeedMultiplier);
                if (Vector3.Distance(_ai.transform.position, _s.CoverPos) < 1f)
                {
                    _s.AtCover = true;
                    _ai.CombatStop();
                }
            }
            else
            {
                _s.AtCover = true;
                Vector3 target = GetBestKnownPos();
                _ai.CombatFaceToward(target, 150f);

                if (_threat != null && _threat.HasLOS)
                {
                    ShootAt(target);
                    _s.ExposedTimer += Time.deltaTime;
                }
                else
                {
                    _s.ExposedTimer = 0f;
                }

                bool exposedTooLong = _s.ExposedTimer >= _s.RepositionWindow;
                bool lostLOS = _threat == null
                                   || (!_threat.HasLOS && _threat.TimeSinceSeen > 2.5f);

                if (exposedTooLong || lostLOS)
                {
                    _s.AtCover = false;
                    ForceRole((_threat != null && _threat.HasLOS)
                        ? CombatRole.Suppress : CombatRole.Reposition);
                }
            }
        }
        // --- Reposition ------------------------------------------------------
        // Move to a new angle on threat to gain LOS without closing distance.
        // Orbits around LastKnownPosition at current range.

        private void TickReposition()
        {
            if (!_s.DestinationSet)
            {
                Vector3 threatPos = GetRoutingDest();
                Vector3 toUnit = (_ai.transform.position - threatPos);
                toUnit.y = 0f;
                float range = Mathf.Clamp(toUnit.magnitude, 8f, 25f);
                float currentAngle = Mathf.Atan2(toUnit.x, toUnit.z) * Mathf.Rad2Deg;
                float[] offsets = { 45f, -45f, 70f, -70f, 100f, -100f, 130f, -130f };

                Vector3 bestPos = Vector3.zero;
                float bestScore = float.MinValue;
                bool found = false;

                foreach (float offset in offsets)
                {
                    float newAngle = (currentAngle + offset) * Mathf.Deg2Rad;
                    Vector3 newDir = new Vector3(Mathf.Sin(newAngle), 0f,
                                                   Mathf.Cos(newAngle));
                    Vector3 tryPos = threatPos + newDir * range;

                    if (!NavMesh.SamplePosition(tryPos, out var hit, 4f,
                        NavMesh.AllAreas)) continue;
                    if (!NavRouter.HasPath(_ai.transform.position, hit.position))
                        continue;
                    if (Vector3.Distance(hit.position, _ai.transform.position) < 3f)
                        continue;

                    // Score -- prefer cover but dont require it
                    float score = 0f;
                    bool hasCover = TacticalFilter.IsExposedToThreat(
                        hit.position, threatPos) == false;
                    if (hasCover) score += 10f;

                    // Prefer positions that are closer to us (less travel)
                    float travelDist = Vector3.Distance(
                        _ai.transform.position, hit.position);
                    score -= travelDist * 0.1f;

                    // Prefer angles that are more different from current
                    score += Mathf.Abs(offset) * 0.05f;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = hit.position;
                        found = true;
                    }
                }

                if (!found) { ForceRole(CombatRole.Suppress); return; }

                _s.Destination = bestPos;
                _s.Waypoints = TacticalPathfinder.BuildAdvanceRoute(
                    _ai, _s.Destination);
                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
            }

            if (_s.Waypoints != null && _s.Waypoints.Count > 0)
            {
                if (_s.WaypointIdx < _s.Waypoints.Count)
                    _ai.CombatMoveTo(_s.Waypoints[_s.WaypointIdx], SpeedMultiplier);
                bool done = TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints,
                    ref _s.WaypointIdx);
                if (done)
                {
                    _ai.CombatStop();
                    _ai.CombatFaceToward(GetRoutingDest(), 120f);
                    if (_threat.HasLOS)
                        ForceRole(CombatRole.Suppress);
                    else if (_s.Timer > _s.MaxTime * 0.8f)
                        ForceRole(CombatRole.Cautious);
                }
            }
            else
                _ai.CombatMoveTo(_s.Destination, SpeedMultiplier);
        }

        // --- Cautious --------------------------------------------------------

        private void TickCautious()
        {
            if (_s.CautiousWaiting)
            {
                _s.CautiousWaitTimer += Time.deltaTime;
                _ai.CombatStop();
                _ai.CombatFaceToward(GetBestKnownPos(), 80f);
                if (_threat.HasLOS) { ForceRole(CombatRole.Cover); return; }
                if (_s.CautiousWaitTimer > Random.Range(0.8f, 1.5f))
                {
                    _s.CautiousWaiting = false;
                    _s.CautiousWaitTimer = 0f;
                    _s.DestinationSet = false;
                }
                return;
            }

            if (!_s.DestinationSet)
            {
                Vector3 dest = GetRoutingDest();
                float distToDest = Vector3.Distance(_ai.transform.position, dest);
                // Move directly toward last known -- stop 6m short to avoid standing on it
                Vector3 toward = (dest - _ai.transform.position).normalized;
                float approachDist = Mathf.Max(0f, distToDest - 3f);
                Vector3 next = _ai.transform.position + toward * Mathf.Min(approachDist, 20f);
                if (NavMesh.SamplePosition(next, out var hit, 4f, NavMesh.AllAreas))
                    _s.Destination = hit.position;
                else
                    _s.Destination = dest;
                _s.DestinationSet = true;
            }

            _ai.CombatMoveTo(_s.Destination, SpeedMultiplier);
            if (Vector3.Distance(_ai.transform.position, _s.Destination) < 2f)
            {
                _s.CautiousWaiting = true;
                _s.CautiousWaitTimer = 0f;
            }

            if (Vector3.Distance(_ai.transform.position, GetRoutingDest()) < 6f)
                ForceRole(CombatRole.Cover);
        }

        // ---------- Constants ------------------------------------------------
        private const float CohesionRadius = 18f;  // POINT 5: max spread from squad
        private const float BlackboardRadius = 10f;  // POINT 2: search overlap prevention

        // --- Search ----------------------------------------------------------
        // POINT 2: registers search dest on blackboard to prevent overlap
        // POINT 5: checks cohesion -- stays within CohesionRadius of squad center

        private void TickSearch()
        {
            var board = SquadBlackboard.Get(_ai.squadID);

            if (!_s.DestinationSet)
            {
                // Get sector -- if Tactician hasnt assigned one yet,
                // use squad index spread so guards dont all go north
                float myAngle;
                bool hasSector = _brain?.Tactician != null
                    && _brain.Tactician.HasSearchSector(_ai);
                if (hasSector)
                    myAngle = _brain.Tactician.GetSearchSectorAngle(_ai);
                else
                    myAngle = GetSquadIndex() * (360f / Mathf.Max(1f, GetSquadCount()))
                           + UnityEngine.Random.Range(-20f, 20f);

                float rad = myAngle * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                // POINT 5: clamp search dest within cohesion radius of squad center
                Vector3 squadCenter = (_brain != null && _brain.SquadAnchor != Vector3.zero)
                    ? _brain.SquadAnchor : GetSquadCenter();
                float maxDist = _brain?.CoherencyRadius ?? CohesionRadius;
                float searchDist = Random.Range(8f, 14f);
                Vector3 tryPos = _ai.transform.position + dir * searchDist;

                if (Vector3.Distance(tryPos, squadCenter) > maxDist)
                    tryPos = squadCenter + (tryPos - squadCenter).normalized * maxDist * 0.8f;

                // POINT 2: avoid positions already being searched
                if (board != null && board.IsPointCovered(tryPos, BlackboardRadius))
                {
                    // Rotate 45 degrees to find uncovered sector
                    float altRad = (myAngle + 45f) * Mathf.Deg2Rad;
                    Vector3 altDir = new Vector3(Mathf.Sin(altRad), 0f, Mathf.Cos(altRad));
                    tryPos = _ai.transform.position + altDir * searchDist;
                }

                if (NavMesh.SamplePosition(tryPos, out var hit, 5f, NavMesh.AllAreas))
                    _s.Destination = hit.position;
                else
                    _s.Destination = _ai.transform.position + dir * 5f;

                // POINT 2: register on blackboard
                board?.RegisterSearchUnit(_ai, _s.Destination);

                // Direct path -- dest is not threat, cover routing would block all waypoints
                _s.Waypoints = BuildDirectPath(_s.Destination);
                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
                _s.SearchScanTimer = 0f;
            }

            float distToDest = Vector3.Distance(_ai.transform.position, _s.Destination);
            if (distToDest > 2f)
            {
                Vector3 moveTarget = (_s.Waypoints != null && _s.WaypointIdx < _s.Waypoints.Count)
                    ? _s.Waypoints[_s.WaypointIdx] : _s.Destination;
                _ai.CombatMoveTo(moveTarget, SpeedMultiplier);
                if (_s.Waypoints != null)
                    TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints, ref _s.WaypointIdx);
            }
            else
            {
                _ai.CombatStop();
                _s.SearchScanTimer += Time.deltaTime;
                _ai.CombatFaceToward(GetRoutingDest(), 60f);
                if (_s.SearchScanTimer > 2f)
                {
                    board?.UnregisterSearchUnit(_ai); // POINT 2: free slot
                    _s.DestinationSet = false;
                }
            }
        }

        // --- Overwatch -------------------------------------------------------

        private void TickOverwatch()
        {
            if (!_s.DestinationSet)
            {
                var ep = _brain.CQB.ActiveEntry;
                Vector3 tryPos = ep != null
                    ? ep.transform.position + (-ep.transform.forward) * 4f
                    : _ai.transform.position;
                _s.Destination = NavMesh.SamplePosition(tryPos, out var hit, 4f,
                    NavMesh.AllAreas) ? hit.position : _ai.transform.position;
                _s.DestinationSet = true;
            }
            float dist = Vector3.Distance(_ai.transform.position, _s.Destination);
            if (dist > 1.5f)
                _ai.CombatMoveTo(_s.Destination, SpeedMultiplier);
            else
            {
                _ai.CombatStop();
                var ep = _brain.CQB.ActiveEntry;
                _ai.CombatFaceToward(
                    ep != null ? ep.transform.position : GetRoutingDest(), 80f);
                if (_threat != null && _threat.HasLOS)
                    ShootAt(_threat.EstimatedPosition);
            }
        }

        // --- Rear Security ---------------------------------------------------
        // Navigate around building to alternate entry point

        private void TickRearSecurity()
        {
            if (!_s.DestinationSet)
            {
                Vector3 threatPos = GetRoutingDest();
                var allEps = EntryPointRegistry.FindAllNear(threatPos, 20f);
                var primary = _brain.CQB.ActiveEntry;
                EntryPoint rearTarget = null;
                for (int i = 0; i < allEps.Count; i++)
                    if (allEps[i] != primary) { rearTarget = allEps[i]; break; }

                if (rearTarget != null)
                {
                    _s.Destination = rearTarget.StackLeftPos;
                    _s.Waypoints = BuildDirectPath(_s.Destination);
                }
                else { ForceRole(CombatRole.Reposition); return; }

                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
            }

            float dist = Vector3.Distance(_ai.transform.position, _s.Destination);
            if (dist > 1.5f)
            {
                Vector3 moveTarget = (_s.Waypoints != null && _s.WaypointIdx < _s.Waypoints.Count)
                    ? _s.Waypoints[_s.WaypointIdx] : _s.Destination;
                _ai.CombatMoveTo(moveTarget, SpeedMultiplier);
                if (_s.Waypoints != null)
                    TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints, ref _s.WaypointIdx);
            }
            else
            {
                _ai.CombatStop();
                _ai.CombatFaceToward(GetRoutingDest(), 80f);
                if (_threat != null && _threat.HasLOS) ShootAt(_threat.EstimatedPosition);
            }
        }

        // --- Withdraw --------------------------------------------------------

        private void TickWithdraw()
        {
            if (!_s.DestinationSet)
            {
                var route = TacticalPathfinder.BuildWithdrawRoute(_ai, GetRoutingDest());
                _s.Waypoints = route;
                _s.Destination = (route != null && route.Count > 0)
                    ? route[route.Count - 1]
                    : _ai.transform.position - _ai.transform.forward * 8f;
                _s.WaypointIdx = 0;
                _s.DestinationSet = true;
            }
            Vector3 wTarget = (_s.Waypoints != null && _s.WaypointIdx < _s.Waypoints.Count)
                ? _s.Waypoints[_s.WaypointIdx] : _s.Destination;
            _ai.CombatMoveTo(wTarget, SpeedMultiplier * 1.5f);
            if (_s.Waypoints != null)
                TacticalPathfinder.FollowWaypoints(_ai, _s.Waypoints, ref _s.WaypointIdx);
        }

        // ---------- Squad cohesion helper ------------------------------------
        // POINT 5: returns average position of live squad members

        private Vector3 GetSquadCenter()
        {
            var units = HuntDirector.AllUnits;
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].IsDead) continue;
                if (units[i].squadID != _ai.squadID) continue;
                sum += units[i].transform.position;
                n++;
            }
            return n > 0 ? sum / n : _ai.transform.position;
        }

        // --- CQB -------------------------------------------------------------

        private void TickCQB()
        {
            if (!_brain.CQB.IsActive) { ForceRole(CombatRole.Advance); return; }

            if (_s.CqbAction == null)
            {
                var role = _brain.CQB.GetRole(_ai);
                // All guards stack first -- holders hold fatal funnel then follow
                _s.CqbAction = (role.HasValue && role.Value.IsHolder)
                    ? (GoapAction)new HoldFatalFunnelAction()
                    : new StackAction();
                _s.CqbAction.OnEnter(_ai, _threat);
            }

            bool done = _s.CqbAction.Execute(_ai, _threat, _brain, Time.deltaTime);
            if (!done) return;

            _s.CqbAction.OnExit(_ai);

            // All guards progress: Stack/Hold -> Breach -> ClearCorner
            GoapAction next = _s.CqbAction switch
            {
                StackAction => new BreachAction(),
                HoldFatalFunnelAction => new BreachAction(), // holder follows as support
                BreachAction => new ClearCornerAction(),
                _ => null,
            };

            if (next != null) { _s.CqbAction = next; _s.CqbAction.OnEnter(_ai, _threat); }
            else { _s.CqbAction = null; ForceRole(CombatRole.Advance); }
        }

        // ---------- CQB evaluation -------------------------------------------

        private void TickCQBEvaluation()
        {
            if (_brain.CQB.IsActive || !_threat.HasIntel) return;

            Vector3 threatPos = _threat.EstimatedPosition;

            // Only evaluate CQB if an entry point is the natural route to player.
            // Check: is there a direct NavMesh path to player WITHOUT going through
            // an entry point? If yes, normal combat -- no CQB needed.
            var ep = EntryPointRegistry.FindBest(_ai.transform.position, threatPos);
            if (ep == null) return; // no entry points near threat

            float distEpToThreat = Vector3.Distance(ep.transform.position, threatPos);
            if (distEpToThreat > 12f) return; // entry point not close to threat

            // Check if direct path to threat is shorter than path via entry point
            // If guard can reach threat directly without entry point, skip CQB
            float directDist = NavRouter.PathLength(_ai.transform.position, threatPos);
            float viaEntryDist = NavRouter.PathLength(_ai.transform.position,
                                      ep.transform.position)
                                + distEpToThreat;

            // CQB only if going via entry point is necessary (no shorter direct path)
            // Allow 20% tolerance -- entry point must be clearly the required route
            if (directDist > 0f && directDist < viaEntryDist * 1.2f) return;

            var members = new List<StealthHuntAI>();
            var all = HuntDirector.AllUnits;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == _ai.squadID && !all[i].IsDead)
                    members.Add(all[i]);

            members.Sort((a, b) =>
                Vector3.Distance(a.transform.position, threatPos)
                .CompareTo(
                Vector3.Distance(b.transform.position, threatPos)));

            bool started = _brain.CQB.EvaluateEntry(_ai.transform.position,
                threatPos, _threat.Confidence, members);

            if (started)
            {
                _brain.SetCommittedGoal(TacticalBrain.CommittedGoalData.GoalType.ClearRoom,
                    threatPos, 120f);
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == null || all[i].squadID != _ai.squadID) continue;
                    all[i].GetComponent<StandardCombat>()?.ForceRole(CombatRole.CQB);
                }
            }
        }

        // ---------- Threat update --------------------------------------------

        private void UpdateThreat()
        {
            var target = _ai.GetTarget();
            if (target == null || _brain == null) return;
            bool canSee = _ai.Sensor != null && _ai.Sensor.CanSeeTarget;
            if (canSee)
                _brain.Intel.Threat.UpdateWithSight(target.Position, target.Velocity);
            else
                _brain.Intel.UpdateNoSight();
        }

        // ---------- Event processing -----------------------------------------

        private void ProcessEvents()
        {
            if (_events == null || !_events.HasEvents) return;
            if (_brain == null || _threat == null) return;
            var evt = _events.ConsumeHighestPriority();
            if (evt == null) return;
            switch (evt.Value.Type)
            {
                case CombatEventType.DamageTaken:
                case CombatEventType.Ambushed:
                    if (evt.Value.Position != Vector3.zero)
                        _threat.RegisterShotFrom(evt.Value.Position,
                            (evt.Value.Position - _ai.transform.position).normalized);
                    // Interrupt current role -- re-claim after recovery
                    ForceRole(_threat.HasLOS ? CombatRole.Cover : CombatRole.Reposition);
                    _s.LastScenario = (SquadTactician.TacticianScenario)(-1);
                    break;
                case CombatEventType.BuddyDown:
                    // Squadmate killed -- suppress and reposition
                    if (_s.Role != CombatRole.Withdraw)
                        ForceRole(_threat.HasLOS ? CombatRole.Suppress : CombatRole.Reposition);
                    break;
                case CombatEventType.BuddyNeedsHelp:
                    if (_threat.HasLOS) ForceRole(CombatRole.Suppress);
                    break;
                case CombatEventType.ThreatFlank:
                    _brain.Intel.Threat.ReceiveIntel(evt.Value.Position, Vector3.zero, 0.8f);
                    if (_brain.CommittedGoal != null &&
                        Vector3.Distance(evt.Value.Position,
                            _brain.CommittedGoal.Position) > 15f)
                        _brain.InterruptCommittedGoal("new intel");
                    ForceRole(CombatRole.Reposition);
                    break;
                case CombatEventType.ThreatFound:
                    if (_s.Role == CombatRole.Search || _s.Role == CombatRole.Idle)
                        ForceRole(CombatRole.Cautious);
                    break;
                case CombatEventType.ThreatLost:
                    if (_s.Role == CombatRole.Advance || _s.Role == CombatRole.Suppress)
                        ForceRole(CombatRole.Cautious);
                    break;
            }
        }
        // ---------- Helpers --------------------------------------------------

        private void ShootAt(Vector3 pos)
            => _shootable?.TryShoot(pos);

        // --- Direct path (no cover routing) --------------------------------
        // Used when destination is NOT a threat position.
        // BuildAdvanceRoute uses dest as threatPos which incorrectly filters waypoints.
        private List<Vector3> BuildDirectPath(Vector3 dest)
        {
            var path = new UnityEngine.AI.NavMeshPath();
            if (!NavMesh.CalculatePath(_ai.transform.position, dest,
                NavMesh.AllAreas, path)) return null;
            if (path.status == UnityEngine.AI.NavMeshPathStatus.PathInvalid) return null;
            var result = new List<Vector3>();
            for (int i = 1; i < path.corners.Length; i++)
                result.Add(path.corners[i]);
            return result;
        }

        // --- TacticalRole mapping -------------------------------------------
        private static CombatRole FromTactical(SquadBlackboard.TacticalRole r) => r switch
        {
            SquadBlackboard.TacticalRole.Advance => CombatRole.Advance,
            SquadBlackboard.TacticalRole.Flank => CombatRole.Flank,
            SquadBlackboard.TacticalRole.Suppress => CombatRole.Suppress,
            SquadBlackboard.TacticalRole.Cover => CombatRole.Cover,
            SquadBlackboard.TacticalRole.Cautious => CombatRole.Cautious,
            SquadBlackboard.TacticalRole.Reposition => CombatRole.Reposition,
            SquadBlackboard.TacticalRole.Search => CombatRole.Search,
            SquadBlackboard.TacticalRole.Overwatch => CombatRole.Overwatch,
            SquadBlackboard.TacticalRole.RearSecurity => CombatRole.RearSecurity,
            SquadBlackboard.TacticalRole.Breach => CombatRole.Breach,
            SquadBlackboard.TacticalRole.Follow => CombatRole.Follow,
            SquadBlackboard.TacticalRole.Withdraw => CombatRole.Withdraw,
            SquadBlackboard.TacticalRole.CQB => CombatRole.CQB,
            _ => CombatRole.Idle,
        };

        private Vector3 GetRoutingDest()
        {
            // Prefer last known position (where we last SAW the player)
            if (_threat != null && _threat.LastSeenTime > -999f
             && _threat.LastKnownPosition != Vector3.zero)
                return _threat.LastKnownPosition;
            // Fall back to estimated position from sound/intel
            if (_threat != null && _threat.EstimatedPosition != Vector3.zero)
                return _threat.EstimatedPosition;
            // Last resort: blackboard shared position
            var board = _brain != null ? SquadBlackboard.Get(_ai.squadID) : null;
            if (board != null && board.SharedLastKnown != Vector3.zero)
                return board.SharedLastKnown;
            // Nothing -- search from current position
            return _ai.transform.position;
        }

        private Vector3 GetBestKnownPos()
        {
            if (_threat.HasLOS) return _threat.EstimatedPosition;
            if (_threat.LastSeenTime > -999f) return _threat.LastKnownPosition;
            var board = SquadBlackboard.Get(_ai.squadID);
            if (board != null && board.SharedConfidence > 0.1f) return board.SharedLastKnown;
            return _ai.transform.position + _ai.transform.forward * 10f;
        }

        private TacticalSpot FindCoverSpot()
        {
            if (TacticalSystem.Instance == null) return null;
            var ctx = TacticalContext.Build(_ai, _threat, _brain, 20f);
            return TacticalSystem.Instance.EvaluateSync(ctx);
        }

        private void ReleaseCoverSpot()
        {
            _s.AtCover = false;
            _s.ReservedSpot = null;
        }

        private void YieldToCore()
        {
            WantsControl = false;
            ReleaseCoverSpot();
            _ai?.CombatRestoreRotation();
            // Keep brain alive -- fast re-entry on next OnEnterCombat
        }

        private int CountSquadRole(CombatRole role)
        {
            int count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID
                 || units[i] == _ai) continue;
                var sc = units[i].GetComponent<StandardCombat>();
                if (sc != null && sc._s.Role == role) count++;
            }
            return count;
        }

        private static bool HasTacticianRunner()
        {
            if (!_tacticianRunnerChecked)
            {
                _tacticianRunnerPresent = Object.FindFirstObjectByType<TacticianRunner>() != null;
                _tacticianRunnerChecked = true;
            }
            return _tacticianRunnerPresent;
        }

        private bool IsSquadLeader()
        {
            int min = int.MaxValue;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID
                 || units[i].IsDead) continue;
                int id = units[i].GetInstanceID();
                if (id < min) min = id;
            }
            return _ai.GetInstanceID() == min;
        }

        private int GetSquadIndex()
        {
            int idx = 0, count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID) continue;
                if (units[i] == _ai) idx = count;
                count++;
            }
            return idx;
        }

        private int GetSquadCount()
        {
            int count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && units[i].squadID == _ai.squadID) count++;
            return count;
        }

        private void CheckStuck()
        {
            var agent = _agent ?? _ai?.GetComponent<NavMeshAgent>();
            if (agent == null || agent.isStopped || !agent.hasPath) return;
            if (agent.pathStatus == NavMeshPathStatus.PathPartial
             || agent.pathStatus == NavMeshPathStatus.PathInvalid)
            { _s.DestinationSet = false; _s.Waypoints = null; return; }
            if (agent.desiredVelocity.magnitude < 0.1f) return;
            float moved = Vector3.Distance(_ai.transform.position, _s.LastPos);
            if (moved > 0.5f) { _s.StuckTimer = 0f; _s.LastPos = _ai.transform.position; return; }
            _s.StuckTimer += Time.deltaTime;
            if (_s.StuckTimer > 2f)
            {
                _s.BlockedCells.Add(WorldToCell(agent.destination));
                _s.DestinationSet = false;
                _s.Waypoints = null;
                _s.Timer = _s.MaxTime; // force role reselect with new dest
                _s.StuckTimer = 0f; _s.LastPos = _ai.transform.position;
                agent.ResetPath();
            }
            _s.BlockedTimer += Time.deltaTime;
            if (_s.BlockedTimer > 8f) { _s.BlockedCells.Clear(); _s.BlockedTimer = 0f; }
        }

        private static Vector3Int WorldToCell(Vector3 p)
            => new Vector3Int(Mathf.RoundToInt(p.x / 2f),
                              Mathf.RoundToInt(p.y / 2f),
                              Mathf.RoundToInt(p.z / 2f));

        public bool IsDestinationBlocked(Vector3 dest)
            => _s.IsDestinationBlocked(dest);
    }
}