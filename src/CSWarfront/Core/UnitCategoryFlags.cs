namespace CSWarfront.Core
{
    /// <summary>
    /// Extensions giving UnitCategory its "fighting style" classification flags (Task79). Rather than
    /// hardcoding string comparisons (TypeKey.Contains etc.) or category enumerations all over the
    /// place, the checks are consolidated here. CombatStep/BaseCombatStep/ThreatCombatStep/
    /// MovementStep/KamikazeStep all classify categories through these helpers, never adding more
    /// direct comparisons against the concrete UnitCategory.SuicideDrone (if ramming-style categories
    /// multiply someday, only the IsKamikaze side needs the one-line fix).
    /// </summary>
    public static class UnitCategoryFlags
    {
        /// <summary>Whether this category fights by suicide attack (diving straight into the target
        /// and dealing its full damage once by ramming, destroying itself) — Task79. SuicideDrone-only
        /// for now. Units of categories returning true are excluded (by early continue) from the
        /// shooting steps that do regular in-range damage and ShotEvent emission (CombatStep/
        /// BaseCombatStep/ThreatCombatStep); instead KamikazeStep and MovementStep's dive movement
        /// handle the whole engagement.</summary>
        public static bool IsKamikaze(this UnitCategory category)
        {
            return category == UnitCategory.SuicideDrone;
        }

        /// <summary>Whether this category is an aircraft (Task86). The Game layer's UnitVisuals uses
        /// it to exclude aircraft from "face the attack direction when firing" (preventing the
        /// side-slipping look of flying while facing away from the flight direction — aircraft always
        /// face their heading and express maneuvers through crossing passes). Unimplemented
        /// placeholders (GroundAttack etc.) are included in anticipation of future additions.</summary>
        public static bool IsAircraft(this UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.AirSuperiority:
                case UnitCategory.GroundAttack:
                case UnitCategory.TacticalBomber:
                case UnitCategory.StrategicBomber:
                case UnitCategory.ElectronicWarfare:
                case UnitCategory.Awacs:
                case UnitCategory.SuicideDrone:
                    return true;
                default:
                    return false;
            }
        }
    }
}
