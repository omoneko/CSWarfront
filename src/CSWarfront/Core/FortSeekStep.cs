namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: the infantry's fortification-seeking AI (design §1.4, user request "infantry should
    /// actively head for trenches and bunkers near the enemy"). Infantry-class units
    /// (AiControlled/FreeAdvance) with a hostile unit inside EnemyRadius are given, as their stand, the
    /// trench/bunker within SeekRadius that lies "closest to the enemy".
    ///
    /// The implementation reuses the same CoverDestination/CoverHold fields as CoverSeekStep's
    /// cover movement, and runs **after CoverSeekStep** to overwrite it (fortifications always beat
    /// building shadows). A stateless design re-derived deterministically every tick; keeping
    /// CoverHoldTimer at 0 also dodges MovementStep's hold cap = the unit stays entrenched as long as
    /// enemies remain. Once the enemy is gone it does nothing = the unit naturally reverts to regular
    /// cover/advance.
    ///
    /// Eligible fortifications: friendly-owned Bunkers (defunct = ownerless ones count too, as
    /// terrain) and Trenches (any owner — but never one already held by enemy infantry).
    /// </summary>
    public static class FortSeekStep
    {
        /// <summary>Seek a fortification when a hostile unit is within this distance.</summary>
        public const float EnemyRadius = 600f;

        /// <summary>The fortification search radius.</summary>
        public const float SeekRadius = 300f;

        /// <summary>Task120 (playtest report "infantry pile into trenches and get stuck; attackers never
        /// capture and eventually vanish"): the maximum time a unit may stay entrenched. The original
        /// implementation re-zeroed CoverHoldTimer every tick, which permanently defeated
        /// MovementStep.MaxCoverHoldHours — units pinned themselves to a trench forever, never resumed the
        /// assault, and (being State==Moving yet motionless) were eventually despawned by
        /// StuckCleanupStep. Entrenching is now time-boxed.</summary>
        public const float MaxFortHoldHours = 4f;

        /// <summary>Task120: after a hold is released, this long must pass before the unit may entrench
        /// again — otherwise the very next tick would re-pin it to the same fortification.</summary>
        public const float ReseekCooldownHours = 8f;

        /// <summary>Task120: a unit this close to its objective (OrderTargetPos — the base it is assaulting
        /// or capturing) never diverts to a fortification. Taking the objective always outranks digging
        /// in nearby.</summary>
        public const float ObjectiveLockRadius = 200f;

        public static void Advance(WarState state, float dt)
        {
            state.UnitGrid.Build(state.Units);

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;
                if (type.Category != UnitCategory.Infantry && type.Category != UnitCategory.MechInfantry) continue;

                // Task120: run down the re-seek cooldown; while it lasts the unit advances normally.
                if (u.FortSeekCooldown > 0f)
                {
                    u.FortSeekCooldown -= dt;
                    if (u.FortSeekCooldown > 0f) continue;
                    u.FortSeekCooldown = 0f;
                }

                // Task120: close to the objective, press the attack instead of digging in.
                if (u.OrderTargetPos.HasValue &&
                    u.Position.HorizontalDistanceTo(u.OrderTargetPos.Value) <= ObjectiveLockRadius)
                {
                    u.FortHoldTimer = 0f;
                    continue;
                }

                UnitInstance enemy = TargetSearch.FindNearestHostile(u, state.UnitGrid, state.Relations,
                    EnemyRadius, DomainMask.All, state.Types);
                if (enemy == null) { u.FortHoldTimer = 0f; continue; } // no enemy nearby: regular cover/advance

                MilitaryBase fort = FindBestFort(state, u, enemy.Position);
                if (fort == null) { u.FortHoldTimer = 0f; continue; }

                // Task120: only count time actually spent entrenched (arrived), not the approach march.
                bool arrived = u.Position.HorizontalDistanceTo(fort.Position) <= MovementStep.CoverArrivalDistance;
                if (arrived)
                {
                    u.FortHoldTimer += dt;
                    if (u.FortHoldTimer > MaxFortHoldHours)
                    {
                        // Time boxed out: let go and resume the advance (the objective matters more).
                        u.FortHoldTimer = 0f;
                        u.FortSeekCooldown = ReseekCooldownHours;
                        u.CoverDestination = null;
                        u.CoverHold = false;
                        u.CoverHoldTimer = 0f;
                        continue;
                    }
                }

                // Head for / hold the fortification (overwriting CoverSeekStep's decision, see the class comment).
                u.CoverDestination = fort.Position;
                u.CoverHold = true;
                u.CoverHoldTimer = 0f; // the fort hold has its own cap (MaxFortHoldHours) instead
            }
        }

        /// <summary>The usable fortification within SeekRadius closest to the enemy position. Null if
        /// none.</summary>
        private static MilitaryBase FindBestFort(WarState state, UnitInstance u, WorldPos enemyPos)
        {
            MilitaryBase best = null;
            float bestEnemyDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                float radius;
                if (mb.Type == BaseType.Trench) radius = FortDefenseBonus.TrenchRadius;
                else if (mb.Type == BaseType.Bunker) radius = FortDefenseBonus.BunkerRadius;
                else continue;

                // Bunkers must be friendly-owned (or defunct = neutral). Never charge into a working enemy bunker.
                if (mb.Type == BaseType.Bunker && mb.OwnerFactionId != null &&
                    mb.OwnerFactionId.Value != u.FactionId) continue;

                if (u.Position.HorizontalDistanceTo(mb.Position) > SeekRadius) continue;
                if (mb.Type == BaseType.Trench && IsHeldByEnemyInfantry(state, mb, u.FactionId, radius)) continue;

                float d = enemyPos.HorizontalDistanceTo(mb.Position);
                if (d < bestEnemyDist) { bestEnemyDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>Whether enemy-faction infantry already sits on this trench (never head for a
        /// captured trench).</summary>
        private static bool IsHeldByEnemyInfantry(WarState state, MilitaryBase trench, byte factionId, float radius)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried) continue;
                if (!state.Relations.Get(factionId, o.FactionId).IsHostile()) continue;
                UnitType t = state.Types.Get(o.TypeKey);
                if (t == null || (t.Category != UnitCategory.Infantry && t.Category != UnitCategory.MechInfantry)) continue;
                if (trench.Position.HorizontalDistanceTo(o.Position) <= radius) return true;
            }
            return false;
        }
    }
}
