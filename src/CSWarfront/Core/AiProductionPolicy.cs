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
    /// AIの自動生産方針（Task46、Task80でゲーム理論ベースへ全面強化）。
    ///
    /// Task46時点の設計は「兵科構成を固定目標比率（Tank30%/MechInf20%/...）へ近づける」という
    /// 静的なルールだった。これは「今払える中で一番高いユニット」という旧々ルールが招いていた
    /// 歩兵スパムは解消したが、対峙している敵が何を並べていようと常に同じ構成を作り続ける
    /// ——ユーザー報告のとおり「生産するユニットが単調」——という新たな問題を生んだ。
    ///
    /// Task80の設計はこれを「観測 → 利得計算 → ヘッジ（混合戦略）」の3段へ置き換える
    /// （AiThreatAssessment/AiCombatScoring参照、詳細な理論的根拠は両クラスのXMLコメントに記載）:
    ///   1. 観測: 敵対する生存ユニット・外部脅威の構成をカテゴリ別に集計し正規化する
    ///      （AiThreatAssessment.Observe）。敵対ユニットが1体も無ければ「平時」とみなし、
    ///      Task46の固定目標比率へそのままフォールバックする（後方互換・平時の量産計画に有効）。
    ///   2. 利得: 各候補兵科について、その敵編成に対する期待効果 E[c] を計算する
    ///      （AiCombatScoring.ExpectedEffectiveness）。これは「相手の観測された混合戦略に対する
    ///      最良応答（best response）」というゲーム理論の標準的な考え方そのもの。
    ///   3. ヘッジ: E[]が最大の兵科だけを常に選ぶと（＝純粋戦略のargmax）、相手から見て次に何が
    ///      来るか読み切られてしまい、これも「単調」の一種になる。上位3候補からE[]²に比例した
    ///      重みでseedハッシュ抽選する（AiCombatScoring.HedgeOrder）ことで、大抵はbest-responseだが
    ///      時々2番手・3番手も選ぶ、という混合戦略に相当する挙動を実現する。
    ///
    /// Tierの選択も同じヘッジの考え方を適用する（ChooseTierHedged）: 「質(高Tier1体)」と
    /// 「量(低Tierを複数体、複数回の意思決定に分けて)」をコスト効率(AiCombatScoring.TierValue)で
    /// 比較し、上位Tierからヘッジ抽選する。研究へ資金を回す確率も、旧来の固定35%から
    /// 「治療の余裕(wealth)・Tierの遅れ(behind)・数的劣勢(outnumbered)」に応じて動く式へ差し替える
    /// （ResearchProbability）。
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

        // --- Task80: 研究確率の状況適応式（旧: 固定35%） ---
        // pResearch = clamp(Base + BehindWeight*behindTiers + WealthWeight*wealthFactor
        //                        - OutnumberedWeight*outnumberedFactor, Min, Max)
        // 各項の意図はResearchProbability()のコメントを参照。
        private const float ResearchBase = 0.15f;
        private const float ResearchBehindWeight = 0.25f;
        private const float ResearchWealthWeight = 0.15f;
        private const float ResearchOutnumberedWeight = 0.3f;
        private const float ResearchProbabilityMin = 0.05f;
        private const float ResearchProbabilityMax = 0.6f;

        /// <summary>wealthFactorが1.0(満額寄与)に達するまでの「予備資金の余裕」の基準額
        /// （Treasury - ResearchReserve がこの額に達すると「十分潤沢」とみなす）。</summary>
        private const float WealthComfortRange = 500f;

        /// <summary>behindTiersが1.0(満額寄与)に達するまでのTier差の基準幅。Tierは1..5なので
        /// 最大差は4。</summary>
        private const float TierBehindRange = 4f;

        /// <summary>兵科構成の目標比率（平時フォールバック専用、Task46から不変）。ドローン兵/歩兵含む
        /// 陸上6兵科。AntiAirは平時（対空目標が存在しない状況）では対象外のまま
        /// ——対空戦力は本質的に対空脅威への反応装備であり、脅威が無い平時に量産する理由が無い、
        /// という意図的な設計（Task46時点の除外理由と同じ）。Task61: 基地がArmy（Domain.Land生産可）
        /// のときに使う。</summary>
        private static readonly UnitCategory[] LandCategories =
        {
            UnitCategory.Tank, UnitCategory.MechInfantry, UnitCategory.Infantry,
            UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.DroneInfantry
        };

        private static readonly float[] LandTargets = { 0.30f, 0.20f, 0.20f, 0.15f, 0.10f, 0.05f };

        /// <summary>Task80: 交戦時（敵編成が観測できる状況）の陸上候補兵科。LandCategoriesにAntiAirを
        /// 追加したもの——旧ルールは「対空目標が存在しないため対象外」としてAntiAirをハードコードで
        /// 除外していたが（Category_choice_never_picks_AntiAirテスト参照）、Task61で航空ユニット
        /// （AirSuperiority/TacticalBomber/SuicideDrone）が実装され、CombatMatchup.AntiAir vs
        /// 航空カテゴリ=2.5倍という相性行が既に存在する。敵編成に航空ユニットの構成比があれば、
        /// AiCombatScoring.ExpectedEffectivenessが自然にAntiAirの期待値を押し上げる
        /// （除外ルールをここで撤廃するだけで、対空生産が"emergent"に起こる——スコア式側に
        /// 個別の特別扱いは不要）。</summary>
        private static readonly UnitCategory[] LandWartimeCategories =
        {
            UnitCategory.Tank, UnitCategory.MechInfantry, UnitCategory.Infantry,
            UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.DroneInfantry, UnitCategory.AntiAir
        };

        /// <summary>Task61: 基地がNavy（Domain.Sea生産可）のときに使う。駆逐艦を主力に、
        /// 空母は少数（高コストのプラットフォームなので数を揃えない）。NavalUnitRosterは
        /// この2種しか定義しないため、平時・交戦時のどちらでも候補は同じ（LandCategoriesのような
        /// 隠れた除外は無い）。</summary>
        private static readonly UnitCategory[] SeaCategories = { UnitCategory.Destroyer, UnitCategory.Carrier };
        private static readonly float[] SeaTargets = { 0.7f, 0.3f };

        /// <summary>Task61: 基地がAirForce（Domain.Air生産可）のときに使う。制空権確保の戦闘機を主力、
        /// 対地打撃の爆撃機、少数の使い捨て自爆ドローンという構成。AirUnitRosterはこの3種しか
        /// 定義しないため、平時・交戦時のどちらでも候補は同じ。</summary>
        private static readonly UnitCategory[] AirCategories =
        {
            UnitCategory.AirSuperiority, UnitCategory.TacticalBomber, UnitCategory.SuicideDrone
        };
        private static readonly float[] AirTargets = { 0.45f, 0.35f, 0.20f };

        /// <summary>Task61: 基地のSpawnableDomainsから、平時フォールバックで使う兵科構成表を選ぶ。
        /// Sea → SeaCategories/SeaTargets、Air → AirCategories/AirTargets、それ以外（Army含む）は
        /// 従来通りLandCategories/LandTargets（既存の全テストが前提とする既定挙動）。</summary>
        private static void CategoriesFor(DomainMask spawnableDomains, out UnitCategory[] categories, out float[] targets)
        {
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea))
            {
                categories = SeaCategories; targets = SeaTargets;
            }
            else if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air))
            {
                categories = AirCategories; targets = AirTargets;
            }
            else
            {
                categories = LandCategories; targets = LandTargets;
            }
        }

        /// <summary>Task80: 基地のSpawnableDomainsから、交戦時（敵編成ベースのスコアリング）で使う
        /// 候補兵科の並びを選ぶ。Land以外はCategoriesFor()と全く同じ配列（隠れた除外が無いため）。</summary>
        private static UnitCategory[] WartimeCandidatesFor(DomainMask spawnableDomains)
        {
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea)) return SeaCategories;
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air)) return AirCategories;
            return LandWartimeCategories;
        }

        /// <summary>兵科の目標比率からのズレを揺らすジッター幅（Task46、平時フォールバック専用）。
        /// 同一tick・同一勢力の複数基地が判で押したように同じ兵科を選び続けないための小さな揺らぎ。
        /// 目標比率間の最小差(5%)より十分小さく抑え、構成方針そのものを覆さない。</summary>
        private const float CompositionJitter = 0.02f;

        // Task80: 交戦時のヘッジ抽選（HedgeOrder/HedgePickIndex）に渡すseedを、同じ意思決定内の
        // 他の抽選（研究判定・後続のフォールバック試行）と無相関にするための固定ソルト値。
        // いずれも「素のseed」ではなく「seed ^ ソルト」をハッシュするだけの単純な手法（AiCombatScoring.Hash
        // 参照）だが、ソルトを変えるだけで実質的に独立した乱数列が得られる。
        private const uint CategoryHedgeSalt = 0xB5297A4Du;
        private const uint TierHedgeSalt = 0x68E31DA4u;

        public static AiDecision Decide(WarState state, Faction faction, MilitaryBase b, uint seed)
        {
            DomainMask domains = b.SpawnableDomains;
            AiThreatAssessment.EnemyMix mix = AiThreatAssessment.Observe(state, faction.Id);

            // Bootstrap rule (Task46 fix, unchanged by Task80): a faction with zero living units
            // (across ALL domains, not just this base's) needs troops before it needs research.
            bool hasUnits = CountTotalLivingUnits(state, faction.Id) > 0;

            if (faction.UnlockedTier < 5 && faction.Treasury >= ResearchReserve)
            {
                float pResearch = ResearchProbability(state, faction);
                uint researchRoll = AiCombatScoring.Hash(seed) % 1000;
                if (researchRoll < (uint)(pResearch * 1000f))
                    return new AiDecision { Choice = AiSpendChoice.Research, TypeKey = null };
            }

            float spendCap = (faction.UnlockedTier < 5 && hasUnits)
                ? faction.Treasury - ResearchReserve
                : faction.Treasury;
            if (spendCap < 0f) spendCap = 0f;

            UnitCategory[] preference;
            if (mix.HasHostiles)
            {
                // --- Task80: best-response-with-hedging (交戦時) ---
                UnitCategory[] candidates = WartimeCandidatesFor(domains);
                float[] scores = new float[candidates.Length];
                for (int i = 0; i < candidates.Length; i++)
                    scores[i] = AiCombatScoring.ExpectedEffectiveness(candidates[i], mix.Shares);

                preference = AiCombatScoring.HedgeOrder(candidates, scores, AiCombatScoring.Hash(seed ^ CategoryHedgeSalt));
            }
            else
            {
                // --- Task46 fallback (平時・固定目標比率、旧来の全ロジックを不変のまま維持) ---
                CategoriesFor(domains, out UnitCategory[] categories, out float[] targets);
                CountByCategory(state, faction.Id, categories, out int[] counts, out int totalUnits);
                preference = CategoryPreferenceOrder(categories, targets, counts, totalUnits, seed);
            }

            // Cross-category fallback (Task46 fix, preserved under Task80): walk the (now hedge-
            // ordered, in wartime) preference list and return the first category with an
            // affordable+unlocked tier, instead of stalling when the top pick is unaffordable.
            for (int i = 0; i < preference.Length; i++)
            {
                uint tierSeed = AiCombatScoring.Hash(seed ^ TierHedgeSalt ^ (uint)(i * 2654435761u));
                UnitType type = ChooseTierHedged(state, preference[i], faction.UnlockedTier, spendCap, tierSeed);
                if (type != null)
                    return new AiDecision { Choice = AiSpendChoice.Produce, TypeKey = type.TypeKey };
            }

            return new AiDecision { Choice = AiSpendChoice.None, TypeKey = null };
        }

        /// <summary>Task80: 研究へ資金を回す確率。旧来の固定35%を、状況に応じて0.05..0.6の範囲で
        /// 動く式へ差し替える:
        ///   pResearch = clamp(0.15 + 0.25×behindTiers + 0.15×wealthFactor - 0.3×outnumberedFactor,
        ///                      0.05, 0.6)
        ///   behindTiers     = 自軍の平均Tierが、敵対勢力の中で最もTierが進んでいる勢力より
        ///                      どれだけ遅れているか（0..1、4Tier差で頭打ち）。遅れているほど
        ///                      「追いつくため」研究を優先する。
        ///   wealthFactor    = 予備資金（Treasury - ResearchReserve）の余裕（0..1、500で頭打ち）。
        ///                      懐が温かいほど研究へ回す余裕がある。
        ///   outnumberedFactor = 数的劣勢の度合い（0..1、2:1で頭打ち）。劣勢が深刻なほど、研究より
        ///                      目の前の兵力を増やす緊急生産を優先する（マイナス項）。
        /// 基準額はいずれも本ゲームのバランス（ResearchReserve=150、Tier1..5）から見て
        /// 「明確に効果が出始める」程度の値をdesign-tuning値として選んだもの。</summary>
        private static float ResearchProbability(WarState state, Faction faction)
        {
            float ownAvgTier = AiThreatAssessment.AverageFieldedTier(state, faction.Id);
            float strongestHostileAvgTier = AiThreatAssessment.StrongestHostileAverageTier(state, faction.Id);

            float behindTiers = 0f;
            if (strongestHostileAvgTier > ownAvgTier)
            {
                behindTiers = (strongestHostileAvgTier - ownAvgTier) / TierBehindRange;
                if (behindTiers > 1f) behindTiers = 1f;
            }

            float wealthFactor = (faction.Treasury - ResearchReserve) / WealthComfortRange;
            if (wealthFactor < 0f) wealthFactor = 0f;
            if (wealthFactor > 1f) wealthFactor = 1f;

            float outnumberedFactor = AiThreatAssessment.OutnumberedFactor(state, faction.Id);

            float p = ResearchBase
                + ResearchBehindWeight * behindTiers
                + ResearchWealthWeight * wealthFactor
                - ResearchOutnumberedWeight * outnumberedFactor;

            if (p < ResearchProbabilityMin) p = ResearchProbabilityMin;
            if (p > ResearchProbabilityMax) p = ResearchProbabilityMax;
            return p;
        }

        /// <summary>faction が保有する生存ユニットの総数（ドメイン不問）。Decideのbootstrap判定
        /// （研究準備金の留保を免除するかどうか）専用。CountByCategoryと違い、特定のcategories配列に
        /// 絞り込まない——例えば海軍しか持たない勢力が陸上基地の意思決定をする場合でも
        /// 「ユニットを1体も持たない新設勢力」と誤判定しないようにするため（Task80で
        /// AntiAir/Sea/Air全ドメインが実際に生産され得るようになったことに合わせた一般化）。</summary>
        private static int CountTotalLivingUnits(WarState state, byte factionId)
        {
            int total = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.IsAlive && u.FactionId == factionId) total++;
            }
            return total;
        }

        /// <summary>勢力が現在保有する生存ユニットを兵科別に数える（平時フォールバック専用、
        /// Task46から不変）。Decideの兵科目標比率の乖離計算（CategoryPreferenceOrder）で使う。</summary>
        private static void CountByCategory(WarState state, byte factionId, UnitCategory[] categories, out int[] counts, out int total)
        {
            counts = new int[categories.Length];
            total = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;
                int idx = IndexOf(categories, t.Category);
                if (idx < 0) continue;
                counts[idx]++;
                total++;
            }
        }

        /// <summary>目標比率から最も下振れている（＝不足している）兵科から順に並べた優先順位を返す
        /// （Task46: 平時フォールバック専用、ロジックは不変）。同点はCategories配列の並び順
        /// （決定的、安定ソート）。seedから導出した小さなジッターを各兵科の乖離へ加え、僅差の場合に
        /// 同一勢力の複数基地が常に同じ兵科へ集中しないようにする。</summary>
        private static UnitCategory[] CategoryPreferenceOrder(UnitCategory[] categories, float[] targets, int[] counts, int total, uint seed)
        {
            float[] deficits = new float[categories.Length];
            uint jitterSeed = AiCombatScoring.Hash(seed ^ 0x9E3779B9u);
            for (int i = 0; i < categories.Length; i++)
            {
                float share = total > 0 ? (float)counts[i] / total : 0f;
                float deficit = targets[i] - share;

                uint categoryHash = AiCombatScoring.Hash(jitterSeed + (uint)i * 2654435761u);
                float jitter = ((categoryHash % 1000) / 1000f - 0.5f) * 2f * CompositionJitter;
                deficits[i] = deficit + jitter;
            }

            int[] order = new int[categories.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            // Stable descending insertion sort: only shifts on strictly-smaller deficit, so ties
            // keep their original categories-array order (same tie-break as the old single-winner
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

            UnitCategory[] result = new UnitCategory[categories.Length];
            for (int i = 0; i < order.Length; i++) result[i] = categories[order[i]];
            return result;
        }

        private static int IndexOf(UnitCategory[] categories, UnitCategory category)
        {
            for (int i = 0; i < categories.Length; i++)
                if (categories[i] == category) return i;
            return -1;
        }

        /// <summary>最大Tier数（ロスターは常にTier1..5）。ChooseTierHedgedのスタック配列のサイズに使う。</summary>
        private const int MaxTierCount = 5;

        /// <summary>Task80: 指定兵科について、unlockedTier以下・spendCap以下（＝現在買える）Tierの
        /// うち、AiCombatScoring.TierValue（質vs量のコスト効率）でヘッジ抽選した1つを返す
        /// （旧: ChooseHighestAffordableTierは常に最高Tierを一意に選んでいた）。買えるTierが
        /// 1つも無ければnull（呼び出し側のcross-category fallbackが次点の兵科を試す）。
        /// 全3ロスター(Land/Sea/Air)のTypeKeyは全て"&lt;Category&gt;_T&lt;tier&gt;"という共通形式
        /// （*UnitRoster.TypeKey参照）のため、Types.All()を毎回走査せずキーを直接組み立てて引ける。</summary>
        private static UnitType ChooseTierHedged(WarState state, UnitCategory category, byte unlockedTier, float spendCap, uint seed)
        {
            UnitType[] candidates = new UnitType[MaxTierCount];
            float[] scores = new float[MaxTierCount];
            int count = 0;

            byte maxTier = unlockedTier > MaxTierCount ? (byte)MaxTierCount : unlockedTier;
            for (byte tier = 1; tier <= maxTier; tier++)
            {
                UnitType t = state.Types.Get(category + "_T" + tier);
                if (t == null) continue; // このカテゴリはこの基地のロスターに存在しない（通常起きない防御的ケース）
                if (t.Cost > spendCap) continue;

                candidates[count] = t;
                scores[count] = AiCombatScoring.TierValue(t);
                count++;
            }

            if (count == 0) return null;
            if (count == 1) return candidates[0];

            int idx = AiCombatScoring.HedgePickIndex(scores, count, seed);
            return candidates[idx];
        }
    }
}
