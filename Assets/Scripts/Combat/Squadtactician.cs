using StealthHuntAI.Combat.CQB;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using static StealthHuntAI.Combat.StandardCombat;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Central squad role controller. Runs on squad leader every 3s or on
    /// significant intel change. Assigns one explicit role to every guard.
    /// Guards execute their assigned role -- no individual role selection.
    ///
    /// Scenarios:
    ///   Search   -- low/no confidence, find player
    ///   Approach -- medium confidence, close in from multiple angles
    ///   Assault  -- high confidence, suppress + flank + advance
    ///   CQB      -- entry point is route to player, breach and clear
    ///   Withdraw -- squad strength critical
    /// </summary>
    public class SquadTactician
    {
        // ---------- Public state ---------------------------------------------

        public enum TacticianScenario
        {
            Search,
            Approach,
            Assault,
            CQB,
            Withdraw,
        }

        public TacticianScenario CurrentScenario { get; private set; }
            = TacticianScenario.Search;

        // ---------- Assigned roles -------------------------------------------

        private readonly Dictionary<int, CombatRole> _assigned
            = new Dictionary<int, CombatRole>();

        public CombatRole GetAssignedRole(StealthHuntAI unit)
        {
            if (_assigned.TryGetValue(unit.GetInstanceID(), out var role))
                return role;
            return CombatRole.Search; // default until first evaluation
        }

        // ---------- Re-evaluate triggers -------------------------------------

        private float _evalTimer;
        private float _lastConfidence;
        private int _lastAliveCount = -1; // -1 forces evaluate on first tick
        private const float EvalInterval = 3f;

        private int _squadID = -1;

        public void Tick(float dt, TacticalBrain brain,
                         IReadOnlyList<StealthHuntAI> allUnits, int squadID)
        {
            _squadID = squadID;
            _evalTimer += dt;

            var intel = brain.Intel;
            int aliveCount = CountAlive(allUnits, squadID);

            bool confChanged = Mathf.Abs(intel.Confidence - _lastConfidence) > 0.2f;
            bool guardDied = aliveCount < _lastAliveCount;
            bool timerExpired = _evalTimer >= EvalInterval;

            // Always evaluate immediately on first tick
            if (confChanged || guardDied || timerExpired || _lastAliveCount < 0)
            {
                // Clear assigned roles on death so dead guard's role isnt reused
                if (guardDied) _assigned.Clear();
                Evaluate(brain, allUnits, squadID);
                _lastConfidence = intel.Confidence;
                _lastAliveCount = aliveCount;
                _evalTimer = 0f;
            }
        }

        // ---------- Evaluation -----------------------------------------------

        private void Evaluate(TacticalBrain brain,
                               IReadOnlyList<StealthHuntAI> allUnits, int squadID)
        {
            var members = GetLiveMembers(allUnits, squadID);
            if (members.Count == 0) return;

            var intel = brain.Intel;
            float conf = intel.Confidence;
            float squadStr = (float)members.Count
                             / Mathf.Max(1, CountTotal(allUnits, squadID));

            // --- Withdraw: squad collapsing ----------------------------------
            if (squadStr < 0.3f)
            {
                AssignAll(members, CombatRole.Withdraw);
                CurrentScenario = TacticianScenario.Withdraw;
                return;
            }

            // --- Search: no meaningful intel ---------------------------------
            if (!intel.HasIntel || conf < 0.15f)
            {
                AssignSearch(members, intel);
                CurrentScenario = TacticianScenario.Search;
                return;
            }
            // Player spotted before but intel is old -- cautious approach
            if (intel.Threat.LastSeenTime < 0f)
            {
                AssignCautious(members);
                CurrentScenario = TacticianScenario.Search;
                return;
            }

            // --- CQB: entry point is the route to player ---------------------
            Vector3 threatPos = intel.EstimatedPos;
            if (IsCQBScenario(members, threatPos, brain))
            {
                AssignCQB(members, threatPos, brain);
                CurrentScenario = TacticianScenario.CQB;
                return;
            }

            // --- Assault: high confidence ------------------------------------
            if (conf >= 0.5f)
            {
                AssignAssault(members, intel);
                CurrentScenario = TacticianScenario.Assault;
                return;
            }

            // --- Approach: medium confidence ---------------------------------
            AssignApproach(members, intel);
            CurrentScenario = TacticianScenario.Approach;
        }

        // ---------- Scenario assignments -------------------------------------

        private void AssignCautious(List<StealthHuntAI> members)
        {
            // All guards move toward estimated position cautiously
            // Used when we have no direct sight but squad was just alerted
            for (int i = 0; i < members.Count; i++)
                Assign(members[i], CombatRole.Cautious);
        }

        // Maps guard instanceID to assigned search sector angle
        private readonly Dictionary<int, float> _searchSectors
            = new Dictionary<int, float>();

        public float GetSearchSectorAngle(StealthHuntAI unit)
        {
            if (_searchSectors.TryGetValue(unit.GetInstanceID(), out float angle))
                return angle;
            return 0f;
        }

        private void AssignSearch(List<StealthHuntAI> members, SquadIntel intel)
        {
            _searchSectors.Clear();
            int count = Mathf.Max(1, members.Count);
            float sectorSize = 360f / count;
            float startAngle = UnityEngine.Random.Range(0f, 360f);

            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] == null || members[i].IsDead) continue;
                float angle = startAngle + sectorSize * i;
                _searchSectors[members[i].GetInstanceID()] = angle;
                Assign(members[i], CombatRole.Search);
            }
        }

        private void AssignApproach(List<StealthHuntAI> members, SquadIntel intel)
        {
            // Check if direct path to threat is exposed
            bool directExposed = IsDirectPathExposed(
                members[0].transform.position, intel.EstimatedPos);

            for (int i = 0; i < members.Count; i++)
            {
                CombatRole role;
                if (i == 0)
                    role = CombatRole.Advance;
                else if (i == 1 && directExposed)
                    role = CombatRole.Flank; // flank only when direct is dangerous
                else
                    role = CombatRole.Reposition;
                Assign(members[i], role);
            }
        }

        private void AssignAssault(List<StealthHuntAI> members, SquadIntel intel)
        {
            bool anyLOS = false;
            for (int i = 0; i < members.Count; i++)
                if (members[i].Sensor != null && members[i].Sensor.CanSeeTarget)
                { anyLOS = true; break; }

            bool directExposed = IsDirectPathExposed(
                members[0].transform.position, intel.EstimatedPos);

            int suppressIdx = anyLOS ? 2 : -1;
            for (int i = 0; i < members.Count; i++)
            {
                CombatRole role = i == 0 ? CombatRole.Advance
                    : i == 1 && directExposed ? CombatRole.Flank
                    : i == suppressIdx ? CombatRole.Suppress
                    : CombatRole.Reposition;
                Assign(members[i], role);
            }
        }

        private void AssignCQB(List<StealthHuntAI> members, Vector3 threatPos,
                                TacticalBrain brain)
        {
            var ep = EntryPointRegistry.FindBest(members[0].transform.position, threatPos);
            if (ep == null) { AssignAssault(members, brain.Intel); return; }

            // Find alternate entry point for rear security
            var allEps = EntryPointRegistry.FindAllNear(threatPos, 20f);
            EntryPoint rearEp = null;
            for (int i = 0; i < allEps.Count; i++)
                if (allEps[i] != ep) { rearEp = allEps[i]; break; }

            // Assign by priority -- fill critical roles first
            // Priority: Breach > Follow > RearSecurity > Overwatch
            var remaining = new List<StealthHuntAI>(members);

            // Breacher -- closest to entry point
            remaining.Sort((a, b) =>
                Vector3.Distance(a.transform.position, ep.transform.position)
                .CompareTo(
                Vector3.Distance(b.transform.position, ep.transform.position)));

            if (remaining.Count > 0)
            { Assign(remaining[0], CombatRole.Breach); remaining.RemoveAt(0); }

            // Follower -- second closest
            if (remaining.Count > 0)
            { Assign(remaining[0], CombatRole.Follow); remaining.RemoveAt(0); }

            // Rear security -- only if alternate entry point exists
            if (remaining.Count > 0 && rearEp != null)
            { Assign(remaining[0], CombatRole.RearSecurity); remaining.RemoveAt(0); }

            // Rest -- overwatch behind breach team
            for (int i = 0; i < remaining.Count; i++)
                Assign(remaining[i], CombatRole.Overwatch);

            // Start CQB in controller
            if (!brain.CQB.IsActive)
            {
                var sortedForCQB = new List<StealthHuntAI>(members);
                sortedForCQB.Sort((a, b) =>
                    Vector3.Distance(a.transform.position, ep.transform.position)
                    .CompareTo(
                    Vector3.Distance(b.transform.position, ep.transform.position)));

                brain.CQB.EvaluateEntry(members[0].transform.position,
                    threatPos, brain.Intel.Confidence, sortedForCQB);
            }
        }

        // ---------- Helpers --------------------------------------------------

        private void Assign(StealthHuntAI unit, CombatRole role)
            => _assigned[unit.GetInstanceID()] = role;

        private void AssignAll(List<StealthHuntAI> members, CombatRole role)
        {
            for (int i = 0; i < members.Count; i++)
                Assign(members[i], role);
        }

        private bool IsCQBScenario(List<StealthHuntAI> members,
                                    Vector3 threatPos, TacticalBrain brain)
        {
            if (brain.CQB.IsActive) return true;

            if (members.Count == 0) return false;
            var ep = EntryPointRegistry.FindBest(members[0].transform.position, threatPos);
            if (ep == null) return false;

            float distEpToThreat = Vector3.Distance(ep.transform.position, threatPos);
            if (distEpToThreat > 12f) return false;

            // Check if direct path is longer than via entry point
            float directDist = NavRouter.PathLength(members[0].transform.position, threatPos);
            float viaEntryDist = NavRouter.PathLength(members[0].transform.position,
                                     ep.transform.position) + distEpToThreat;

            return directDist < 0f || directDist > viaEntryDist * 1.2f;
        }

        /// <summary>
        /// Returns true if the straight line from start to end is mostly exposed.
        /// Samples midpoint and 75% point -- if both are exposed, path is dangerous.
        /// </summary>
        private static bool IsDirectPathExposed(Vector3 from, Vector3 to)
        {
            // Check two points along the path
            Vector3 mid = Vector3.Lerp(from, to, 0.5f);
            Vector3 nearEnd = Vector3.Lerp(from, to, 0.75f);
            bool midExposed = TacticalFilter.IsExposedToThreat(mid, to);
            bool nearExposed = TacticalFilter.IsExposedToThreat(nearEnd, to);
            return midExposed && nearExposed;
        }

        private static List<StealthHuntAI> GetLiveMembers(
            IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            var result = new List<StealthHuntAI>();
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID && !all[i].IsDead)
                    result.Add(all[i]);
            return result;
        }

        private static int CountAlive(IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            int n = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID && !all[i].IsDead)
                    n++;
            return n;
        }

        private static int CountTotal(IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            int n = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID) n++;
            return n;
        }
    }
}