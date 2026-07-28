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
    ///
    /// Task50: モード2（交戦中）は、同じ相手（TargetId）と戦い続けている間は遮蔽の再評価を一切
    /// 行わないよう変更した（UnitInstance.CoverTargetId参照）。旧仕様はCoverReevaluateHoursごとの
    /// クールダウンだけで間引いていたが、それでも一定間隔で遮蔽物マップを再探索するため、僅かな
    /// スコア差やユニットの微妙な位置変化で選ばれる候補が変わり、「建物の陰に隠れながら戦闘中なのに
    /// 頻繁に位置を変える、せわしない動き」に見える不具合の原因だった。新仕様では、TargetIdが変わる
    /// （新しい相手と交戦を始める）まで一切位置を選び直さない＝到達後は完全に停止したまま撃ち合う。
    /// 遮蔽が見つからなかった場合も同様にその判断を記憶し、同じ相手との交戦中は毎tick探索し直さない。
    /// モード3（進軍中のbounding advance）のクールダウン間引きは変更していない。
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

                if (isEngaging)
                {
                    UnitInstance target = state.FindUnit(u.TargetId.Value);
                    if (target == null || !target.IsAlive)
                    {
                        // 交戦が終わった＝次に交戦し始めたら即座に再評価してほしいので、
                        // クールダウンも一緒にリセットする（残クールダウンを持ち越さない）。
                        ClearCover(u);
                        u.CoverReevaluateCooldown = 0f;
                        continue;
                    }

                    // Task50: 同じ相手と交戦し続けている間は、既に決定済みの遮蔽（見つからなかった
                    // という判断も含む）を一切変更しない。頻繁な位置変更を防ぐための最重要ガード。
                    if (u.CoverTargetId.HasValue && u.CoverTargetId.Value == u.TargetId.Value)
                        continue;

                    // 新規の交戦、または相手が変わった: このtickで即座に（クールダウンを待たず）評価する。
                    u.CoverTargetId = u.TargetId;
                    if (state.Cover.TryFindBestCover(u.Position, target.Position, searchRadius, u.InstanceId, out WorldPos coverPos))
                    {
                        u.CoverDestination = coverPos;
                        u.CoverHold = true;
                    }
                    else
                    {
                        // 遮蔽が見つからなかった、という判断そのものを記憶する（CoverTargetIdは
                        // 維持し、CoverDestination/CoverHoldのみクリアする＝ClearCoverは使わない。
                        // ClearCoverはCoverTargetIdもnullへ戻してしまい、同じ相手との交戦中に
                        // 毎tick探索し直すことになってしまうため）。
                        u.CoverDestination = null;
                        u.CoverHold = false;
                    }
                    continue;
                }

                // ここに来るのは非交戦（Mode3進軍中）のみ。次に交戦を始めたら即座に評価してほしいので
                // ロックを解放しておく。
                u.CoverTargetId = null;

                if (!u.OrderTargetPos.HasValue)
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

        /// <summary>CoverDestination/CoverHoldに加え、CoverTargetId（Task50の交戦中ロック）も解放する。
        /// 「遮蔽の意思決定そのものを白紙に戻す」経路すべてで使う。交戦中に遮蔽が見つからなかった
        /// 場合だけは、このメソッドを使わずCoverDestination/CoverHoldのみ直接クリアする
        /// （CoverTargetIdは維持し、同じ相手との再探索を防ぐため。上のisEngagingブロック参照）。</summary>
        private static void ClearCover(UnitInstance u)
        {
            u.CoverDestination = null;
            u.CoverHold = false;
            u.CoverTargetId = null;
        }
    }
}
