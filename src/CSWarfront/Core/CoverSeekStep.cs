using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// ユニットに、近くの遮蔽物（建物/Prop、WarState.Cover）を活かした立ち位置
    /// （UnitInstance.CoverDestination）を割り当てる（純ロジック、Task44/Task45）。
    /// MilitaryManager.OnSimTickではMovementStepより前に呼ぶこと（このtickで決めた立ち位置へ
    /// 同じtick内でMovementStepが動き出せるようにするため、RoadGraph→InvasionOrders→MovementStepと
    /// 同じ「先に意思決定、後で移動」の順序）。
    ///
    /// Task45で「交戦し始めたら遮蔽に向かう」から「自勢力圏を出た段階で遮蔽伝いに進む」へ変更した。
    /// 各生存ユニットは以下の3モードのいずれかに分類される：
    ///   1. 自勢力圏内（IsInFriendlyTerritory）: 遮蔽移動なし。道路沿いに速く移動させる。
    ///   2. 圏外＋交戦中（State==Engaging、TargetIdの相手が生存）: 従来通り、脅威(TargetIdの位置)から
    ///      身を隠す立ち位置を選び、CoverHold=trueでその場に留まって撃ち続ける。
    ///   3. 圏外＋進軍中（交戦していないがOrderTargetPosがある）: 目的地(OrderTargetPos＝進軍先の敵基地)
    ///      を脅威方向とみなし、目的地に確実に近づく（MinForwardProgress以上）候補があればそこを
    ///      CoverHold=falseで設定する（遮蔽から遮蔽へ跳ぶように前進、MovementStepが到達時に自動でクリアする）。
    ///      前進にならない候補しか無ければCoverDestinationは設定せず、通常の経路移動に任せる。
    /// </summary>
    public static class CoverSeekStep
    {
        /// <summary>ユニットごとの遮蔽再評価間隔（ゲーム内時間）。毎tick探索しない（性能とジッタ抑制の
        /// 両方が目的）。UnitInstance.CoverReevaluateCooldownで管理する。</summary>
        public const float CoverReevaluateHours = 0.5f;

        /// <summary>Mode3（進軍中のbounding advance）で候補の遮蔽を採用するために必要な、目的地への
        /// 最小前進量（マップ単位）。これを満たさない候補は「前進にならない」として却下し、
        /// 遮蔽を求めて後退・停滞するよりも道路沿いの進軍を優先させる（Task45）。</summary>
        public const float MinForwardProgress = 5f;

        /// <summary>カテゴリ別の遮蔽探索半径。0以下＝そのカテゴリは遮蔽移動をしない
        /// （Artilleryは後方から曲射するため、遮蔽物の陰に隠れる必要がない）。</summary>
        private static readonly Dictionary<UnitCategory, float> SearchRadiusByCategory = new Dictionary<UnitCategory, float>
        {
            { UnitCategory.Infantry, 60f },
            { UnitCategory.MechInfantry, 60f },
            { UnitCategory.DroneInfantry, 60f },
            { UnitCategory.Apc, 45f },
            { UnitCategory.Tank, 45f },
            { UnitCategory.AntiAir, 45f },
            { UnitCategory.Artillery, 0f },
        };

        /// <summary>uが自勢力（u.FactionId）のいずれかの基地の勢力圏（水平距離がInfluenceRadius以内）に
        /// いるか（Task45）。敵勢力の基地の勢力圏は数えない。基地が1つも無ければfalse。</summary>
        public static bool IsInFriendlyTerritory(WarState state, UnitInstance u)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId != u.FactionId) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= b.InfluenceRadius) return true;
            }
            return false;
        }

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                // Task48: Hold/RallyHold は「持ち場を守る受動防御」定義そのものなので遮蔽移動の対象外
                // （追撃や遮蔽から遮蔽への前進を一切しない）。FreeAdvanceはAiControlledと同じ扱いのため
                // ここでは特別扱いしない（このメソッドの残りのロジックがそのまま適用される）。
                if (u.Order == UnitOrder.Hold || u.Order == UnitOrder.RallyHold)
                {
                    ClearCover(u);
                    continue;
                }

                // Mode 1: 自勢力圏内にいる間は遮蔽移動をしない（速く・道路沿いに移動させたい）。
                // 圏内に居る/戻った時点でクールダウンもリセットし、圏外へ出た次のtickで
                // 即座に遮蔽の評価を始められるようにする（残クールダウンを持ち越さない）。
                if (IsInFriendlyTerritory(state, u))
                {
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null)
                {
                    ClearCover(u);
                    continue;
                }

                float searchRadius = SearchRadiusByCategory.TryGetValue(type.Category, out float r) ? r : 0f;
                if (searchRadius <= 0f || state.Cover == null)
                {
                    ClearCover(u);
                    continue;
                }

                bool isEngaging = u.State == UnitState.Engaging && u.TargetId.HasValue;

                UnitInstance target = null;
                if (isEngaging)
                {
                    target = state.FindUnit(u.TargetId.Value);
                    if (target == null || !target.IsAlive)
                    {
                        // 交戦が終わった＝次に交戦し始めたら即座に再評価してほしいので、
                        // クールダウンも一緒にリセットする（残クールダウンを持ち越さない）。
                        ClearCover(u);
                        u.CoverReevaluateCooldown = 0f;
                        continue;
                    }
                }
                else if (!u.OrderTargetPos.HasValue)
                {
                    // 交戦もしていない・進軍目的地も無い（Idle等）→遮蔽移動の対象外。
                    ClearCover(u);
                    continue;
                }

                // 再評価クールダウン: まだ間隔が空いていなければ既存のCoverDestination/CoverHoldを維持する
                // （設定済みでも未設定でも、そのまま変えない＝毎tick探索しないための間引き。
                // ただしMovementStepがbounding到達時にCoverReevaluateCooldownを0へリセットするため、
                // そのケースではここを素通りしてすぐ次の候補を探しに行く）。
                u.CoverReevaluateCooldown -= dt;
                if (u.CoverReevaluateCooldown > 0f) continue;
                u.CoverReevaluateCooldown = CoverReevaluateHours;

                if (isEngaging)
                {
                    // Mode 2: 交戦中。脅威(target)から身を隠す位置へ。到着したらそこに留まり撃ち続ける。
                    if (state.Cover.TryFindBestCover(u.Position, target.Position, searchRadius, u.InstanceId, out WorldPos coverPos))
                    {
                        u.CoverDestination = coverPos;
                        u.CoverHold = true;
                    }
                    else
                    {
                        ClearCover(u);
                    }
                    continue;
                }

                // Mode 3: 進軍中（交戦していない）。目的地(進軍先の敵基地)方向を脅威とみなし、
                // 目的地に確実に近づく候補があれば遮蔽から遮蔽へ跳ぶように前進させる。
                WorldPos objective = u.OrderTargetPos.Value;
                if (state.Cover.TryFindBestCover(u.Position, objective, searchRadius, u.InstanceId, out WorldPos boundCover))
                {
                    float distNow = u.Position.HorizontalDistanceTo(objective);
                    float distAfter = boundCover.HorizontalDistanceTo(objective);
                    if (distAfter < distNow - MinForwardProgress)
                    {
                        u.CoverDestination = boundCover;
                        u.CoverHold = false;
                    }
                    else
                    {
                        // 前進にならない候補しか無い＝遮蔽を求めて足踏み/後退するより、
                        // 道路沿いの進軍(Path/OrderTargetPos)をそのまま続けさせる方が良い。
                        ClearCover(u);
                    }
                }
                else
                {
                    ClearCover(u);
                }
            }
        }

        private static void ClearCover(UnitInstance u)
        {
            u.CoverDestination = null;
            u.CoverHold = false;
        }
    }
}
