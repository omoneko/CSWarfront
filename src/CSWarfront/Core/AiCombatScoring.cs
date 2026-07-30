namespace CSWarfront.Core
{
    /// <summary>
    /// AI生産方針のゲーム理論的強化（Task80）の「payoff（利得）」「hedge（混合戦略）」フェーズ。
    /// AiThreatAssessment.Observeが数えた敵編成(EnemyMix)に対して各兵科の期待効果を採点し、
    /// 上位の候補からseedハッシュで確率的に選ぶ（=毎回argmaxだけを選ぶ「読み切られる」戦略を避ける
    /// 混合戦略のヘッジ）。AiProductionPolicy.Decideからのみ呼ばれる純ロジック・決定的・
    /// UnityEngine非依存（System.Randomは一切使わない）。
    /// </summary>
    public static class AiCombatScoring
    {
        /// <summary>敵編成に対して兵科cを生産した場合の期待効果 E[c]（設計要件の式そのもの）:
        ///   E[c] = Σ_target share[target] × Multiplier(c, target) × survivability(c, enemyMix)
        /// 第1項（Σ）はCombatMatchup上の「攻撃側=c」視点の期待火力（相手の構成比で重み付けした
        /// 相性倍率の期待値）。survivability項は下記Survivability()参照——「相手の構成に対してcが
        /// どれだけ狙われ弱いか」を1つの係数に潰した安い近似（counter-counterの見積り）。</summary>
        public static float ExpectedEffectiveness(UnitCategory c, float[] enemyShares)
        {
            float expectedAttack = 0f;
            for (int t = 0; t < enemyShares.Length; t++)
            {
                float share = enemyShares[t];
                if (share <= 0f) continue;
                expectedAttack += share * CombatMatchup.Multiplier(c, (UnitCategory)t);
            }
            return expectedAttack * Survivability(c, enemyShares);
        }

        /// <summary>survivability(c, mix) = 1 / (1 + Σ share[t] × Multiplier(t, c) / 2)。
        /// Σ項は「敵の構成比で重み付けした、敵からcへの期待被弾倍率」——cを狙い撃ちできる相性を
        /// 敵が多く持っているほど大きくなる。/2は「相性差の影響を弱める」安全弁（相性1.0のときの
        /// 分母への影響を抑え、極端な相性(2.0倍等)だけでスコアが吹き飛ばないようにする）。
        /// 1/(1+x)の形にすることで、値は常に(0,1]に収まり、相性が悪いほど滑らかに0へ近づく
        /// （counter-counter項: 「相手がこの兵科を狩れる兵科を多く持っているなら、この兵科は
        /// 選びにくくする」という2次的な読み合いの安価な近似）。</summary>
        public static float Survivability(UnitCategory c, float[] enemyShares)
        {
            float expectedIncomingMultiplier = 0f;
            for (int t = 0; t < enemyShares.Length; t++)
            {
                float share = enemyShares[t];
                if (share <= 0f) continue;
                expectedIncomingMultiplier += share * CombatMatchup.Multiplier((UnitCategory)t, c);
            }
            return 1f / (1f + expectedIncomingMultiplier / 2f);
        }

        /// <summary>Tierの「質vs量」評価: value(tier) = sqrt(HP × Attack) / Cost。
        /// sqrt(HP×Attack)は「継続的に与えられるダメージ量×耐えられる時間」の粗い近似
        /// （Lanchesterの二次法則が示す「戦力は質×量の二乗で効く」を、1体あたりの実効戦力指標として
        /// crudeに落とし込んだもの。HPとAttackを対称に扱うため、片方だけ極端に高い/低いユニットを
        /// 過大/過小評価しにくい）。それをCostで割ることで「1コストあたりの実効戦力」＝
        /// コスト効率を表す。TierScalingはHP+35%/Attack+40%/Cost+60%（1Tierあたり）なので、
        /// sqrt(HP×Attack)の成長(約+37.5%/Tier)はCostの成長(+60%/Tier)に追いつかず、この指標だけを
        /// 見ると常にTier1が最もコスト効率が良い（＝物量で押す方が「素の効率」は高いという、
        /// このゲームの意図的なTierコストカーブそのものを表す）。AiProductionPolicyはこの値を
        /// 候補Tier群の中でHedgePickにかけることで、常にTier1へ張り付くのではなく、時には
        /// 効率で劣る高Tierへの投資（層の厚み・生存性・研究先行への布石）も選べるようにする。</summary>
        public static float TierValue(UnitType t)
        {
            return (float)System.Math.Sqrt((double)t.MaxHP * t.Attack) / t.Cost;
        }

        /// <summary>決定的な整数ハッシュ（MurmurHash3のfinalizer相当）。AiProductionPolicy.Hashと同じ
        /// 手法（System.Random不使用、同じ入力には常に同じ出力）。HedgePick系の確率的抽選の
        /// 乱数源として使う。</summary>
        public static uint Hash(uint x)
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

        /// <summary>scores[0..length)を降順に安定ソートした添字配列を返す（同点はscores配列の
        /// 元の並び順を保つ）。HedgeOrder/HedgePickIndexの下請け。</summary>
        private static int[] SortDescendingIndices(float[] scores, int length)
        {
            int[] order = new int[length];
            for (int i = 0; i < length; i++) order[i] = i;

            for (int i = 1; i < length; i++)
            {
                int key = order[i];
                float keyScore = scores[key];
                int j = i - 1;
                while (j >= 0 && scores[order[j]] < keyScore)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }
            return order;
        }

        /// <summary>降順にソート済みのorder配列の先頭topCount件から、score²に比例した重みで
        /// seedハッシュにより1件を選び、order配列中での位置（0..topCount-1）を返す。
        /// これが設計要件の「ヘッジ（混合戦略）」の核: 常にargmax(位置0)だけを選ぶと相手から
        /// 完全に読み切られる（＝旧来の「単調な生産」そのもの）ため、2位・3位にも常に一定の
        /// 出現確率を残す。score²（線形より鋭い重み）にする理由: 最有力候補への偏りは残しつつ、
        /// 僅差の2位・3位にも十分な出現頻度を与え、「大抵はbest-response、時々2番手/3番手」という
        /// 狙った分布に近づける。上位の合計スコアが0以下（全滅・相性データ皆無等の縮退ケース）
        /// なら常に最有力候補(位置0)を返す。</summary>
        private static int PickHedgedPosition(float[] scores, int[] order, int topCount, uint seed)
        {
            if (topCount <= 1) return 0;

            float totalWeight = 0f;
            for (int i = 0; i < topCount; i++)
            {
                float s = scores[order[i]];
                float w = s > 0f ? s * s : 0f;
                totalWeight += w;
            }
            if (totalWeight <= 0f) return 0;

            uint h = Hash(seed);
            float roll = (h % 1000000u) / 1000000f * totalWeight;

            float cumulative = 0f;
            for (int i = 0; i < topCount; i++)
            {
                float s = scores[order[i]];
                float w = s > 0f ? s * s : 0f;
                cumulative += w;
                if (roll < cumulative) return i;
            }
            return topCount - 1;
        }

        /// <summary>candidates[i]のスコアがscores[i]であるとき、上位min(3,length)件の中からヘッジ抽選で
        /// 1件を選び、それを先頭に置いた「優先順位」配列を返す（残りはスコア降順のまま続く）。
        /// AiProductionPolicy.Decideの兵科選択で使う: 先頭（ヘッジ抽選の結果）から順に「実際に
        /// このTierで買えるか」を試し、買えなければ次点へフォールバックする（旧来のcross-category
        /// fallbackと同じ安全網を、ヘッジされた優先順位の上でも維持する）。</summary>
        public static UnitCategory[] HedgeOrder(UnitCategory[] candidates, float[] scores, uint seed)
        {
            int n = candidates.Length;
            int[] order = SortDescendingIndices(scores, n);
            int topCount = n < 3 ? n : 3;
            int hedgePos = PickHedgedPosition(scores, order, topCount, seed);

            UnitCategory[] result = new UnitCategory[n];
            result[0] = candidates[order[hedgePos]];
            int w2 = 1;
            for (int i = 0; i < n; i++)
            {
                if (i == hedgePos) continue;
                result[w2++] = candidates[order[i]];
            }
            return result;
        }

        /// <summary>scores[0..length)の上位min(3,length)件からヘッジ抽選で1件選び、そのscores配列上の
        /// 添字を返す。AiProductionPolicy.ChooseTierHedgedのTier選択（質vs量のヘッジ）で使う。</summary>
        public static int HedgePickIndex(float[] scores, int length, uint seed)
        {
            int[] order = SortDescendingIndices(scores, length);
            int topCount = length < 3 ? length : 3;
            int hedgePos = PickHedgedPosition(scores, order, topCount, seed);
            return order[hedgePos];
        }
    }
}
