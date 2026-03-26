using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Central registry for all StealthHuntAI units, squads and the player target.
    /// Extracted from HuntDirector -- owns no MonoBehaviour lifecycle.
    /// HuntDirector delegates all registration calls here.
    ///
    /// Auto-cleared on domain reload via RuntimeInitializeOnLoadMethod.
    /// </summary>
    public static class UnitRegistry
    {
        // ---------- Data -----------------------------------------------------

        private static StealthTarget _target;
        private static List<StealthHuntAI> _units = new List<StealthHuntAI>();
        public static IReadOnlyList<StealthHuntAI> AllUnits => _units;
        private static List<SquadBlackboard> _squads = new List<SquadBlackboard>();
        private static bool _alertingSquad = false;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReload()
        {
            _target = null;
            _units = new List<StealthHuntAI>();
            _squads = new List<SquadBlackboard>();
            _alertingSquad = false;
        }

        // ---------- Target ---------------------------------------------------

        public static void RegisterTarget(StealthTarget target)
        {
            _target = target;
            for (int i = 0; i < _units.Count; i++)
                _units[i]?.ReceiveTarget(target);
        }

        public static void UnregisterTarget(StealthTarget target)
        {
            if (_target == target) _target = null;
        }

        public static StealthTarget GetTarget() => _target;

        // ---------- Units ----------------------------------------------------

        public static void RegisterUnit(StealthHuntAI unit)
        {
            if (!_units.Contains(unit))
                _units.Add(unit);

            EnsureSquad(unit);

            if (_target != null)
                unit.ReceiveTarget(_target);

            // Apply current alert level effects immediately
        }

        public static void UnregisterUnit(StealthHuntAI unit)
        {
            _units.Remove(unit);
            for (int i = 0; i < _squads.Count; i++)
                _squads[i]?.RemoveUnit(unit);
        }

        public static void ReportStateChange(StealthHuntAI unit,
                                              AlertState newAlert, SubState newSub)
        {
            SquadBlackboard squad = GetSquadFor(unit);
            squad?.OnUnitStateChanged(unit, newAlert, newSub);
        }

        // ---------- Squads ---------------------------------------------------

        public static SquadBlackboard GetBlackboard(int squadID)
        {
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i] != null && _squads[i].SquadID == squadID)
                    return _squads[i];
            return null;
        }

        private static void EnsureSquad(StealthHuntAI unit)
        {
            int id = unit.squadID;

            if (id == 0)
            {
                for (int i = 0; i < _squads.Count; i++)
                {
                    if (_squads[i].UnitCount < 6)
                    {
                        _squads[i].AddUnit(unit);
                        unit.AssignSquadID(_squads[i].SquadID);
                        return;
                    }
                }

                var newSquad = new SquadBlackboard(_squads.Count + 1);
                newSquad.AddUnit(unit);
                unit.AssignSquadID(newSquad.SquadID);
                _squads.Add(newSquad);
            }
            else
            {
                SquadBlackboard found = null;
                for (int i = 0; i < _squads.Count; i++)
                {
                    if (_squads[i].SquadID == id)
                    {
                        found = _squads[i];
                        break;
                    }
                }

                if (found == null)
                {
                    found = new SquadBlackboard(id);
                    _squads.Add(found);
                }

                found.AddUnit(unit);
            }
        }

        private static SquadBlackboard GetSquadFor(StealthHuntAI unit)
        {
            for (int i = 0; i < _squads.Count; i++)
            {
                if (_squads[i].Contains(unit)) return _squads[i];
            }
            return null;
        }

        // ---------- Alert propagation ----------------------------------------

        public static void AlertSquad(StealthHuntAI source, float alertRadius = 40f)
        {
            if (_alertingSquad) return;
            _alertingSquad = true;

            try
            {
                Vector3 sourcePos = source.transform.position;
                var sourceBoard = SquadBlackboard.Get(source.squadID);

                for (int i = 0; i < _units.Count; i++)
                {
                    var unit = _units[i];
                    if (unit == null || unit == source) continue;
                    if (unit.squadID != source.squadID) continue;

                    float dist = Vector3.Distance(sourcePos, unit.transform.position);

                    // Always alert non-hostile squad members
                    if (unit.CurrentAlertState != AlertState.Hostile)
                        unit.ForceHostileSilent();

                    // Share intel with ALL squad members including already-hostile ones
                    if (sourceBoard != null)
                    {
                        var board = SquadBlackboard.Get(unit.squadID);
                        if (dist <= alertRadius)
                            board?.ShareIntel(sourceBoard.SharedLastKnown,
                                sourceBoard.SharedConfidence * 0.8f);
                        else
                            board?.ShareIntel(sourcePos, 0.2f);
                    }
                }
            }
            finally
            {
                _alertingSquad = false;
            }
        }
    }
}