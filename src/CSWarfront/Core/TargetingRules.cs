namespace CSWarfront.Core
{
    /// <summary>
    /// The centralized per-category "what may it attack" rules (Task85, user request):
    ///  - Only ground forces can capture enemy bases (grind them to HP 0). Air and naval forces can only
    ///    reduce base HP to 1 (the last point must be taken by land troops = no capture without a ground
    ///    force).
    ///  - Fighters (AirSuperiority) may attack only fighters/bombers (= air units) and KAIJU (external
    ///    threats). Bases are off-limits.
    ///  - Bombers (TacticalBomber) may attack only ground targets (ground units and bases) and KAIJU.
    ///  - Missile destroyers (Destroyer) may attack ground targets, KAIJU and naval forces, but cannot
    ///    capture bases (subject to the HP floor of 1 above).
    ///  - Carriers (Carrier) are dedicated flight platforms (CarrierAirWing) and never attack anything.
    ///
    /// Unit-vs-unit targetability is handled by UnitType.CanTargetDomains (roster-defined, applied by
    /// TargetSearch); this class covers the non-unit targets ("bases", "external threats") and the base
    /// HP floor. Applied in: BaseCombatStep (CanAttackBase/BaseHpFloor), KamikazeStep (BaseHpFloor),
    /// ThreatCombatStep (CanAttackThreat).
    ///
    /// Note: "KAIJU only" is implemented as external threats in general (Godzilla=Kaiju /
    /// tripods=Alien) — no ThreatKind distinction (there is no reason for the asymmetry of a fighter
    /// that can shoot Godzilla but not a tripod).
    /// </summary>
    public static class TargetingRules
    {
        /// <summary>Whether this category may attack bases (MilitaryBase). False for fighters
        /// (air-superiority specialists), carriers (platform specialists), supply trucks (Task99:
        /// unarmed — even with Attack 0, CombatMath.DamagePerHit's guaranteed minimum of 1 would get
        /// through, so they are excluded explicitly here), both helicopters and the military train
        /// (Task101: attack helicopters are ground-unit specialists; transport helicopters/trains are
        /// unarmed).</summary>
        public static bool CanAttackBase(UnitCategory category)
        {
            return category != UnitCategory.AirSuperiority && category != UnitCategory.Carrier
                && category != UnitCategory.SupplyTruck
                && category != UnitCategory.AttackHelicopter && category != UnitCategory.TransportHelicopter
                && category != UnitCategory.MilitaryTrain;
        }

        /// <summary>Task101: whether this category is a helicopter (subject to the anti-helicopter rules).</summary>
        public static bool IsHelicopter(UnitCategory category)
        {
            return category == UnitCategory.AttackHelicopter || category == UnitCategory.TransportHelicopter;
        }

        /// <summary>Task101: only tanks, anti-air and fighters may attack helicopters (user spec; used by
        /// TargetSearch/UnitSpatialGrid when judging helicopter-target candidates. Tanks keep
        /// CanTargetDomains=Land, but this exception lets them target helicopters specifically).</summary>
        public static bool CanTargetHelicopter(UnitCategory attacker)
        {
            return attacker == UnitCategory.Tank || attacker == UnitCategory.AntiAir
                || attacker == UnitCategory.AirSuperiority;
        }

        /// <summary>Base HP floor by attacker domain. Only land can grind to 0 (= capture); air and sea
        /// stop at 1.</summary>
        public static float BaseHpFloor(Domain attackerDomain)
        {
            return attackerDomain == Domain.Land ? 0f : 1f;
        }

        /// <summary>Whether this category may attack external threats (KAIJU/Alien). False for carriers,
        /// supply trucks (Task99: unarmed), both helicopters and the military train (Task101).</summary>
        public static bool CanAttackThreat(UnitCategory category)
        {
            return category != UnitCategory.Carrier && category != UnitCategory.SupplyTruck
                && category != UnitCategory.AttackHelicopter && category != UnitCategory.TransportHelicopter
                && category != UnitCategory.MilitaryTrain;
        }
    }
}
