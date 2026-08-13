namespace CSWarfront.Core
{
    /// <summary>
    /// Task127 (Workshop request: "is it possible for an infantry unit to garrison a civilian building
    /// when entering a combat zone and exit later after the fight, rather than being on the open
    /// road?"): infantry take up positions against nearby city buildings while fighting, and go back
    /// to advancing once the shooting stops.
    ///
    /// This is not the old building cover that Task104 removed. That was "run building to building
    /// while bounding forward", which sent squads under overpasses and across the map chasing
    /// geometry. A garrison is the opposite: a unit already in contact stops running around, takes the
    /// nearest building to it, and stays there for as long as the fight lasts.
    ///
    ///  - Only infantry classes, only while an enemy is inside EnemyRadius, and only when they are not
    ///    already doing something better with their position: a fortification (FortSeekStep runs after
    ///    this and overwrites — a trench beats a shopfront) or an objective within reach
    ///    (ObjectiveLockRadius, the same rule that stops units digging in next to the base they are
    ///    supposed to be taking).
    ///  - The building is picked by closeness to the unit, not to the enemy: the point is to get off
    ///    the open road right now, not to cross one to reach a better wall.
    ///  - GarrisonRadius is deliberately short. A unit that would have to march to a building is
    ///    better off fighting where it stands.
    ///  - Holding is time-boxed by MaxGarrisonHours exactly like entrenching (Task120), so a garrison
    ///    can never stall an advance, and units in one are exempt from the stall watchdog because they
    ///    are standing still on purpose.
    ///
    /// The defensive benefit and the risk to the building itself are handled elsewhere:
    /// FortDefenseBonus grants the cover bonus, and CombatZones already reports the fighting so the
    /// existing collateral rules can set the place on fire — a garrison makes a building a target, as
    /// the user chose.
    ///
    /// Pure logic: deterministic, no RNG, UnityEngine-free. Runs before FortSeekStep in the tick.
    /// </summary>
    public static class BuildingGarrisonStep
    {
        /// <summary>Garrison only while a hostile unit is this close (the same trigger distance as
        /// FortSeekStep, so infantry react to contact consistently).</summary>
        public const float EnemyRadius = 600f;

        /// <summary>How far a unit will go to reach a building. Short on purpose: this is "step off the
        /// road into that doorway", not a cross-map errand.</summary>
        public const float GarrisonRadius = 90f;

        /// <summary>Maximum time in one building before the unit lets go and resumes its advance.</summary>
        public const float MaxGarrisonHours = 4f;

        /// <summary>Cooldown after a hold is released, so the unit does not re-enter the same doorway on
        /// the very next tick.</summary>
        public const float ReseekCooldownHours = 8f;

        /// <summary>Inside this distance of its objective the unit presses the attack instead of taking
        /// cover (same rule as FortSeekStep).</summary>
        public const float ObjectiveLockRadius = 200f;

        public static void Advance(WarState state, float dt)
        {
            if (state.Cover == null) return;
            state.UnitGrid.Build(state.Units);

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;
                if (type.Category != UnitCategory.Infantry && type.Category != UnitCategory.MechInfantry) continue;

                if (u.GarrisonCooldown > 0f)
                {
                    u.GarrisonCooldown -= dt;
                    if (u.GarrisonCooldown > 0f) continue;
                    u.GarrisonCooldown = 0f;
                }

                // Taking the objective outranks taking cover.
                if (u.OrderTargetPos.HasValue &&
                    u.Position.HorizontalDistanceTo(u.OrderTargetPos.Value) <= ObjectiveLockRadius)
                {
                    u.GarrisonHoldTimer = 0f;
                    continue;
                }

                UnitInstance enemy = TargetSearch.FindNearestHostile(u, state.UnitGrid, state.Relations,
                    EnemyRadius, DomainMask.All, state.Types);
                if (enemy == null)
                {
                    // The fight is over: leave the building and get moving again.
                    u.GarrisonHoldTimer = 0f;
                    continue;
                }

                WorldPos stand;
                if (!TryFindGarrison(state, u, enemy.Position, out stand))
                {
                    u.GarrisonHoldTimer = 0f;
                    continue;
                }

                bool arrived = u.Position.HorizontalDistanceTo(stand) <= MovementStep.CoverArrivalDistance;
                if (arrived)
                {
                    u.GarrisonHoldTimer += dt;
                    if (u.GarrisonHoldTimer > MaxGarrisonHours)
                    {
                        u.GarrisonHoldTimer = 0f;
                        u.GarrisonCooldown = ReseekCooldownHours;
                        u.CoverDestination = null;
                        u.CoverHold = false;
                        u.CoverHoldTimer = 0f;
                        continue;
                    }
                }

                u.CoverDestination = stand;
                u.CoverHold = true;
                u.CoverHoldTimer = 0f; // the garrison hold has its own cap (MaxGarrisonHours)
            }
        }

        /// <summary>The standing position against the building nearest to the unit, on the side away
        /// from the enemy. False when no building is within GarrisonRadius.</summary>
        private static bool TryFindGarrison(WarState state, UnitInstance u, WorldPos enemyPos, out WorldPos stand)
        {
            return state.Cover.TryFindNearestStandingPosition(u.Position, enemyPos, GarrisonRadius, out stand);
        }
    }
}
