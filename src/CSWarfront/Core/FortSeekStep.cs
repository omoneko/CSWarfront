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

                UnitInstance enemy = TargetSearch.FindNearestHostile(u, state.UnitGrid, state.Relations,
                    EnemyRadius, DomainMask.All, state.Types);
                if (enemy == null) continue; // no enemy nearby: stay with regular cover/advance

                MilitaryBase fort = FindBestFort(state, u, enemy.Position);
                if (fort == null) continue;

                // Head for / hold the fortification (overwriting CoverSeekStep's decision, see the class comment).
                u.CoverDestination = fort.Position;
                u.CoverHold = true;
                u.CoverHoldTimer = 0f; // keeps the hold cap (MovementStep.MaxCoverHoldHours) permanently defeated
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
