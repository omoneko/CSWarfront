namespace CSWarfront.Core
{
    /// <summary>
    /// Constants and the damage compensation for air units' engagement pass movement (Task86, user
    /// request "bombers drop and hit-and-run; fighters dogfight in crossing passes without stopping").
    ///
    /// Under the old rules an air unit arriving at its objective hovered in place and kept firing into
    /// range — a "floating gun platform". Under the new rules, while an engagement anchor exists (locked
    /// enemy unit → hostile threat in range → hostile base in range, in that priority;
    /// MovementStepAirPass.ResolveAirCombatAnchor), the unit repeats the racetrack pass:
    ///   approach → at point blank (PassTriggerDistance) arm an egress point (PassEgressDistance) beyond
    ///   along the heading → fly out to the egress point → turn and re-enter.
    /// Damage still applies only "while in range" (CombatStep/BaseCombatStep/ThreatCombatStep), so
    /// bombs/guns land only during the fly-over = hit-and-run / crossing-pass dogfights.
    /// </summary>
    public static class AirCombat
    {
        /// <summary>On the approach leg, the egress point arms once within this distance of the anchor
        /// (= the point-blank distance counting as "passed overhead"). Well below the ranges (fighter
        /// 90 / bomber 70), so the unit always punches through its range before egressing.</summary>
        public const float PassTriggerDistance = 40f;

        /// <summary>Distance to the egress point (this far past the anchor along the heading). Kept well
        /// above the ranges so most of the egress leg is out of range = time unable to fire (the "run"
        /// of hit-and-run).</summary>
        public const float PassEgressDistance = 350f;

        /// <summary>Arrival distance at the egress point. On arrival the egress leg ends and re-entry
        /// starts next tick.</summary>
        public const float PassArrivalDistance = 20f;

        /// <summary>Pass movement cuts in-range time to roughly a quarter (in-range window ≈ 2×range vs
        /// circuit length ≈ 2×PassEgressDistance), so the dt-scaled damage of air units (suicide drones
        /// excluded) is multiplied by this to compensate. Effective DPS lands at ~60–75% of the old
        /// value, preserving the balance "air power is strong but never decisive alone" (a calibration
        /// value subject to playtest tuning).</summary>
        public const float PassDamageCompensation = 3f;

        /// <summary>The multiplier applied to this class's dt-scaled damage. PassDamageCompensation only
        /// for pass-flying air units (Domain=Air, excluding suicide drones — KamikazeStep's single
        /// full-damage ram is exempt); 1 for everything else.</summary>
        public static float DamageMultiplier(UnitType type)
        {
            if (type == null) return 1f;
            // Task101: helicopters hover (no racetrack passes = they stay in range), so no compensation.
            if (TargetingRules.IsHelicopter(type.Category)) return 1f;
            return (type.Domain == Domain.Air && !type.Category.IsKamikaze()) ? PassDamageCompensation : 1f;
        }
    }
}
