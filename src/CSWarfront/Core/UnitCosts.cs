namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: unit costs split across the three resources, and payment
    /// (design: 2026-08-03-economy-supply-design.md §2).
    ///
    /// Splits the existing single-value UnitType.Cost into "manpower cost + production cost" by
    /// per-category ratios. Manpower is non-substitutable (no people, no troops). A production
    /// shortfall can be covered by pricier cash (FundsPerProduction — even a city with no industry
    /// can maintain an army, at a premium). Research and ballistic missiles stay cash-only as before
    /// (Research/MissileStockpile untouched).
    /// </summary>
    public static class UnitCosts
    {
        /// <summary>The rate when substituting cash for one unit of production (2 cash = 1
        /// production).</summary>
        public const float FundsPerProduction = 2f;

        /// <summary>The per-category manpower ratio (the remainder is the production ratio). Balance
        /// values matching the intuition that infantry classes are mostly people while armor, ships
        /// and aircraft are mostly equipment (production).</summary>
        public static float ManpowerShare(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.Infantry:
                case UnitCategory.MechInfantry:
                case UnitCategory.DroneInfantry:
                    return 0.6f;
                case UnitCategory.Apc:
                case UnitCategory.Artillery:
                case UnitCategory.AntiAir:
                    return 0.4f;
                case UnitCategory.Tank:
                    return 0.3f;
                case UnitCategory.SupplyTruck:
                    return 0.5f;
                case UnitCategory.Destroyer:
                case UnitCategory.Carrier:
                case UnitCategory.AirSuperiority:
                case UnitCategory.TacticalBomber:
                case UnitCategory.SuicideDrone:
                case UnitCategory.AttackHelicopter:     // Task101
                case UnitCategory.TransportHelicopter:
                case UnitCategory.MilitaryTrain:
                    return 0.2f;
                default:
                    return 0.4f;
            }
        }

        public static float ManpowerCost(UnitType t) { return t.Cost * ManpowerShare(t.Category); }

        public static float ProductionCost(UnitType t) { return t.Cost * (1f - ManpowerShare(t.Category)); }

        /// <summary>Whether it is affordable (spends nothing). fundsCap is the ceiling on cash usable
        /// for substitution (the AI passes its treasury minus the research reserve; manual production
        /// passes f.Treasury as-is).</summary>
        public static bool CanAfford(Faction f, UnitType t, float fundsCap)
        {
            if (f == null || t == null) return false;
            if (f.Manpower < ManpowerCost(t)) return false;

            float productionShortfall = ProductionCost(t) - f.Production;
            if (productionShortfall <= 0f) return true;

            float fundsNeeded = productionShortfall * FundsPerProduction;
            float fundsAvailable = fundsCap < f.Treasury ? fundsCap : f.Treasury;
            return fundsAvailable >= fundsNeeded;
        }

        /// <summary>Pays in full (all-or-nothing). Spends production first, covering only the
        /// shortfall from cash at the FundsPerProduction rate. If unaffordable, spends nothing and
        /// returns false.</summary>
        public static bool TryPay(Faction f, UnitType t, float fundsCap)
        {
            if (!CanAfford(f, t, fundsCap)) return false;

            f.TrySpendManpower(ManpowerCost(t));

            float productionCost = ProductionCost(t);
            if (f.Production >= productionCost)
            {
                f.TrySpendProduction(productionCost);
            }
            else
            {
                float shortfall = productionCost - f.Production;
                f.TrySpendProduction(f.Production);
                f.TrySpend(shortfall * FundsPerProduction);
            }
            return true;
        }
    }
}
