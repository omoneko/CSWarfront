namespace CSWarfront.Core
{
    public static class AiTargeting
    {
        /// <summary>Task59: 宿敵(Nemesis)勢力が所有する基地が1つでもあれば、その中で最近接のものを
        /// 通常のHostile所有基地より優先して返す。宿敵所有基地が無ければ従来通り最近接のHostile所有
        /// 基地を返す（宿敵が存在しない場合は挙動が完全に従来のまま）。</summary>
        public static MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from)
        {
            MilitaryBase bestHostile = null; float bestHostileDist = float.MaxValue;
            MilitaryBase bestNemesis = null; float bestNemesisDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                Relation r = state.Relations.Get(factionId, b.OwnerFactionId.Value);
                if (!r.IsHostile()) continue;
                float d = from.HorizontalDistanceTo(b.Position);

                if (r == Relation.Nemesis)
                {
                    if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = b; }
                }
                else
                {
                    if (d < bestHostileDist) { bestHostileDist = d; bestHostile = b; }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }
    }

    public static class InvasionOrders
    {
        /// <summary>道路スナップ判定に使う最大距離（水平）。これより道路から離れているユニット/基地は直線移動にフォールバック。</summary>
        public const float PathSnapRadius = 200f;

        /// <summary>目的地が変わったとみなす閾値（X/Z）。これ未満の差は同一目的地として扱い経路を再利用する。</summary>
        private const float TargetChangeEpsilon = 1f;

        /// <summary>FindPath失敗後、同じユニットで再試行するまでのクールダウン（ゲーム内時間）。
        /// 到達不能なユニットが毎tickフルA*を再実行して予算を独占するのを防ぐ（Task23レビューImportant）。</summary>
        public const float PathRetryFailCooldownHours = 2f;

        /// <summary>経路探索の辺コストに掛けるジッタの最大割合（0.35 = 各辺が最大35%長く見える場合がある）。
        /// ユニットごとに(seed=InstanceId)決定的に導かれるため、全員が同一の最短経路に密集せず、
        /// 一部は並行する別路を「好み」として選ぶようになる。値を上げるほど遠回りが大きくなる
        /// （0.35は「並行する別路を選ぶ程度」を狙ったチューニング値。上げすぎると不合理な大回りになる）。</summary>
        public const float PathJitter = 0.35f;

        /// <summary>Task58: 自勢力の所有基地からこの水平距離以内に生きている外部脅威（ゴジラ/エイリアン）
        /// がいる場合、非プレイヤーユニットは敵基地への進軍より脅威への迎撃を優先する
        /// （FindDivertThreat参照）。到着後はThreatCombatStepが通常通り交戦する。</summary>
        public const float ThreatDivertRadius = 600f;

        /// <summary>当該勢力の非交戦ユニットに、各自位置から最寄りの敵基地へ進軍命令を与える。
        /// state.Roadsが供給されていれば道路経路(A*)も計算する（1回の呼び出しでmaxPathComputations件まで）。
        /// FindPathに失敗したユニットはPathRetryCooldownが尽きるまで再試行しない（予算を消費しない）。
        /// Task48: プレイヤーが Hold/RallyHold を指示したユニットは常にスキップする（AIが目標/経路を
        /// 一切上書きしない）。AiControlled/FreeAdvance のみ従来通り対象になる（FreeAdvanceはAIが
        /// 目標基地を更新してよいが、プレイヤーが別命令を出すまで自由進撃モード自体は変わらない）。
        /// Task58: 自勢力の領土（所有基地）の近くに外部脅威がいる間は、対象ユニット全員がそちらへ
        /// 迂回する（敵基地への進軍より優先）。脅威が消える/撃破されると、次回の呼び出しから
        /// 自動的に通常の敵基地進軍へ戻る（状態を持たず毎回再判定するだけなので「revert」に
        /// 特別なロジックは不要）。</summary>
        public static void AssignAdvance(WarState state, byte factionId, float dt, int maxPathComputations = 4)
        {
            int pathComputations = 0;
            WorldPos? divertTarget = FindNearbyThreatToOwnTerritory(state, factionId);
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                if (u.State == UnitState.Engaging) continue;

                u.PathRetryCooldown -= dt;
                if (u.PathRetryCooldown < 0f) u.PathRetryCooldown = 0f;

                WorldPos targetPos;
                if (divertTarget.HasValue)
                {
                    targetPos = divertTarget.Value;
                }
                else
                {
                    var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position);
                    if (target == null) continue;
                    targetPos = target.Position;
                }

                u.OrderTargetPos = targetPos;
                u.State = UnitState.Moving;

                if (u.PathTarget.HasValue && !IsSameTarget(u.PathTarget.Value, u.OrderTargetPos.Value))
                    u.ClearPath();

                if (state.Roads != null && u.Path == null && u.PathRetryCooldown <= 0f)
                {
                    if (pathComputations >= maxPathComputations) continue; // 予算超過。次回に持ち越し

                    pathComputations++;
                    // InstanceIdをseedにすることでユニットごとに安定した「好みの遠回り」を持たせる。
                    // InstanceIdは一意かつユニットの生存中不変なので、同じユニットが再試行しても
                    // 同じ経路を選び続け、フリップフロップ（毎回別の経路を選び直す）が起きない。
                    var path = state.Roads.FindPath(u.Position, u.OrderTargetPos.Value, PathSnapRadius, u.InstanceId, PathJitter);
                    u.Path = path;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                    u.PathRetryCooldown = path == null ? PathRetryFailCooldownHours : 0f;
                }
            }
        }

        /// <summary>Task58: factionIdが所有する基地のいずれかからThreatDivertRadius以内（水平距離）に
        /// 生きている（未撃破の）、かつ当該勢力にとって敵対（Task59: WarState.ThreatRelationsが
        /// Hostile/Nemesis）な外部脅威が1体でもいれば、その脅威の位置を返す。
        /// Task59: 宿敵(Nemesis)関係の脅威が対象内に1体でもあれば、通常のHostile脅威より優先し、
        /// 自勢力の基地への最短距離が最小のものを返す。宿敵が無ければ、従来通り同条件で最短距離が
        /// 最小の通常Hostile脅威を返す（宿敵が存在しない場合は挙動が完全に従来のまま）。
        /// どちらも無ければnull（＝通常の敵基地進軍のまま）。</summary>
        private static WorldPos? FindNearbyThreatToOwnTerritory(WarState state, byte factionId)
        {
            WorldPos? bestHostile = null; float bestHostileDist = float.MaxValue;
            WorldPos? bestNemesis = null; float bestNemesisDist = float.MaxValue;

            for (int t = 0; t < state.Threats.Count; t++)
            {
                var threat = state.Threats[t];
                if (threat.IsDefeated) continue;

                Relation rel = state.ThreatRelations.Get(factionId, threat.Kind);
                if (!rel.IsHostile()) continue;

                float nearestOwnedBaseDist = float.MaxValue;
                for (int b = 0; b < state.Bases.Count; b++)
                {
                    var ownedBase = state.Bases[b];
                    if (ownedBase.OwnerFactionId != factionId) continue;
                    float d = threat.Position.HorizontalDistanceTo(ownedBase.Position);
                    if (d < nearestOwnedBaseDist) nearestOwnedBaseDist = d;
                }
                if (nearestOwnedBaseDist > ThreatDivertRadius) continue;

                if (rel == Relation.Nemesis)
                {
                    if (nearestOwnedBaseDist < bestNemesisDist) { bestNemesisDist = nearestOwnedBaseDist; bestNemesis = threat.Position; }
                }
                else
                {
                    if (nearestOwnedBaseDist < bestHostileDist) { bestHostileDist = nearestOwnedBaseDist; bestHostile = threat.Position; }
                }
            }
            return bestNemesis ?? bestHostile;
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b)
        {
            return System.Math.Abs(a.X - b.X) < TargetChangeEpsilon && System.Math.Abs(a.Z - b.Z) < TargetChangeEpsilon;
        }
    }
}
