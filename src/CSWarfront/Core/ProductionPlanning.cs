namespace CSWarfront.Core
{
    /// <summary>各勢力が軍資金を使って所有基地の生産キューを補充する（純ロジック・決定的）。
    /// Task46: 選定ロジックは AiProductionPolicy.Decide に切り出した（「今払える中で一番高い
    /// ユニット」という旧ルールは終盤の歩兵スパムを招いていたため）。</summary>
    public static class ProductionPlanning
    {
        public const int QueueCap = 2;             // 1基地あたり最大キュー長

        /// <summary>Task97（実機フィードバック「戦闘が進むと重くなる」）: 勢力あたりの生存ユニット数が
        /// この値以上の間、その勢力の自動生産（AutoProduce基地のキュー補充）を止める。実測で総690体
        /// （Blue単独355体）まで無制限に膨張し、交戦判定・表示同期の負荷が支配的になっていたための
        /// 上限。研究投資もこのゲートの内側にあるため一緒に止まるが、ユニットが消耗して上限を割れば
        /// 次の経済tickから自動的に再開する。手動生産（AutoProduce=OFF基地のUI操作）とミサイル建造は
        /// 対象外（プレイヤーの明示操作を妨げない）。</summary>
        public const int MaxUnitsPerFaction = 150;

        /// <summary>1基地・1tickあたりの意思決定回数の上限（防御的）。研究への投資はキューを
        /// 消費しないため、理論上は「研究ばかり選ばれ続ける」限りループが続きうる。Treasuryが
        /// ResearchInvestPerDecisionずつ減っていくため実際には有限だが、念のため上限を設ける
        /// （WarState.MaxRecentShotsPerTickと同じ、無限ループへの防御的上限という設計方針）。</summary>
        private const int MaxDecisionsPerBasePerTick = 20;

        public static void Advance(WarState state)
        {
            // Task97: 勢力別の生存ユニット数を一度だけ数える（+1はInvader勢力のぶん）。
            var aliveCounts = new int[Faction.InvaderFactionId + 1];
            for (int ui = 0; ui < state.Units.Count; ui++)
            {
                UnitInstance u = state.Units[ui];
                if (u.IsAlive && u.FactionId < aliveCounts.Length) aliveCounts[u.FactionId]++;
            }

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated) continue;
                bool atUnitCap = f.Id < aliveCounts.Length && aliveCounts[f.Id] >= MaxUnitsPerFaction;

                for (int bi = 0; bi < state.Bases.Count; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (!b.AutoProduce) continue; // Task34: プレイヤーが手動管理を選んだ基地はAIが触らない

                    // Task63: ミサイル基地はユニットのQueueを一切使わない（SpawnableDomains=Noneのため
                    // 以下のAiProductionPolicy.Decideも常にNoneしか返せない）。代わりにMissileStockpile経由で
                    // 「未着手なら1発分の建造を開始する」を試みるだけの単純な処理に分岐する。
                    if (b.Type == BaseType.MissileBase)
                    {
                        MissileStockpile.TryBuildMissile(state, b.BaseId);
                        continue;
                    }

                    // Task97: ユニット上限に達している勢力は自動生産を止める（コメントはMaxUnitsPerFaction参照。
                    // ミサイル建造は上で済ませているため対象外）。
                    if (atUnitCap) continue;

                    // seedの由来: (勢力Id, 基地Id, この基地でこのtick中に下した意思決定の通し番号)。
                    // 同じ基地・同じtickでも決定のたびに種を変え、研究/生産の選択が単調に固定化しない
                    // ようにする（Task46）。
                    uint decisionCount = 0;
                    for (int attempts = 0; b.Queue.Count < QueueCap && attempts < MaxDecisionsPerBasePerTick; attempts++)
                    {
                        uint seed = MakeSeed(f.Id, b.BaseId, decisionCount);
                        decisionCount++;

                        AiDecision decision = AiProductionPolicy.Decide(state, f, b, seed);
                        if (decision.Choice == AiSpendChoice.None) break; // 何も買えない・投資できない

                        if (decision.Choice == AiSpendChoice.Research)
                        {
                            // Decideは事前にTreasury>=ResearchReserveを確認済みなのでTryInvestは
                            // 通常成功するが、防御的にfalseなら打ち切る。
                            if (!Research.TryInvest(f, AiProductionPolicy.ResearchInvestPerDecision)) break;
                            Research.TryUnlockNext(f); // 足りていれば即座にTierを1つ解禁
                            continue; // キュー枠は消費していないので同じ枠へ再挑戦
                        }

                        UnitType type = state.Types.Get(decision.TypeKey);
                        if (type == null) break;
                        // Task61: 防御的ガード。AiProductionPolicy.Decideは既に基地のSpawnableDomainsに
                        // 応じた兵科構成表しか選ばないため通常は不要だが、将来の実装ミスで領域不一致の
                        // TypeKeyが返ってきても、ここでキュー投入自体をブロックする（二重の安全網）。
                        if (!DomainMaskUtil.Contains(b.SpawnableDomains, type.Domain)) break;
                        if (!f.TrySpend(type.Cost)) break;
                        b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
                    }
                }
            }
        }

        private static uint MakeSeed(byte factionId, ushort baseId, uint decisionCount)
        {
            unchecked
            {
                uint h = factionId;
                h = h * 2654435761u + baseId;
                h = h * 2654435761u + decisionCount;
                return h;
            }
        }
    }
}
