namespace CSWarfront.Core
{
    /// <summary>The engagement tick (pure logic). Finds enemies in range and applies DamagePerHit each tick.</summary>
    public static class CombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            // Task97: rebuild the spatial grid from this tick's unit positions to avoid the O(N²)
            // brute-force target search (O(N); results are exactly identical to the brute-force version —
            // see UnitSpatialGrid).
            state.UnitGrid.Build(state.Units);

            // 1) Target selection and firing (the simple per-encounter application — damage is not batched
            //    at the end)
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                // Task79: suicide drones (UnitCategoryFlags.IsKamikaze) never enter the normal firing
                // pipeline (no ShotEvents, no dt-scaled damage). Target locking, the dive and the ramming
                // detonation are handled entirely by KamikazeStep (in concert with MovementStep). If this
                // unit's CurrentHP hits 0 from KamikazeStep's detonation, the second pass below (death
                // derives from CurrentHP<=0 as the single source of truth) picks it up as-is.
                if (type.Category.IsKamikaze()) continue;

                // Task99/101: unarmed units (supply trucks / transport helicopters / military trains) and
                // carried units never enter the firing pipeline.
                if (type.Category == UnitCategory.SupplyTruck
                    || type.Category == UnitCategory.TransportHelicopter
                    || type.Category == UnitCategory.MilitaryTrain) continue;
                if (self.IsCarried) continue;

                // Task99: out of ammo (Ammo<=0) only stops firing — drop the target and return to
                // non-engaged (moving and capturing still work). Once resupplied
                // (ResupplyStep/SupplyTruckStep) the unit re-enters combat normally from the next tick.
                // Invaders and non-ammo-bound categories always have HasAmmo true.
                if (!AmmoRules.HasAmmo(self, type))
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }

                // Target selection remains the simple "nearest hostile in range" (TargetSearch). A smarter
                // AI that prefers favorable matchups (CombatMatchup) is future work.
                // Task61: type.CanTargetDomains applies the domain filter (land categories other than
                // AntiAir never target aircraft, ships never target aircraft, aircraft can target every
                // domain, etc.).
                var target = TargetSearch.FindNearestHostile(self, state.UnitGrid, state.Relations, type.Range,
                    type.CanTargetDomains, state.Types);
                if (target == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }
                self.State = UnitState.Engaging;
                self.TargetId = target.InstanceId;
                var targetType = state.Types.Get(target.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                float matchup = targetType != null
                    ? CombatMatchup.Multiplier(type.Category, targetType.Category)
                    : 1.0f;

                // Task90 (user request): AA fire against aircraft resolves as discrete per-shot hit rolls
                // (machine gun against suicide drones, SAMs against fighters/bombers; tier-based hit
                // chances with real misses). Neither the expected-value continuous damage nor the
                // FireEffects throttling applies (see AntiAirCombat).
                if (type.Category == UnitCategory.AntiAir && targetType != null && targetType.Domain == Domain.Air)
                {
                    AdvanceAntiAirShot(state, self, type, target, targetType, targetArmor, matchup, dt);
                    continue;
                }
                // Attack is damage per in-game hour. The damage actually applied scales with elapsed
                // in-game time (dt), the category matchup multiplier (CombatMatchup, Task29) and accuracy
                // (CombatSynergy.AccuracyFor, Task38). Accuracy applies as an expected-value damage
                // multiplier rather than a random roll (preserving deterministic simulation).
                float accuracy = CombatSynergy.AccuracyFor(state, self, type);
                // Task86: air units spend less time in range due to pass movement; AirCombat.DamageMultiplier compensates.
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * dt * matchup * accuracy
                    * AirCombat.DamageMultiplier(type);
                dmg *= FortDefenseBonus.Multiplier(state, target, targetType); // Task101: infantry on trenches/bunkers take less
                // Kill reward (Task35): only at the instant this damage first drops HP to 0 or below does
                // the attacker's (self's) faction receive Research.KillReward. The wasAlive flag decides
                // "was this damage the killing blow", so multiple hits on the same victim within one tick
                // cannot double-award.
                bool wasAlive = target.CurrentHP > 0f;
                target.CurrentHP -= dmg;
                if (wasAlive && target.CurrentHP <= 0f)
                {
                    AwardKillReward(state, self.FactionId, targetType);
                    AmmoRules.RewardInvaderKill(self, type); // Task100: Invader scavenging (ammo from kills)
                }

                // Task54: report the hit location as a "combat zone" (for civilian traffic avoidance).
                // Nearby reports are merged inside CombatZoneTracker, so no throttling is needed here.
                state.CombatZones.ReportCombat(target.Position);

                // Muzzle-effect throttling (Task42; consolidated into FireEffects.EmitThrottled in Task58):
                // FireCooldown is decremented by dt only on ticks that actually dealt damage. Only at the
                // moment it reaches 0 is a single ShotEvent queued, then it resets to FireIntervalHours
                // (no RNG, deterministic).
                FireEffects.EmitThrottled(state, self, type, target.Position, target.InstanceId, dt);

                // Task99: ammo is consumed only on firing ticks (continuous consumption matching the
                // continuous damage model).
                AmmoRules.ConsumeFire(self, type, dt);
            }
            // 2) Death resolution
            // Task51: this is the exact moment a unit transitions to Dead, so exactly one KillEvent is
            // queued as the kill-sound trigger (the State != Dead condition prevents double-queuing).
            // Task53: the KillEvent carries the victim's UnitCategory (for omitting the kill sound for
            // infantry). In the defensive case of an unresolvable UnitType, default(UnitCategory) is used
            // (Tank=0 — the side that does play the sound).
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.State = UnitState.Dead;
                    var uType = state.Types.Get(u.TypeKey);
                    var category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }
        }

        /// <summary>Task90: AA-vs-air discrete firing. FireCooldown is consumed by dt; the moment it
        /// reaches 0 one round is fired: AntiAirCombat.RollHit (deterministic hash, tier-based hit chance)
        /// rolls hit/miss, and only a hit applies the lump damage "Attack × FireIntervalHours × matchup"
        /// (= one firing interval's worth of the continuous model in one packet; expected DPS = hit chance
        /// × Attack × matchup). A miss deals no damage and only queues a Missed=true ShotEvent (for the
        /// Game layer's flare/evasion display). ShotKind is Gunfire for the machine gun and SamMissile for
        /// the SAM (the UnitType's ShotKind is not used — the weapon is chosen by target kind).</summary>
        private static void AdvanceAntiAirShot(WarState state, UnitInstance self, UnitType type,
            UnitInstance target, UnitType targetType, float targetArmor, float matchup, float dt)
        {
            self.FireCooldown -= dt;
            if (self.FireCooldown > 0f) return;
            self.FireCooldown = type.FireIntervalHours;

            bool usesMissile = AntiAirCombat.UsesMissileAgainst(targetType.Category);
            float chance = AntiAirCombat.HitChanceFor(type.Tier, targetType.Category);
            bool hit = AntiAirCombat.RollHit(self.InstanceId, target.InstanceId, state.TickCounter, chance);

            if (hit)
            {
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * type.FireIntervalHours * matchup;
                bool wasAlive = target.CurrentHP > 0f;
                target.CurrentHP -= dmg;
                if (wasAlive && target.CurrentHP <= 0f)
                {
                    AwardKillReward(state, self.FactionId, targetType);
                    AmmoRules.RewardInvaderKill(self, type); // Task100: Invader scavenging (ammo from kills)
                }
                state.CombatZones.ReportCombat(target.Position);
            }

            state.AddShot(new ShotEvent(self.Position, target.Position,
                usesMissile ? ShotKind.SamMissile : ShotKind.Gunfire,
                self.FactionId, self.InstanceId, target.InstanceId, type.Category, !hit));

            // Task99: each round consumes one firing interval's worth of ammo (misses consume too; the
            // expected consumption rate matches the continuous model's dt drain, since firing is discrete
            // at one round per FireIntervalHours).
            AmmoRules.ConsumeFire(self, type, type.FireIntervalHours);
        }

        /// <summary>Credits the kill reward (Task35) to the killerFactionId faction. Does nothing when the
        /// victimType/faction cannot be resolved (defensive: never throw on inconsistent state).
        /// internal (Task79): KamikazeStep's ramming detonation reuses the same kill-reward logic, so the
        /// access was relaxed from private to let it call in directly instead of duplicating the logic
        /// (same assembly only; no growth of the public API).</summary>
        internal static void AwardKillReward(WarState state, byte killerFactionId, UnitType victimType)
        {
            if (victimType == null) return;
            Faction killer = state.FindFaction(killerFactionId);
            if (killer == null) return;
            killer.AddResearchPoints(Research.KillReward(victimType));
        }
    }
}
