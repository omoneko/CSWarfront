namespace CSWarfront.Core
{
    /// <summary>
    /// Task64（空母の艦載機化）: 空母(UnitCategory.Carrier)は基地(MilitaryBase)ではなくUnitInstanceなので、
    /// ProductionOrder/ProductionStepのキュー機構は使わず、専用のtickでCarrier単位の護衛航空隊
    /// （艦載機ウイング）を直接管理する。基本方針:
    ///
    ///  - 生存中の各Carrierは、自機からWingRadius以内にいる自勢力の生存中Air-domainユニットを数え、
    ///    WingSize未満なら1機ずつ艦載機を建造する（他ソース＝陸上のAirForce基地から飛来した機体も
    ///    ウイング数に数える。空母は「近くにAirドメイン機がWingSize機いてほしい」だけで、
    ///    自分が生んだかどうかは問わない）。
    ///  - 建造費はUnitInstance.CarrierBuildProgressが0（＝建造未着手）から動き出す瞬間に
    ///    Faction.TrySpendで一括徴収する（tickごとの分割徴収はしない）。支払えなければ何もせず
    ///    次tickに再試行する（MissileStockpileと同じ「着手時払い・待って再試行」方針）。
    ///  - 建造する兵科は AirSuperiority, AirSuperiority, TacticalBomber, SuicideDrone
    ///    という固定4件のサイクルを、空母ごとに「艦載機id＋この空母の累計建造着手数
    ///    (UnitInstance.CarrierBuildCounter)」のハッシュで回す（System.Random不使用、決定的）。
    ///    戦闘機が4件中2件を占め、「戦闘機主体」の構成になる。
    ///  - 各カテゴリの実際に建造するTierは、勢力のUnlockedTier以下で登録済みの最高Tierを選ぶ
    ///    （AiProductionPolicy.ChooseHighestAffordableTierと同じ考え方。予算判定は既に着手時払いで
    ///    済んでいるためコスト条件はここでは見ない＝Tierだけを絞り込む）。該当カテゴリが1つも
    ///    登録されていなければ（ロスター未登録/将来の兵科追加待ち）何もしない。
    ///  - 完成した機体は空母の現在位置にスポーンし、UnitOrder.AiControlled（既定）を引き継ぐ
    ///    （艦隊と共に自由に動き回れるよう、空中ユニットの自由移動ロジックにそのまま乗る）。
    ///
    /// UnityEngine非依存。MilitaryManager.OnSimTickからProductionStep.Advanceの直後に呼ぶ想定。
    /// </summary>
    public static class CarrierAirWing
    {
        /// <summary>空母から見て「護衛ウイングの一員」とみなす水平距離。</summary>
        public const float WingRadius = 250f;

        /// <summary>維持したい護衛ウイングの機数。これ未満の間だけ建造を続ける。</summary>
        public const int WingSize = 4;

        /// <summary>建造する兵科の固定サイクル（Task64指定: 戦闘機2・爆撃機1・自爆ドローン1）。</summary>
        private static readonly UnitCategory[] BuildCycle =
        {
            UnitCategory.AirSuperiority, UnitCategory.AirSuperiority,
            UnitCategory.TacticalBomber, UnitCategory.SuicideDrone
        };

        /// <summary>建造着手の瞬間にCarrierBuildProgressへ設定する微小な正の値。0f（建造中でない）と
        /// 区別するためだけの印で、直後のdt加算で自然に上書きされる（MissileStockpile.StartProgressと同じ手法）。</summary>
        private const float StartProgress = 0.0001f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance carrier = state.Units[i];
                if (!carrier.IsAlive) continue;

                UnitType carrierType = state.Types.Get(carrier.TypeKey);
                if (carrierType == null || carrierType.Category != UnitCategory.Carrier) continue;

                if (CountNearbyAirUnits(state, carrier) >= WingSize)
                {
                    carrier.CarrierBuildProgress = 0f; // ウイング充足中は着手済みの半端な進捗を残さない
                    continue;
                }

                Faction faction = state.FindFaction(carrier.FactionId);
                if (faction == null) continue;

                UnitCategory category = NextBuildCategory(carrier);
                UnitType buildType = HighestUnlocked(state, category, faction.UnlockedTier);
                if (buildType == null) continue; // このカテゴリはまだ何も解禁/登録されていない

                if (carrier.CarrierBuildProgress <= 0f)
                {
                    // 建造着手: 費用を一括徴収する。払えなければ何もせず次tickに再試行（MissileStockpileと同じ方針）。
                    if (!faction.TrySpend(buildType.Cost)) continue;
                    carrier.CarrierBuildProgress = StartProgress;
                }

                if (buildType.BuildTime <= 0f) carrier.CarrierBuildProgress = 1f;
                else carrier.CarrierBuildProgress += dt / buildType.BuildTime;

                if (carrier.CarrierBuildProgress >= 1f)
                {
                    uint id = state.AllocInstanceId();
                    var aircraft = new UnitInstance(id, buildType.TypeKey, carrier.FactionId, buildType.MaxHP, carrier.Position);
                    aircraft.Order = UnitOrder.AiControlled;
                    state.Units.Add(aircraft);

                    carrier.CarrierBuildProgress = 0f;
                    carrier.CarrierBuildCounter++;
                }
            }
        }

        /// <summary>carrierの勢力に属する生存中Air-domainユニットで、carrierからWingRadius以内にいるものを数える
        /// （艦載機として自前で生んだかどうかは問わない）。</summary>
        private static int CountNearbyAirUnits(WarState state, UnitInstance carrier)
        {
            int count = 0;
            for (int j = 0; j < state.Units.Count; j++)
            {
                UnitInstance u = state.Units[j];
                if (!u.IsAlive || u.FactionId != carrier.FactionId) continue;

                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Air) continue;

                if (carrier.Position.HorizontalDistanceTo(u.Position) > WingRadius) continue;
                count++;
            }
            return count;
        }

        /// <summary>carrier.InstanceId（空母自身のid）のハッシュから空母ごとに異なる開始位置(offset)を
        /// 決定的に選び、そこへCarrierBuildCounterを足してBuildCycle上を1件ずつ巡回する
        /// （＝厳密な意味での「サイクル」: 4回連続で着手すれば必ずBuildCycleの4件を1回ずつ、
        /// 空母ごとに異なる回転順で消化する）。同じ空母・同じ着手回数なら常に同じ結果
        /// （System.Random不使用、AiProductionPolicy.Hashと同じMurmurHash3finalizer手法）。</summary>
        private static UnitCategory NextBuildCategory(UnitInstance carrier)
        {
            uint offset = Hash(carrier.InstanceId) % (uint)BuildCycle.Length;
            uint idx = (offset + carrier.CarrierBuildCounter) % (uint)BuildCycle.Length;
            return BuildCycle[idx];
        }

        /// <summary>指定カテゴリのうち、unlockedTier以下で登録済みの最高Tierを返す。
        /// 該当が無ければnull（AiProductionPolicy.ChooseHighestAffordableTierと同じ形だが、
        /// 建造費は既に着手時に払っているためコスト条件は見ない）。</summary>
        private static UnitType HighestUnlocked(WarState state, UnitCategory category, byte unlockedTier)
        {
            UnitType best = null;
            foreach (UnitType t in state.Types.All())
            {
                if (t.Category != category) continue;
                if (t.Tier > unlockedTier) continue;
                if (best == null || t.Tier > best.Tier) best = t;
            }
            return best;
        }

        /// <summary>決定的な整数ハッシュ（MurmurHash3のfinalizer相当、AiProductionPolicy.Hash/BallisticMissiles.Hashと同一手法）。</summary>
        private static uint Hash(uint x)
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
    }
}
