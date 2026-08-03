namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: 補給物資の自動生産と基地圏内自動補給（設計: 2026-08-03-economy-supply-design.md §4.1-4.2）。
    ///
    /// 補給物資（Faction.SupplyStock）は勢力共通プール。経済tickごとにProduceSuppliesが生産力
    /// （不足時は資金代替、UnitCosts.FundsPerProductionと同レート）から自動生産して備蓄する。
    ///
    /// Advance（毎tick）は「自勢力基地からResupplyRadius以内」の味方ユニット（弾薬制対象のみ）の
    /// Ammoを、SupplyStockを消費しながらRefillPerHourで回復させる。空母（生存中）は航空機に
    /// 対してのみ同条件の補給点になる（艦載機の母艦再武装）。ストックが尽きたら回復は止まる。
    /// Invader勢力は弾薬無限（AmmoRules）のためAmmoが減らず、ここでも自然に対象外になる。
    /// </summary>
    public static class ResupplyStep
    {
        /// <summary>補給物資ストックの上限。</summary>
        public const float SupplyStockCap = 1000f;

        /// <summary>経済tick1回あたりの補給物資の自動生産量の上限（生産力が続く限り）。</summary>
        public const float SupplyPerEconomyTick = 50f;

        /// <summary>補給物資1あたりの生産力コスト。</summary>
        public const float ProductionPerSupply = 1f;

        /// <summary>基地/空母を中心とした自動補給の半径（m）。</summary>
        public const float ResupplyRadius = 200f;

        /// <summary>補給圏内でのAmmo回復速度（ゲーム内1時間あたりの割合）。</summary>
        public const float RefillPerHour = 0.25f;

        /// <summary>Ammoを0→1まで満タンにするのに消費する補給物資量。</summary>
        public const float SupplyPerFullReload = 10f;

        /// <summary>経済tickごと: 生産力（不足分は資金代替）から補給物資を自動生産する。
        /// 生産量はSupplyPerEconomyTickと「上限までの空き」の小さい方。原資が尽きたら
        /// そのぶんだけ生産する（部分生産）。</summary>
        public static void ProduceSupplies(Faction f)
        {
            if (f == null || f.Id == Faction.InvaderFactionId) return;

            float want = SupplyPerEconomyTick;
            float room = SupplyStockCap - f.SupplyStock;
            if (room < want) want = room;
            if (want <= 0f) return;

            // 生産力から払えるだけ払い、足りない分は資金で代替する（UnitCosts.TryPayと同じ優先順位）。
            float fromProduction = f.Production < want * ProductionPerSupply ? f.Production : want * ProductionPerSupply;
            float produced = fromProduction / ProductionPerSupply;
            f.TrySpendProduction(fromProduction);

            float shortfall = want - produced;
            if (shortfall > 0f)
            {
                float fundsAffordable = f.Treasury / (ProductionPerSupply * UnitCosts.FundsPerProduction);
                float fromFunds = shortfall < fundsAffordable ? shortfall : fundsAffordable;
                if (fromFunds > 0f)
                {
                    f.TrySpend(fromFunds * ProductionPerSupply * UnitCosts.FundsPerProduction);
                    produced += fromFunds;
                }
            }

            f.AddSupply(produced);
        }

        /// <summary>毎tick: 補給圏内の味方ユニットの弾薬を回復させる（クラスコメント参照）。</summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.Ammo >= 1f) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.AmmoCombatHours <= 0f) continue; // 弾薬制の対象外
                // Task100: Invaderは補給網を一切使えない（現地調達方式＝敵撃破でのみ回復、
                // AmmoRules.RewardInvaderKill）。占領した基地の圏内でも回復させない。
                if (u.FactionId == Faction.InvaderFactionId) continue;

                Faction f = state.FindFaction(u.FactionId);
                if (f == null || f.SupplyStock <= 0f) continue;

                if (!IsNearResupplyPoint(state, u, type)) continue;

                float refill = RefillPerHour * dt;
                if (refill > 1f - u.Ammo) refill = 1f - u.Ammo;

                // 物資が足りなければ払えるぶんだけ回復（部分回復）。
                float supplyCost = refill * SupplyPerFullReload;
                if (supplyCost > f.SupplyStock)
                {
                    supplyCost = f.SupplyStock;
                    refill = supplyCost / SupplyPerFullReload;
                }
                f.TrySpendSupply(supplyCost);
                u.Ammo += refill;
                if (u.Ammo > 1f) u.Ammo = 1f;
            }
        }

        /// <summary>自勢力の基地（全種別）からResupplyRadius以内か。航空ユニットに限り、
        /// 自勢力の生存空母も補給点として扱う。</summary>
        public static bool IsNearResupplyPoint(WarState state, UnitInstance u, UnitType type)
        {
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (u.Position.HorizontalDistanceTo(mb.Position) <= ResupplyRadius) return true;
            }

            if (type.Domain == Domain.Air)
            {
                for (int i = 0; i < state.Units.Count; i++)
                {
                    UnitInstance other = state.Units[i];
                    if (!other.IsAlive || other.FactionId != u.FactionId || other.InstanceId == u.InstanceId) continue;
                    UnitType otherType = state.Types.Get(other.TypeKey);
                    if (otherType == null || otherType.Category != UnitCategory.Carrier) continue;
                    if (u.Position.HorizontalDistanceTo(other.Position) <= ResupplyRadius) return true;
                }
            }
            return false;
        }
    }
}
