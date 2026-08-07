using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// The category matchup (rock-paper-scissors) table. CombatStep reads it as the attacker→target
    /// damage multiplier. Combinations absent from the table (undefined pairs) return 1.0 (no matchup).
    ///
    /// Asymmetric: e.g. Tank→Infantry is 1.1 (tanks beat infantry) but Infantry→Tank is 0.4
    /// (bare infantry is weak against tanks; by design, anti-tank drones (DroneInfantry) are the
    /// intended tank counter).
    ///
    /// Implementation: a UnitCategory-count × count 2D array filled with 1.0 in the static constructor,
    /// then only the tabled values are overwritten. Array indexing, so O(1) and deterministic (no RNG).
    ///
    /// Future extension point: when adding Sea/Air units, append multipliers here for
    /// vs Carrier/Cruiser/Destroyer/... and vs AirSuperiority/GroundAttack/... In particular AntiAir is
    /// currently 0.5 against every ground category (weak in ground fights), but anti-air units live for
    /// air combat — when air units land, do not forget to add high multipliers (e.g. 2.0+) against the
    /// Air categories.
    /// </summary>
    public static class CombatMatchup
    {
        private static readonly int CategoryCount = Enum.GetValues(typeof(UnitCategory)).Length;
        private static readonly float[,] Table;

        static CombatMatchup()
        {
            Table = new float[CategoryCount, CategoryCount];
            for (int a = 0; a < CategoryCount; a++)
                for (int t = 0; t < CategoryCount; t++)
                    Table[a, t] = 1.0f;

            // Infantry
            Set(UnitCategory.Infantry, UnitCategory.Apc, 0.6f);
            Set(UnitCategory.Infantry, UnitCategory.Tank, 0.4f);
            Set(UnitCategory.Infantry, UnitCategory.MechInfantry, 0.9f);
            Set(UnitCategory.Infantry, UnitCategory.Artillery, 1.3f);
            Set(UnitCategory.Infantry, UnitCategory.DroneInfantry, 1.2f);
            Set(UnitCategory.Infantry, UnitCategory.AntiAir, 1.2f);

            // MechInfantry
            Set(UnitCategory.MechInfantry, UnitCategory.Infantry, 1.2f);
            Set(UnitCategory.MechInfantry, UnitCategory.Apc, 0.8f);
            Set(UnitCategory.MechInfantry, UnitCategory.Tank, 0.6f);
            Set(UnitCategory.MechInfantry, UnitCategory.Artillery, 1.3f);

            // Apc
            Set(UnitCategory.Apc, UnitCategory.Infantry, 1.4f);
            Set(UnitCategory.Apc, UnitCategory.Tank, 0.5f);
            Set(UnitCategory.Apc, UnitCategory.Artillery, 1.2f);

            // Tank
            Set(UnitCategory.Tank, UnitCategory.Infantry, 1.1f);
            Set(UnitCategory.Tank, UnitCategory.MechInfantry, 1.3f);
            Set(UnitCategory.Tank, UnitCategory.Apc, 1.4f);
            Set(UnitCategory.Tank, UnitCategory.Artillery, 1.5f);
            Set(UnitCategory.Tank, UnitCategory.DroneInfantry, 1.1f);
            Set(UnitCategory.Tank, UnitCategory.AntiAir, 1.3f);

            // Artillery
            Set(UnitCategory.Artillery, UnitCategory.Infantry, 1.6f);
            Set(UnitCategory.Artillery, UnitCategory.MechInfantry, 1.4f);
            Set(UnitCategory.Artillery, UnitCategory.Apc, 1.1f);
            Set(UnitCategory.Artillery, UnitCategory.Tank, 0.7f);
            Set(UnitCategory.Artillery, UnitCategory.Artillery, 1.2f);

            // DroneInfantry (anti-tank drones)
            Set(UnitCategory.DroneInfantry, UnitCategory.Tank, 2.0f);
            Set(UnitCategory.DroneInfantry, UnitCategory.Apc, 1.7f);
            Set(UnitCategory.DroneInfantry, UnitCategory.MechInfantry, 1.2f);
            Set(UnitCategory.DroneInfantry, UnitCategory.Infantry, 0.6f);

            // AntiAir (weak on the ground; the vs-Air multipliers are added in the Task61 block below)
            Set(UnitCategory.AntiAir, UnitCategory.Infantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.MechInfantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Apc, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Tank, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Artillery, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.DroneInfantry, 0.5f);

            // --- Task61: naval/air matchups ---
            // The "ground category" roll-up (so AirSuperiority/AntiAir vs-ground multipliers can be set
            // in one loop).
            UnitCategory[] groundCategories =
            {
                UnitCategory.Tank, UnitCategory.Apc, UnitCategory.MechInfantry, UnitCategory.Artillery,
                UnitCategory.DroneInfantry, UnitCategory.Infantry, UnitCategory.AntiAir
            };
            UnitCategory[] airCategories =
            {
                UnitCategory.AirSuperiority, UnitCategory.TacticalBomber, UnitCategory.SuicideDrone
            };

            // AirSuperiority (fighters): strong vs air (2.0), weak vs ground (0.3). Designed as a
            // dedicated air-superiority platform.
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.AirSuperiority, airCategories[i], 2.0f);
            for (int i = 0; i < groundCategories.Length; i++)
                Set(UnitCategory.AirSuperiority, groundCategories[i], 0.3f);

            // AntiAir: finally in its element against aircraft (2.5). Vs ground stays at the existing 0.5.
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.AntiAir, airCategories[i], 2.5f);

            // TacticalBomber: strong vs ground (armor 1.6 / infantry 1.2), near-helpless vs air (0.2).
            Set(UnitCategory.TacticalBomber, UnitCategory.Tank, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.Apc, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.MechInfantry, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.Infantry, 1.2f);
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.TacticalBomber, airCategories[i], 0.2f);

            // Destroyer (missile destroyer): strong vs ships and tanks (coastal bombardment) at 1.4.
            // Everything else stays at the default 1.0.
            Set(UnitCategory.Destroyer, UnitCategory.Carrier, 1.4f);
            Set(UnitCategory.Destroyer, UnitCategory.Destroyer, 1.4f);
            Set(UnitCategory.Destroyer, UnitCategory.Tank, 1.4f);

            // Carrier: a platform of survivability over striking power. Weak (0.6) against every category.
            for (int t = 0; t < CategoryCount; t++)
                Table[(int)UnitCategory.Carrier, t] = 0.6f;

            // --- Task101: helicopters ---
            // AttackHelicopter: strong vs armor (1.6), infantry 1.2, supply-truck hunting 1.5.
            Set(UnitCategory.AttackHelicopter, UnitCategory.Tank, 1.6f);
            Set(UnitCategory.AttackHelicopter, UnitCategory.Apc, 1.6f);
            Set(UnitCategory.AttackHelicopter, UnitCategory.MechInfantry, 1.2f);
            Set(UnitCategory.AttackHelicopter, UnitCategory.Infantry, 1.2f);
            Set(UnitCategory.AttackHelicopter, UnitCategory.SupplyTruck, 1.5f);
            // Vs helicopters: AntiAir 2.5 (the SAM's specialty) / fighters 2.0 / tanks 0.6 (return fire
            // from the coax MG — rarely a telling blow).
            Set(UnitCategory.AntiAir, UnitCategory.AttackHelicopter, 2.5f);
            Set(UnitCategory.AntiAir, UnitCategory.TransportHelicopter, 2.5f);
            Set(UnitCategory.AirSuperiority, UnitCategory.AttackHelicopter, 2.0f);
            Set(UnitCategory.AirSuperiority, UnitCategory.TransportHelicopter, 2.0f);
            Set(UnitCategory.Tank, UnitCategory.AttackHelicopter, 0.6f);
            Set(UnitCategory.Tank, UnitCategory.TransportHelicopter, 0.6f);
        }

        private static void Set(UnitCategory attacker, UnitCategory target, float multiplier)
        {
            Table[(int)attacker, (int)target] = multiplier;
        }

        /// <summary>The damage multiplier when attacker attacks target. Undefined pairs are 1.0.</summary>
        public static float Multiplier(UnitCategory attacker, UnitCategory target)
        {
            return Table[(int)attacker, (int)target];
        }
    }
}
