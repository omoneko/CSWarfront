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
    /// おりTrySampleHeightが成功すれば、X/Zを計算した直後に必ずそれでYを上書きする（ウェイポイント移動・
    /// 直線移動・遮蔽移動・集結移動のすべての経路で共通）。state.Height == null、またはTrySampleHeightが
    /// 失敗（false）した場合は、従来のY補間をそのまま維持する（安全側フォールバック・既存テストの前提を
    /// 変えない）。ハードニング（Task53追記）: 失敗時に0f等の失敗値をそのままYへ採用すると、地表が
    /// 0f付近ではないマップ（例: 実測約270）で1tickだけ地表の遥か下へテレポートする可視バグになるため、
    /// Try形式で「失敗」を明示的に判別しフォールバックする（ResolvePosition参照）。
    ///
    /// Task55:「ユニットが空中戦を始める（地面から浮いたまま戦闘する）」不具合の修正。原因はGame層の
    /// SurfaceHeightSamplerがTerrainManager.SampleDetailHeightの誤ったオーバーロード（(float,float)、
    /// ワールド座標をそのままdetailグリッド座標として渡し、かつ1/64のスケール変換も欠落）を呼んでいた
    /// ことで、Task53のTrySampleHeightは常に「成功（true）」しつつ荒唐無稽な高さを返していた
    /// （Game層側の修正はSurfaceHeightSampler.cs参照）。Core側では多層防御として、ResolvePositionに
    /// MaxSurfaceDeviationによる乖離クランプを追加した: TrySampleHeightがtrueを返しても、値が
    /// 補間済みYから大きく乖離していれば採用せず補間済みYを使う。これにより、IHeightSampler実装側の
    /// 座標系/API誤用が将来再発しても、Coreはユニットを空へ打ち上げるような致命的な被害を機械的に
    /// 防げる（ResolvePosition参照）。</summary>
    public static partial class MovementStep
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

        /// <summary>Task55（「ユニットが空中戦を始める」不具合の多層防御）: state.Height
        /// （IHeightSampler、Game層実装）が返すサンプリング済みYは、Task53の補間済みY
        /// （ウェイポイント/直線移動の従来ロジックが計算した値）からこの値を超えて乖離していれば
        /// 採用しない。IHeightSampler実装（Game層のTerrainManager呼び出し）が将来誤ったAPI/
        /// オーバーロード・座標系を使う不具合を再発しても、Core側のこのクランプが「ユニットを
        /// 空へ打ち上げる」規模の被害を機械的に防ぐ（Task53が意図した盛土などcm～m規模の
        /// 小さな地表補正はそのまま許容する）。</summary>
        public const float MaxSurfaceDeviation = 15f;

        /// <summary>Task61: 航空ユニットが巡航する、地表からの高さ（マップ単位）。AdvanceAirが
        /// state.Height(IHeightSampler)で地表をサンプリングできた場合、Yを常に groundHeight + この値へ
        /// 設定する（道路の高さに合わせるMovementStepの陸上ロジックとは全く別の垂直方向の扱い）。</summary>
        public const float CruiseAltitude = 120f;

        /// <summary>Task79: 自爆ドローン（UnitCategoryFlags.IsKamikaze）が目標をロックしてダイブする際の
        /// 速度倍率。type.Speed（通常の巡航速度）にこれを掛けた値をダイブの実効速度として使う——
        /// 「目標に向かって加速して突っ込む」という体当たり特攻らしい見た目にするための、ドキュメント化
        /// されたバランス調整用の定数（KamikazeStepではなくMovementStep側で管理する。ダイブの移動
        /// そのものはこのクラスの責務のため）。</summary>
        public const float DiveSpeedMultiplier = 1.5f;

        // --- Task77: 「地上ユニットが橋の上を渡ってくれない」「海の中に入っていける」不具合の修正 ---
        //
        // 【橋】RoadGraphBuilderを確認した結果、Road-serviceのセグメントは橋も含めて全て道路網グラフに
        // 採用されており（ItemClass.Service.Roadでの判定のみ、橋固有のフラグでの除外は無い）、グラフ
        // 自体に橋の欠落は無かった。真因はCore側のY解決にあった: Task53は「TrySampleHeightが成功すれば
        // X/Zを計算した直後に必ずそれでYを上書きする」を、ウェイポイント追従(ConsumePath)と直線移動
        // (AdvanceStraight)の区別なく一律に適用していた。橋は地形をその高さまでフラット化しない（橋桁は
        // 地形とは別に浮いている構造物）ため、橋の直下でGame層のSurfaceHeightSampler
        // (TerrainManager.SampleDetailHeight)が返すのは水面/川底の高さであり、それが橋の上のユニットの
        // Yへ採用されると「橋を渡っているのに水中へ沈んで見える」→ユーザーには「橋を渡ってくれない」
        // ように見えていた（MaxSurfaceDeviationの15fクランプは橋の高さ次第で乖離が15f以内に収まる
        // こともあり、常にガードできるわけではなかった）。
        //
        // 修正: Yの取得元を移動の文脈で明確に分離する。
        //   - 経路上（Path/ConsumePath、ウェイポイントを道路網に沿って辿っている間）: Terrainサンプラー
        //     には一切触れず、道路網ノード自身のY（RoadGraphBuilderがNetNode.m_position.yから
        //     そのまま持ってきた値＝橋なら橋桁の高さ）をそのまま採用する（Task37が最初に導入した
        //     挙動へ、経路上に限定して回帰させた形）。
        //   - オフロード（経路が尽きた後の直線フォールバック、遮蔽移動AdvanceTowardCover、集結移動
        //     AdvanceTowardRallyの直線部分、経路自体が無いケース。いずれもAdvanceStraight/MoveToward
        //     経由）: 従来どおりstate.Height(IHeightSampler)で"見た目の"地表へスナップする
        //     （Task53/55の多層防御はそのまま維持）。
        //
        // 【海】陸上(Land)ユニットのオフロード移動には、Sea/AirのAdvanceSeaと違って水域チェックが
        // 一切無く、これが「地上ユニットが海の中に入っていける」不具合の直接の原因だった
        // （state.Water(IWaterSampler)自体はTask61でGame層まで配線済みだったが、Land分岐の
        // ConsumePath/AdvanceStraightからは一度も参照されていなかった）。
        // 修正: オフロード移動（AdvanceStraight/MoveToward）でのみ、移動後の着地点がwater.IsWaterなら
        // そのtickの移動を丸ごとキャンセルする（Positionを更新しない＝波打ち際で足止めする）。
        // AdvanceSea（陸へ踏み込む一歩を捨てて足止めする、Task61）と対称な設計にし、タイムアウトで
        // Idleへ遷移させたりはしない（u.State/OrderTargetPosは変更しないため、後続tickで目的地が
        // 変わる・道路網ができる等の理由で経路が見つかれば自然に再開できる、シンプルさと決定性を
        // 優先した仕様）。経路上(ConsumePath)には意図的にこのチェックを追加しない
        // ——橋の直下はHasWater的には"水"だが、路面(道路網ノード)を辿っている限りは通行可能で
        // なければならないため（水域チェックを経路上にまで広げると橋を渡れなくなる回帰になる）。
        //
        // CoverSeekStep側にも対をなす修正を入れている（陸上ユニットの立ち位置候補が水中ならその候補を
        // 棄却する）。詳細はCoverSeekStep.csを参照。

        public static void Advance(WarState state, float dt)
        {
            IHeightSampler height = state.Height; // Task53: null-safeなローカルへ1回だけ拾っておく。
            IWaterSampler water = state.Water; // Task61: 同様にnull-safeなローカルへ拾う。

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.Order == UnitOrder.Hold) continue; // Task48: 停止命令＝常に不動。

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                float stepLen = type.Speed * dt;
                if (stepLen <= 0f) continue;

                // Task61: Sea/Airは陸上の道路経路(Path)・遮蔽(CoverDestination)・territory-based
                // slowdownを一切使わない、完全に別の移動則（クラス冒頭コメント参照）。CoverSeekStepが
                // そもそもLand以外にはCoverDestinationを設定しないため、通常はu.CoverDestinationが
                // 立っていることは無いが、防御的に陸上ロジック（下のif以降）へは絶対に流れ込ませない。
                if (type.Domain != Domain.Land)
                {
                    // Task79: 自爆ドローンが目標をロック中（KamikazeStep.Advanceが前tickで
                    // TargetId/TargetThreatIdを書いた）なら、通常の巡航(AdvanceAir)ではなく専用の
                    // ダイブ移動で目標へ直進する。ロックが無い/失った（目標を撃破・見失った）場合は
                    // 下のResolveDomainObjectiveへフォールスルーし、通常の航空ユニットと同じく
                    // 巡航高度へ戻ってOrderTargetPos（進撃/集結命令）へ向かう。
                    if (type.Category.IsKamikaze())
                    {
                        WorldPos? diveTarget = ResolveKamikazeTarget(state, u);
                        if (diveTarget.HasValue)
                        {
                            AdvanceKamikaze(u, stepLen, diveTarget.Value);
                            continue;
                        }
                    }

                    WorldPos? objective = ResolveDomainObjective(u);
                    if (!objective.HasValue) continue;

                    if (type.Domain == Domain.Air)
                        AdvanceAir(u, stepLen, objective.Value, height);
                    else // Domain.Sea
                        AdvanceSea(u, stepLen, objective.Value, water, dt);
                    continue;
                }

                if (u.CoverDestination.HasValue)
                {
                    AdvanceTowardCover(u, stepLen, dt, height, water);
                    continue;
                }

                if (u.Order == UnitOrder.RallyHold)
                {
                    if (u.RallyPoint.HasValue) AdvanceTowardRally(u, stepLen, height, water);
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

                stepLen = ConsumePath(u, stepLen);
                if (stepLen > 0f)
                    AdvanceStraight(u, u.OrderTargetPos.Value, stepLen, height, water);
            }
        }

        /// <summary>Task48: RallyPointへ向けたキネマティック移動。UnitCommands.ApplyRallyがRallyPoint宛に
        /// 計算した道路経路(Path)があればまずそれを消化し、残りは直線移動でフォールバックする
        /// （ConsumePath/AdvanceStraightは通常のOrderTargetPos移動と全く同じヘルパーを再利用する）。
        /// CoverArrivalDistance以内まで近づいたら以後は何もしない（その場に留まる）。</summary>
        private static void AdvanceTowardRally(UnitInstance u, float stepLen, IHeightSampler height, IWaterSampler water)
        {
            WorldPos rally = u.RallyPoint.Value;
            float dist = u.Position.HorizontalDistanceTo(rally);
            if (dist <= CoverArrivalDistance) return;

            stepLen = ConsumePath(u, stepLen);
            if (stepLen > 0f)
                AdvanceStraight(u, rally, stepLen, height, water);
        }

        /// <summary>CoverDestinationへ向けたキネマティック移動。CoverArrivalDistance以内に入ったら、
        /// CoverHoldに応じて「その場で停止し続ける」(true、Task52でMaxCoverHoldHoursの上限つき)か
        /// 「CoverDestinationをクリアして次tickから通常の経路/直線移動または次の遮蔽評価へ委ねる」
        /// (false、互換維持用のフォールバック経路)かを分岐する。それ以外の距離ではAdvanceStraightと
        /// 同じ補間で進む（この間はCoverHoldTimerを0のまま維持し、実際に静止した時間だけを計測する）。</summary>
        private static void AdvanceTowardCover(UnitInstance u, float stepLen, float dt, IHeightSampler height, IWaterSampler water)
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
            AdvanceStraight(u, coverPos, stepLen, height, water);
        }

        /// <summary>Pathが残っていればウェイポイントを順に消化する。残ったstepLen（直線フォールバック用）を返す。
        /// Task77: 経路上（道路網ノード間）の移動であり、Terrainサンプラー/水域チェックのどちらにも
        /// 一切触れない（下のMoveTowardOnPath/waypointスナップ参照）。橋を含む道路網ノードのYは
        /// RoadGraphBuilderが道路網自身から取得した値であり信頼できるため、地形サンプラーで上書きすると
        /// 橋の直下の水面/地形へ沈む（クラス冒頭のTask77コメント参照）。同じ理由で水域チェックも行わない
        /// ——橋の直下はHasWater的に"水"だが、道路網を辿っている限り通行可能でなければならない。</summary>
        private static float ConsumePath(UnitInstance u, float stepLen)
        {
            if (u.Path == null) return stepLen;

            while (stepLen > 0f && u.PathIndex < u.Path.Count)
            {
                WorldPos waypoint = u.Path[u.PathIndex];
                float dist = u.Position.HorizontalDistanceTo(waypoint);

                if (dist <= stepLen || dist <= 0.01f)
                {
                    // Task37/Task77: ウェイポイントに到達したらそのウェイポイントのYをそのまま採用する
                    // （道路網ノード自身のY＝橋なら橋桁の高さ）。Task53〜76はここもTerrainサンプラーで
                    // 上書きしていたが、それが「橋を渡ってくれない」不具合の真因だったため、経路上では
                    // 二度とTerrainサンプラーを参照しない（クラス冒頭のTask77コメント参照）。
                    u.Position = new WorldPos(waypoint.X, waypoint.Y, waypoint.Z);
                    stepLen -= dist;
                    u.PathIndex++;
                }
                else
                {
                    MoveTowardOnPath(u, waypoint, stepLen);
                    return 0f;
                }
            }

            return stepLen;
        }

        /// <summary>Task77: 経路上（ウェイポイント間）の部分移動。X/Zと同じ補間係数でウェイポイント自身の
        /// Yへ向けて補間するのみで、Terrainサンプラー/水域チェックのどちらにも触れない（ConsumePathの
        /// 到達分岐と同じ理由。橋の勾配はウェイポイントのY自体が表現しているため、これで十分かつ正確）。</summary>
        private static void MoveTowardOnPath(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }

        /// <summary>Task77: オフロード（経路が尽きた後の直線フォールバック/遮蔽移動/集結移動の直線部分）の
        /// 移動。ConsumePathと異なり、ここはTerrainサンプラー（見た目の地表へスナップ、Task53/55）と
        /// 水域チェック（陸上ユニットが水中へ踏み込む一歩を丸ごとキャンセルする、Task77）の両方を適用する。
        /// 道路網に無い場所を進む以上、地表の変化を追従しつつ、水域には入らせない。
        /// Task77ハードニング: dist&lt;=0.01f（呼び出し時点で既にtargetへ到達済み）なら何もしない。
        /// これはConsumePathがPathを最後まで消化し、かつPathの最終ウェイポイントがOrderTargetPosと
        /// 一致する（＝道路が目的地まで通じている）場合に発生する「消化しきれなかった端数stepLen」で
        /// このメソッドが呼ばれるケースを吸収するために必須: そのケースではConsumePathが既に道路網
        /// ノード自身のY（橋なら橋桁の高さ）へ厳密にスナップ済みであり、dist==0のこの時点でTerrain
        /// サンプラーによる"到達"処理を重ねて行うと、せっかく経路上ロジックが避けたはずの地表への
        /// スナップが土壇場で起きてしまう（橋の終点＝目的地というケースで再発する）。実際に0より大きい
        /// 距離を移動する時だけ地表判定/水域判定を適用する、という原則をここで明確にする。</summary>
        private static void AdvanceStraight(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height, IWaterSampler water)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return; // 既に到達済み（多くの場合ConsumePathの経路上スナップ）: 何もしない。

            if (dist <= stepLen)
            {
                // Task77: 着地点そのものが水中なら、このtickは一切移動しない（波打ち際で足止め）。
                if (water != null && water.IsWater(target.X, target.Z)) return;
                // 到達: targetのYをそのまま採用（Task37、旧:u.Position.Y維持）。
                // Task53: state.Heightが供給されていれば実際の地表のYへスナップする。
                u.Position = ResolvePosition(target.X, target.Y, target.Z, height);
            }
            else
            {
                MoveToward(u, target, stepLen, height, water);
            }
        }

        /// <summary>X/Zと同じ補間係数(t = stepLen/dist)でYも目標へ向けて補間する（Task37、オフロード専用）。
        /// 旧仕様は常に u.Position.Y を維持していたため、道路の勾配（橋・坂）を無視して水平飛行して
        /// しまい「路面から浮いている」ように見えるバグの原因だった。X/Zの補間ロジック自体は変更していない
        /// （オーバーシュートは発生しない、既存のAdvance_stops_at_target_without_overshootで保証）。
        /// Task53: heightが供給されていれば、この補間したY自体は使わず、計算済みのX/Zで実際の地表を
        /// サンプリングした値をYに採用する（ResolvePositionが分岐）。
        /// Task77: 一歩先(nx,nz)が水域なら、このtickは一切移動しない（陸上ユニットが海へ踏み込む一歩を
        /// 丸ごとキャンセルする＝波打ち際で足止め、AdvanceSeaの陸地版と対称の設計）。</summary>
        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height, IWaterSampler water)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            if (water != null && water.IsWater(nx, nz)) return;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = ResolvePosition(nx, ny, nz, height);
        }

        /// <summary>Task53: 移動計算で得たX/Y/Zから実際に採用するWorldPosを組み立てる。heightが供給されて
        /// おりTrySampleHeightが成功すれば、渡されたy（従来のウェイポイント/補間Y）を捨てて
        /// サンプリングした値（建設後の実地表）で上書きする。heightがnull、またはTrySampleHeightが
        /// 失敗（false）した場合は渡されたyをそのまま使う（従来どおりの補間・スナップ挙動、
        /// 既存テストの前提を変えない安全側フォールバック）。
        /// ハードニング: サンプリング失敗時に失敗値（out引数の不定値）をYへ採用することは絶対にしない
        /// （TerrainManager瞬断時に0fがそのまま採用され地表の遥か下へテレポートする不具合の再発防止）。
        /// Task55（多層防御の追加段）: TrySampleHeightがtrueを返していても、値が渡されたy（補間済みY、
        /// このユニットが既に持っているローカルな地表の目安）からMaxSurfaceDeviationを超えて乖離して
        /// いれば、サンプリング値を信用せず補間済みyをそのまま採用する。IHeightSampler実装側が将来
        /// また誤った座標系/APIで荒唐無稽な値を返しても（「ユニットが空中戦を始める」規模の不具合）、
        /// Coreはこの1点で機械的に被害を止める。Task53が意図した小さな地表補正（盛土・橋など）は
        /// 乖離が小さいためそのまま通る。</summary>
        private static WorldPos ResolvePosition(float x, float y, float z, IHeightSampler height)
        {
            float sampled;
            if (height != null && height.TrySampleHeight(x, z, out sampled))
            {
                float deviation = sampled - y;
                if (deviation < 0f) deviation = -deviation;
                if (deviation <= MaxSurfaceDeviation) y = sampled;
            }
            return new WorldPos(x, y, z);
        }

        // --- Task61: Sea/Air共通の目的地解決 ---

        /// <summary>Sea/Airユニットが向かうべき目的地を返す（無ければnull＝このtickは動かない）。
        /// RallyHold中はRallyPointへ（land同様、CoverArrivalDistanceの停止判定は不要——後述の
        /// AdvanceAir/AdvanceSeaはdist<=stepLenで厳密に目的地へスナップするため、次tick以降は
        /// dist=0でno-opになり自然に停止する）。それ以外はState==Moving/Engagingの間だけ
        /// OrderTargetPosへ向かう（land版と異なりCoverSeekStepがSea/AirにCoverDestinationを
        /// 一切設定しないため「Engaging中は停止して遮蔽で応戦する」という陸上の駆け引きは無く、
        /// 単純に目的地へ向かい続けながら射程内なら交戦する、というMVPの割り切り）。
        /// Idle・目的地未設定の場合はnullを返し、このtickは静止する。</summary>
        private static WorldPos? ResolveDomainObjective(UnitInstance u)
        {
            if (u.Order == UnitOrder.RallyHold) return u.RallyPoint;
            if (u.State != UnitState.Moving && u.State != UnitState.Engaging) return null;
            return u.OrderTargetPos;
        }

        /// <summary>Task61: 航空ユニットの移動。RoadGraph/CoverMapを一切使わず目的地へ直線移動し、
        /// Yは常に「(移動後のX/Zで)地表高さをサンプリングできればそれ+CruiseAltitude」、
        /// サンプリングに失敗すれば従来のYをそのまま維持する（クラス冒頭のIHeightSampler供給パターンと
        /// 同じ安全側フォールバック）。地表からの相対高度を毎tick取り直すため、山岳地帯の上を飛べば
        /// 自然にYも上下する。</summary>
        private static void AdvanceAir(UnitInstance u, float stepLen, WorldPos objective, IHeightSampler height)
        {
            float dist = u.Position.HorizontalDistanceTo(objective);
            float nx, nz;
            if (dist <= stepLen || dist <= 0.01f) { nx = objective.X; nz = objective.Z; }
            else
            {
                float t = stepLen / dist;
                nx = u.Position.X + (objective.X - u.Position.X) * t;
                nz = u.Position.Z + (objective.Z - u.Position.Z) * t;
            }

            float ny = u.Position.Y; // サンプリング失敗時は従来のYを維持（フォールバック）。
            float groundY;
            if (height != null && height.TrySampleHeight(nx, nz, out groundY))
                ny = groundY + CruiseAltitude;

            u.Position = new WorldPos(nx, ny, nz);
        }

        // Task79: ResolveKamikazeTarget/AdvanceKamikazeはMovementStepKamikaze.csへ分離した
        // （500行/ファイルの上限に収めるため。基地ロック対応でこのファイルが超過したのが分離の
        // 直接のきっかけ——MovementStepSea.csと同じpartial classパターン）。
    }
}
