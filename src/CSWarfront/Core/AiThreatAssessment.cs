using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// AI生産方針のゲーム理論的強化（Task80）の「観測」フェーズ。敵編成をベクトル化し、
    /// 研究/生産の状況判断に使う集計値（自軍平均Tier・最強敵勢力平均Tier・数的劣勢度）を提供する。
    /// AiProductionPolicy.Decide からのみ呼ばれる純ロジック・決定的（乱数不使用）・UnityEngine非依存。
    ///
    /// 設計意図: 旧方針は固定比率（Tank30%/MechInf20%/...）を常に目標にしていたため、対峙している
    /// 敵の兵科構成に関係なく同じ構成を作り続けていた（ユーザー報告「生産するユニットが単調」）。
    /// この方針は代わりに「今、実際に殺し合っている（または隣接して敵対している）敵は何を並べて
    /// いるか」を数え、その相手に対する期待効果（AiCombatScoring.ExpectedEffectiveness）へ差し替える
    /// —— 標準的なゲーム理論の用語で言えば、固定戦略から相手の観測された混合戦略への
    /// best-response（最良応答）への切り替えである。
    /// </summary>
    public static class AiThreatAssessment
    {
        /// <summary>UnitCategoryの全メンバー数。敵編成ベクトル（Shares配列）の長さに使う。
        /// CombatMatchup.CategoryCountと同じ導出だが、Coreの他クラスに依存させないためここでも
        /// 独立に計算する（値は必ず一致する。UnitCategoryの列挙値そのものを参照しているため）。</summary>
        public static readonly int CategoryCount = Enum.GetValues(typeof(UnitCategory)).Length;

        /// <summary>宿敵(Relation.Nemesis)勢力の生存ユニット1体を数える際の重み。通常の敵対(Hostile)の
        /// 2倍として扱う（設計要件: 「Nemesis counts double weight — priority enemy」）。</summary>
        public const float NemesisWeight = 2f;

        /// <summary>通常の敵対(Hostile)勢力の生存ユニット1体を数える際の重み。</summary>
        public const float HostileWeight = 1f;

        /// <summary>外部脅威（KAIJU/Alien、Task58/59）の生存1体を数える際の重み。外部脅威には
        /// UnitCategoryが無い（勢力ユニットではないため）が、設計要件により「重装甲（Tank相当）」の
        /// 疑似カテゴリとして扱う（"Kaiju/Alien ≈ heavy armor for countering purposes"）。
        /// この定数はその重みそのもの（1体 = 通常のHostileユニット1体分の重み）。</summary>
        public const float ThreatWeight = 1f;

        /// <summary>外部脅威をUnitCategory換算する際に割り当てる疑似カテゴリ（Tank-like）。</summary>
        public const UnitCategory ThreatPseudoCategory = UnitCategory.Tank;

        /// <summary>敵編成の観測結果。Sharesは(int)UnitCategoryで添字付けした正規化済み構成比（合計1.0）。
        /// HasHostilesがfalseの間はSharesは全て0（＝敵対する生存ユニット/脅威が1体も無い＝平時）。</summary>
        public struct EnemyMix
        {
            public float[] Shares;
            public bool HasHostiles;
        }

        /// <summary>factionId視点で敵対する（RelationMatrix上でHostile/Nemesis、または
        /// ThreatRelations上でHostile/Nemesis）生存ユニット・外部脅威をUnitCategory別に数え、
        /// 正規化した構成比を返す。宿敵勢力のユニットは通常の2倍の重みでカウントする（Task80設計）。
        /// 該当する敵が1体も無ければ HasHostiles=false（呼び出し側はこれを「平時」とみなし、
        /// 旧来の固定目標比率へフォールバックする）。</summary>
        public static EnemyMix Observe(WarState state, byte factionId)
        {
            float[] weights = new float[CategoryCount];
            float total = 0f;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId == factionId) continue;

                Relation rel = state.Relations.Get(factionId, u.FactionId);
                if (!rel.IsHostile()) continue;

                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;

                float w = rel == Relation.Nemesis ? NemesisWeight : HostileWeight;
                weights[(int)t.Category] += w;
                total += w;
            }

            for (int i = 0; i < state.Threats.Count; i++)
            {
                ExternalThreat threat = state.Threats[i];
                if (threat.IsDefeated) continue;

                Relation rel = state.ThreatRelations.Get(factionId, threat.Kind);
                if (!rel.IsHostile()) continue;

                float w = rel == Relation.Nemesis ? NemesisWeight * ThreatWeight : ThreatWeight;
                weights[(int)ThreatPseudoCategory] += w;
                total += w;
            }

            EnemyMix mix;
            mix.HasHostiles = total > 0f;
            mix.Shares = new float[CategoryCount];
            if (mix.HasHostiles)
            {
                for (int i = 0; i < CategoryCount; i++)
                    mix.Shares[i] = weights[i] / total;
            }
            return mix;
        }

        /// <summary>factionIdが現在保有する生存ユニットの平均Tier（0..5）。ユニットが1体も無ければ0。
        /// 研究確率の「自軍が敵より遅れているか」判定（AiProductionPolicy.ResearchProbability）に使う。</summary>
        public static float AverageFieldedTier(WarState state, byte factionId)
        {
            int count = 0;
            int tierSum = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;
                tierSum += t.Tier;
                count++;
            }
            return count > 0 ? (float)tierSum / count : 0f;
        }

        /// <summary>factionIdに対して敵対する（Eliminatedでない）勢力のうち、最も平均Tierが高い勢力の
        /// 平均Tierを返す。敵対勢力が無い、またはどの敵対勢力もユニットを保有していなければ0。</summary>
        public static float StrongestHostileAverageTier(WarState state, byte factionId)
        {
            float best = 0f;
            for (int i = 0; i < state.Factions.Count; i++)
            {
                Faction other = state.Factions[i];
                if (other.Id == factionId || other.Eliminated) continue;

                Relation rel = state.Relations.Get(factionId, other.Id);
                if (!rel.IsHostile()) continue;

                float avg = AverageFieldedTier(state, other.Id);
                if (avg > best) best = avg;
            }
            return best;
        }

        /// <summary>「数的劣勢の度合い」を0(互角以下)..1(2:1以上で完全劣勢)で返す。自軍ユニット0体で
        /// 敵対ユニットが1体でもいればこれ以上ない劣勢として1を返す。敵対ユニットが1体も無ければ0
        /// （劣勢ではない）。研究確率のformula（AiProductionPolicy.ResearchProbability）が、劣勢が
        /// 深刻なほど研究より緊急生産へ資金を回す（式のマイナス項）ために使う。</summary>
        public static float OutnumberedFactor(WarState state, byte factionId)
        {
            int own = 0;
            int hostile = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.FactionId == factionId) { own++; continue; }

                Relation rel = state.Relations.Get(factionId, u.FactionId);
                if (rel.IsHostile()) hostile++;
            }

            if (own == 0) return hostile > 0 ? 1f : 0f;

            float factor = (float)hostile / own - 1f; // 1:1で0、2:1で1
            if (factor < 0f) factor = 0f;
            if (factor > 1f) factor = 1f;
            return factor;
        }
    }
}
