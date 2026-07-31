using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// プレイヤーが範囲選択した部隊へ与える指揮コマンド（自由進撃・停止・集結待機・AI委任）を
    /// UnitInstance.Order/RallyPoint へ適用する（純ロジック、Task48）。Game層（UnitCommandInput/
    /// MilitaryManager）は選択中の InstanceId 一覧をここへ渡すだけでよい。
    ///
    /// 各Applyは「古い注文由来の実行時状態」（Path/PathIndex/PathTarget/OrderTargetPos/CoverDestination/
    /// CoverHold）を新しい注文にふさわしい形へ揃えてから Order を書き換える。取り残した状態が
    /// 次tickのMovementStep/CoverSeekStepへ誤って伝わらないようにするための契約。
    /// 存在しない/死亡済みのIDは黙ってスキップする（返り値のカウントにも含めない、例外も投げない）。
    /// </summary>
    public static class UnitCommands
    {
        /// <summary>自由進撃（FreeAdvance）。各自の最高速度で最寄りの敵拠点へ進み通常通り交戦する。
        /// 目標基地/経路は次回のInvasionOrders.AssignAdvanceが張り直すため、ここでは古い目標/経路/
        /// 遮蔽状態を捨てるだけでよい。</summary>
        public static int ApplyFreeAdvance(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.FreeAdvance;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
                if (u.State != UnitState.Engaging) u.State = UnitState.Idle;
            });
        }

        /// <summary>停止（Hold）。その場から一切動かない（MovementStepがOrder==Holdを見て常にskipする）が、
        /// 射程内の敵には引き続き応戦する（CombatStepは変更しないため自動的に受動防御になる）。</summary>
        public static int ApplyHold(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.Hold;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
                if (u.State != UnitState.Engaging) u.State = UnitState.Idle;
            });
        }

        /// <summary>集結待機（RallyHold）。rallyPointへ移動し到着後は停止、移動中・停止後を問わず
        /// 射程内の敵にしか応戦しない（CoverSeekStepがOrder==RallyHoldを遮蔽移動の対象外にするため、
        /// 追撃・拠点進撃は発生しない）。state.Roadsが供給されていれば、InvasionOrders.AssignAdvanceと
        /// 同じ道路経路探索(A*)をrallyPoint宛に1回だけ計算しPath/PathIndex/PathTargetへ格納する
        /// （毎tick再計算はしない。MovementStep.AdvanceTowardRallyがそれを消化する）。</summary>
        public static int ApplyRally(WarState state, IList<uint> instanceIds, WorldPos rallyPoint)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.RallyHold;
                u.RallyPoint = rallyPoint;
                u.CoverDestination = null;
                u.CoverHold = false;
                u.ClearPath();

                // Task92: 経路はドメインで使い分ける（陸=道路A*、海=SeaGrid、空=経路なしの直線）。
                // 従来は海上ユニットにも道路経路を張っていたが、海はPathを無視していたため無害だった。
                // AdvanceSeaがPathを消化するようになったので、誤った道路経路を渡さないようにする。
                UnitType rallyType = state.Types.Get(u.TypeKey);
                Domain rallyDomain = rallyType != null ? rallyType.Domain : Domain.Land;
                if (rallyDomain == Domain.Land && state.Roads != null)
                {
                    List<WorldPos> path = state.Roads.FindPath(
                        u.Position, rallyPoint, InvasionOrders.PathSnapRadius, u.InstanceId, InvasionOrders.PathJitter);
                    if (path != null)
                    {
                        u.Path = path;
                        u.PathIndex = 0;
                        u.PathTarget = rallyPoint;
                    }
                }
                else if (rallyDomain == Domain.Sea && state.SeaNav != null)
                {
                    List<WorldPos> path = state.SeaNav.FindPath(u.Position, rallyPoint, InvasionOrders.SeaPathSnapRadius);
                    if (path != null)
                    {
                        u.Path = path;
                        u.PathIndex = 0;
                        u.PathTarget = rallyPoint;
                    }
                }

                if (u.State != UnitState.Engaging) u.State = UnitState.Moving;
            });
        }

        /// <summary>AI委任（AiControlled）へ戻す。次回のInvasionOrders.AssignAdvanceが目標/経路を
        /// 通常通り張り直すため、ここでは古い状態を全て捨てるだけでよい。</summary>
        public static int ClearOrders(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.AiControlled;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
            });
        }

        private static int ForEachLiving(WarState state, IList<uint> instanceIds, System.Action<UnitInstance> apply)
        {
            if (state == null || instanceIds == null) return 0;
            int count = 0;
            for (int i = 0; i < instanceIds.Count; i++)
            {
                UnitInstance u = state.FindUnit(instanceIds[i]);
                if (u == null || !u.IsAlive) continue;
                apply(u);
                count++;
            }
            return count;
        }
    }
}
