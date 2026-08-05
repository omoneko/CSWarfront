using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// simスレッド駆動（OnSimTick）向けの MilitaryManager 追加メンバー。MilitaryManager.cs の
    /// 500行制限のため分離した partial class（Task34のMilitaryManagerManualProduction等と同じ方針）。
    /// _stateLock / State や、Reset()が0クリアする蓄積カウンタ（_economyAccum等）は MilitaryManager.cs
    /// 側で宣言された private static メンバーで、partial class なのでこちらからもそのままアクセスできる。
    ///
    /// OnSimTick は ThreadingExtensionBase.OnAfterSimulationTick 経由でsimスレッドから呼ばれ、
    /// Core判断ロジック＋CSバッファ読み取り専用（Unity GameObject操作は一切行わない）。
    /// </summary>
    public static partial class MilitaryManager
    {
        // 診断ログの間引き用（simスレッドのみが触る）。
        private static int _diagTicks;
        private const int DiagIntervalTicks = 300;
        private const float EconomyIntervalHours = 6f;      // 経済tick間隔（ゲーム内時間、1日4回）
        // Task35: 0.01fだと収入額が小さすぎてUI上ほぼ0にしか見えず「収入が実装されていない」という
        // 誤解の原因になっていた。0.04fはゲームバランス調整用の値（バランスノブ）であり、
        // プレイテストの結果次第で今後さらに調整してよい。
        private const float IncomeRate = 0.04f;

        private const float RoadRebuildIntervalHours = 12f;

        // Task92: 海上航行グリッド（State.SeaNav）の再構築間隔。水域は道路より変化が稀なので長め。
        private const float SeaGridRebuildIntervalHours = 24f;
        private static float _seaGridRebuildAccum;
        private static bool _hasAttemptedSeaGridBuild;

        // Task94: 襲来発生をメインスレッドのトースト表示へ伝えるフラグ（sim→mainの一方向、boolのため良性）。
        private static bool _invasionToastPending;

        // Task101: 線路網（State.Rails）の構築タイマー（道路網と同じパターン）。
        private static float _railBuildRetryAccum;
        private static float _railRebuildAccum;
        private static bool _hasAttemptedRailBuild;
        private const float RoadBuildRetryIntervalHours = 0.25f;

        private const float CoverRebuildIntervalHours = 12f;
        private const float CoverBuildRetryIntervalHours = 0.25f;

        private const float BaseReconcileIntervalHours = 6f;

        private const float MaxHoursPerTick = 1f; // セーブロード直後等の大きな時計ジャンプに対するクランプ上限

        /// <summary>
        /// simスレッド（ThreadingExtensionBase.OnAfterSimulationTick経由）：判断ロジック（Core）と
        /// CS実体操作（建物バッファ読み取り等）をこの一箇所に集約する。
        /// Task19以降、ユニット自体はCS実体（車両）を持たないため、ここでの車両生成/解放は無い。
        /// 注意: ゲームが一時停止中はOnAfterSimulationTickが発火しないため、基地配置・生産は
        /// 停止解除まで待機する（MVPとして許容）。
        /// </summary>
        public static void OnSimTick()
        {
            EnsureInitialized();
            if (State == null) return;

            // dt = 前回tickからの経過ゲーム内時間（時間単位）。ゲーム速度(1x/2x/3x)・一時停止を
            // 自動的に反映する（Task21）。SimulationManager.instance.m_currentGameTime は
            // Assembly-CSharp.dll をリフレクションで確認済みのDateTimeフィールド。
            DateTime now = SimulationManager.instance.m_currentGameTime;
            float dt;
            if (!_hasLastGameTime)
            {
                // 初回tick（またはReset()直後）：時刻の基準を取るだけで進行はさせない。
                _lastGameTime = now;
                _hasLastGameTime = true;
                dt = 0f;
            }
            else
            {
                dt = (float)(now - _lastGameTime).TotalHours;
                _lastGameTime = now;
            }

            if (dt <= 0f) return; // 一時停止中・ゲーム内時計未進行：タイムスタンプ更新のみでこのtickは何もしない
            if (dt > MaxHoursPerTick) dt = MaxHoursPerTick; // セーブロード直後等の巨大ジャンプから保護

            SpeedCalibrationDiagnostics.AccumulateGameHours(dt);

            lock (_stateLock)
            {
                // Task42: 発砲エフェクト(ShotEvent)は「直近1tick分」だけを保持するトランジェント・バッファ
                // なので、戦闘stepより前に必ずクリアする（そうしないと過去tickの分がGame層で二重に
                // 消費され続け、際限なく肥大化する）。
                State.RecentShots.Clear();
                // Task51: 撃破イベント(KillEvent)もRecentShotsと全く同じ理由・同じタイミングでクリアする。
                State.RecentKills.Clear();
                // Task63: ミサイルの着弾/迎撃イベント(RecentImpacts)もRecentShots/RecentKillsと全く同じ
                // 理由・同じタイミングでクリアする（MissileStep.Advance自身はクリアしない契約）。
                State.RecentImpacts.Clear();

                // プレイヤーがOptions指定建物として配置/解体した軍事基地建物を論理基地(WarState.Bases)へ
                // 反映する（Task18、Task82で電力タブの複製プレハブ経路を撤去しこの経路のみに一本化）。
                // CS建物バッファの読み取りを伴うためsimスレッド専用。新規登録された基地は
                // この直後のProductionPlanningから同tickで生産対象になる。
                BasePlacementWatcher.ProcessPending(State);

                // Task106: 塹壕ライン敷設（UIが積んだ2点間へCreateBuildingで連続配置。生成された建物は
                // 次tickのBasePlacementWatcher.ProcessPendingが論理塹壕として登録する）。
                ProcessPendingTrenchLines();

                // Task106: 築城系建物の問題アイコン（道路未接続・電気・水道等）を消す
                // （野戦築城は都市インフラ不要という扱い）。
                SuppressFortificationProblems();

                // Task71: 勢力別アセットのオーバーレイ生成/破棄（BaseVisuals、メインスレッド）が
                // 記録した「この拠点のバニラ見た目を隠すべきか」のペンディングをCS建物バッファへ
                // 反映する（要件2、スタッキング防止）。BasePlacementWatcher.ProcessPendingの直後
                // （基地の登録/解体が確定した後）に置く。
                BaseHiddenSync.ApplyPending();

                // 幽霊基地（建物実体が既に無い論理基地）の掃除（Task24）。CS建物バッファの読み取りを
                // 伴うためsimスレッド専用。毎tickフルスキャンは無駄なので一定間隔でのみ実行する。
                _baseReconcileAccum += dt;
                if (_baseReconcileAccum >= BaseReconcileIntervalHours)
                {
                    _baseReconcileAccum -= BaseReconcileIntervalHours;
                    BasePlacementWatcher.ReconcileBases(State);
                }

                // 生産計画（軍資金消費でキュー補充）→ 生産 → 完成分をUnitInstanceとして追加するのみ
                // （Task19：CS車両のCreateVehicleは行わない。見た目はOnMainVisualUpdate側が
                // State.Unitsから宣言的に再構築する）。
                ProductionPlanning.Advance(State);
                var completed = ProductionStep.Advance(State, dt);
                foreach (var c in completed)
                {
                    uint id = State.AllocInstanceId();
                    var type = State.Types.Get(c.TypeKey);
                    State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                }
                // Task63: 弾道ミサイルの備蓄建造の進捗（ProductionPlanning.Advanceが着手済みの基地のみ対象）。
                // ユニット生産と同じ「生産計画→進捗消化」の並びに揃える。
                MissileStockpile.Advance(State, dt);

                // Task64: 空母の艦載機運用。基地(MilitaryBase)のキュー機構を使わずCarrierAirWingが
                // 直接UnitInstanceを追加する（ProductionStepと同じ並び位置、ProductionStep自身の
                // すぐ後）。例外はCarrierAirWing.Advance内で発生させない設計だが、simループを
                // 絶対に止めないための最終防御としてtry/catchで包む（他のCore step呼び出しには
                // 無い追加のガードだが、新規ロジックを初めてゲームループに繋ぐタスクのため慎重を期す）。
                try
                {
                    CarrierAirWing.Advance(State, dt);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("MilitaryManager: exception in CarrierAirWing.Advance: " + e);
                }

                // 道路網（State.Roads）の構築/再構築。InvasionOrdersが同tickで経路計算できるよう、
                // 進軍命令より先に済ませる（Task23）。未供給ならここで即座に構築し、供給済みなら
                // プレイヤーの道路建設/破壊を反映するため一定間隔で作り直す。ビルド失敗（null）時は
                // 既存グラフをそのまま維持する（一時的な失敗で経路探索能力を失わないため）。
                if (State.Roads == null)
                {
                    // 失敗が続く間、毎tickフルビルド（＋失敗ログ）を試みないよう間隔を空ける
                    // （Task23レビューImportant）。セッション初回の試行だけは間隔を待たず即座に行う。
                    _roadBuildRetryAccum += dt;
                    if (!_hasAttemptedRoadBuild || _roadBuildRetryAccum >= RoadBuildRetryIntervalHours)
                    {
                        _hasAttemptedRoadBuild = true;
                        _roadBuildRetryAccum -= RoadBuildRetryIntervalHours;
                        if (_roadBuildRetryAccum < 0f) _roadBuildRetryAccum = 0f;
                        State.Roads = RoadGraphBuilder.Build();
                    }
                }
                else
                {
                    _roadRebuildAccum += dt;
                    if (_roadRebuildAccum >= RoadRebuildIntervalHours)
                    {
                        _roadRebuildAccum -= RoadRebuildIntervalHours;
                        var rebuilt = RoadGraphBuilder.Build();
                        if (rebuilt != null) State.Roads = rebuilt;
                    }
                }

                // Task92: 海上航行グリッド（State.SeaNav）の構築/再構築。道路網と同じ供給パターン。
                // 初回は即座に構築し、以後はSeaGridRebuildIntervalHoursごとに作り直す（失敗時は既存を維持）。
                if (State.SeaNav == null)
                {
                    if (!_hasAttemptedSeaGridBuild)
                    {
                        _hasAttemptedSeaGridBuild = true;
                        State.SeaNav = SeaGridBuilder.Build();
                    }
                    else
                    {
                        // 初回失敗後（水の無いマップ含む）は再構築間隔でだけ再試行する。
                        _seaGridRebuildAccum += dt;
                        if (_seaGridRebuildAccum >= SeaGridRebuildIntervalHours)
                        {
                            _seaGridRebuildAccum = 0f;
                            State.SeaNav = SeaGridBuilder.Build();
                        }
                    }
                }
                else
                {
                    _seaGridRebuildAccum += dt;
                    if (_seaGridRebuildAccum >= SeaGridRebuildIntervalHours)
                    {
                        _seaGridRebuildAccum = 0f;
                        var rebuiltSea = SeaGridBuilder.Build();
                        if (rebuiltSea != null) State.SeaNav = rebuiltSea;
                    }
                }

                // Task101: 線路網（State.Rails）の構築/再構築。道路網と同じ供給パターン（12hごと）。
                // 再構築のたびに貨物駅のレール接続判定（RailConnected）も引き直す。
                if (State.Rails == null)
                {
                    _railBuildRetryAccum += dt;
                    if (!_hasAttemptedRailBuild || _railBuildRetryAccum >= RoadBuildRetryIntervalHours)
                    {
                        _hasAttemptedRailBuild = true;
                        _railBuildRetryAccum = 0f;
                        State.Rails = RailGraphBuilder.Build();
                        if (State.Rails != null) CargoStationRules.RefreshConnectivity(State);
                    }
                }
                else
                {
                    _railRebuildAccum += dt;
                    if (_railRebuildAccum >= RoadRebuildIntervalHours)
                    {
                        _railRebuildAccum = 0f;
                        var rebuiltRail = RailGraphBuilder.Build();
                        if (rebuiltRail != null) State.Rails = rebuiltRail;
                        CargoStationRules.RefreshConnectivity(State);
                    }
                }

                // 地表高さサンプラー（State.Height）の供給（Task53）。RoadGraph/Coverと違って毎tick
                // 作り直す必要のある「スナップショット」ではなく、TerrainManagerへその場で問い合わせる
                // 薄いアダプタなので、一度だけ生成して以後はそのまま使い回す（未供給時はnullのまま
                // ＝MovementStepが自動的に従来のY補間へフォールバックするため、失敗時の再試行ロジックは
                // 不要）。State自体が破棄されればHeightも道連れで消える（Roadsと同じライフサイクル）。
                if (State.Height == null)
                {
                    State.Height = new SurfaceHeightSampler();
                }

                // 水面サンプラー（State.Water）の供給（Task61）。State.Heightと全く同じパターン：
                // 一度だけ生成して以後はそのまま使い回す薄いアダプタ（未供給時はnullのまま＝
                // MovementStepのSea分岐が自動的に「常に水上」フォールバックへ切り替わる）。
                if (State.Water == null)
                {
                    State.Water = new WaterSampler();
                }

                // 遮蔽物マップ（State.Cover）の構築/再構築（Task44）。RoadGraphと同じ「未供給なら即座に
                // 構築を試みる／供給済みなら一定間隔で作り直す／失敗時は既存マップを維持する」パターン。
                // CoverSeekStepが同tickでこのマップを使えるよう、進軍命令より先に済ませる。
                if (State.Cover == null)
                {
                    _coverBuildRetryAccum += dt;
                    if (!_hasAttemptedCoverBuild || _coverBuildRetryAccum >= CoverBuildRetryIntervalHours)
                    {
                        _hasAttemptedCoverBuild = true;
                        _coverBuildRetryAccum -= CoverBuildRetryIntervalHours;
                        if (_coverBuildRetryAccum < 0f) _coverBuildRetryAccum = 0f;
                        State.Cover = CoverMapBuilder.Build();
                    }
                }
                else
                {
                    _coverRebuildAccum += dt;
                    if (_coverRebuildAccum >= CoverRebuildIntervalHours)
                    {
                        _coverRebuildAccum -= CoverRebuildIntervalHours;
                        var rebuiltCover = CoverMapBuilder.Build();
                        if (rebuiltCover != null) State.Cover = rebuiltCover;
                    }
                }

                // 外部脅威（ゴジラ災害/エイリアン侵略、Task58）の同期。他MODが導入されていなければ
                // 何もしない（ExternalThreatBridge内部で間引き・リフレクション解決結果をキャッシュする）。
                // AI進軍命令（迂回判定）・ThreatCombatStepより前に済ませ、このtick中は最新の位置を使う。
                ExternalThreatBridge.Advance(State, dt);

                // Task94: MissileDisaster（災害ミサイル）の着弾をユニット被害へ反映する
                // （Workshopコメント対応。未導入/旧バージョンなら内部で自動無効化）。
                DisasterImpactBridge.Advance(State);

                // Task94: 外部襲来イベント（Optionsトグル、Workshopコメント要望）。スポーンした部隊は
                // 次のInvasionOrders.AssignAdvanceが通常のAIとして最寄りの敵基地へ進軍させる。
                int invaders = InvasionEvents.Advance(State, dt,
                    WarfrontSettings.InvasionEventsEnabled, WarfrontSettings.InvasionFrequencyIndex);
                if (invaders > 0)
                {
                    _invasionToastPending = true; // メインスレッド（OnMainVisualUpdate）でトースト表示
                    ModConfig.Log("InvasionEvents: spawned an invasion wave of " + invaders + " unit(s).");
                }

                // AI進軍命令（非プレイヤー勢力）。Task58: 自勢力の territory 近くに外部脅威がいれば
                // 敵基地より優先してそちらへ迂回する（InvasionOrders.AssignAdvance内部で判定）。
                foreach (var f in State.Factions)
                    if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id, dt);

                // Task63: AI勢力の弾道ミサイル自動発射（宿敵優先/遠距離Hostile、基地ごとのクールダウン）。
                // 通常のAI進軍命令の直後に判断させる（同じ「AIの意思決定」フェーズにまとめる）。
                MissileDoctrine.Advance(State, dt);

                // 遮蔽移動の意思決定（交戦中のユニットへ遮蔽物を活かした立ち位置を割り当てる、Task44）。
                // MovementStepより前に呼ぶことで、このtickで決めた立ち位置へ同じtick内で動き出せるようにする。
                CoverSeekStep.Advance(State, dt);

                // Task101: 歩兵の陣地志向（敵接近時に塹壕/掩蔽壕へ）。CoverSeekStepの直後に走り、
                // 陣地が使える場合は遮蔽の決定を上書きする（陣地＞建物の陰、FortSeekStepコメント参照）。
                FortSeekStep.Advance(State, dt);

                // 移動（Moving状態のユニットをOrderTargetPosへキネマティック前進、CoverDestination優先はTask44）
                MovementStep.Advance(State, dt);

                // Task99: 基地/空母圏内の自動補給（弾薬回復、SupplyStock消費）と補給トラックの
                // 配車・転送。移動の直後＝このtickの最終位置で「圏内かどうか」を判定する。
                ResupplyStep.Advance(State, dt);
                SupplyTruckStep.Advance(State, dt);
                TransportHeliStep.Advance(State, dt); // Task101: 輸送ヘリ兵站＋搭乗ユニットの位置追従
                TrainStep.Advance(State, dt);         // Task101: 軍用列車の運行（積載/搭乗/走行/降車）

                // Task98: 水際等でスタックしたユニットの自動消滅（移動直後＝このtickの実際の変位を
                // 見た上で判定する。自拠点付近・非Moving状態は対象外、無音無爆発でDead化のみ）。
                int stuckDespawned = StuckCleanupStep.Advance(State, dt);
                if (stuckDespawned > 0)
                    ModConfig.Log("StuckCleanupStep: despawned " + stuckDespawned + " stuck unit(s).");

                // Task79: 自爆ドローンの目標ロック・体当たり起爆。MovementStepの直後・CombatStepより前に
                // 置く（このtickで決めたロックが次tickのMovementStepダイブ移動から参照されるのは通常の
                // AI意思決定ステップと同じ1tick遅延パターン、CoverSeekStep→MovementStepと対称）。
                // CombatStepより前に置くことで、KamikazeStepがCurrentHPを0にした自爆ドローン自身・
                // 起爆で倒した相手ユニットの両方を、直後のCombatStep第2パス（死亡判定・KillEvent発行）が
                // 同tick内で拾える。
                KamikazeStep.Advance(State, dt);

                // 戦闘（ユニット同士＋基地攻撃＋外部脅威、Task58）→ 占領 → 勢力状態の再導出（Task46:
                // 拠点の自衛射撃は廃止。Eliminated/HomeBaseIdはOccupationが直接いじらず、
                // FactionStatus.Refreshが毎tick所有基地の有無から導出し直す＝一度Eliminatedになっても
                // 基地を取り戻せば復活する）。ThreatCombatStepは通常の戦闘に「加えて」実行するだけで、
                // ターゲット選定を奪い合わない（射程内なら両方に同時に撃つ、Core/ThreatCombatStep参照）。
                // Task101: 築城（掩蔽壕/砲兵陣地）の自動射撃。CombatStepの前に置くことで、
                // ここで撃破したユニットをCombatStep第2パス（死亡判定・KillEvent）が同tickで拾う
                // （KamikazeStepと同じパターン）。
                FortCombatStep.Advance(State, dt);
                CombatStep.Advance(State, dt);
                BaseCombatStep.Advance(State, dt);
                ThreatCombatStep.Advance(State, dt);

                // Task65: 脅威（ゴジラ/エイリアン）の近接オーラダメージ。ThreatCombatStepの直後
                // （ユニット→脅威の攻撃が確定した直後）に実行する。ThreatRelationsを見ない逆方向
                // （脅威→ユニット）のダメージなので、通常戦闘のターゲット選定には一切影響しない。
                ThreatAuraStep.Advance(State, dt);

                // Task63: 弾道ミサイルの飛翔進捗・迎撃・着弾解決。仕様どおりThreatCombatStepの直後・
                // 経済tickより前に実行する（着弾ダメージが同tickのOccupation/FactionStatus再導出に反映される）。
                MissileStep.Advance(State, dt);

                Occupation.ResolveCaptures(State);
                FactionStatus.Refresh(State);

                // 戦闘域（Task54）の期限管理。上のCombatStep/BaseCombatStepが今tick分の報告を
                // 積み終えた後に減算する（同tickに報告された分がいきなり0未満になって消えないように）。
                State.CombatZones.Advance(dt);

                // 戦闘域に応じた道路封鎖（Task54）。CombatZonesが確定した後、民間の経路計算より前に
                // 反映しておきたいところだが、MovementStep（経路の消化・新規計算含む）は既に上で
                // 終わっている。次tickの経路計算からは反映されるため1tickの遅延は許容する。
                CombatRoadBlocker.Advance(State, dt);

                // Task65: 戦闘域(State.CombatZones)付近のまれな火災/建物崩壊。DisasterHelpersはsim
                // スレッド専用のため、同じくCS建物バッファ絡みのCombatRoadBlockerの直後に置く
                // （道路封鎖の判定が終わった後で問題ない＝両者は互いに依存しない独立した処理）。
                CombatCollateral.Advance(State, dt);

                // 経済（低頻度・ゲーム内時間基準）。時間を失わないよう間隔ぶんだけ減算する
                // （ゼロクリアだとdtの端数が毎回捨てられ、実質的な頻度が下がってしまうため）。
                _economyAccum += dt;
                if (_economyAccum >= EconomyIntervalHours)
                {
                    _economyAccum -= EconomyIntervalHours;
                    var samples = DevelopmentSampler.Sample(); // Task 12
                    foreach (var b in State.Bases)
                    {
                        if (b.OwnerFactionId == null) continue;
                        // Task101: 築城・貨物駅は収入を生まない（1km圏収入は軍事基地4種のみ。
                        // 塹壕を並べるだけで収入が倍々になるのを防ぐ）。
                        if (FortificationRules.IsFortification(b.Type)) { b.LastIncome = 0f; continue; }
                        // Task99: 3資源経済。1km圏のゾーン別発展度から住宅→人的資源、
                        // 商業/オフィス→資金、工業→生産力を産出する（旧: 全建物→資金のみ）。
                        ZonedIncome inc = TerritoryIncome.ZonedForBase(b, samples, IncomeRate);
                        Faction owner = State.FindFaction(b.OwnerFactionId.Value);
                        if (owner != null)
                        {
                            owner.AddTreasury(inc.Funds);
                            owner.AddManpower(inc.Manpower);
                            owner.AddProduction(inc.Production);
                        }
                        b.LastIncome = inc.Funds; // Task35: UIが基地パネルへ表示するためのキャッシュ（非永続化）
                    }

                    // Task99: 補給物資の自動生産（生産力→SupplyStock、不足時は資金代替）と
                    // 補給トラックの自動維持（陸軍基地ごと、勢力30台上限）。どちらも経済tickの頻度で十分。
                    foreach (var f in State.Factions)
                        ResupplyStep.ProduceSupplies(f);
                    SupplyTruckStep.MaintainTrucks(State);
                    TransportHeliStep.MaintainHelis(State); // Task101: 輸送ヘリの自動維持
                    TrainStep.MaintainTrains(State);        // Task101: 軍用列車の自動維持（駅ペアごと）
                }

                // 死亡ユニットの掃除。見た目（GameObject）は表現を持たないためここでの結合は不要
                // （UnitVisuals.Syncが次回のOnMainVisualUpdateでState.Unitsとの差分から自動的に
                // 破棄する＝宣言的reconcile）。
                State.Units.RemoveAll(u => u.State == UnitState.Dead);

                LogDiagnostics(dt);
            }
        }

        /// <summary>
        /// 一定tickごとに実行時状態を1行で記録する診断ログ（実機でしか再現しない不具合の調査用）。
        /// ユニットが実際に移動しているか・交戦しているか・基地HPが削れているかを事実として残す。
        /// 呼び出し元が _stateLock を保持していること。
        /// </summary>
        private static void LogDiagnostics(float dt)
        {
            _diagTicks++;
            if (_diagTicks < DiagIntervalTicks) return;
            _diagTicks = 0;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("DIAG dt=").Append(dt.ToString("0.000")).Append("h");
                sb.Append(" units=").Append(State.Units.Count);

                // 勢力別ユニット数（Task24）：どの勢力にもユニットが存在しない不具合を一目で分かるようにする。
                // +1はInvader勢力（Task95、Faction.InvaderFactionId=5）のぶん。
                var unitsPerFaction = new int[WarfrontSettings.MaxFactions + 1];
                for (int u = 0; u < State.Units.Count; u++)
                {
                    byte fid = State.Units[u].FactionId;
                    if (fid < unitsPerFaction.Length) unitsPerFaction[fid]++;
                }
                sb.Append(" |");
                for (int f2 = 0; f2 < unitsPerFaction.Length; f2++)
                    sb.Append(" uf").Append(f2).Append("=").Append(unitsPerFaction[f2]);

                sb.Append(" | roads=").Append(State.Roads != null ? State.Roads.NodeCount : 0);
                sb.Append(" cover=").Append(State.Cover != null ? State.Cover.Count : 0);
                // Task58: 現在アクティブな外部脅威（ゴジラ/エイリアン）の残りHP%を一目で分かるようにする。
                for (int ti = 0; ti < State.Threats.Count; ti++)
                {
                    var t = State.Threats[ti];
                    float pct = t.MaxHP > 0f ? (t.CurrentHP / t.MaxHP) * 100f : 0f;
                    sb.Append(" threat=").Append(t.Kind).Append(" ").Append(pct.ToString("0")).Append("%");
                }
                for (int i = 0; i < State.Units.Count && i < 2; i++)
                {
                    UnitInstance u = State.Units[i];
                    UnitType ut = State.Types.Get(u.TypeKey);
                    sb.Append(" | u").Append(u.InstanceId)
                      .Append(" type=").Append(u.TypeKey)
                      .Append(" f=").Append(u.FactionId)
                      .Append(" st=").Append(u.State)
                      .Append(" hp=").Append(u.CurrentHP.ToString("0"))
                      .Append(" pos=").Append(u.Position.X.ToString("0")).Append(",").Append(u.Position.Z.ToString("0"))
                      .Append(" tgt=").Append(u.OrderTargetPos.HasValue
                          ? u.OrderTargetPos.Value.X.ToString("0") + "," + u.OrderTargetPos.Value.Z.ToString("0")
                          : "none");
                    // 遮蔽移動モード（Task45）: territory=自勢力圏内で遮蔽移動なし、hold=交戦中で遮蔽に留まる、
                    // bound=進軍中で遮蔽から遮蔽へ跳んでいる最中、none=遮蔽移動の対象外/候補なし。
                    string coverMode = CoverSeekStep.IsInFriendlyTerritory(State, u) ? "territory"
                        : u.CoverDestination.HasValue ? (u.CoverHold ? "hold" : "bound")
                        : "none";
                    sb.Append(" cov=").Append(coverMode);
                    // Speed（マップ距離/ゲーム内時間）を較正定数（想定値）でkm/hに逆変換して表示する（Task26）。
                    if (ut != null)
                        sb.Append(" spd=").Append((ut.Speed * SpeedCalibration.InGameHoursPerRealSecond * 3.6f).ToString("0")).Append("km/h");
                    if (i == 0)
                    {
                        // 最初にサンプルしたユニットについてのみ、道路経路の消化状況を記録する（Task23）。
                        sb.Append(" path=").Append(u.Path != null ? u.PathIndex + "/" + u.Path.Count : "none");
                    }
                }
                for (int j = 0; j < State.Bases.Count; j++)
                {
                    MilitaryBase b = State.Bases[j];
                    sb.Append(" | base").Append(b.BaseId)
                      .Append(" own=").Append(b.OwnerFactionId.HasValue ? b.OwnerFactionId.Value.ToString() : "-")
                      .Append(" hp=").Append(b.CurrentHP.ToString("0"))
                      .Append(" g=").Append(b.CaptureGraceHours.ToString("0"))
                      .Append(" pos=").Append(b.Position.X.ToString("0")).Append(",").Append(b.Position.Z.ToString("0"));
                }
                for (int k = 0; k < State.Factions.Count; k++)
                {
                    Faction f = State.Factions[k];
                    if (f.Treasury > 0f || f.HomeBaseId.HasValue)
                        sb.Append(" | f").Append(f.Id).Append(" $").Append(f.Treasury.ToString("0"));
                }
                sb.Append(" | visuals=").Append(UnitVisuals.Count);
                ModConfig.Log(sb.ToString());
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LogDiagnostics error: " + e);
            }
        }
    }
}
