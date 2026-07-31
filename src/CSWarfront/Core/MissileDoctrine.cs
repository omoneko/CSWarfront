namespace CSWarfront.Core
{
    /// <summary>
    /// AI勢力の弾道ミサイル自動発射方針（Task63）。ProductionPlanning/AiProductionPolicyとは独立した
    /// 小さなヘルパーとして分離する（生産計画とは判断のタイミング・対象が異なるため）。
    ///
    /// 方針: 備蓄が1発以上あり、クールダウンが明けている自軍ミサイル基地は、
    ///  1. 宿敵(Nemesis)関係にある勢力の所有基地、または宿敵関係の外部脅威（KAIJU/Alien）があれば、
    ///     そのうち最近接のものへ距離無制限で発射する（AiTargeting.ChooseTargetBase/InvasionOrdersの
    ///     宿敵優先ロジックと同じ思想：宿敵は距離を問わず最優先）。
    ///  2. 宿敵が無ければ、通常の敵対(Hostile)所有基地のうち MinLaunchDistance を超えて離れている
    ///     ものの中で最近接を狙う（近距離は通常戦力の役割であり、ミサイルは遠距離専用という設計意図）。
    ///  3. どちらも無ければ発射しない。
    /// 決定的（乱数不使用）。UnityEngine非依存。
    /// </summary>
    public static class MissileDoctrine
    {
        /// <summary>これより近い通常Hostile基地はミサイルの対象にしない（宿敵は例外、距離無制限）。</summary>
        public const float MinLaunchDistance = 800f;

        /// <summary>1基地あたりの発射クールダウン（ゲーム内時間）。</summary>
        public const float LaunchCooldownHours = 12f;

        /// <summary>
        /// 全ミサイル基地のクールダウンを消化し、条件を満たす基地から自動発射する
        /// （simスレッド、MissileStep.Advanceの前後どちらでもよいが、MilitaryManager.OnSimTickでは
        /// 生産計画と同様のタイミング＝AI進軍命令の近くで呼ぶ想定）。プレイヤー勢力（Faction.IsPlayer）の
        /// 基地は対象外（プレイヤーはUI経由で手動発射する、Part1仕様）。
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.MissileBase) continue;

                if (b.MissileLaunchCooldownRemaining > 0f)
                {
                    b.MissileLaunchCooldownRemaining -= dt;
                    if (b.MissileLaunchCooldownRemaining < 0f) b.MissileLaunchCooldownRemaining = 0f;
                }

                if (b.OwnerFactionId == null) continue;
                if (!b.AutoLaunchMissiles) continue; // Task90: 手動発射に切り替えられた基地はAIが撃たない
                Faction f = state.FindFaction(b.OwnerFactionId.Value);
                if (f == null || f.IsPlayer || f.Eliminated) continue;
                if (b.StockpiledMissiles <= 0) continue;
                if (b.MissileLaunchCooldownRemaining > 0f) continue;

                WorldPos? target = ChooseTarget(state, f.Id, b.Position);
                if (!target.HasValue) continue;

                LaunchResult result = MissileStep.TryLaunch(state, b.BaseId, target.Value);
                if (result == LaunchResult.Ok) b.MissileLaunchCooldownRemaining = LaunchCooldownHours;
            }
        }

        /// <summary>宿敵（基地/外部脅威、距離無制限）を最優先、無ければMinLaunchDistanceを超える
        /// 最近接の通常Hostile基地を返す。どちらも無ければnull。</summary>
        private static WorldPos? ChooseTarget(WarState state, byte factionId, WorldPos from)
        {
            WorldPos? bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase ob = state.Bases[j];
                if (ob.OwnerFactionId == null) continue;
                if (state.Relations.Get(factionId, ob.OwnerFactionId.Value) != Relation.Nemesis) continue;
                float d = from.HorizontalDistanceTo(ob.Position);
                if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = ob.Position; }
            }

            for (int t = 0; t < state.Threats.Count; t++)
            {
                ExternalThreat threat = state.Threats[t];
                if (threat.IsDefeated) continue;
                if (state.ThreatRelations.Get(factionId, threat.Kind) != Relation.Nemesis) continue;
                float d = from.HorizontalDistanceTo(threat.Position);
                if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = threat.Position; }
            }

            if (bestNemesis.HasValue) return bestNemesis;

            WorldPos? bestHostile = null;
            float bestHostileDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase ob = state.Bases[j];
                if (ob.OwnerFactionId == null) continue;
                if (!state.Relations.Get(factionId, ob.OwnerFactionId.Value).IsHostile()) continue;
                float d = from.HorizontalDistanceTo(ob.Position);
                if (d <= MinLaunchDistance) continue; // 近距離は通常戦力の役割（ミサイルの対象外）
                if (d < bestHostileDist) { bestHostileDist = d; bestHostile = ob.Position; }
            }
            return bestHostile;
        }
    }
}
