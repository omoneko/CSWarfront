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
        // --- Task130 (playtest: "the AT pillbox is too strong"): fortification ranges are derived from
        // --- the weapon class each emplacement represents, instead of being picked in isolation.
        // ---
        // --- The rule: a prepared position outranges the mobile version of the same weapon by a
        // --- modest margin (better sightlines, no need to move), never by a multiple. Unit ranges grow
        // --- +10%/tier, so tier 5 is 1.4x the tier 1 figures below:
        // ---     infantry 40 (T5 56) | tank 75 (T5 105) | artillery 145 (T5 203) | AA 120 (T5 168)
        // --- Before this pass the AT pillbox reached 200 against a tank's 60-84: tanks were shot at
        // --- from outside any distance they could ever answer from, which is what made it feel
        // --- unbeatable rather than merely strong. Each emplacement now engages at a distance where
        // --- the target can fight back.
        public const float BunkerAttack = 54f;        // infantry T1 (18) × 3
        /// <summary>Infantry-class engagement: a little beyond a tier-5 rifle squad (56).</summary>
        public const float BunkerRange = 60f;
        public const float BunkerAccuracy = 0.75f;    // same as infantry
        public const float BunkerAmmoHours = 12f;     // same as infantry
        public const float BunkerFireIntervalHours = 0.3f;

        public const float ArtAttack = 55f;           // artillery T1
        /// <summary>Outranges every artillery unit including tier 5, which is what Task111's Workshop
        /// request asked for — at the original 120 it was routinely outranged and never fired. Task130
        /// pulled it back from 250, where it reached half again as far as any gun in the game; Task131
        /// then moved it with the SPG's own reach (120 -> 145, so tier 5 is 203).</summary>
        public const float ArtRange = 215f;
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

        // --- Task117 (Workshop request): AT pillbox — anti-armor direct fire. "Artillery with less
        // --- fire rate, higher damage, a little less range": heavy single shots, building-blocked
        // --- line of sight like the bunker, and the Tank matchup profile (strong vs armor). ---
        /// <summary>Task130: 110 -> 80. Still the hardest-hitting emplacement (a tank musters 42), but
        /// it now lands those shots at a distance where the tank can shoot back, so the exchange is a
        /// fight rather than a firing-squad.</summary>
        public const float AtAttack = 80f;
        /// <summary>Task130: 200 -> 100. Outranges a tier-5 tank by a useful margin — the reason to
        /// build one — without the old three-to-one advantage over the tanks it is meant to stop.
        /// Task131: 100 -> 130, alongside the direct-fire vehicles' own reach going up (tank 60 -> 75,
        /// so tier 5 is now 105). Gunfights happen a little further out; the margin is unchanged.</summary>
        public const float AtRange = 130f;
        public const float AtAccuracy = 0.55f;
        public const float AtAmmoHours = 24f;
        public const float AtFireIntervalHours = 3f;  // slow, weighty muzzle reports

        // --- Task117: AA position — static anti-air. Reuses the AntiAir category's discrete
        // --- per-shot scheme (AntiAirCombat): every AaFireIntervalHours one shot at the nearest
        // --- hostile aircraft rolls hit/miss deterministically; only hits deal the lump damage. ---
        public const float AaAttack = 45f;
        /// <summary>Task130: 250 -> 180, matching the artillery position and staying above a tier-5
        /// SPAAG (168).</summary>
        public const float AaRange = 180f;
        /// <summary>The AntiAir tier whose hit-chance table the emplacement borrows
        /// (AntiAirCombat.HitChanceFor).</summary>
        public const byte AaTier = 3;
        public const float AaAmmoHours = 24f;
        public const float AaFireIntervalHours = 0.8f;

        public static void Advance(WarState state, float dt)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.OwnerFactionId == null || b.CurrentHP <= 0f) continue;

                // Task117: the two new emplacements have their own firing models.
                if (b.Type == BaseType.AtPillbox)
                {
                    if (b.FortAmmo > 0f) AdvanceAtPillbox(state, b, dt);
                    continue;
                }
                if (b.Type == BaseType.AaPosition)
                {
                    if (b.FortAmmo > 0f) AdvanceAaPosition(state, b, dt);
                    continue;
                }

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

        /// <summary>Task117: AT pillbox — the bunker's flow (nearest hostile land unit, sight line
        /// blocked by buildings) with the Tank matchup profile, so the continuous expected-value
        /// damage lands hard on armor. The slow AtFireIntervalHours only paces the visible muzzle
        /// shots (ShotEvents), exactly like the other forts.</summary>
        private static void AdvanceAtPillbox(WarState state, MilitaryBase b, float dt)
        {
            UnitInstance target = FindNearestHostileLand(state, b, AtRange);
            if (target == null) return;
            if (state.Cover != null &&
                state.Cover.BlocksLine(b.Position, target.Position, SelfLosIgnoreRadius))
                return; // direct fire: no shooting through buildings

            ApplyFortDamage(state, b, target, AtAttack, AtAccuracy, UnitCategory.Tank, dt);

            b.FortAmmo -= dt / AtAmmoHours;
            if (b.FortAmmo < 0f) b.FortAmmo = 0f;

            b.FortFireCooldown -= dt;
            if (b.FortFireCooldown <= 0f)
            {
                b.FortFireCooldown = AtFireIntervalHours;
                state.AddShot(new ShotEvent(b.Position, target.Position, ShotKind.DirectFire,
                    b.OwnerFactionId.Value, 0, target.InstanceId, UnitCategory.Tank, false));
            }
        }

        /// <summary>Task117: AA position — discrete per-shot anti-air, borrowing the AntiAir
        /// category's scheme (AntiAirCombat, AaTier hit table): every AaFireIntervalHours one shot at
        /// the nearest hostile aircraft rolls hit/miss deterministically; a hit deals the lump
        /// "Attack × interval × matchup", a miss deals nothing and only queues the Missed=true
        /// ShotEvent (SAM-veering-off display). Ammo drains per shot.</summary>
        private static void AdvanceAaPosition(WarState state, MilitaryBase b, float dt)
        {
            b.FortFireCooldown -= dt;
            if (b.FortFireCooldown > 0f) return;

            UnitInstance target = FindNearestHostileAir(state, b, AaRange);
            if (target == null)
            {
                b.FortFireCooldown = 0f; // stay ready while no target is in range
                return;
            }
            b.FortFireCooldown = AaFireIntervalHours;

            UnitType targetType = state.Types.Get(target.TypeKey);
            UnitCategory targetCategory = targetType != null ? targetType.Category : default(UnitCategory);
            float chance = AntiAirCombat.HitChanceFor(AaTier, targetCategory);
            bool hit = AntiAirCombat.RollHit(b.BaseId, target.InstanceId, state.TickCounter, chance);
            if (hit)
            {
                float armor = targetType != null ? targetType.Armor : 0f;
                float matchup = targetType != null
                    ? CombatMatchup.Multiplier(UnitCategory.AntiAir, targetType.Category) : 1f;
                target.CurrentHP -= CombatMath.DamagePerHit(AaAttack, armor) * AaFireIntervalHours * matchup;
                state.CombatZones.ReportCombat(target.Position);
            }

            b.FortAmmo -= AaFireIntervalHours / AaAmmoHours;
            if (b.FortAmmo < 0f) b.FortAmmo = 0f;

            bool usesMissile = AntiAirCombat.UsesMissileAgainst(targetCategory);
            state.AddShot(new ShotEvent(b.Position, target.Position,
                usesMissile ? ShotKind.SamMissile : ShotKind.Gunfire,
                b.OwnerFactionId.Value, 0, target.InstanceId, UnitCategory.AntiAir, !hit));
        }

        /// <summary>The nearest hostile aircraft (Domain.Air — helicopters included) in range.
        /// Carried units are excluded as usual.</summary>
        private static UnitInstance FindNearestHostileAir(WarState state, MilitaryBase b, float range)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried) continue;
                if (!state.Relations.Get(b.OwnerFactionId.Value, u.FactionId).IsHostile()) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Air) continue;
                float d = b.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best;
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
