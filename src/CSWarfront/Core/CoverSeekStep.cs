using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// ユニットに、近くの遮蔽物（建物/Prop、WarState.Cover）を活かした立ち位置
    /// （UnitInstance.CoverDestination）を割り当てる（純ロジック、Task44/Task45/Task50/Task52）。
    /// MilitaryManager.OnSimTickではMovementStepより前に呼ぶこと（このtickで決めた立ち位置へ
    /// 同じtick内でMovementStepが動き出せるようにするため、RoadGraph→InvasionOrders→MovementStepと
    /// 同じ「先に意思決定、後で移動」の順序）。
    ///
    /// Task45で「交戦し始めたら遮蔽に向かう」から「自勢力圏を出た段階で遮蔽伝いに進む」へ変更した。
    /// 各生存ユニットは以下の3モードのいずれかに分類される：
    ///   1. 自勢力圏内（IsInFriendlyTerritory）: 遮蔽移動なし。道路沿いに速く移動させる。
    ///   2. 圏外＋交戦中（State==Engaging、TargetIdの相手が生存）: 脅威(TargetIdの位置)から
    ///      身を隠す立ち位置を選び、CoverHold=trueでその場に留まって撃ち続ける（ただしTask52の
    ///      MaxCoverHoldHours/MaxEngageHoldHoursにより無期限には固定されない、後述）。
    ///   3. 圏外＋進軍中（交戦していないがOrderTargetPosがある）: 目的地(OrderTargetPos＝進軍先の敵基地)
    ///      を脅威方向とみなし、CoverUseIntervalHoursごとにだけ（毎tickではなく）候補を探す。
    ///      目的地に確実に近づく（MinForwardProgress以上）かつ現在地からMaxCoverDetour以内の候補が
    ///      あればそこをCoverHold=trueで設定する（Task52: 到着したら少し隠れて止まり、MovementStep側の
    ///      MaxCoverHoldHoursで自動的に前進を再開する）。条件を満たす候補が無ければ道路経路(Path/
    ///      OrderTargetPos)にそのまま任せる。
    ///
    /// Task50: モード2（交戦中）は、同じ相手（TargetId）と戦い続けている間は遮蔽の再評価を一切
    /// 行わない（UnitInstance.CoverTargetId参照）。TargetIdが変わる（新しい相手と交戦を始める）まで
    /// 一切位置を選び直さない＝到達後は完全に停止したまま撃ち合う（Task52のホールド上限に達するまで）。
    /// 遮蔽が見つからなかった場合も同様にその判断を記憶し、同じ相手との交戦中は毎tick探索し直さない。
    ///
    /// Task52（「敵拠点への進軍が途中でスタックする」不具合の修正）: Task50は「同じ相手と交戦中は
    /// 遮蔽から一切動かない」を導入したが、これは（a）長射程での睨み合いや（b）倒しきれない相手との
    /// 交戦で恒久的なフリーズを引き起こしていた。加えて、モード3のbounding advanceも遮蔽が
    /// 見つからず足踏みし続けることがあった。以下の仕組みで「進軍は必ず進む・遮蔽はあくまで時々の
    /// 演出」というガードレールを敷く：
    ///   1. 遮蔽は「時々」: モード3はCoverUseIntervalHoursごとにしか遮蔽を探さない（従来の0.5hより
    ///      大幅に間隔を空けた）。候補はMinForwardProgress（前進量）とMaxCoverDetour（現在地からの
    ///      迂回距離）の両方を満たす必要がある。
    ///   2. 保持時間の上限: 遮蔽で静止する時間はMovementStep.MaxCoverHoldHoursで頭打ちにする
    ///      （UnitInstance.CoverHoldTimerで計測、MovementStep側で管理）。モード2・モード3どちらの
    ///      「保持」も対象。
    ///   3. 交戦の膠着防止: 同じ相手と交戦し続ける時間をEngageHoldTimerで計測し、MaxEngageHoldHoursを
    ///      超えたら（遮蔽の有無に関わらず）MovementStepが移動を再開させる（射程内なら移動しながらでも
    ///      CombatStepが撃ち合いを継続する）。
    ///   4. 膠着ウォッチドッグ: OrderTargetPosまでの距離が一定時間（StallTimeoutHours）
    ///      StallEpsilon以上縮まらなければ、CoverSuppressedHoursの間だけ遮蔽探索そのものを完全に
    ///      止め、道路経路をそのまま進ませる（Belt and braces：上記1〜3で捕捉しきれない膠着への保険）。
    /// </summary>
    public static class CoverSeekStep
    {
        /// <summary>モード3（進軍中のbounding advance）が遮蔽を探す間隔（ゲーム内時間、Task52）。
        /// 毎tick探索すると「遮蔽から遮蔽へ跳び続けて実質止まらない」状態になりかねないため、
        /// これだけ間隔を空けて「時々」だけ隠れるようにする。UnitInstance.CoverReevaluateCooldownで
        /// 管理する（Task44から使っているフィールドをそのまま流用、意味だけTask52で変更）。</summary>
        public const float CoverUseIntervalHours = 3f;

        /// <summary>Mode3（進軍中のbounding advance）で候補の遮蔽を採用するために必要な、目的地への
        /// 最小前進量（マップ単位）。これを満たさない候補は「前進にならない」として却下し、
        /// 遮蔽を求めて後退・停滞するよりも道路沿いの進軍を優先させる（Task45）。</summary>
        public const float MinForwardProgress = 5f;

        /// <summary>Mode3で候補の遮蔽を採用するために許容する、現在地からの最大迂回距離（Task52）。
        /// これを超える遠回りな遮蔽は「時々隠れる」の範囲を逸脱するため却下し、道路経路を優先する。</summary>
        public const float MaxCoverDetour = 40f;

        /// <summary>同じ相手（TargetId）と交戦し続けられる最大時間（ゲーム内時間、Task52）。
        /// これを超えたら、遮蔽の有無に関わらずMovementStepが目的地への移動を再開させる
        /// （交戦自体はCombatStepが毎tick射程で再判定するため、移動しながらでも撃ち合いは続く）。
        /// UnitInstance.EngageHoldTimerで計測し、TargetIdが変わった/交戦が終わった瞬間に0へ戻す。</summary>
        public const float MaxEngageHoldHours = 3f;

        /// <summary>膠着ウォッチドッグ（Task52）: OrderTargetPosまでの距離をこの時間だけ監視し、
        /// StallEpsilon以上縮まっていなければ膠着とみなす。</summary>
        public const float StallTimeoutHours = 2f;

        /// <summary>膠着ウォッチドッグが「前進した」とみなす最小距離（マップ単位、Task52）。</summary>
        public const float StallEpsilon = 5f;

        /// <summary>膠着を検知した際、遮蔽探索を完全に止めて道路経路のみに任せる時間（ゲーム内時間、
        /// Task52）。UnitInstance.CoverSuppressionRemainingで残り時間を管理する。</summary>
        public const float CoverSuppressedHours = 4f;

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

                // Task48: Hold/RallyHold は「持ち場を守る受動防御」定義そのものなので遮蔽移動・
                // Task52の各種タイマーの対象外（追撃や遮蔽から遮蔽への前進を一切しない）。
                // FreeAdvanceはAiControlledと同じ扱いのためここでは特別扱いしない。
                if (u.Order == UnitOrder.Hold || u.Order == UnitOrder.RallyHold)
                {
                    ClearCover(u);
                    continue;
                }

                // Mode 1: 自勢力圏内にいる間は遮蔽移動もTask52のタイマーも対象外にする
                // （速く・道路沿いに移動させたい）。圏内に居る/戻った時点でクールダウンもリセットし、
                // 圏外へ出た次のtickで即座に遮蔽の評価を始められるようにする。
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

                bool wasEngagingWithTarget = u.State == UnitState.Engaging && u.TargetId.HasValue;
                UnitInstance target = wasEngagingWithTarget ? state.FindUnit(u.TargetId.Value) : null;
                bool targetAlive = target != null && target.IsAlive;

                if (wasEngagingWithTarget && !targetAlive)
                {
                    // 交戦が終わった＝次に交戦し始めたら即座に再評価してほしいので、
                    // クールダウン・Task52の各タイマーも一緒にリセットする。
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                bool isEngaging = wasEngagingWithTarget && targetAlive;

                // Task52 rule3: 同じ相手と交戦し続けている時間を、遮蔽の可否とは無関係に計測する
                // （Artillery等、遮蔽を一切探さないカテゴリでも「交戦で永久に足止め」を防ぐため）。
                bool sameTarget = isEngaging && u.CoverTargetId.HasValue && u.CoverTargetId.Value == u.TargetId.Value;
                u.EngageHoldTimer = isEngaging ? (sameTarget ? u.EngageHoldTimer + dt : dt) : 0f;

                float searchRadius = SearchRadiusByCategory.TryGetValue(type.Category, out float r) ? r : 0f;
                bool coverEligible = searchRadius > 0f && state.Cover != null;

                if (!coverEligible)
                {
                    // Artillery、または遮蔽マップ未供給: 遮蔽は一切探さない（Task44のまま）。
                    // ただしCoverTargetIdはEngageHoldTimerの「同じ相手」判定に使うため、交戦中は
                    // 維持する（次tickのsameTarget判定に必要）。
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    u.CoverTargetId = isEngaging ? u.TargetId : null;
                    continue;
                }

                if (isEngaging)
                {
                    // Task50: 同じ相手と交戦し続けている間は、既に決定済みの遮蔽（見つからなかった
                    // という判断も含む）を一切変更しない。頻繁な位置変更を防ぐための最重要ガード。
                    if (sameTarget) continue;

                    // 新規の交戦、または相手が変わった: このtickで即座に（クールダウンを待たず）評価する。
                    u.CoverTargetId = u.TargetId;
                    u.CoverHoldTimer = 0f;
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

                // Task52 rule4: 進軍中の膠着ウォッチドッグ（OrderTargetPosが無ければ内部で自然にリセットする）。
                UpdateStallWatchdog(u, dt);

                if (!u.OrderTargetPos.HasValue)
                {
                    // 交戦もしていない・進軍目的地も無い（Idle等）→遮蔽移動の対象外。
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    u.CoverHoldTimer = 0f;
                    continue;
                }

                // Task52 rule4: 膠着ウォッチドッグが発動中は遮蔽探索そのものを完全に止め、
                // 道路経路(Path/OrderTargetPos)にそのまま任せる。
                if (u.CoverSuppressionRemaining > 0f)
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    continue;
                }

                // Task52 rule1: 再評価クールダウン(CoverUseIntervalHours)。毎tick探索しない＝
                // 「時々」だけ隠れる。設定済みでも未設定でも、間隔が空いていなければそのまま変えない
                // （MovementStepがbounding到達時にCoverReevaluateCooldownを0へリセットするため、
                // そのケースではここを素通りしてすぐ次の候補を探しに行く）。
                u.CoverReevaluateCooldown -= dt;
                if (u.CoverReevaluateCooldown > 0f) continue;
                u.CoverReevaluateCooldown = CoverUseIntervalHours;

                // Mode 3: 進軍中（交戦していない）。目的地(進軍先の敵基地)方向を脅威とみなし、
                // 目的地に確実に近づく（MinForwardProgress）かつ現在地から近い（MaxCoverDetour）
                // 候補があれば、その遮蔽へ立ち寄って少し隠れる（CoverHold=true、Task52）。
                WorldPos objective = u.OrderTargetPos.Value;
                if (state.Cover.TryFindBestCover(u.Position, objective, searchRadius, u.InstanceId, out WorldPos boundCover))
                {
                    float distNow = u.Position.HorizontalDistanceTo(objective);
                    float distAfter = boundCover.HorizontalDistanceTo(objective);
                    float detour = u.Position.HorizontalDistanceTo(boundCover);
                    if (distAfter < distNow - MinForwardProgress && detour <= MaxCoverDetour)
                    {
                        u.CoverDestination = boundCover;
                        u.CoverHold = true;
                        u.CoverHoldTimer = 0f;
                    }
                    else
                    {
                        // 前進にならない/遠すぎる候補しか無い＝遮蔽を求めて足踏み/大回りするより、
                        // 道路沿いの進軍(Path/OrderTargetPos)をそのまま続けさせる方が良い。
                        u.CoverDestination = null;
                        u.CoverHold = false;
                    }
                }
                else
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                }
            }
        }

        /// <summary>Task52 rule4: OrderTargetPosまでの距離が縮まり続けているかを監視する。
        /// StallEpsilon以上縮まればチェックポイントを更新して安全（膠着ではない）とみなす。
        /// StallTimeoutHoursの間縮まらなければ、CoverSuppressionRemainingをCoverSuppressedHoursへ
        /// セットし、以後その時間は遮蔽探索を完全に止める（Advance側で読む）。
        /// OrderTargetPosが無い場合はウォッチドッグ自体を無効化（チェックポイントをリセット）する。</summary>
        private static void UpdateStallWatchdog(UnitInstance u, float dt)
        {
            // 既に発動中の抑制時間は、進捗の有無に関わらず先に消化する（「後で自動的に再有効化される」ため）。
            if (u.CoverSuppressionRemaining > 0f)
            {
                u.CoverSuppressionRemaining -= dt;
                if (u.CoverSuppressionRemaining < 0f) u.CoverSuppressionRemaining = 0f;
            }

            if (!u.OrderTargetPos.HasValue)
            {
                u.StallTimer = 0f;
                u.LastObjectiveDistance = null;
                return;
            }

            float dist = u.Position.HorizontalDistanceTo(u.OrderTargetPos.Value);

            if (!u.LastObjectiveDistance.HasValue || u.LastObjectiveDistance.Value - dist >= StallEpsilon)
            {
                // 初回計測、または十分前進した：チェックポイントを更新し、膠着タイマーをリセットする。
                u.LastObjectiveDistance = dist;
                u.StallTimer = 0f;
                return;
            }

            u.StallTimer += dt;
            if (u.StallTimer >= StallTimeoutHours)
            {
                u.CoverSuppressionRemaining = CoverSuppressedHours;
                u.StallTimer = 0f;
                u.LastObjectiveDistance = dist; // 次の判定ウィンドウをここから再スタートする
            }
        }

        /// <summary>CoverDestination/CoverHoldに加え、CoverTargetId（Task50の交戦中ロック）と
        /// Task52で追加した各タイマー（CoverHoldTimer/EngageHoldTimer/StallTimer/
        /// LastObjectiveDistance/CoverSuppressionRemaining）も全て解放する。
        /// 「遮蔽・進軍の意思決定そのものを白紙に戻す」経路すべてで使う。交戦中に遮蔽が見つからなかった
        /// 場合だけは、このメソッドを使わずCoverDestination/CoverHoldのみ直接クリアする
        /// （CoverTargetIdは維持し、同じ相手との再探索を防ぐため。上のisEngagingブロック参照）。</summary>
        private static void ClearCover(UnitInstance u)
        {
            u.CoverDestination = null;
            u.CoverHold = false;
            u.CoverTargetId = null;
            u.CoverHoldTimer = 0f;
            u.EngageHoldTimer = 0f;
            u.StallTimer = 0f;
            u.LastObjectiveDistance = null;
            u.CoverSuppressionRemaining = 0f;
        }
    }
}
