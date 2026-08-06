namespace CSWarfront.Core
{
    /// <summary>
    /// Task101 (Update 3): the centralized rules for field fortifications and cargo stations
    /// (design: 2026-08-04-fortifications-heli-rail-design.md §1).
    ///
    /// The five types:
    ///  - Bunker: automatic fire worth three infantry (FortCombatStep, blocked by buildings) plus a +50%
    ///    infantry defense bonus. Stops functioning at HP 0 (Owner nulled; not capturable, never
    ///    reactivates — only the terrain bonus remains).
    ///  - ArtilleryPost: area fire worth one artillery piece. HP 0 handled like the Bunker.
    ///  - SupplyDepot: its own stock (StoredSupplies) + 200m auto-resupply + truck pickup. Capturable
    ///    like a base (stock seized with it — StoredSupplies rides along on the base object).
    ///  - Trench: an untargetable terrain effect (+50% infantry defense, friend and foe alike).
    ///  - CargoStation: a rail-transport terminal plus stock. Capturable. Unused for transport while
    ///    disconnected from the rails.
    /// </summary>
    public static class FortificationRules
    {
        public static bool IsFortification(BaseType type)
        {
            return type == BaseType.Bunker || type == BaseType.ArtilleryPost
                || type == BaseType.SupplyDepot || type == BaseType.Trench
                || type == BaseType.CargoStation;
        }

        /// <summary>Whether it can be attacked. Only the Trench is false (excluded from BaseCombatStep,
        /// missile impacts and suicide drones — and from the AI's advance objectives
        /// (ChooseTargetBase)).</summary>
        public static bool IsTargetable(BaseType type)
        {
            return type != BaseType.Trench;
        }

        /// <summary>Whether HP 0 means capture. Only Bunker/ArtilleryPost are false = at HP 0 the owner
        /// is nulled (function stops). Occupation.ResolveCaptures branches on this.</summary>
        public static bool IsCapturable(BaseType type)
        {
            return type != BaseType.Bunker && type != BaseType.ArtilleryPost;
        }

        /// <summary>Maximum HP at placement. The Trench is effectively invulnerable (untargetable, so it
        /// normally never loses HP — the huge value is defensive).</summary>
        public static float DefaultMaxHP(BaseType type)
        {
            switch (type)
            {
                case BaseType.Bunker: return 300f;
                case BaseType.ArtilleryPost: return 250f;
                case BaseType.SupplyDepot: return 400f;
                case BaseType.CargoStation: return 400f;
                case BaseType.Trench: return 1000000000f;
                default: return 500f; // the normal-base default (same as MilitaryBase's field initializer)
            }
        }

        /// <summary>Stock (StoredSupplies) cap. Depot 300 / Station 500; nothing else holds stock.</summary>
        public static float StoredSupplyCap(BaseType type)
        {
            if (type == BaseType.SupplyDepot) return 300f;
            if (type == BaseType.CargoStation) return 500f;
            return 0f;
        }

        /// <summary>Task103 (user request): whether this base type may produce this category.
        ///  - Military trains (MilitaryTrain) can be produced only at cargo stations (CargoStation).
        ///  - Cargo stations can produce nothing but military trains (SpawnableDomains=Land, but they are
        ///    train-only factories).
        ///  - Every other combination is decided by SpawnableDomains (domain match) as before.
        /// Read by ManualProduction.TryEnqueue and the production menu's roster construction
        /// (BaseInfoPanelProductionRoster). AI auto-production (ProductionPlanning) skips fortifications
        /// and cargo stations wholesale, so train production at stations is manual-only (the AI's trains
        /// come from TrainStep.MaintainTrains).</summary>
        public static bool CanProduceUnit(BaseType baseType, UnitCategory category)
        {
            if (category == UnitCategory.MilitaryTrain) return baseType == BaseType.CargoStation;
            if (baseType == BaseType.CargoStation) return false; // trains were allowed above; everything else is barred
            return true;
        }
    }
}
