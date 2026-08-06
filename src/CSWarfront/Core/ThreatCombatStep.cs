namespace CSWarfront.Core
{
    /// <summary>
    /// Combat against threats (ExternalThreat) spawned by other mods (Godzilla Disaster / Alien Invasion)
    /// (pure logic, Task58).
    ///
    /// Design notes:
    ///  - Task59: relations toward threats are configurable per faction via WarState.ThreatRelations
    ///    (faction × ThreatKind, default Hostile). Damage is dealt only for IsHostile()
    ///    (Hostile/Nemesis) pairs; units of factions set to Neutral/Allied do not damage that threat
    ///    (Task58's "always unconditionally hostile toward everyone" survives as the ThreatRelations
    ///    default).
    ///  - This step runs every tick IN ADDITION TO the normal CombatStep/BaseCombatStep. There is no
    ///    fight over target selection = a unit with both a threat and a regular enemy in range attacks
    ///    both simultaneously (a deliberate design favoring simplicity and predictability; see
    ///    ExternalThreatCombatStepTests).
    ///  - Damage from the threat to units is never applied here. The Godzilla/Alien mods already wreck
    ///    the city on their own, so CSWarfront has no need to double-damage units (a deliberate
    ///    asymmetric design).
    /// </summary>
    public static class ThreatCombatStep
    {
        /// <summary>Armor value of threats. Godzilla/aliens are outrageously tough: small arms only get
        /// through at CombatMath.DamagePerHit's guaranteed minimum (1) and are all but neutralized, while
        /// tank/artillery-class attack values (Attack &gt; ThreatArmor) are what first constitutes an
        /// effective hit (see ThreatCombatStepTests).
        ///
        /// Task64 rebalance (old 20 → 45): the old 20 was tuned around tier-1 firepower and was far too
        /// soft against tier-5 armies. Actual math (TierScaling: Attack ×2.6, Accuracy 1.24× base at
        /// tier 5; CombatMath.DamagePerHit = max(1, attack-armor)):
        ///   Tier-5 tank (Attack 40×2.6=104, Accuracy 0.70×1.24=0.868):
        ///     old armor 20 → DamagePerHit(104,20)=84 × 0.868 ≈ 72.9/h.
        ///     A 50-tank force: 50*72.9 ≈ 3645/h — kills the old Godzilla (20000) in ~5.5 hours. It
        ///     melted instantly against tier-5 armies; the direct cause of the "way too weak" feedback.
        ///     new armor 45 → DamagePerHit(104,45)=59 × 0.868 ≈ 51.2/h (about 30% less).
        ///   Tier-5 infantry (Attack 20×2.6=52, Accuracy 0.75×1.24=0.93):
        ///     new armor 45 → DamagePerHit(52,45)=max(1,7)=7 × 0.93 ≈ 6.5/h.
        ///     About 1/8 of a tank (≈51.2/h), so the design intent "small arms are nearly useless" holds
        ///     at tier 5 (not as extreme as the old tier-1 comparison — old 19× — but armor 45 is tuned
        ///     deliberately to shave right at infantry's raw attack of 52, keeping solo infantry threat
        ///     hunting unrealistic).
        /// Derivations of the new HP values (Godzilla 65000 / Alien 26000) and the missile-vs-threat
        /// damage (2000) are in the task-64 report.</summary>
        public const float ThreatArmor = 45f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                // Task79: suicide drones do not pour continuous dt-scaled fire into threats
                // (Godzilla/aliens) either. KamikazeStep locks the target using the same effective range
                // (Range+threat.Radius) and the same ThreatArmor/ThreatRelations rules and lands a single
                // full-damage ram (the "one big hit works well on giants" design: ThreatArmor mostly
                // nullifies small arms while the SuicideDrone's high Attack punches through).
                if (type.Category.IsKamikaze()) continue;

                // Task85: carriers are dedicated flight platforms and do not attack threats either.
                if (!TargetingRules.CanAttackThreat(type.Category)) continue;

                // Task99: out of ammo also stops anti-threat fire (same convention as
                // CombatStep/BaseCombatStep).
                if (!AmmoRules.HasAmmo(self, type)) continue;

                bool firedAtAnyThreat = false;
                for (int j = 0; j < state.Threats.Count; j++)
                {
                    var threat = state.Threats[j];
                    if (threat.IsDefeated) continue; // defeated threats take no further damage (awaiting Game-layer removal)

                    // Task59: no damage unless this threat is hostile (Hostile/Nemesis) to the unit's
                    // faction (configurable to Neutral/Allied in the Options "faction relations" screen).
                    if (!state.ThreatRelations.Get(self.FactionId, threat.Kind).IsHostile()) continue;

                    // Threats are huge, so the effective range is the normal range (unitType.Range) plus
                    // the threat's hit radius (CombatStep/BaseCombatStep ignore target size, but a giant
                    // like Godzilla is perfectly targetable even right at the edge of range).
                    float effectiveRange = type.Range + threat.Radius;
                    if (self.Position.HorizontalDistanceTo(threat.Position) > effectiveRange) continue;

                    // Attack is damage per in-game hour. Accuracy (CombatSynergy.AccuracyFor) applies as
                    // an expected-value damage multiplier (as in CombatStep — no random roll).
                    // CombatMatchup (category matchups) does not apply to threats: they are independent
                    // entities without a UnitCategory, so armor (ThreatArmor) is their only defense.
                    float accuracy = CombatSynergy.AccuracyFor(state, self, type);
                    // Task86: air units spend less time in range due to pass movement; compensated here too.
                    float dmg = CombatMath.DamagePerHit(type.Attack, ThreatArmor) * dt * accuracy
                        * AirCombat.DamageMultiplier(type);
                    threat.CurrentHP -= dmg;
                    if (threat.CurrentHP < 0f) threat.CurrentHP = 0f;

                    // Muzzle effects (Task42/58). The target is a threat, so TargetId=0 as with base
                    // attacks (threats have no logical unit id; the Game layer's existing contract treats
                    // TargetId==0 as "not a unit").
                    FireEffects.EmitThrottled(state, self, type, threat.Position, 0, dt);
                    firedAtAnyThreat = true;
                }

                // Task99: ammo consumption ("one tick's worth, once" — the same convention as
                // BaseCombatStep).
                if (firedAtAnyThreat) AmmoRules.ConsumeFire(self, type, dt);
            }
        }
    }
}
