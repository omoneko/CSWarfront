namespace CSWarfront.Core
{
    /// <summary>ManualProduction.TryEnqueue/TryCancelLastのQueueResultと同じ「判定順序に沿ったenumを返す」
    /// スタイルを弾道ミサイル備蓄の発注に適用したもの（Task63）。QueueResult自体を再利用しないのは、
    /// UnknownType/TierLocked/WrongDomain等ユニット生産固有の値が弾道ミサイルには一切当てはまらないため
    /// （意味の合わない値を混在させるとAPI利用側の分岐が誤読を招く）。</summary>
    public enum MissileBuildResult
    {
        Ok,
        BaseNotFound,
        NotMissileBase,
        NoOwner,
        /// <summary>この基地は既に1発を建造中（MissileBuildProgress &gt; 0）。同時に複数発は建造できない
        /// （MilitaryBaseはQueueではなく単一の進捗フィールドしか持たないため、Task63の意図的な単純化）。</summary>
        AlreadyBuilding,
        /// <summary>StockpiledMissilesが既にMaxStockpileに達している。</summary>
        StockpileFull,
        NotAffordable
    }

    /// <summary>
    /// 弾道ミサイルの備蓄生産（Task63）。BaseType.MissileBase専用。通常のユニット生産
    /// （ProductionOrder/ProductionStep、MilitaryBase.Queue）とは別の仕組みにする: ミサイルは
    /// UnitTypeを持たない（UnitInstanceとして戦場に出るものではない）ため、Queue&lt;ProductionOrder&gt;を
    /// 再利用せず、MilitaryBase.StockpiledMissiles/MissileBuildProgressという2フィールドのみで完結させる
    /// （同時に1発しか建造できない単純なモデル）。
    ///
    /// UnityEngine非依存。決定的（乱数不使用）。
    /// </summary>
    public static class MissileStockpile
    {
        /// <summary>ミサイル1発の建造コスト（軍資金）。</summary>
        public const float MissileCost = 250f;

        /// <summary>建造に要するゲーム内時間。</summary>
        public const float MissileBuildHours = 24f;

        /// <summary>1基地あたりの備蓄上限。</summary>
        public const int MaxStockpile = 5;

        /// <summary>建造開始の瞬間にMissileBuildProgressへ設定する微小な正の値。0f（建造中でない）と
        /// 区別するためだけの印であり、Advance側の最初のdt加算で自然に上書きされる
        /// （MilitaryBase.MissileBuildProgressのXMLコメント参照）。</summary>
        private const float StartProgress = 0.0001f;

        /// <summary>この基地が現在ミサイルを建造中か（MissileBuildProgress &gt; 0）。</summary>
        public static bool IsBuilding(MilitaryBase b) { return b.MissileBuildProgress > 0f; }

        /// <summary>
        /// baseIdの基地でミサイル1発の建造を開始する（プレイヤー手動発注・Game層UI向け、Task63）。
        /// AiProductionPolicy相当のAI自動着手（ProductionPlanning.Advance内のMissileBase分岐）からも
        /// 同じ実装を再利用する。判定順序（先に失敗した方を返す）:
        ///  1. baseId の基地が存在するか -&gt; BaseNotFound
        ///  2. b.Type が BaseType.MissileBase か -&gt; NotMissileBase
        ///  3. 所有勢力がいるか（Faction解決含む） -&gt; NoOwner
        ///  4. 既に建造中でないか（IsBuilding） -&gt; AlreadyBuilding
        ///  5. StockpiledMissiles &lt; MaxStockpile か -&gt; StockpileFull
        ///  6. 所有勢力がMissileCostを払えるか（Faction.TrySpend。成功した場合のみ控除） -&gt; NotAffordable
        /// 全て通ればMissileBuildProgressをStartProgressへ設定しOkを返す。
        /// </summary>
        public static MissileBuildResult TryBuildMissile(WarState state, ushort baseId)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return MissileBuildResult.BaseNotFound;
            if (b.Type != BaseType.MissileBase) return MissileBuildResult.NotMissileBase;
            if (b.OwnerFactionId == null) return MissileBuildResult.NoOwner;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return MissileBuildResult.NoOwner; // 整合性が崩れている場合の防御

            if (IsBuilding(b)) return MissileBuildResult.AlreadyBuilding;
            if (b.StockpiledMissiles >= MaxStockpile) return MissileBuildResult.StockpileFull;
            if (!owner.TrySpend(MissileCost)) return MissileBuildResult.NotAffordable;

            b.MissileBuildProgress = StartProgress;
            return MissileBuildResult.Ok;
        }

        /// <summary>
        /// 建造の進捗を進める（simスレッド、MilitaryManager.OnSimTick経由で毎tick呼ばれる想定）。
        /// 建造中（IsBuilding）の基地のみを対象に、dt/MissileBuildHours分だけ進捗を加算し、1.0に達したら
        /// StockpiledMissiles（MaxStockpile上限、防御的クランプ）を1加算して進捗を0へ戻す。
        /// 所有者の有無は問わない（解体等で所有者を失っても、既に払い込み済みの建造は完了させる）。
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.MissileBase) continue;
                if (!IsBuilding(b)) continue;

                b.MissileBuildProgress += dt / MissileBuildHours;
                if (b.MissileBuildProgress >= 1f)
                {
                    if (b.StockpiledMissiles < MaxStockpile) b.StockpiledMissiles++;
                    b.MissileBuildProgress = 0f;
                }
            }
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
