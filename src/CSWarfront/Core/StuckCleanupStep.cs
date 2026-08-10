namespace CSWarfront.Core
{
    /// <summary>
    /// Task98 (playtest feedback): automatic despawn of units stuck and unable to move at shorelines,
    /// dead ends, etc.
    ///
    /// Definition of "stuck": a unit in State==Moving (it wants to move) that has covered less than
    /// ProgressFraction of the distance its own speed should allow from its anchor position (StuckAnchor)
    /// for DespawnAfterHours. (Task98 addendum: this was originally a fixed 20m, but infantry advance
    /// only ~1.0 m/game-hour, so a normally marching squad covered 12m &lt; 20m in 12h and was despawned
    /// by mistake — the threshold must be speed-proportional. The MinProgressDistance cap remains as the
    /// false-positive margin for fast units.)
    /// Idle/Engaging/Dead are exempt (standing still is their normal condition; troops waiting at their
    /// own base or standing ground while fighting are not Moving, so the timer never even runs).
    ///
    /// Exception: units within OwnBaseExemptRadius of a friendly base are never removed (user request:
    /// "units stopped at my own bases are different" — assets near a base are preserved even if they are
    /// somehow marking time). The timer resets so re-evaluation starts over.
    ///
    /// The despawn is silent, no explosion (just transition to Dead without queuing a KillEvent) —
    /// avoiding effects that could be mistaken for combat losses; things simply get "quietly tidied up".
    /// MilitaryManagerSimTick's per-tick dead-unit sweep removes the Dead unit from the list.
    /// </summary>
    public static class StuckCleanupStep
    {
        /// <summary>Despawn after being unable to move for this long (in-game hours).</summary>
        public const float DespawnAfterHours = 12f;

        /// <summary>Speed-proportional factor of the progress threshold: "covered less than 25% of the
        /// distance the unit's own speed should allow" = stuck. Enough slack that the diagonal component
        /// of wall-following detours still counts as progress.</summary>
        public const float ProgressFraction = 0.25f;

        /// <summary>Cap on the progress threshold (horizontal meters). For fast units (tanks, ships,
        /// aircraft) the speed-proportional value would reach hundreds of meters and even back-and-forth
        /// at a wall would satisfy it, so it stays capped at the traditional fixed value.</summary>
        public const float MinProgressDistance = 20f;

        /// <summary>Units within this distance of a friendly base are exempt from despawning.</summary>
        public const float OwnBaseExemptRadius = 200f;

        /// <summary>Advances the stuck check by one tick. Returns the number of despawned units (for
        /// logging).</summary>
        public static int Advance(WarState state, float dt)
        {
            int despawned = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.IsCarried) { u.StuckAnchor = null; u.StuckHours = 0f; continue; } // Task101: carried units are exempt

                if (u.State != UnitState.Moving)
                {
                    u.StuckAnchor = null;
                    u.StuckHours = 0f;
                    continue;
                }

                // Task120: a unit deliberately standing at a cover/fortification position is not stuck —
                // it is doing exactly what CoverSeekStep/FortSeekStep told it to. It stays State==Moving
                // (the enemy that pinned it may be out of weapons range, so CombatStep never flips it to
                // Engaging), and the old code silently despawned such units after DespawnAfterHours —
                // the playtest report "attackers dig in near the objective and then vanish without ever
                // capturing". The holds themselves are time-boxed (MovementStep.MaxCoverHoldHours /
                // FortSeekStep.MaxFortHoldHours), so exempting them cannot resurrect the permanent-stall
                // case this cleanup exists for.
                if (u.CoverHold && u.CoverDestination.HasValue)
                {
                    u.StuckAnchor = null;
                    u.StuckHours = 0f;
                    continue;
                }

                if (!u.StuckAnchor.HasValue ||
                    u.Position.HorizontalDistanceTo(u.StuckAnchor.Value) >= ProgressThresholdFor(state, u))
                {
                    u.StuckAnchor = u.Position;
                    u.StuckHours = 0f;
                    continue;
                }

                u.StuckHours += dt;
                if (u.StuckHours < DespawnAfterHours) continue;

                if (IsNearOwnBase(state, u))
                {
                    u.StuckHours = 0f; // preserved near own bases (see the class comment); re-evaluation starts over
                    continue;
                }

                // Silent despawn, no explosion (no KillEvent queued; CombatStep's death pass checks
                // State==Dead and does not double-process).
                u.State = UnitState.Dead;
                u.CurrentHP = 0f;
                despawned++;
            }
            return despawned;
        }

        /// <summary>This unit's progress threshold: min(speed × DespawnAfterHours × ProgressFraction,
        /// MinProgressDistance). About 3m for infantry (~1.0 m/game-hour); the 20m cap for tanks and up.
        /// The defensive unresolvable-type case returns 0 (= always counts as progressing; errs on the
        /// side of not despawning).</summary>
        private static float ProgressThresholdFor(WarState state, UnitInstance u)
        {
            UnitType type = state.Types.Get(u.TypeKey);
            if (type == null) return 0f;
            float threshold = type.Speed * DespawnAfterHours * ProgressFraction;
            return threshold < MinProgressDistance ? threshold : MinProgressDistance;
        }

        private static bool IsNearOwnBase(WarState state, UnitInstance u)
        {
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (u.Position.HorizontalDistanceTo(mb.Position) <= OwnBaseExemptRadius) return true;
            }
            return false;
        }
    }
}
