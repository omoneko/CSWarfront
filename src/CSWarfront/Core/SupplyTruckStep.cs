namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: 補給トラックの自動維持と兵站ループ（設計: 2026-08-03-economy-supply-design.md §4.3）。
    ///
    /// トラック（UnitCategory.SupplyTruck）は非武装の通常ユニットで、完全自動運用:
    ///   1. 基地（自勢力の陸軍基地）でSupplyStockから物資を積載する
    ///   2. 弾薬が減った味方陸上ユニット（基地圏内で自動補給されるものは除く）のうち
    ///      最も弾薬が少ないものへ道路経路で向かう
    ///   3. TransferRadius以内に着いたら周囲の味方陸上ユニットへ弾薬を転送する
    ///   4. 積載が空になったら基地へ戻って再積載（補給対象が無ければ基地付近で待機）
    ///
    /// 状態機械は持たない——毎tick、SupplyLoad と位置から行動を導出する（決定的・ステートレス、
    /// AssignAdvance/脅威迎撃と同じ設計）。AI進軍（AssignAdvance）と通常戦闘（CombatStep）からは
    /// 除外済み。プレイヤーがHold/RallyHoldを指示したトラックには一切触れない（部隊コマンド優先）。
    /// 撃破されれば積荷ごと失われる（特別処理は不要——護衛の価値を生む）。
    /// </summary>
    public static class SupplyTruckStep
    {
        /// <summary>勢力あたりのトラック上限（ユーザー仕様: 戦闘150体とは別枠で30台）。</summary>
        public const int MaxTrucksPerFaction = 30;

        /// <summary>陸軍基地1つが維持するトラック数（勢力上限の内側で）。</summary>
        public const int TrucksPerArmyBase = 2;

        /// <summary>積載/帰還完了とみなす基地からの距離。ResupplyStepの補給圏と揃える。</summary>
        public const float LoadRadius = ResupplyStep.ResupplyRadius;

        /// <summary>満載（SupplyLoad=1）にするのに消費する補給物資量。</summary>
        public const float SupplyPerTruckLoad = 30f;

        /// <summary>この距離以内の味方陸上ユニットへ弾薬を転送する。</summary>
        public const float TransferRadius = 60f;

        /// <summary>転送によるAmmo回復速度（ゲーム内1時間あたりの割合、1ユニットごと）。
        /// 基地圏内自動補給（25%/h）より速い＝前線へのお届け便としての価値を出す。</summary>
        public const float TransferPerHour = 0.5f;

        /// <summary>1ユニットをAmmo0→1まで回復させるのに消費する積載量（＝満載で5回ぶん）。</summary>
        public const float LoadPerFullReload = 0.2f;

        /// <summary>この弾薬割合を下回っている味方を配送対象とする。</summary>
        public const float NeedThreshold = 0.5f;

        /// <summary>1回のAdvanceで許す道路経路探索の回数（AssignAdvanceのmaxPathComputationsと同じ趣旨）。</summary>
        private const int MaxPathComputationsPerTick = 2;

        /// <summary>経済tickごと: 陸軍基地がトラックを自動維持する。勢力上限・基地あたり枠の内側で
        /// 1基地1台ずつスポーンを試みる（人的資源＋生産力で支払い。払えなければ補充されない）。</summary>
        public static void MaintainTrucks(WarState state)
        {
            UnitType truckType = state.Types.Get(LandUnitRoster.TypeKey(UnitCategory.SupplyTruck, 1));
            if (truckType == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated || f.Id == Faction.InvaderFactionId) continue; // Invaderは弾薬無限＝兵站不要

                int trucks = CountTrucks(state, f.Id);
                if (trucks >= MaxTrucksPerFaction) continue;

                for (int bi = 0; bi < state.Bases.Count && trucks < MaxTrucksPerFaction; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (b.Type != BaseType.Army) continue;
                    if (CountTrucksNearBase(state, f.Id, b) >= TrucksPerArmyBase) continue;
                    if (!UnitCosts.TryPay(f, truckType, f.Treasury)) break; // 資源切れ: この勢力は今tickは打ち切り

                    var u = new UnitInstance(state.AllocInstanceId(), truckType.TypeKey, f.Id, truckType.MaxHP,
                        new WorldPos(b.Position.X, b.Position.Y, b.Position.Z));
                    u.State = UnitState.Idle;
                    state.Units.Add(u);
                    trucks++;
                }
            }
        }

        /// <summary>毎tick: 全トラックの積載/配送/転送/帰還を進める（クラスコメント参照）。</summary>
        public static void Advance(WarState state, float dt)
        {
            int pathComputations = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.SupplyTruck) continue;
                if (u.Order == UnitOrder.Hold || u.Order == UnitOrder.RallyHold) continue; // 部隊コマンド優先

                // トラックはAssignAdvanceの対象外のため、経路再試行クールダウンの消化はここで行う。
                u.PathRetryCooldown -= dt;
                if (u.PathRetryCooldown < 0f) u.PathRetryCooldown = 0f;

                MilitaryBase home = FindNearestOwnArmyBase(state, u);
                if (home == null)
                {
                    // 陸軍基地を失った: その場で待機（基地を取り戻せば次tickから再開）。
                    u.OrderTargetPos = null;
                    u.ClearPath();
                    u.State = UnitState.Idle;
                    continue;
                }

                if (u.SupplyLoad <= 0f)
                {
                    AdvanceEmptyTruck(state, u, home, ref pathComputations);
                    continue;
                }

                AdvanceLoadedTruck(state, u, type, home, dt, ref pathComputations);
            }
        }

        /// <summary>空荷: 最寄りの積載元（陸軍基地=勢力プール、または備蓄のある補給拠点/貨物駅）へ
        /// 向かい、着いたら積載する（Task101: 拠点・駅からの積出に対応）。</summary>
        private static void AdvanceEmptyTruck(WarState state, UnitInstance u, MilitaryBase home, ref int pathComputations)
        {
            Faction f = state.FindFaction(u.FactionId);
            MilitaryBase source = FindNearestLoadSource(state, u, f);
            if (source == null)
            {
                // どこにも物資が無い: 陸軍基地付近で待機。
                if (u.Position.HorizontalDistanceTo(home.Position) > LoadRadius)
                    SetDestination(state, u, home.Position, ref pathComputations);
                else
                    Wait(u);
                return;
            }

            if (u.Position.HorizontalDistanceTo(source.Position) > LoadRadius)
            {
                SetDestination(state, u, source.Position, ref pathComputations);
                return;
            }

            bool fromDepot = FortificationRules.IsFortification(source.Type);
            float available = fromDepot ? source.StoredSupplies : f.SupplyStock;
            float room = 1f - u.SupplyLoad;
            float loadable = available / SupplyPerTruckLoad;
            float load = room < loadable ? room : loadable;
            if (load <= 0f) { Wait(u); return; }

            if (fromDepot) source.StoredSupplies -= load * SupplyPerTruckLoad;
            else f.TrySpendSupply(load * SupplyPerTruckLoad);
            u.SupplyLoad += load;
            u.SupplyLoadFromDepot = fromDepot; // 拠点由来の荷は拠点へ積み直さない（UnitInstanceのコメント参照）
            Wait(u); // 積載完了。次tickのAdvanceLoadedTruckが配送先を決める
        }

        /// <summary>Task101: 積載元の候補=自軍の陸軍基地（勢力プールに物資がある場合）＋
        /// 稼働中で備蓄のある自軍SupplyDepot/CargoStation。最寄りを返す（無ければnull）。</summary>
        private static MilitaryBase FindNearestLoadSource(WarState state, UnitInstance u, Faction f)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            bool poolHasStock = f != null && f.SupplyStock > 0f;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;

                bool valid;
                if (mb.Type == BaseType.Army) valid = poolHasStock;
                else if (mb.Type == BaseType.SupplyDepot || mb.Type == BaseType.CargoStation) valid = mb.StoredSupplies > 0f;
                else valid = false;
                if (!valid) continue;

                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>積載あり: 補給対象へ向かい、届いたら転送する。対象が無ければ基地付近で待機。</summary>
        private static void AdvanceLoadedTruck(WarState state, UnitInstance u, UnitType type, MilitaryBase home,
            float dt, ref int pathComputations)
        {
            UnitInstance target = FindNeediestLandUnit(state, u);
            if (target == null)
            {
                // Task101: 補給を必要とする味方がいない間は、補給拠点への備蓄輸送を行う
                // （勢力プール由来の荷のみ。拠点由来の荷の積み直しはしない＝無限シャッフル防止）。
                MilitaryBase depot = u.SupplyLoadFromDepot ? null : FindNearestDepotWithRoom(state, u);
                if (depot != null)
                {
                    if (u.Position.HorizontalDistanceTo(depot.Position) > LoadRadius)
                    {
                        SetDestination(state, u, depot.Position, ref pathComputations);
                        return;
                    }
                    // 荷下ろし: 備蓄の空きぶんだけ移す。
                    float cap = FortificationRules.StoredSupplyCap(depot.Type);
                    float roomSupplies = cap - depot.StoredSupplies;
                    float carried = u.SupplyLoad * SupplyPerTruckLoad;
                    float transfer = carried < roomSupplies ? carried : roomSupplies;
                    depot.StoredSupplies += transfer;
                    u.SupplyLoad -= transfer / SupplyPerTruckLoad;
                    if (u.SupplyLoad < 0.001f) u.SupplyLoad = 0f;
                    Wait(u);
                    return;
                }

                // 輸送先も無い: 基地付近まで戻って待機。
                if (u.Position.HorizontalDistanceTo(home.Position) > LoadRadius)
                    SetDestination(state, u, home.Position, ref pathComputations);
                else
                    Wait(u);
                return;
            }

            if (u.Position.HorizontalDistanceTo(target.Position) > TransferRadius)
            {
                SetDestination(state, u, target.Position, ref pathComputations);
                return;
            }

            // 到着: 周囲TransferRadius以内の味方陸上ユニット全員へ転送する（対象指定した1体に限らない
            // ——集団で前線に居る部隊へまとめて配る方が自然で、挙動も単純になる）。
            Wait(u);
            for (int i = 0; i < state.Units.Count && u.SupplyLoad > 0f; i++)
            {
                UnitInstance ally = state.Units[i];
                if (!IsResupplyCandidate(state, u, ally, 1f)) continue;
                if (u.Position.HorizontalDistanceTo(ally.Position) > TransferRadius) continue;

                float refill = TransferPerHour * dt;
                if (refill > 1f - ally.Ammo) refill = 1f - ally.Ammo;
                float loadCost = refill * LoadPerFullReload;
                if (loadCost > u.SupplyLoad)
                {
                    loadCost = u.SupplyLoad;
                    refill = loadCost / LoadPerFullReload;
                }
                ally.Ammo += refill;
                if (ally.Ammo > 1f) ally.Ammo = 1f;
                u.SupplyLoad -= loadCost;
            }
            if (u.SupplyLoad < 0.001f) u.SupplyLoad = 0f; // 端数を切って「空荷→帰還」へ確実に遷移させる
        }

        /// <summary>弾薬が最も少ない配送対象（NeedThreshold未満の味方陸上ユニット）。
        /// 同値はリスト先頭優先（決定的）。</summary>
        private static UnitInstance FindNeediestLandUnit(WarState state, UnitInstance truck)
        {
            UnitInstance best = null;
            float bestAmmo = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance ally = state.Units[i];
                if (!IsResupplyCandidate(state, truck, ally, NeedThreshold)) continue;
                if (ally.Ammo < bestAmmo) { bestAmmo = ally.Ammo; best = ally; }
            }
            return best;
        }

        /// <summary>トラックの補給対象か: 生存・同勢力・弾薬制の陸上ユニット（トラック自身は除く）で、
        /// Ammoがthreshold未満、かつ基地圏内自動補給（ResupplyStep）の圏外にいるもの。</summary>
        private static bool IsResupplyCandidate(WarState state, UnitInstance truck, UnitInstance ally, float threshold)
        {
            if (!ally.IsAlive || ally.FactionId != truck.FactionId || ally.InstanceId == truck.InstanceId) return false;
            if (ally.Ammo >= threshold) return false;
            UnitType allyType = state.Types.Get(ally.TypeKey);
            if (allyType == null || allyType.Domain != Domain.Land) return false;
            if (allyType.AmmoCombatHours <= 0f) return false; // 弾薬制の対象外（トラック含む）
            if (ResupplyStep.IsNearResupplyPoint(state, ally, allyType)) return false; // 基地圏内は基地が賄う
            return true;
        }

        /// <summary>Task101: 備蓄に空きのある自軍SupplyDepot（稼働中）のうち最寄り。無ければnull。
        /// CargoStationへの備蓄輸送は行わない（駅は鉄道輸送=TrainStepが満たす）。</summary>
        private static MilitaryBase FindNearestDepotWithRoom(WarState state, UnitInstance u)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (mb.Type != BaseType.SupplyDepot) continue;
                if (mb.StoredSupplies >= FortificationRules.StoredSupplyCap(mb.Type)) continue;
                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        private static MilitaryBase FindNearestOwnArmyBase(WarState state, UnitInstance u)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (mb.Type != BaseType.Army) continue;
                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>目的地を設定し、必要なら道路経路を計算する（AssignAdvanceの陸上分岐と同じ規約:
        /// 目的地が大きく変わったら経路を作り直し、失敗はPathRetryCooldownで抑制）。</summary>
        private static void SetDestination(WarState state, UnitInstance u, WorldPos dest, ref int pathComputations)
        {
            const float targetChangeEpsilon = 30f; // 補給対象は動くため、少々の移動では経路を作り直さない

            u.OrderTargetPos = dest;
            u.State = UnitState.Moving;

            if (u.PathTarget.HasValue &&
                (System.Math.Abs(u.PathTarget.Value.X - dest.X) > targetChangeEpsilon ||
                 System.Math.Abs(u.PathTarget.Value.Z - dest.Z) > targetChangeEpsilon))
                u.ClearPath();

            if (state.Roads != null && u.Path == null && u.PathRetryCooldown <= 0f &&
                pathComputations < MaxPathComputationsPerTick)
            {
                pathComputations++;
                var path = state.Roads.FindPath(u.Position, dest, InvasionOrders.PathSnapRadius,
                    u.InstanceId, InvasionOrders.PathJitter, float.MaxValue);
                u.Path = path;
                u.PathIndex = 0;
                u.PathTarget = dest;
                u.PathRetryCooldown = path == null ? InvasionOrders.PathRetryFailCooldownHours : 0f;
            }
        }

        private static void Wait(UnitInstance u)
        {
            u.OrderTargetPos = null;
            u.ClearPath();
            u.State = UnitState.Idle;
        }

        private static int CountTrucks(WarState state, byte factionId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t != null && t.Category == UnitCategory.SupplyTruck) count++;
            }
            return count;
        }

        /// <summary>この基地の「担当」トラック数。厳密な所属は持たないため、基地からLoadRadius×4以内の
        /// 自軍トラックを担当とみなす（維持スポーンの過剰生成を防ぐだけの緩い判定。配送で遠出している
        /// トラックが数え漏れても、勢力上限MaxTrucksPerFactionが最終的な歯止めになる）。</summary>
        private static int CountTrucksNearBase(WarState state, byte factionId, MilitaryBase b)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Category != UnitCategory.SupplyTruck) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= LoadRadius * 4f) count++;
            }
            return count;
        }
    }
}
