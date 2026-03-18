using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Full combat behaviour implementation for StealthHuntAI.
    /// Implements ICombatBehaviour -- assign to StealthHuntAI.combatBehaviourOverride.
    ///
    /// Features:
    ///   Cover seeking with multi-factor scoring
    ///   Role-based tactics (Tracker, Flanker, Overwatch, Blocker)
    ///   Peek fire from cover
    ///   Suppression fire
    ///   Repositioning when cover is compromised
    ///   Squad crossfire coordination
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Standard Combat")]
    [RequireComponent(typeof(StealthHuntAI))]
    public class StandardCombat : MonoBehaviour, ICombatBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Cover")]
        [Tooltip("Max distance to search for cover points.")]
        [Range(5f, 50f)] public float coverSearchRange = 20f;

        [Tooltip("Distance at which unit stops moving to cover and starts engaging.")]
        [Range(1f, 5f)] public float coverArrivalThreshold = 1.2f;

        [Tooltip("Seconds in cover before peeking to fire.")]
        [Range(0.5f, 5f)] public float coverWaitTime = 1.5f;

        [Tooltip("Seconds exposed while peeking to fire.")]
        [Range(0.3f, 3f)] public float peekDuration = 1.0f;

        [Tooltip("Reposition if cover is compromised for this many seconds.")]
        [Range(1f, 10f)] public float repositionThreshold = 3f;

        [Header("Engagement")]
        [Tooltip("Range at which unit suppresses rather than peeks.")]
        [Range(5f, 40f)] public float suppressionRange = 15f;

        [Tooltip("Seconds of suppression fire before seeking new cover.")]
        [Range(1f, 8f)] public float suppressionDuration = 2.5f;

        [Header("Cover Weights")]
        public CoverWeights weights = new CoverWeights();

        [Header("Animation")]
        [Tooltip("Clip assignments for combat animations. " +
                 "Supports multiple clips per trigger for random variation.")]
        public List<CombatAnimSlot> animSlots = new List<CombatAnimSlot>();

        [Tooltip("CrossFade transition duration between combat animations.")]
        [Range(0f, 0.5f)] public float animTransitionDuration = 0.12f;

        // ---------- ICombatBehaviour -----------------------------------------

        public bool WantsControl { get; private set; }

        public void OnEnterCombat(StealthHuntAI ai)
        {
            WantsControl = true;
            _ai = ai;
            _state = CombatState.SeekCover;
            _stateTimer = 0f;
            _currentCover = null;
            _cachedRole = ai.ActiveRole;

            // Register sector watch for Overwatch role
            if (_cachedRole == SquadRole.Overwatch && _ai.GetTarget() != null)
            {
                Vector3 dir = (_ai.GetTarget().Position - ai.transform.position).normalized;
                HuntDirector.RegisterSectorWatch(dir, ai);
            }
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            _stateTimer += Time.deltaTime;

            switch (_state)
            {
                case CombatState.SeekCover: TickSeekCover(); break;
                case CombatState.MoveToCover: TickMoveToCover(); break;
                case CombatState.InCover: TickInCover(); break;
                case CombatState.PeekFire: TickPeekFire(); break;
                case CombatState.Suppressing: TickSuppressing(); break;
                case CombatState.Reposition: TickReposition(); break;
            }

            // Always update heat map with current position
            HuntDirector.RegisterHeat(ai.transform.position, 0.1f * Time.deltaTime);
        }

        public void OnExitCombat(StealthHuntAI ai)
        {
            WantsControl = false;

            ReleaseCover();
            HuntDirector.ClearSectorWatch(ai);
            ai.CombatRestoreRotation();
        }

        // ---------- States ---------------------------------------------------

        private enum CombatState
        {
            SeekCover, MoveToCover, InCover, PeekFire, Suppressing, Reposition
        }

        private StealthHuntAI _ai;
        private CombatState _state;
        private float _stateTimer;
        private CoverPoint _currentCover;
        private SquadRole _cachedRole;
        private float _exposedTimer;
        private bool _peekingLeft;

        private void TickSeekCover()
        {
            var target = _ai.GetTarget();
            if (target == null) { WantsControl = false; return; }

            // Evaluate available cover points
            var scored = CoverEvaluator.Evaluate(
                _ai, target.Position, _cachedRole, weights, coverSearchRange);

            if (scored.Count == 0)
            {
                // No cover -- fall back to Core Pursuing behaviour
                WantsControl = false;
                return;
            }

            // Take best cover
            _currentCover = scored[0].Point;
            _currentCover.Occupy(_ai);

            TransitionTo(CombatState.MoveToCover);
            PlayAnim(CombatAnimTrigger.MoveToCover);
        }

        private void TickMoveToCover()
        {
            if (_currentCover == null) { TransitionTo(CombatState.SeekCover); return; }

            _ai.CombatMoveTo(_currentCover.transform.position);

            float dist = Vector3.Distance(
                _ai.transform.position, _currentCover.transform.position);

            if (dist <= coverArrivalThreshold)
            {
                _ai.CombatStop();
                TransitionTo(CombatState.InCover);
                PlayAnim(CombatAnimTrigger.TakeCover);
            }

            // If cover is compromised while moving -- reseek
            if (_stateTimer > 3f && !IsCoverValid())
            {
                ReleaseCover();
                TransitionTo(CombatState.SeekCover);
            }
        }

        private void TickInCover()
        {
            if (_currentCover == null) { TransitionTo(CombatState.SeekCover); return; }

            // Face away from target while in cover
            var target = _ai.GetTarget();
            if (target != null)
            {
                Vector3 awayDir = (_ai.transform.position - target.Position);
                awayDir.y = 0f;
                if (awayDir.magnitude > 0.1f)
                    _ai.CombatFaceToward(_ai.transform.position + awayDir);
            }

            PlayAnim(CombatAnimTrigger.CoverIdle);

            // Check if cover is still valid
            if (!IsCoverValid())
            {
                _exposedTimer += Time.deltaTime;
                if (_exposedTimer >= repositionThreshold)
                {
                    ReleaseCover();
                    TransitionTo(CombatState.Reposition);
                    return;
                }
            }
            else
            {
                _exposedTimer = 0f;
            }

            // Wait before peeking
            if (_stateTimer < coverWaitTime) return;

            // Decide: peek fire or suppress?
            if (target != null)
            {
                float dist = Vector3.Distance(_ai.transform.position, target.Position);
                if (dist <= suppressionRange && _ai.Sensor.CanSeeTarget)
                    TransitionTo(CombatState.Suppressing);
                else
                    TransitionTo(CombatState.PeekFire);
            }
        }

        private void TickPeekFire()
        {
            var target = _ai.GetTarget();
            if (target == null) { TransitionTo(CombatState.InCover); return; }

            if (_currentCover != null)
            {
                // Face toward peek direction
                Vector3 peekTarget = _currentCover.transform.position
                                   + _currentCover.PeekDir * 3f;
                _ai.CombatFaceToward(peekTarget);

                // Choose peek side based on cover type
                PlayAnim(DeterminePeekSide(target.Position)
                    ? CombatAnimTrigger.PeekLeft
                    : CombatAnimTrigger.PeekRight);
            }

            PlayAnim(CombatAnimTrigger.CoverFire);

            // Return to cover after peek duration
            if (_stateTimer >= peekDuration)
                TransitionTo(CombatState.InCover);
        }

        private void TickSuppressing()
        {
            var target = _ai.GetTarget();
            if (target != null)
            {
                _ai.CombatFaceToward(target.Position);
                PlayAnim(CombatAnimTrigger.Suppressing);
            }

            if (_stateTimer >= suppressionDuration)
                TransitionTo(CombatState.InCover);
        }

        private void TickReposition()
        {
            PlayAnim(CombatAnimTrigger.Reposition);

            // Small delay then seek new cover
            if (_stateTimer >= 0.5f)
                TransitionTo(CombatState.SeekCover);
        }

        // ---------- Helpers --------------------------------------------------

        private void TransitionTo(CombatState newState)
        {
            _state = newState;
            _stateTimer = 0f;
        }

        private bool IsCoverValid()
        {
            if (_currentCover == null) return false;
            var target = _ai.GetTarget();
            if (target == null) return true;

            // Cover is invalid if target has direct line of sight to unit
            Vector3 toUnit = _ai.transform.position - target.Position;
            if (!Physics.Raycast(target.Position + Vector3.up,
                                  toUnit.normalized, toUnit.magnitude - 0.3f))
                return false; // target can see us -- cover compromised

            return true;
        }

        private bool DeterminePeekSide(Vector3 targetPos)
        {
            if (_currentCover == null) return true;
            Vector3 toTarget = (targetPos - _currentCover.transform.position).normalized;
            float dot = Vector3.Dot(_currentCover.transform.right, toTarget);
            return dot < 0f; // true = peek left
        }

        private void ReleaseCover()
        {
            if (_currentCover != null)
            {
                _currentCover.Release(_ai);
                _currentCover = null;
            }
            _ai.CombatRestoreRotation();
        }

        /// <summary>Play a CombatAnimTrigger clip. Picks randomly if multiple assigned.</summary>
        public void PlayCombatAnim(CombatAnimTrigger trigger)
        {
            if (_ai == null) return;
            string clip = GetClip(trigger);
            if (string.IsNullOrEmpty(clip)) return;
            try { _ai.animator.CrossFade(clip, animTransitionDuration); } catch { }
        }

        /// <summary>Play a Custom combat anim by name.</summary>
        public void PlayCombatAnim(string customName)
        {
            if (_ai == null) return;
            for (int i = 0; i < animSlots.Count; i++)
            {
                var slot = animSlots[i];
                if (slot.trigger == CombatAnimTrigger.Custom
                 && slot.customName == customName)
                {
                    string clip = slot.Pick();
                    if (!string.IsNullOrEmpty(clip))
                        try { _ai.animator.CrossFade(clip, animTransitionDuration); } catch { }
                    return;
                }
            }
        }

        public string GetClip(CombatAnimTrigger trigger)
        {
            for (int i = 0; i < animSlots.Count; i++)
                if (animSlots[i].trigger == trigger)
                    return animSlots[i].Pick();
            return null;
        }

        public void EnsureDefaultSlots()
        {
            var defaults = new[]
            {
                CombatAnimTrigger.MoveToCover, CombatAnimTrigger.TakeCover,
                CombatAnimTrigger.CoverIdle,   CombatAnimTrigger.Reposition,
                CombatAnimTrigger.PeekLeft,    CombatAnimTrigger.PeekRight,
                CombatAnimTrigger.CoverFire,   CombatAnimTrigger.Suppressing,
                CombatAnimTrigger.HitReaction, CombatAnimTrigger.Reload,
                CombatAnimTrigger.ThrowGrenade,CombatAnimTrigger.GoProne,
                CombatAnimTrigger.ProneFire,   CombatAnimTrigger.GetUp,
                CombatAnimTrigger.Vault,       CombatAnimTrigger.Advance,
                CombatAnimTrigger.StandingFire,CombatAnimTrigger.KneelingFire,
            };
            foreach (var t in defaults)
            {
                bool found = false;
                for (int i = 0; i < animSlots.Count; i++)
                    if (animSlots[i].trigger == t) { found = true; break; }
                if (!found)
                    animSlots.Add(new CombatAnimSlot
                    { trigger = t, clips = new List<string>() });
            }
        }

        private void Reset() => EnsureDefaultSlots();

        private void PlayAnim(CombatAnimTrigger trigger)
            => PlayCombatAnim(trigger);

        // Legacy string overload -- kept for compatibility
        private void PlayAnim(string name) { }
    }
}