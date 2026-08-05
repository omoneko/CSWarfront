namespace CSWarfront.Core
{
    public static class AiTargeting
    {
        /// <summary>Task59: 宿敵(Nemesis)勢力が所有する基地が1つでもあれば、その中で最近接のものを
        /// 通常のHostile所有基地より優先して返す。宿敵所有基地が無ければ従来通り最近接のHostile所有
        /// 基地を返す（宿敵が存在しない場合は挙動が完全に従来のまま）。
        ///
        /// Task64: domainを省略（またはDomain.Land/Airのまま呼ぶ）した従来の呼び出し元は、基地の
        /// BaseTypeを問わず最近接のHostile/Nemesis所有基地を返す従来通りの挙動を維持する。
        /// domain=Domain.Seaで呼んだ場合のみ、BaseType.Navyの基地に絞り込む——海上ユニットは
        /// 内陸のArmy/AirForce/MissileBaseへ直線で向かって座礁するのを防ぐため（ユーザー要望
        /// 「海上経路探索は敵海軍基地までの直線でひとまず」）。Nemesis優先の優先順位そのものは
        /// Navy基地の集合の中でも変わらず適用される。</summary>
        public static MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from, Domain domain = Domain.Land)
        {
            bool navyOnly = domain == Domain.Sea;
            MilitaryBase bestHostile = null; float bestHostileDist = float.MaxValue;
            MilitaryBase bestNemesis = null; float bestNemesisDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (!FortificationRules.IsTargetable(b.Type)) continue; // Task101: 塹壕は進軍目標にしない
                if (navyOnly && b.Type != BaseType.Navy) continue;
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

        /// <summary>Task92: 海上経路（SeaGrid）のスナップ半径。港・海軍基地は岸辺にあるため、
        /// 道路より大きめに取って最寄りの航行可能セルへ確実に載せる。</summary>
        public const float SeaPathSnapRadius = 400f;

        /// <summary>目的地が変わったとみなす閾値（X/Z）。これ未満の差は同一目的地として扱い経路を再利用する。</summary>
        private const float TargetChangeEpsilon = 1f;

        /// <summary>Task88: 移動する脅威（宿敵KAIJU等）を追う場合の経路再計算閾値。脅威は毎tick
        /// 少しずつ動くため、TargetChangeEpsilon(1m)のままでは呼び出しのたびにClearPath→再探索の
        /// 繰り返しになり、経路が定着せずほぼ常時オフロード直線移動になっていた（ユーザー報告
        /// 「宿敵への移動が道路上を通らない」の一因）。脅威がこの距離を超えて動いたときだけ
        /// 再計算する——経路の末端は多少古い位置になるが、経路が尽きた後の直線フォールバックが
        /// 常に最新のOrderTargetPosへ向かうため実害はない。</summary>
        private const float ThreatTargetChangeEpsilon = 100f;

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

            // Task105（鉄道の積極利用）: この勢力の稼働駅ペアを1回だけ列挙しておく（駅が無ければ空）。
            // 陸上ユニットの道路経路を「鉄道経由が得なら乗車駅へ」差し替えるのに使う。
            System.Collections.Generic.List<TrainStep.StationPair> railPairs =
                TrainStep.FindStationPairs(state, factionId);
            // Task96: 外部襲来（Invader勢力）の部隊が生きている間は、外部脅威（KAIJU/Alien）に次ぐ
            // 優先度で迎撃対象にする（敵基地への進軍より優先。詳細はFindInvaderToInterceptのコメント参照）。
            if (!divertTarget.HasValue) divertTarget = FindInvaderToIntercept(state, factionId);
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                if (u.State == UnitState.Engaging) continue;

                u.PathRetryCooldown -= dt;
                if (u.PathRetryCooldown < 0f) u.PathRetryCooldown = 0f;

                // Task61: Sea/Airは道路経路を一切使わない（MovementStepのSea/Air分岐はu.Pathを参照しない、
                // Core/MovementStep.cs参照）。道路パスファインディングの予算(maxPathComputations)を
                // 陸上ユニットのために温存するため、対象外のドメインではFindPathを一切試みない。
                UnitType type = state.Types.Get(u.TypeKey);
                Domain domain = type != null ? type.Domain : Domain.Land; // 型が引けない防御的ケースは従来通りLand扱い
                bool isLand = domain == Domain.Land;

                // Task99/101: 兵站ユニット（補給トラック/輸送ヘリ/軍用列車）と搭乗中ユニットは
                // AI進軍の対象外（それぞれ専用step＝SupplyTruckStep/TransportHeliStep/TrainStepが
                // 全移動を扱う。ここで敵基地への進軍目標を与えると非武装のまま前線へ突っ込んでしまう）。
                if (type != null && (type.Category == UnitCategory.SupplyTruck
                    || type.Category == UnitCategory.TransportHelicopter
                    || type.Category == UnitCategory.MilitaryTrain)) continue;
                if (u.IsCarried) continue;

                // Task99: 弾切れの航空・海上ユニットには新しい進軍目標を与えず、Idleへ戻して
                // 既存の帰還ロジック（MovementStep.ResolveHomeObjective）に自基地/空母へ連れ帰らせる。
                // 帰還先の基地圏内でResupplyStepが弾薬を回復させ、回復すれば（HasAmmoがtrueに戻れば）
                // 次の呼び出しから自動的に通常の進軍＝再出撃になる。陸上ユニットは対象外
                // （弾切れでも移動・拠点占領は可能という仕様のため、進軍は続ける）。
                if (!isLand && type != null && type.AmmoCombatHours > 0f && !AmmoRules.HasAmmo(u, type))
                {
                    u.OrderTargetPos = null;
                    u.ClearPath();
                    u.State = UnitState.Idle;
                    continue;
                }

                WorldPos targetPos;
                if (divertTarget.HasValue)
                {
                    targetPos = divertTarget.Value;
                }
                else
                {
                    // Task64: Sea(艦艇)は BaseType.Navy の敵対所有基地のみを狙う（内陸のArmy/AirForce/
                    // MissileBaseへ直線で向かって座礁するのを防ぐ）。
                    var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position, domain);
                    if (target != null)
                    {
                        targetPos = target.Position;
                    }
                    else
                    {
                        // Task78:「敵性の目標(脅威/敵基地)が1つも無い」不具合の修正。従来はここで単に
                        // continue しており、前回呼び出し時のOrderTargetPos/State/Pathがそのまま
                        // 残っていた——外部脅威(KAIJU等)が自然消滅した後もその地点へ進み続ける、
                        // 敵拠点を陥落させた後もその場所へ進み続ける、として報告された不具合の直接の原因。
                        // 対象が無い間は自勢力の最寄り所有基地へ撤収させる（無ければその場でIdle）。
                        // 状態を持たず毎回再判定するだけなので、次の呼び出しで新たな脅威/敵基地が
                        // 現れれば自動的に通常の進軍へ戻る（Task58と同じ設計）。
                        MilitaryBase home = FindNearestOwnedBase(state, factionId, u.Position);
                        if (home == null)
                        {
                            // 撤収先が無い（基地を1つも持たない勢力）: その場でIdleにし、
                            // 古い目標を追い続けないよう目標/経路をクリアする。
                            u.OrderTargetPos = null;
                            u.ClearPath();
                            u.State = UnitState.Idle;
                            continue;
                        }

                        targetPos = home.Position;
                        if (u.Position.HorizontalDistanceTo(targetPos) <= MovementStep.CoverArrivalDistance)
                        {
                            // 撤収完了: 自拠点付近まで戻ったのでIdleへ（既存のMovementStepの到着距離を再利用）。
                            u.OrderTargetPos = null;
                            u.ClearPath();
                            u.State = UnitState.Idle;
                            continue;
                        }
                    }
                }

                u.OrderTargetPos = targetPos;
                u.State = UnitState.Moving;

                // Task88: 移動する脅威を追っている間は、脅威が大きく動いたときだけ経路を組み直す
                // （閾値のコメント参照。基地目標は従来どおり1mで再計算＝挙動変更なし）。
                float sameTargetEps = divertTarget.HasValue ? ThreatTargetChangeEpsilon : TargetChangeEpsilon;
                if (u.PathTarget.HasValue && !IsSameTarget(u.PathTarget.Value, u.OrderTargetPos.Value, sameTargetEps))
                    u.ClearPath();

                if (isLand && state.Roads != null && u.Path == null && u.PathRetryCooldown <= 0f)
                {
                    if (pathComputations >= maxPathComputations) continue; // 予算超過。次回に持ち越し

                    pathComputations++;

                    // Task105: 鉄道経由の方が十分に得なら、道路経路の行き先を乗車駅へ差し替える
                    // （OrderTargetPos自体は最終目的地のまま＝駅到着後にBoardRadius内で列車に拾われ、
                    // 降車後は保持している最終目的地へ自走を再開する。列車が来なければ経路消化後の
                    // 直線フォールバックが従来どおり最終目的地へ向かわせるため詰まない）。
                    WorldPos pathGoal = u.OrderTargetPos.Value;
                    WorldPos boardingStation;
                    if (!divertTarget.HasValue && railPairs.Count > 0 &&
                        TrainStep.TryFindBoardingStation(railPairs, u.Position, pathGoal, out boardingStation))
                        pathGoal = boardingStation;
                    // InstanceIdをseedにすることでユニットごとに安定した「好みの遠回り」を持たせる。
                    // InstanceIdは一意かつユニットの生存中不変なので、同じユニットが再試行しても
                    // 同じ経路を選び続け、フリップフロップ（毎回別の経路を選び直す）が起きない。
                    // Task88: 脅威追撃時は目的地側のスナップ半径を無制限にする——脅威は道路から
                    // PathSnapRadius(200)以上離れていることが多く、従来はスナップ失敗→経路null→
                    // 全行程オフロード直線になっていた。無制限スナップなら「脅威に最も近い道路
                    // ノードまでは道路で行き、残りだけ直線」になる（ユーザー要望「可能な限り道路上を」）。
                    float destSnap = divertTarget.HasValue ? float.MaxValue : PathSnapRadius;
                    var path = state.Roads.FindPath(u.Position, pathGoal, PathSnapRadius,
                        u.InstanceId, PathJitter, destSnap); // Task105: pathGoal=最終目的地 or 乗車駅
                    u.Path = path;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                    u.PathRetryCooldown = path == null ? PathRetryFailCooldownHours : 0f;
                }
                else if (domain == Domain.Sea && state.SeaNav != null && u.Path == null && u.PathRetryCooldown <= 0f)
                {
                    // Task92: 海上ユニットはSeaGrid（航行グリッドA*）で経路を張る。予算は道路探索と共有。
                    // 経路が張れない（完全に陸に囲まれた目標等）場合は従来どおり直線＋壁沿い迂回のみで
                    // 進む（MovementStepSea）。
                    if (pathComputations >= maxPathComputations) continue;

                    pathComputations++;
                    var seaPath = state.SeaNav.FindPath(u.Position, u.OrderTargetPos.Value, SeaPathSnapRadius);
                    u.Path = seaPath;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                    u.PathRetryCooldown = seaPath == null ? PathRetryFailCooldownHours : 0f;
                }
            }
        }

        /// <summary>Task58: factionIdが所有する基地のいずれかからThreatDivertRadius以内（水平距離）に
        /// 生きている（未撃破の）、かつ当該勢力にとって敵対（Task59: WarState.ThreatRelationsが
        /// Hostile/Nemesis）な外部脅威が1体でもいれば、その脅威の位置を返す。
        /// Task59: 宿敵(Nemesis)関係の脅威が対象内に1体でもあれば、通常のHostile脅威より優先し、
        /// 自勢力の基地への最短距離が最小のものを返す。宿敵が無ければ、従来通り同条件で最短距離が
        /// 最小の通常Hostile脅威を返す（宿敵が存在しない場合は挙動が完全に従来のまま）。
        /// Task62: 宿敵(Nemesis)関係の脅威に限り、ThreatDivertRadiusによる距離制限を一切適用しない
        /// （陸海空いずれかの基地を1つでも所有していれば、マップ上どこにいてもその宿敵を敵基地進軍より
        /// 優先して迎撃対象にする）。基地を1つも持たない勢力（＝迎撃に向かわせる拠点自体が無い）は
        /// 宿敵であっても対象外のまま（従来のHostile同様、何もしない）。通常のHostile脅威は
        /// 引き続きThreatDivertRadius以内という条件を維持する（挙動変更なし）。
        /// どちらも無ければnull（＝通常の敵基地進軍のまま）。</summary>
        private static WorldPos? FindNearbyThreatToOwnTerritory(WarState state, byte factionId)
        {
            WorldPos? bestHostile = null; float bestHostileDist = float.MaxValue;
            WorldPos? bestNemesis = null; float bestNemesisDist = float.MaxValue;

            // Task62: 「基地を1つでも持っているか」は距離に関係ない全体条件なので、脅威ループの外で一度だけ判定する。
            bool ownsAnyBase = false;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                if (state.Bases[b].OwnerFactionId == factionId) { ownsAnyBase = true; break; }
            }

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

                if (rel == Relation.Nemesis)
                {
                    // Task62: 距離制限を一切課さない。基地を1つも持たない勢力だけは対象外にする
                    // （nearestOwnedBaseDistは基地が無ければ計算しようがなくfloat.MaxValueのまま＝
                    // ownsAnyBaseで明示的に弾く。基地が1つでもあれば必ず有限の距離が入る）。
                    if (!ownsAnyBase) continue;
                    if (nearestOwnedBaseDist < bestNemesisDist) { bestNemesisDist = nearestOwnedBaseDist; bestNemesis = threat.Position; }
                }
                else
                {
                    if (nearestOwnedBaseDist > ThreatDivertRadius) continue;
                    if (nearestOwnedBaseDist < bestHostileDist) { bestHostileDist = nearestOwnedBaseDist; bestHostile = threat.Position; }
                }
            }
            return bestNemesis ?? bestHostile;
        }

        /// <summary>Task96（実機フィードバック「配置したユニットが侵攻部隊の迎撃に向かわない」）:
        /// AIの進軍先は従来「敵対勢力の所有基地」と「外部脅威（state.Threats）」だけで、敵ユニット
        /// そのものを追う行動が存在しなかった。Invader勢力（外部襲来、Faction.InvaderFactionId）は
        /// 基地を持たず脅威リストにも載らないため、防衛側は誰も迎撃に向かわず、侵攻部隊が基地の
        /// 射程に入るまで放置される状態だった。
        ///
        /// このメソッドは、生きているInvaderユニットのうち「自勢力の最寄り所有基地に最も近い」
        /// ものの位置を返す（＝最も差し迫った脅威から順に潰す）。規則は宿敵(Nemesis)脅威への
        /// 迎撃（Task62）と同じ:
        ///  - 距離無制限（基地を1つでも所有していれば、マップ端の上陸地点へも積極的に出撃する）。
        ///  - 基地を1つも持たない勢力は対象外（迎撃に向かわせる拠点自体が無い）。
        ///  - 状態を持たず毎tick再判定するだけなので、侵攻部隊を殲滅すれば次の呼び出しから
        ///    自動的に通常の敵基地進軍へ戻る（Task58/Task62と同じ設計）。
        /// Invader勢力自身の部隊はこの迎撃の対象外の側（呼び出し元がInvaderならnull＝従来どおり
        /// 基地へ進軍する）。ターゲットが毎tick動く点は宿敵脅威と同じで、呼び出し元の
        /// ThreatTargetChangeEpsilon（100m動いたときだけ経路再計算）・目的地スナップ無制限が
        /// そのまま適用される。</summary>
        private static WorldPos? FindInvaderToIntercept(WarState state, byte factionId)
        {
            if (factionId == Faction.InvaderFactionId) return null;

            WorldPos? best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.FactionId != Faction.InvaderFactionId || !u.IsAlive) continue;

                // このInvaderユニットから自勢力の最寄り所有基地までの距離（基地が無ければMaxValueの
                // まま＝下の < 比較が一度も成立せず、結果的に「基地なし勢力は対象外」が成立する）。
                float nearestOwnedBaseDist = float.MaxValue;
                for (int b = 0; b < state.Bases.Count; b++)
                {
                    MilitaryBase ownedBase = state.Bases[b];
                    if (ownedBase.OwnerFactionId != factionId) continue;
                    float d = u.Position.HorizontalDistanceTo(ownedBase.Position);
                    if (d < nearestOwnedBaseDist) nearestOwnedBaseDist = d;
                }

                if (nearestOwnedBaseDist < bestDist) { bestDist = nearestOwnedBaseDist; best = u.Position; }
            }
            return best;
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b)
        {
            return IsSameTarget(a, b, TargetChangeEpsilon);
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b, float epsilon)
        {
            return System.Math.Abs(a.X - b.X) < epsilon && System.Math.Abs(a.Z - b.Z) < epsilon;
        }

        /// <summary>Task78: factionIdが所有する基地のうち、fromに最も近いものを返す（1つも所有していなければnull）。
        /// 距離がほぼ同点（TargetChangeEpsilon以内）の場合は本拠地(IsHeadquarters)を優先する——
        /// 複数の自拠点が同距離にある稀なケースでも、呼び出しのたびに同じ基地を選ぶ決定的な
        /// タイブレークにするため（乱数は使わない、Core全体の方針）。</summary>
        private static MilitaryBase FindNearestOwnedBase(WarState state, byte factionId, WorldPos from)
        {
            MilitaryBase best = null; float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                var mb = state.Bases[b];
                if (mb.OwnerFactionId != factionId) continue;
                float d = from.HorizontalDistanceTo(mb.Position);
                if (best == null || d < bestDist - TargetChangeEpsilon)
                {
                    best = mb; bestDist = d;
                }
                else if (System.Math.Abs(d - bestDist) <= TargetChangeEpsilon && mb.IsHeadquarters && !best.IsHeadquarters)
                {
                    best = mb; bestDist = d;
                }
            }
            return best;
        }
    }
}
