namespace CSWarfront.Core
{
    /// <summary>Moving状態のユニットを前進させる（キネマティック・純ロジック）。
    /// Task37: Yはもはや維持しない（旧仕様）。X/Zと同じ補間係数でウェイポイント/目標のYへ向けて
    /// 補間することで、道路の勾配（橋・坂）に沿って高さが変化するようにする（路面から浮くバグの修正）。
    /// Path（道路経路）があればウェイポイントを順に消化し、尽きたらOrderTargetPosへの直線移動にフォールバックする。
    /// Task44/Task45: CoverDestinationが設定されているユニットは、State(Engaging/Moving)に関わらず
    /// Path/OrderTargetPosを無視してCoverDestinationへ向けて進む（遮蔽を取りに行く動き）。
    /// 旧仕様(Task44)は「State==Engagingの時だけ」honorしていたが、Task45でCoverSeekStepが
    /// 進軍中（交戦前、自勢力圏の外）のユニットにもCoverDestinationを設定するようになったため、
    /// このガードは撤廃した。CoverArrivalDistance以内に入った時の挙動はUnitInstance.CoverHoldで分岐する：
    ///   - CoverHold==true（交戦中）: その場で停止し、そこから撃ち続ける（従来通り）。
    ///   - CoverHold==false（進軍中のbounding advance）: CoverDestinationをクリアして即座に
    ///     Path/OrderTargetPosへの追従を再開する。CoverReevaluateCooldownも0へリセットするため、
    ///     同じtick内の次のCoverSeekStep評価（次tick）で次の遮蔽が選ばれ、遮蔽から遮蔽へ「跳ぶ」ように
    ///     前進する（半開けた場所で立ち止まって次の評価まで待つことがない）。
    /// CoverDestinationが無いユニットは従来通りの経路/直線移動のまま変わらない。
    ///
    /// Task48: UnitInstance.Order による分岐を追加した。
    ///   - Hold: ループの先頭で即continueし、一切移動しない（CoverDestination等の取り残しがあっても
    ///     無視する。停止命令の定義そのもの）。
    ///   - RallyHold: CoverSeekStepがCoverDestinationを一切設定しないため上のCoverDestination分岐は
    ///     通らない。代わりにOrderTargetPosの代わりにRallyPointへ向け、Path（UnitCommands.ApplyRallyが
    ///     RallyPoint宛に計算済み）があれば消化してから直線移動でフォールバックする。CoverArrivalDistance
    ///     以内まで近づいたら停止する（新規の定数を増やさず、Cover到達判定と同じ基準を再利用する）。
    ///     RallyHoldは移動中・停止後を問わず射程内の敵に応戦する設計（UnitInstance.Order参照）のため、
    ///     State==Engagingであっても下のTask50ガードより先に判定し、RallyPointへの移動は続ける
    ///     （「持ち場へ向かいながら応戦する」という意図的な仕様。ここは変更しない）。
    ///   - FreeAdvance/AiControlled: 従来通りOrderTargetPos/Pathで移動する（挙動変更なし）。
    ///
    /// Task50: 「建物の陰に隠れながら戦闘するときは停車する」フィードバック対応。CoverDestinationを
    /// 持たない（＝適した遮蔽が見つからなかった、または自勢力圏内で遮蔽移動そのものの対象外の）
    /// Engagingユニットは、Order==RallyHoldでない限り一切移動しない（OrderTargetPos/Pathへ進まない）。
    /// State列挙体はEngaging/Movingが排他のため、この分岐が無くても下のMoving判定だけで実質的には
    /// 同じ結果になっていたが、意図を明示するため独立したガードとして残す（「交戦中は遮蔽位置以外へは
    /// 絶対に動かない」という要件を、将来Stateの意味が広がっても壊れない形で保証する）。</summary>
    public static class MovementStep
    {
        /// <summary>CoverDestinationへ到達したとみなす距離（Task44）。これ未満まで近づいたら停止する。
        /// Task48: RallyPointへの到達判定にも同じ閾値を再利用する。</summary>
        public const float CoverArrivalDistance = 3f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.Order == UnitOrder.Hold) continue; // Task48: 停止命令＝常に不動。

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                float stepLen = type.Speed * dt;
                if (stepLen <= 0f) continue;

                if (u.CoverDestination.HasValue)
                {
                    AdvanceTowardCover(u, stepLen);
                    continue;
                }

                if (u.Order == UnitOrder.RallyHold)
                {
                    if (u.RallyPoint.HasValue) AdvanceTowardRally(u, stepLen);
                    continue;
                }

                // Task50: 遮蔽位置を持たない交戦中ユニットは停車したまま応戦する（RallyHoldは上で処理済み）。
                if (u.State == UnitState.Engaging) continue;

                if (u.State != UnitState.Moving || !u.OrderTargetPos.HasValue) continue;

                stepLen = ConsumePath(u, stepLen);
                if (stepLen > 0f)
                    AdvanceStraight(u, u.OrderTargetPos.Value, stepLen);
            }
        }

        /// <summary>Task48: RallyPointへ向けたキネマティック移動。UnitCommands.ApplyRallyがRallyPoint宛に
        /// 計算した道路経路(Path)があればまずそれを消化し、残りは直線移動でフォールバックする
        /// （ConsumePath/AdvanceStraightは通常のOrderTargetPos移動と全く同じヘルパーを再利用）。
        /// CoverArrivalDistance以内まで近づいたら以後は何もしない（その場に留まる）。</summary>
        private static void AdvanceTowardRally(UnitInstance u, float stepLen)
        {
            WorldPos rally = u.RallyPoint.Value;
            float dist = u.Position.HorizontalDistanceTo(rally);
            if (dist <= CoverArrivalDistance) return;

            stepLen = ConsumePath(u, stepLen);
            if (stepLen > 0f)
                AdvanceStraight(u, rally, stepLen);
        }

        /// <summary>CoverDestinationへ向けたキネマティック移動。CoverArrivalDistance以内に入ったら、
        /// CoverHoldに応じて「その場で停止し続ける」(true)か「CoverDestinationをクリアして
        /// 次tickから通常の経路/直線移動または次の遮蔽評価へ委ねる」(false、Task45のbounding advance)
        /// かを分岐する。それ以外の距離ではAdvanceStraightと同じ補間で進む。</summary>
        private static void AdvanceTowardCover(UnitInstance u, float stepLen)
        {
            WorldPos coverPos = u.CoverDestination.Value;
            float dist = u.Position.HorizontalDistanceTo(coverPos);
            if (dist <= CoverArrivalDistance)
            {
                if (!u.CoverHold)
                {
                    // 遮蔽から遮蔽への前進中（保持しない）: ここで停止させ続けるのではなく、
                    // 次のCoverSeekStep評価がすぐ走るようクールダウンをリセットして手放す。
                    u.CoverDestination = null;
                    u.CoverReevaluateCooldown = 0f;
                }
                return;
            }
            AdvanceStraight(u, coverPos, stepLen);
        }

        /// <summary>Pathが残っていればウェイポイントを順に消化する。残ったstepLen（直線フォールバック用）を返す。</summary>
        private static float ConsumePath(UnitInstance u, float stepLen)
        {
            if (u.Path == null) return stepLen;

            while (stepLen > 0f && u.PathIndex < u.Path.Count)
            {
                WorldPos waypoint = u.Path[u.PathIndex];
                float dist = u.Position.HorizontalDistanceTo(waypoint);

                if (dist <= stepLen || dist <= 0.01f)
                {
                    // Task37: ウェイポイントに到達したらそのウェイポイントのYをそのまま採用する
                    // （旧: u.Position.Yを維持 → 路面から浮く原因だった）。
                    u.Position = new WorldPos(waypoint.X, waypoint.Y, waypoint.Z);
                    stepLen -= dist;
                    u.PathIndex++;
                }
                else
                {
                    MoveToward(u, waypoint, stepLen);
                    return 0f;
                }
            }

            return stepLen;
        }

        private static void AdvanceStraight(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= stepLen || dist <= 0.01f)
                u.Position = new WorldPos(target.X, target.Y, target.Z); // 到達: targetのYをそのまま採用（Task37、旧:u.Position.Y維持）
            else
                MoveToward(u, target, stepLen);
        }

        /// <summary>X/Zと同じ補間係数(t = stepLen/dist)でYも目標へ向けて補間する（Task37）。
        /// 旧仕様は常に u.Position.Y を維持していたため、道路の勾配（橋・坂）を無視して水平飛行して
        /// しまい「路面から浮いている」ように見えるバグの原因だった。X/Zの補間ロジック自体は変更していない
        /// （オーバーシュートは発生しない、既存のAdvance_stops_at_target_without_overshootで保証）。</summary>
        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
