namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: 輸送ヘリの兵站ループ（設計§2）。トラック（SupplyTruckStep）の空路版＋歩兵空輸。
    ///
    /// サイクル（毎tickステートレス導出、道路不要の直行）:
    ///   1. 空荷: 母基地（最寄りの自軍陸軍基地）へ帰投し、圏内で物資を積載（勢力プールから
    ///      CargoSupplyぶん＝SupplyLoad 1.0）＋圏内のIdle歩兵系を最大MaxPassengers体搭乗させる
    ///   2. 積載あり: 目的地（備蓄に空きのある最寄り自軍SupplyDepot。無ければ「交戦中の味方
    ///      陸上ユニット」の最寄り＝前線）へ直行
    ///   3. 到着（DropRadius）: 物資をDepotのStoredSuppliesへ荷下ろし（前線降下の場合は物資は
    ///      降ろさず持ち帰る）、歩兵を降機（周囲へ決定的散開・State=Idle→次のAssignAdvanceが
    ///      再任務）
    ///   4. 空になったら母基地へ帰投
    ///
    /// 搭乗（CarriedByUnitId）: 搭乗中ユニットは全stepから除外され標的にもならない。位置は毎tick
    /// このstepが追従させ、ヘリが撃墜されたら道連れ（無音消滅＝積荷・搭乗兵ロスト）。
    /// 維持はトラックと同型（陸軍基地1機・勢力上限MaxHelisPerFaction、経済tickでMaintainHelis、
    /// Invader除外）。プレイヤーのHold/RallyHold指示があるヘリには触れない。
    /// </summary>
    public static class TransportHeliStep
    {
        public const int MaxHelisPerFaction = 6;
        public const int HelisPerArmyBase = 1;

        /// <summary>満載（SupplyLoad=1）が運ぶ補給物資量（トラック1台=30の2倍）。</summary>
        public const float CargoSupply = 60f;

        public const int MaxPassengers = 3;

        /// <summary>母基地での積載・搭乗判定半径（基地の補給圏と同じ）。</summary>
        public const float PickupRadius = 200f;

        /// <summary>目的地への到着＝荷下ろし/降機判定半径。</summary>
        public const float DropRadius = 60f;

        /// <summary>降機時の散開半径。</summary>
        public const float DisembarkScatter = 20f;

        /// <summary>経済tickごと: 陸軍基地が輸送ヘリを自動維持する（SupplyTruckStep.MaintainTrucksと同型）。</summary>
        public static void MaintainHelis(WarState state)
        {
            UnitType heliType = state.Types.Get(AirUnitRoster.TypeKey(UnitCategory.TransportHelicopter, 1));
            if (heliType == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated || f.Id == Faction.InvaderFactionId) continue;

                int helis = CountHelis(state, f.Id);
                if (helis >= MaxHelisPerFaction) continue;

                for (int bi = 0; bi < state.Bases.Count && helis < MaxHelisPerFaction; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (b.Type != BaseType.Army) continue;
                    if (CountHelisNearBase(state, f.Id, b) >= HelisPerArmyBase) continue;
                    if (!UnitCosts.TryPay(f, heliType, f.Treasury)) break;

                    var u = new UnitInstance(state.AllocInstanceId(), heliType.TypeKey, f.Id, heliType.MaxHP,
                        new WorldPos(b.Position.X, b.Position.Y, b.Position.Z));
                    u.State = UnitState.Idle;
                    state.Units.Add(u);
                    helis++;
                }
            }
        }

        public static void Advance(WarState state, float dt)
        {
            // 1) 搭乗中ユニットの位置追従と、運搬役死亡時の道連れ（列車の搭乗もここで一括処理する
            //    ——CarriedByUnitIdは運搬役の種別を問わない共通機構のため）。
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.CarriedByUnitId.HasValue) continue;
                UnitInstance carrier = state.FindUnit(u.CarriedByUnitId.Value);
                if (carrier == null || !carrier.IsAlive)
                {
                    // 道連れ（無音消滅: KillEventを積まない。スタック消滅と同じ方針）。
                    u.CurrentHP = 0f;
                    u.State = UnitState.Dead;
                    u.CarriedByUnitId = null;
                    continue;
                }
                u.Position = carrier.Position;
            }

            // 2) 輸送ヘリ本体のサイクル。
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance heli = state.Units[i];
                if (!heli.IsAlive || heli.IsCarried) continue;
                UnitType type = state.Types.Get(heli.TypeKey);
                if (type == null || type.Category != UnitCategory.TransportHelicopter) continue;
                if (heli.Order == UnitOrder.Hold || heli.Order == UnitOrder.RallyHold) continue;

                MilitaryBase home = FindNearestOwnArmyBase(state, heli);
                if (home == null) { Wait(heli); continue; }

                int passengers = CountPassengers(state, heli.InstanceId);
                bool hasCargo = heli.SupplyLoad > 0f || passengers > 0;

                if (!hasCargo)
                {
                    AdvanceEmptyHeli(state, heli, home, passengers);
                    continue;
                }

                AdvanceLoadedHeli(state, heli, home, passengers);
            }
        }

        private static void AdvanceEmptyHeli(WarState state, UnitInstance heli, MilitaryBase home, int passengers)
        {
            if (heli.Position.HorizontalDistanceTo(home.Position) > PickupRadius)
            {
                FlyTo(heli, home.Position);
                return;
            }

            Faction f = state.FindFaction(heli.FactionId);

            // 物資の積載（プールが空でも歩兵輸送だけは成立させる）。
            if (f != null && f.SupplyStock > 0f)
            {
                float loadable = f.SupplyStock / CargoSupply;
                float load = loadable < 1f ? loadable : 1f;
                f.TrySpendSupply(load * CargoSupply);
                heli.SupplyLoad = load;
                heli.SupplyLoadFromDepot = false;
            }

            // 歩兵の搭乗（基地圏内のIdle歩兵系、AiControlled/FreeAdvanceのみ）。
            int boarded = passengers;
            for (int i = 0; i < state.Units.Count && boarded < MaxPassengers; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried || u.InstanceId == heli.InstanceId) continue;
                if (u.FactionId != heli.FactionId) continue;
                if (u.State != UnitState.Idle) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || (t.Category != UnitCategory.Infantry && t.Category != UnitCategory.MechInfantry)) continue;
                if (u.Position.HorizontalDistanceTo(heli.Position) > PickupRadius) continue;

                u.CarriedByUnitId = heli.InstanceId;
                u.OrderTargetPos = null;
                u.ClearPath();
                boarded++;
            }

            Wait(heli); // 積むものが無ければ待機、積めたら次tickのAdvanceLoadedHeliが目的地を決める
        }

        private static void AdvanceLoadedHeli(WarState state, UnitInstance heli, MilitaryBase home, int passengers)
        {
            // 目的地: 備蓄に空きのある最寄りDepot → 無ければ交戦中の味方陸上ユニットの最寄り（前線）。
            MilitaryBase depot = FindNearestDepotWithRoom(state, heli);
            WorldPos? dest = depot != null ? depot.Position : (WorldPos?)null;
            if (!dest.HasValue) dest = FindFrontline(state, heli);

            if (!dest.HasValue)
            {
                // 目的地なし: 歩兵だけ積んでいるなら母基地で降機、物資は持ったまま待機。
                if (passengers > 0 && heli.Position.HorizontalDistanceTo(home.Position) <= PickupRadius)
                    Disembark(state, heli);
                Wait(heli);
                return;
            }

            if (heli.Position.HorizontalDistanceTo(dest.Value) > DropRadius)
            {
                FlyTo(heli, dest.Value);
                return;
            }

            // 到着: Depotなら物資を荷下ろし。歩兵はどちらの目的地でも降機。
            if (depot != null && heli.SupplyLoad > 0f)
            {
                float cap = FortificationRules.StoredSupplyCap(depot.Type);
                float room = cap - depot.StoredSupplies;
                float carried = heli.SupplyLoad * CargoSupply;
                float transfer = carried < room ? carried : room;
                depot.StoredSupplies += transfer;
                heli.SupplyLoad -= transfer / CargoSupply;
                if (heli.SupplyLoad < 0.001f) heli.SupplyLoad = 0f;
            }
            Disembark(state, heli);
            Wait(heli); // 空になっていれば次tickのAdvanceEmptyHeliが帰投させる
        }

        /// <summary>搭乗中の歩兵を降機させる（周囲へ決定的散開、State=Idle→次のAssignAdvanceが再任務）。</summary>
        private static void Disembark(WarState state, UnitInstance heli)
        {
            int n = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.CarriedByUnitId.HasValue || u.CarriedByUnitId.Value != heli.InstanceId) continue;
                // 決定的散開: 搭乗順に90度刻みのリング配置。
                float ox = (n % 2 == 0 ? 1f : -1f) * DisembarkScatter * ((n / 2) + 1) * 0.5f;
                float oz = (n % 4 < 2 ? 1f : -1f) * DisembarkScatter * 0.5f;
                u.CarriedByUnitId = null;
                u.Position = new WorldPos(heli.Position.X + ox, heli.Position.Y, heli.Position.Z + oz);
                u.State = UnitState.Idle;
                n++;
            }
        }

        private static WorldPos? FindFrontline(WarState state, UnitInstance heli)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried || u.FactionId != heli.FactionId) continue;
                if (u.State != UnitState.Engaging) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Land) continue;
                float d = heli.Position.HorizontalDistanceTo(u.Position);
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best != null ? best.Position : (WorldPos?)null;
        }

        private static void FlyTo(UnitInstance heli, WorldPos dest)
        {
            heli.OrderTargetPos = dest;
            heli.State = UnitState.Moving;
        }

        private static void Wait(UnitInstance heli)
        {
            heli.OrderTargetPos = null;
            heli.ClearPath();
            heli.State = UnitState.Idle;
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

        private static int CountPassengers(WarState state, uint heliId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
                if (state.Units[i].CarriedByUnitId.HasValue && state.Units[i].CarriedByUnitId.Value == heliId) count++;
            return count;
        }

        private static int CountHelis(WarState state, byte factionId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t != null && t.Category == UnitCategory.TransportHelicopter) count++;
            }
            return count;
        }

        private static int CountHelisNearBase(WarState state, byte factionId, MilitaryBase b)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Category != UnitCategory.TransportHelicopter) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= PickupRadius * 4f) count++;
            }
            return count;
        }
    }
}
