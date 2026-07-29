namespace CSWarfront.Core
{
    /// <summary>Moving状態のユニットを前進させる（キネマティック・純ロジック）。
    /// Task37: Yはもはや維持しない（旧仕様）。X/Zと同じ補間係数でウェイポイント/目標のYへ向けて
    /// 補間することで、道路の勾配（橋・坂）に沿って高さが変化するようにする（路面から浮くバグの修正）。
    /// Path（道路経路）があればウェイポイントを順に消化し、尽きたらOrderTargetPosへの直線移動にフォールバックする。
    /// Task44/Task45: CoverDestinationが設定されているユニットは、State(Engaging/Moving)に関わらず
    /// Path/OrderTargetPosを無視してCoverDestinationへ向けて進む（遮蔽を取りに行く動き）。
    /// CoverArrivalDistance以内に入った時の挙動はUnitInstance.CoverHoldで分岐する：
    ///   - CoverHold==true（交戦中、またはTask52以降はMode3のbounding advanceも）: その場で停止し、
    ///     そこから撃ち続ける/隠れる。ただしTask52のMaxCoverHoldHoursで頭打ちにする（後述）。
    ///   - CoverHold==false（互換維持用のフォールバック。現行のCoverSeekStepはもう作らないが、
    ///     手動でCoverHold=falseを設定した呼び出し元のために挙動を維持する）: 到達したら即座に
    ///     CoverDestinationをクリアしてPath/OrderTargetPosへの追従を再開する。
    /// CoverDestinationが無いユニットは従来通りの経路/直線移動のまま変わらない。
    ///
    /// Task48: UnitInstance.Order による分岐を追加した。
    ///   - Hold: ループの先頭で即continueし、一切移動しない。
    ///   - RallyHold: CoverSeekStepがCoverDestinationを一切設定しないため上のCoverDestination分岐は
    ///     通らない。代わりにOrderTargetPosの代わりにRallyPointへ向け、Path（UnitCommands.ApplyRallyが
    ///     RallyPoint宛に計算済み）があれば消化してから直線移動でフォールバックする。CoverArrivalDistance
    ///     以内まで近づいたら停止する。RallyHoldは移動中・停止後を問わず射程内の敵に応戦する設計のため、
    ///     State==Engagingであっても下のTask50/52ガードより先に判定し、RallyPointへの移動は続ける
    ///     （「持ち場へ向かいながら応戦する」という意図的な仕様。ここは変更しない）。
    ///   - FreeAdvance/AiControlled: 従来通りOrderTargetPos/Pathで移動する（挙動変更なし）。
    ///
    /// Task50: 「建物の陰に隠れながら戦闘するときは停車する」フィードバック対応。CoverDestinationを
    /// 持たない（＝適した遮蔽が見つからなかった、または自勢力圏内で遮蔽移動そのものの対象外の）
    /// Engagingユニットは、Order==RallyHoldでない限り一切移動しない（OrderTargetPos/Pathへ進まない）。
    ///
    /// Task52（「敵拠点への進軍が途中でスタックする」不具合の修正）: Task50の「交戦中は遮蔽位置以外へは
    /// 絶対に動かない」は、遮蔽が見つからない/倒しきれない相手と長時間睨み合うケースで恒久的な
    /// フリーズを引き起こしていた。以下の2つの独立したタイマーで「進軍は必ず再開される」ことを保証する：
    ///   - MaxCoverHoldHours: CoverDestinationで実際に静止し続けている時間の上限（UnitInstance.
    ///     CoverHoldTimerで計測、AdvanceTowardCoverが管理）。超えたらCoverDestinationを解放する。
    ///   - CoverSeekStep.MaxEngageHoldHours: 同じ相手と交戦し続けている時間の上限
    ///     （UnitInstance.EngageHoldTimerで計測、CoverSeekStepが管理）。超えたら、CoverDestinationの
    ///     有無に関わらずEngaging中でもOrderTargetPos/Pathへの移動を許可する（射程内ならCombatStepが
    ///     移動しながらでも撃ち合いを継続する＝「it may keep firing while moving」）。
    /// どちらか一方でも条件を満たせば、Engaging中でも移動を再開する（詳細はAdvanceの本体を参照）。
    ///
    /// Task53:「ユニットが時々地面にめり込む」不具合の修正。従来はYをウェイポイント/目標のYへ向けて
    /// 補間するだけだった（上記Task37）が、これはウェイポイント間・オフロードの直線移動・遮蔽/集結移動の
    /// 途中で、道路の盛土・建設後に変化した地形・橋などの"実際の"地表を下回ってしまうことがあった。
    /// state.Height（IHeightSampler、Game層がTerrainManager.SampleDetailHeightで実装）が供給されて
    /// いれば、X/Zを計算した直後に必ずそれでYを上書きする（ウェイポイント移動・直線移動・遮蔽移動・
    /// 集結移動のすべての経路で共通）。state.Height == nullなら、従来のY補間をそのまま維持する
    /// （安全側フォールバック・既存テストの前提を変えない）。</summary>
    public static class MovementStep
    {
        /// <summary>CoverDestinationへ到達したとみなす距離（Task44）。これ未満まで近づいたら停止する。
        /// Task48: RallyPointへの到達判定にも同じ閾値を再利用する。</summary>
        public const float CoverArrivalDistance = 3f;

        /// <summary>CoverHold==trueで静止し続けられる最大時間（ゲーム内時間、Task52）。
        /// UnitInstance.CoverHoldTimerがこれを超えたらCoverDestinationを解放し、次tickから
        /// 通常の経路/直線移動（Engaging中でもCoverSeekStep.MaxEngageHoldHoursの条件次第で）を
        /// 再開できるようにする。これが「隠れている間は動かないが、いつまでも隠れ続けはしない」
        /// というユーザー要件のガードレールになる。</summary>
        public const float MaxCoverHoldHours = 1.0f;

        public static void Advance(WarState state, float dt)
        {
            IHeightSampler height = state.Height; // Task53: null-safeなローカルへ1回だけ拾っておく。

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
                    AdvanceTowardCover(u, stepLen, dt, height);
                    continue;
                }

                if (u.Order == UnitOrder.RallyHold)
                {
                    if (u.RallyPoint.HasValue) AdvanceTowardRally(u, stepLen, height);
                    continue;
                }

                // Task50/52: 遮蔽位置を持たない交戦中ユニットは、原則停車したまま応戦する
                // （RallyHoldは上で処理済み）。ただしMaxCoverHoldHours（直前まで保持していた遮蔽から
                // 解放された）またはCoverSeekStep.MaxEngageHoldHours（同じ相手との交戦が長引きすぎた）
                // のいずれかを満たしていれば、Engagingのままでも移動を再開させる（Task52）。
                if (u.State == UnitState.Engaging)
                {
                    bool releasedFromCoverHold = u.CoverHoldTimer > MaxCoverHoldHours;
                    bool engageHoldExpired = u.EngageHoldTimer >= CoverSeekStep.MaxEngageHoldHours;
                    if (!releasedFromCoverHold && !engageHoldExpired) continue;
                }
                else if (u.State != UnitState.Moving)
                {
                    continue;
                }

                if (!u.OrderTargetPos.HasValue) continue;

                stepLen = ConsumePath(u, stepLen, height);
                if (stepLen > 0f)
                    AdvanceStraight(u, u.OrderTargetPos.Value, stepLen, height);
            }
        }

        /// <summary>Task48: RallyPointへ向けたキネマティック移動。UnitCommands.ApplyRallyがRallyPoint宛に
        /// 計算した道路経路(Path)があればまずそれを消化し、残りは直線移動でフォールバックする
        /// （ConsumePath/AdvanceStraightは通常のOrderTargetPos移動と全く同じヘルパーを再利用する）。
        /// CoverArrivalDistance以内まで近づいたら以後は何もしない（その場に留まる）。</summary>
        private static void AdvanceTowardRally(UnitInstance u, float stepLen, IHeightSampler height)
        {
            WorldPos rally = u.RallyPoint.Value;
            float dist = u.Position.HorizontalDistanceTo(rally);
            if (dist <= CoverArrivalDistance) return;

            stepLen = ConsumePath(u, stepLen, height);
            if (stepLen > 0f)
                AdvanceStraight(u, rally, stepLen, height);
        }

        /// <summary>CoverDestinationへ向けたキネマティック移動。CoverArrivalDistance以内に入ったら、
        /// CoverHoldに応じて「その場で停止し続ける」(true、Task52でMaxCoverHoldHoursの上限つき)か
        /// 「CoverDestinationをクリアして次tickから通常の経路/直線移動または次の遮蔽評価へ委ねる」
        /// (false、互換維持用のフォールバック経路)かを分岐する。それ以外の距離ではAdvanceStraightと
        /// 同じ補間で進む（この間はCoverHoldTimerを0のまま維持し、実際に静止した時間だけを計測する）。</summary>
        private static void AdvanceTowardCover(UnitInstance u, float stepLen, float dt, IHeightSampler height)
        {
            WorldPos coverPos = u.CoverDestination.Value;
            float distBefore = u.Position.HorizontalDistanceTo(coverPos);

            if (distBefore <= CoverArrivalDistance)
            {
                if (!u.CoverHold)
                {
                    // 遮蔽から遮蔽への前進中（保持しない）: ここで停止させ続けるのではなく、
                    // 次のCoverSeekStep評価がすぐ走るようクールダウンをリセットして手放す。
                    u.CoverDestination = null;
                    u.CoverReevaluateCooldown = 0f;
                    u.CoverHoldTimer = 0f;
                    return;
                }

                // Task52: 保持中の静止時間を計測し、MaxCoverHoldHoursを超えたら強制的に解放する
                // （交戦中でも「resumes advancing — even if it is still engaging」）。到着した
                // まさにこのtickではまだ0のままなので、静止1回分としてカウントされ始めるのは
                // 次tick以降になる（巨大なdtで一気に到着したケースでも即座に頭打ちにならないため）。
                u.CoverHoldTimer += dt;
                if (u.CoverHoldTimer > MaxCoverHoldHours)
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    // CoverHoldTimerはあえてリセットしない: 同じ交戦（CoverTargetId不変）が続く限り
                    // MovementStep側のreleasedFromCoverHold判定がtrueであり続け、以後このtargetとの
                    // 交戦中は再度足止めされない（新しい交戦/新しい遮蔽決定でCoverSeekStepが0へ戻す）。
                }
                return;
            }

            // まだ到達していない＝保持はまだ始まっていないのでタイマーは0のまま。
            u.CoverHoldTimer = 0f;
            AdvanceStraight(u, coverPos, stepLen, height);
        }

        /// <summary>Pathが残っていればウェイポイントを順に消化する。残ったstepLen（直線フォールバック用）を返す。</summary>
        private static float ConsumePath(UnitInstance u, float stepLen, IHeightSampler height)
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
                    // Task53: state.Heightが供給されていれば、そのウェイポイントのYではなく
                    // 実際の地表（建設後）のYへスナップする（ウェイポイント自体のYが古い/不正確でも安全）。
                    u.Position = ResolvePosition(waypoint.X, waypoint.Y, waypoint.Z, height);
                    stepLen -= dist;
                    u.PathIndex++;
                }
                else
                {
                    MoveToward(u, waypoint, stepLen, height);
                    return 0f;
                }
            }

            return stepLen;
        }

        private static void AdvanceStraight(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= stepLen || dist <= 0.01f)
                // 到達: targetのYをそのまま採用（Task37、旧:u.Position.Y維持）。
                // Task53: state.Heightが供給されていれば実際の地表のYへスナップする。
                u.Position = ResolvePosition(target.X, target.Y, target.Z, height);
            else
                MoveToward(u, target, stepLen, height);
        }

        /// <summary>X/Zと同じ補間係数(t = stepLen/dist)でYも目標へ向けて補間する（Task37）。
        /// 旧仕様は常に u.Position.Y を維持していたため、道路の勾配（橋・坂）を無視して水平飛行して
        /// しまい「路面から浮いている」ように見えるバグの原因だった。X/Zの補間ロジック自体は変更していない
        /// （オーバーシュートは発生しない、既存のAdvance_stops_at_target_without_overshootで保証）。
        /// Task53: heightが供給されていれば、この補間したY自体は使わず、計算済みのX/Zで実際の地表を
        /// サンプリングした値をYに採用する（ResolvePositionが分岐）。</summary>
        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = ResolvePosition(nx, ny, nz, height);
        }

        /// <summary>Task53: 移動計算で得たX/Y/Zから実際に採用するWorldPosを組み立てる。heightが供給されて
        /// いれば、渡されたy（従来のウェイポイント/補間Y）を捨てて height.SampleHeight(x, z)（建設後の
        /// 実地表）で上書きする。heightがnullなら渡されたyをそのまま使う（従来どおりの補間・スナップ挙動、
        /// 既存テストの前提を変えない安全側フォールバック）。</summary>
        private static WorldPos ResolvePosition(float x, float y, float z, IHeightSampler height)
        {
            if (height != null) y = height.SampleHeight(x, z);
            return new WorldPos(x, y, z);
        }
    }
}
