namespace CSWarfront.Core
{
    /// <summary>AI自動生産が1回の意思決定でとる行動（Task46）。</summary>
    public enum AiSpendChoice { None, Research, Produce }

    /// <summary>AiProductionPolicy.Decide の結果。Choice==Produce の時だけ TypeKey が有効。</summary>
    public struct AiDecision
    {
        public AiSpendChoice Choice;
        public string TypeKey;
    }

    /// <summary>
    /// AIの自動生産方針（Task46）。旧ProductionPlanning.ChooseUnitKeyの「今払える中で一番高いユニット」
    /// というルールは、終盤に最安・最速回転のInfantryだけを延々量産する結果（実質「歩兵スパム」）を
    /// 生んでいた。この方針は代わりに (1) 研究に資金を回してTierを上げる、(2) 兵科構成を目標比率へ
    /// 近づける、の2つを組み合わせる。
    ///
    /// 決定的・RNG不使用: 「ランダムに見える」挙動はすべて seed の整数ハッシュから導出する
    /// （System.Random は一切使わない。同じ (state, faction, base, seed) には常に同じ結果を返す）。
    /// UnityEngine非依存。
    /// </summary>
    public static class AiProductionPolicy
    {
        /// <summary>この額を下回るまで研究へ資金を回さない下限（Task46）。ProductionPlanningの
        /// ユニット選定も、UnlockedTier&lt;5の間はこの額を残した上での予算しか使わない
        /// （研究投資の原資を生産で使い切ってしまわないようにするため）。</summary>
        public const float ResearchReserve = 150f;

        /// <summary>研究が選ばれた際に1回の意思決定でResearch.TryInvestへ渡す固定額（Task46）。</summary>
        public const float ResearchInvestPerDecision = 50f;

        /// <summary>研究が選ばれる確率（0..99のハッシュ値がこの値未満なら研究）。約35%。</summary>
        private const uint ResearchChancePercent = 35;

        /// <summary>兵科構成の目標比率（ドローン兵/歩兵含む陸上6兵科。AntiAirは対空目標が
        /// まだ存在しないため対象外＝ProductionPlanning.ChooseUnitKeyの旧除外ルールを踏襲）。</summary>
        private static readonly UnitCategory[] Categories =
        {
            UnitCategory.Tank, UnitCategory.MechInfantry, UnitCategory.Infantry,
            UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.DroneInfantry
        };

        private static readonly float[] Targets = { 0.30f, 0.20f, 0.20f, 0.15f, 0.10f, 0.05f };

        /// <summary>兵科の目標比率からのズレを揺らすジッター幅（Task46）。同一tick・同一勢力の
        /// 複数基地が判で押したように同じ兵科を選び続けないための小さな揺らぎ。目標比率間の
        /// 最小差(5%)より十分小さく抑え、構成方針そのものを覆さない。</summary>
        private const float CompositionJitter = 0.02f;

        public static AiDecision Decide(WarState state, Faction faction, MilitaryBase b, uint seed)
        {
            CountByCategory(state, faction.Id, out int[] counts, out int totalUnits);

            if (faction.UnlockedTier < 5 && faction.Treasury >= ResearchReserve)
            {
                uint researchRoll = Hash(seed) % 100;
                if (researchRoll < ResearchChancePercent)
                    return new AiDecision { Choice = AiSpendChoice.Research, TypeKey = null };
            }

            // Bootstrap rule (Task46 fix): a faction with zero living units needs troops before it
            // needs research. Without this exemption, ResearchReserve(150) alone can exceed a fresh
            // faction's entire spendable margin (e.g. Treasury=200 -> spendCap=50 < Tank_T1's 60),
            // and with no cross-category fallback the AI produced NOTHING until treasury passed
            // ~210. Once the faction has any living unit, the ordinary reserve applies again.
            bool hasUnits = totalUnits > 0;
            float spendCap = (faction.UnlockedTier < 5 && hasUnits)
                ? faction.Treasury - ResearchReserve
                : faction.Treasury;
            if (spendCap < 0f) spendCap = 0f;

            // Cross-category fallback (Task46 fix): try categories in deficit order (largest
            // deficit first) and return the first one with an affordable unlocked unit, instead of
            // being locked to the single most-deficient category. Only None when NO category has
            // anything affordable.
            UnitCategory[] preference = CategoryPreferenceOrder(counts, totalUnits, seed);
            for (int i = 0; i < preference.Length; i++)
            {
                UnitType type = ChooseHighestAffordableTier(state, preference[i], faction.UnlockedTier, spendCap);
                if (type != null)
                    return new AiDecision { Choice = AiSpendChoice.Produce, TypeKey = type.TypeKey };
            }

            return new AiDecision { Choice = AiSpendChoice.None, TypeKey = null };
        }

        /// <summary>勢力が現在保有する生存ユニットを兵科別に数える。Decideの予算判定（bootstrap判定）と
        /// CategoryPreferenceOrderの両方で使うため一度だけ走査する。</summary>
        private static void CountByCategory(WarState state, byte factionId, out int[] counts, out int total)
        {
            counts = new int[Categories.Length];
            total = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;
                int idx = IndexOf(t.Category);
                if (idx < 0) continue;
                counts[idx]++;
                total++;
            }
        }

        /// <summary>目標比率から最も下振れている（＝不足している）兵科から順に並べた優先順位を返す
        /// （Task46: 旧ChooseCategoryは最有力の1件だけを返していたが、その兵科が予算内で買えない場合の
        /// 代替先が無く生産が完全停止していた。この関数は全兵科をdeficit降順で返し、呼び出し側が
        /// 先頭から順に「買えるか」を試せるようにする）。同点はCategories配列の並び順（決定的、安定ソート）。
        /// seedから導出した小さなジッターを各兵科の乖離へ加え、僅差の場合に同一勢力の複数基地が
        /// 常に同じ兵科へ集中しないようにする。</summary>
        private static UnitCategory[] CategoryPreferenceOrder(int[] counts, int total, uint seed)
        {
            float[] deficits = new float[Categories.Length];
            uint jitterSeed = Hash(seed ^ 0x9E3779B9u);
            for (int i = 0; i < Categories.Length; i++)
            {
                float share = total > 0 ? (float)counts[i] / total : 0f;
                float deficit = Targets[i] - share;

                uint categoryHash = Hash(jitterSeed + (uint)i * 2654435761u);
                float jitter = ((categoryHash % 1000) / 1000f - 0.5f) * 2f * CompositionJitter;
                deficits[i] = deficit + jitter;
            }

            int[] order = new int[Categories.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            // Stable descending insertion sort: only shifts on strictly-smaller deficit, so ties
            // keep their original Categories-array order (same tie-break as the old single-winner
            // loop's strict `>` comparison, which favored the first-seen index).
            for (int i = 1; i < order.Length; i++)
            {
                int key = order[i];
                float keyDeficit = deficits[key];
                int j = i - 1;
                while (j >= 0 && deficits[order[j]] < keyDeficit)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            UnitCategory[] result = new UnitCategory[Categories.Length];
            for (int i = 0; i < order.Length; i++) result[i] = Categories[order[i]];
            return result;
        }

        private static int IndexOf(UnitCategory category)
        {
            for (int i = 0; i < Categories.Length; i++)
                if (Categories[i] == category) return i;
            return -1;
        }

        /// <summary>指定兵科のうち、unlockedTier以下・spendCap以下で最もTierが高いものを返す。
        /// 何も条件を満たさなければnull。</summary>
        private static UnitType ChooseHighestAffordableTier(WarState state, UnitCategory category, byte unlockedTier, float spendCap)
        {
            UnitType best = null;
            foreach (UnitType t in state.Types.All())
            {
                if (t.Domain != Domain.Land) continue;
                if (t.Category != category) continue;
                if (t.Tier > unlockedTier) continue;
                if (t.Cost > spendCap) continue;
                if (best == null || t.Tier > best.Tier) best = t;
            }
            return best;
        }

        /// <summary>決定的な整数ハッシュ（MurmurHash3のfinalizer相当）。System.Randomを使わずに
        /// 「ランダムに見える」が同じ入力からは常に同じ出力を返す値を作るために使う。</summary>
        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }
    }
}
