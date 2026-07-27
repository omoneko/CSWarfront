namespace CSWarfront.Core
{
    /// <summary>各勢力が軍資金を使って所有基地の生産キューを補充する（純ロジック・決定的）。</summary>
    public static class ProductionPlanning
    {
        public const int QueueCap = 2;             // 1基地あたり最大キュー長

        public static void Advance(WarState state)
        {
            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated) continue;
                for (int bi = 0; bi < state.Bases.Count; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (!b.AutoProduce) continue; // Task34: プレイヤーが手動管理を選んだ基地はAIが触らない

                    // 空きスロットができるたびに選び直す：購入のたびに軍資金が減るため、
                    // 同じtick内でも「もう最強は買えない」場合は次点が選ばれる（決定的）。
                    while (b.Queue.Count < QueueCap)
                    {
                        string key = ChooseUnitKey(state, f, b);
                        if (key == null) break; // 何も買えない
                        UnitType type = state.Types.Get(key);
                        if (type == null) break;
                        if (!f.TrySpend(type.Cost)) break;
                        b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
                    }
                }
            }
        }

        /// <summary>
        /// 勢力が今払える範囲で最も強力（＝最もCostが高い）陸上ユニットを選ぶ（Task28）。
        /// 同点はTierが高い方、さらに同点ならTypeKeyの序数（Ordinal）比較で決める（完全決定的）。
        /// faction.UnlockedTier を超えるTierのユニットは候補から除外する（Task35: 研究未解禁のTierを
        /// 自動生産が選ぶことはない）。何も買えなければnullを返す（現行の「TrySpend失敗時は何も積まない」
        /// 挙動を維持）。bは現状未使用（基地種別ごとの生産制限など将来の絞り込み用に予約）。
        /// </summary>
        public static string ChooseUnitKey(WarState state, Faction faction, MilitaryBase b)
        {
            UnitType best = null;
            foreach (UnitType t in state.Types.All())
            {
                if (t.Domain != Domain.Land) continue;
                // AntiAirは今のところ対空できる相手（空ユニット）が存在しないため除外する。
                // 空ユニットのロスターが実装されたらこの除外を外すこと。
                if (t.Category == UnitCategory.AntiAir) continue;
                if (t.Tier > faction.UnlockedTier) continue; // Task35: 未解禁Tierは選ばない
                if (t.Cost > faction.Treasury) continue;

                if (best == null || IsBetter(t, best)) best = t;
            }
            return best != null ? best.TypeKey : null;
        }

        private static bool IsBetter(UnitType candidate, UnitType current)
        {
            if (candidate.Cost != current.Cost) return candidate.Cost > current.Cost;
            if (candidate.Tier != current.Tier) return candidate.Tier > current.Tier;
            return string.CompareOrdinal(candidate.TypeKey, current.TypeKey) < 0;
        }
    }
}
