namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: ユニットコストの3資源分解と支払い（設計: 2026-08-03-economy-supply-design.md §2）。
    ///
    /// 既存のUnitType.Cost（単一値）を「人的資源コスト＋生産力コスト」へ兵科比率で分解する。
    /// 人的資源は代替不可（人がいなければ部隊は作れない）。生産力は不足分を割高な資金で
    /// 代替できる（FundsPerProduction。工業の無い都市でも割高ながら軍を維持できる）。
    /// 研究・弾道ミサイルは従来どおり資金のみ（Research/MissileStockpileは変更しない）。
    /// </summary>
    public static class UnitCosts
    {
        /// <summary>生産力1ぶんを資金で代替する時のレート（資金2＝生産力1）。</summary>
        public const float FundsPerProduction = 2f;

        /// <summary>兵科ごとの人的資源比率（残りが生産力比率）。歩兵系は人手が主、
        /// 機甲・艦艇・航空は装備（生産力）が主、という直感に合わせたバランス値。</summary>
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
                    return 0.2f;
                default:
                    return 0.4f;
            }
        }

        public static float ManpowerCost(UnitType t) { return t.Cost * ManpowerShare(t.Category); }

        public static float ProductionCost(UnitType t) { return t.Cost * (1f - ManpowerShare(t.Category)); }

        /// <summary>支払えるか（消費しない）。fundsCapは資金からの代替に使ってよい上限
        /// （AIは研究準備金を差し引いた額を渡す。手動生産はf.Treasuryをそのまま渡す）。</summary>
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

        /// <summary>全額支払う（all-or-nothing）。生産力を優先消費し、不足分だけを
        /// FundsPerProductionのレートで資金から支払う。払えなければ何も消費せずfalse。</summary>
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
