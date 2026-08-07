using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: fortification auto-fire (design: 2026-08-04-fortifications-heli-rail-design.md §1.1).
    ///
    ///  - Bunker: firepower worth three infantry. Cannot fire when the sight line (fort → target) is
    ///    blocked by city buildings (no shooting through buildings, CoverMap.BlocksLine). Single target
    ///    (the nearest hostile land unit in range).
    ///  - ArtilleryPost: firepower worth one artillery piece. Lobbed fire, so no line-of-sight check.
    ///    Simultaneous damage to every hostile land unit within Splash of the nearest target.
    ///
    /// Common rules: only while operational (has an Owner, HP&gt;0). The fort's own ammunition
    /// (MilitaryBase.FortAmmo) drains only on ticks it fires; going dry merely stops fire (ResupplyStep
    /// refills it inside supply zones). Damage uses the same expected-value scheme as the existing
    /// CombatStep (accuracy × matchup × dt). Death is not resolved in this step — that is left to
    /// CombatStep's second pass (HP&lt;=0 → Dead+KillEvent), the same convention as KamikazeStep.
    /// Invader forces are fired on as normal (the relation table hardcodes them hostile, so it just
    /// works).
    /// </summary>
    public static class FortCombatStep
    {
        public const float BunkerAttack = 54f;        // infantry T1 (18) × 3
        public const float BunkerRange = 120f;
        public const float BunkerAccuracy = 0.75f;    // same as infantry
        public const float BunkerAmmoHours = 12f;     // same as infantry
        public const float BunkerFireIntervalHours = 0.3f;

        public const float ArtAttack = 55f;           // artillery T1
        /// <summary>Task111 (Workshop request): 120 -> 250. A static emplacement should outrange the mobile
        /// artillery it is built from; at 120 it was routinely outranged and never got to fire.</summary>
        public const float ArtRange = 250f;
        public const float ArtSplash = 30f;
        public const float ArtAccuracy = 0.35f;       // same as artillery units
        /// <summary>Task111 (Workshop request "15 shots instead of only 2 before resupply"): 4 -> 24 hours
        /// of firing per full ammo load. Together with the shorter visual fire interval below this yields
        /// about 16 visible shots per resupply.</summary>
        public const float ArtAmmoHours = 24f;
        public const float ArtFireIntervalHours = 1.5f; // Task111: 2.5 -> 1.5 (more visible firing)

        /// <summary>Radius excluding the bunker's own building from the sight-line check (a bit over the
        /// diagonal radius of its 16×16m footprint).</summary>
        public const float SelfLosIgnoreRadius = 14f;

        public static void Advance(WarState state, float dt)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.OwnerFactionId == null || b.CurrentHP <= 0f) continue;
                if (b.Type != BaseType.Bunker && b.Type != BaseType.ArtilleryPost) continue;
                if (b.FortAmmo <= 0f) continue; // dry = stop firing, nothing more

                bool isBunker = b.Type == BaseType.Bunker;
                float range = isBunker ? BunkerRange : ArtRange;

                UnitInstance target = FindNearestHostileLand(state, b, range);
                if (target == null) continue;

                if (isBunker && state.Cover != null &&
                    state.Cover.BlocksLine(b.Position, target.Position, SelfLosIgnoreRadius))
                    continue; // no shooting through buildings: the sight line is blocked

                float ammoHours = isBunker ? BunkerAmmoHours : ArtAmmoHours;
                float attack = isBunker ? BunkerAttack : ArtAttack;
                float accuracy = isBunker ? BunkerAccuracy : ArtAccuracy;
                UnitCategory attackerCategory = isBunker ? UnitCategory.Infantry : UnitCategory.Artillery;

                if (isBunker)
                {
                    ApplyFortDamage(state, b, target, attack, accuracy, attackerCategory, dt);
                }
                else
                {
                    // Artillery post: every hostile land unit within Splash of the target (target included).
                    for (int i = 0; i < state.Units.Count; i++)
                    {
                        UnitInstance u = state.Units[i];
                        if (!IsHostileLand(state, b, u)) continue;
                        if (target.Position.HorizontalDistanceTo(u.Position) > ArtSplash) continue;
                        ApplyFortDamage(state, b, u, attack, accuracy, attackerCategory, dt);
                    }
                }

                // Ammo drain (only on ticks fired, the same convention as units' AmmoRules.ConsumeFire).
                b.FortAmmo -= dt / ammoHours;
                if (b.FortAmmo < 0f) b.FortAmmo = 0f;

                // Muzzle-effect throttling (same idea as the units' FireEffects; forts have no unit id,
                // so events are queued with attackerId=0).
                b.FortFireCooldown -= dt;
                if (b.FortFireCooldown <= 0f)
                {
                    b.FortFireCooldown = isBunker ? BunkerFireIntervalHours : ArtFireIntervalHours;
                    state.AddShot(new ShotEvent(b.Position, target.Position,
                        isBunker ? ShotKind.Gunfire : ShotKind.IndirectFire,
                        b.OwnerFactionId.Value, 0, target.InstanceId, attackerCategory, false));
                }
            }
        }

        private static void ApplyFortDamage(WarState state, MilitaryBase b, UnitInstance target,
            float attack, float accuracy, UnitCategory attackerCategory, float dt)
        {
            UnitType targetType = state.Types.Get(target.TypeKey);
            float armor = targetType != null ? targetType.Armor : 0f;
            float matchup = targetType != null ? CombatMatchup.Multiplier(attackerCategory, targetType.Category) : 1f;
            float dmg = CombatMath.DamagePerHit(attack, armor) * dt * accuracy * matchup;
            dmg *= FortDefenseBonus.Multiplier(state, target, targetType); // infantry in trenches/bunkers take less
            target.CurrentHP -= dmg;
            state.CombatZones.ReportCombat(target.Position);
        }

        private static UnitInstance FindNearestHostileLand(WarState state, MilitaryBase b, float range)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!IsHostileLand(state, b, u)) continue;
                float d = b.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best;
        }

        private static bool IsHostileLand(WarState state, MilitaryBase b, UnitInstance u)
        {
            if (!u.IsAlive || u.IsCarried) return false;
            if (!state.Relations.Get(b.OwnerFactionId.Value, u.FactionId).IsHostile()) return false;
            UnitType t = state.Types.Get(u.TypeKey);
            return t != null && t.Domain == Domain.Land;
        }
    }
}
