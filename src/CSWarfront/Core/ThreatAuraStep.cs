namespace CSWarfront.Core
{
    /// <summary>
    /// Proximity aura damage from threats (Godzilla/aliens) (Task65, user request "units should get worn
    /// down just by being nearby"). Runs right after ThreatCombatStep in MilitaryManager.OnSimTick.
    ///
    /// Differences from ThreatCombatStep (a deliberate asymmetric design):
    ///  - ThreatCombatStep is the unit → threat direction: only units of factions whose
    ///    WarState.ThreatRelations is Hostile/Nemesis deal damage (individually configurable in Options).
    ///  - This step is the reverse, threat → unit, and never consults ThreatRelations. A rampaging giant
    ///    does not discriminate by standing (allied/neutral/hostile) — it hurts every nearby unit equally
    ///    (a unit of a Neutral/Allied faction standing unharmed right beside the threat would be absurd).
    ///  - Flat damage (dt-proportional) that never consults armor (UnitType.Armor). ThreatArmor is only
    ///    meaningful in the unit → threat direction and is no defense in this one (simplicity first).
    ///
    /// Death resolution / KillEvent emission runs independently here in exactly the same shape as
    /// CombatStep.Advance's second pass (the death loop). CombatStep/BaseCombatStep/ThreatCombatStep each
    /// already finalize the deaths from their own damage inside their own Advance, so this step never
    /// double-queues a KillEvent within a tick (units already transitioned to Dead by other steps are
    /// skipped naturally by the State != Dead condition).
    /// </summary>
    public static class ThreatAuraStep
    {
        /// <summary>Godzilla's (Kaiju) aura damage (per in-game hour, armor-ignoring).
        /// A T1 tank (HP 140, LandUnitRoster) dies to the aura alone in 140/90 ≈ 1.6 hours; a T5 tank
        /// (HP 336, TierScaling.Hp(140,5)=140*2.4) in 336/90 ≈ 3.7 hours. The ~50-strong T5 tank force
        /// recommended against Godzilla is exposed to this aura throughout the ThreatCombatStep
        /// engagement (hours to a dozen hours), so the value is tuned for continuous attrition that
        /// rotates the force every few hours = "an expensive campaign" (see the worked examples in the
        /// task-65 report).</summary>
        public const float GodzillaAuraDamage = 90f;

        /// <summary>The alien's (Alien) aura damage (per in-game hour, armor-ignoring). Slightly below
        /// GodzillaAuraDamage, matching its positioning as the smaller threat.</summary>
        public const float AlienAuraDamage = 50f;

        /// <summary>Extra reach of the aura added to the threat's hit radius (ExternalThreat.Radius).
        /// The counterpart of ThreatCombatStep.Advance's "range + Radius" effective range: "near" a giant
        /// threat expressed as a simple constant margin.</summary>
        public const float AuraMargin = 60f;

        public static void Advance(WarState state, float dt)
        {
            for (int t = 0; t < state.Threats.Count; t++)
            {
                var threat = state.Threats[t];
                if (threat.IsDefeated) continue; // defeated threats no longer emit an aura

                float dmgPerHour = DamagePerHourFor(threat.Kind);
                if (dmgPerHour <= 0f) continue;
                float effectiveRadius = threat.Radius + AuraMargin;

                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (!u.IsAlive) continue;
                    if (u.Position.HorizontalDistanceTo(threat.Position) > effectiveRadius) continue;

                    // Flat damage ignoring armor and faction/ThreatRelations (see the class comment).
                    u.CurrentHP -= dmgPerHour * dt;
                }
            }

            // Death resolution (the same pattern as CombatStep.Advance's death pass). Units already
            // transitioned to Dead by other steps are skipped naturally by the State != Dead condition,
            // so no KillEvent is double-queued.
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

        private static float DamagePerHourFor(ThreatKind kind)
        {
            switch (kind)
            {
                case ThreatKind.Kaiju: return GodzillaAuraDamage;
                case ThreatKind.Alien: return AlienAuraDamage;
                default: return 0f;
            }
        }
    }
}
