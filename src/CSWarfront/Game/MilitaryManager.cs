using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。
    /// スレッド境界（重要・Task19で変更）:
    ///  - ユニットはもうCS車両（VehicleManager）を借用しない。CS車両AI（例: FireTruckAI）は
    ///    TransferManager等サービス系のディスパッチに組み込まれており、TransferReason.None等の
    ///    無効な文脈で呼ばれるとオファー簿記が配列範囲外アクセスでクラッシュする（実機ログ確認済み、
    ///    TransferManager.RemoveIncomingOffer）。Task15でCore（MovementStep）がユニット位置を
    ///    論理的に所有するようになって以降、CS車両AI（パスファインディング等）は一切使われておらず
    ///    得るものがない。そのためユニットの見た目は素のUnity GameObject（UnitVisuals）とし、
    ///    車両プレハブからはメッシュ＋マテリアルのみを借用してAIには一切触れない。
    ///  - sim スレッド（OnSimTick、ThreadingExtensionBase.OnAfterSimulationTick経由）:
    ///    Core判断ロジック＋CSバッファ読み取り（基地建物・地形サンプリング等）専用。
    ///    _stateLock を保持したまま直列に実行する。
    ///  - メインスレッド（OnMainVisualUpdate、ThreadingExtensionBase.OnUpdate経由）:
    ///    Unityオブジェクト操作（GameObject生成/移動/破棄）専用。CS実体（Vehicle/Building等）には
    ///    一切触れない。_stateLock はスナップショット構築中のみ保持し、Unity操作はロック解放後に行う
    ///    （ロック保持中にUnity APIを呼ぶと最悪ケースでsimスレッドを長時間ブロックしうるため）。
    ///  - 注意: ゲームが一時停止中は OnAfterSimulationTick が発火しないため、基地配置やスポーンは
    ///    停止解除まで待機する（MVPとして許容）。一方 OnUpdate は一時停止中も動くため、見た目の同期は
    ///    停止中も続く（意図的：一時停止中も現在の配置を表示し続けるため）。
    ///  - _stateLock は、save/loadスレッド（SerializeLocked/LoadAndRebuild）とsimスレッド(OnSimTick)
    ///    の State への同時アクセスを防ぐために引き続き必要（net35のため
    ///    System.Collections.Concurrent は使用不可）。
    /// </summary>
    public static partial class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        // 診断ログの間引き用（simスレッドのみが触る）。
        private static int _diagTicks;
        private const int DiagIntervalTicks = 300;
        private const float EconomyIntervalHours = 6f;      // 経済tick間隔（ゲーム内時間、1日4回）
        // Task35: 0.01fだと収入額が小さすぎてUI上ほぼ0にしか見えず「収入が実装されていない」という
        // 誤解の原因になっていた。0.04fはゲームバランス調整用の値（バランスノブ）であり、
        // プレイテストの結果次第で今後さらに調整してよい。
        private const float IncomeRate = 0.04f;

        // 道路グラフの再構築間隔（ゲーム内時間）。プレイヤーが道路を敷設/破壊し続けるため定期的に作り直す
        // （Task23）。simスレッドのみが触る。
        private static float _roadRebuildAccum;
        private const float RoadRebuildIntervalHours = 12f;

        // 道路グラフ未構築時（State.Roads == null）の再試行間隔（ゲーム内時間）。NetManagerがまだ
        // 準備できていない等の失敗が続く間、毎tickフルビルドを試みてログを埋め尽くさないため
        // （Task23レビューImportant）。simスレッドのみが触る。
        private static float _roadBuildRetryAccum;
        private const float RoadBuildRetryIntervalHours = 0.25f;
        // セッション中まだ一度も構築を試みていない場合は初回のみ即座に試行する（上の間隔待ちをしない）。
        private static bool _hasAttemptedRoadBuild;

        // 遮蔽物マップ（State.Cover）の再構築間隔（ゲーム内時間）。RoadGraphと同じ理由
        // （プレイヤーが建物を建設/解体し続けるため定期的に作り直す、Task44）。simスレッドのみが触る。
        private static float _coverRebuildAccum;
        private const float CoverRebuildIntervalHours = 12f;

        // 遮蔽物マップ未構築時（State.Cover == null）の再試行間隔（ゲーム内時間）。RoadGraphの
        // _roadBuildRetryAccumと同じ間引きパターン（Task44）。simスレッドのみが触る。
        private static float _coverBuildRetryAccum;
        private const float CoverBuildRetryIntervalHours = 0.25f;
        private static bool _hasAttemptedCoverBuild;

        // 幽霊基地（建物が既に存在しない論理基地）掃除の間引き用（ゲーム内時間）。毎tickフルスキャンは
        // 無駄なため一定間隔でのみ実行する（Task24）。simスレッドのみが触る。
        private static float _baseReconcileAccum;
        private const float BaseReconcileIntervalHours = 6f;

        // ゲーム内時間ベースのdt計算用（Task21）。simスレッドのみが触る。
        private static DateTime _lastGameTime;
        private static bool _hasLastGameTime;
        private const float MaxHoursPerTick = 1f; // セーブロード直後等の大きな時計ジャンプに対するクランプ上限

        // OnMainVisualUpdate で使い回すスナップショット（GC回避）。メインスレッド専用アクセス。
        private static readonly List<UnitVisualState> _visualSnapshot = new List<UnitVisualState>();

        // Task60: 同上、軍事拠点（BaseVisuals）向けのスナップショット。メインスレッド専用アクセス。
        private static readonly List<BaseVisualState> _baseVisualSnapshot = new List<BaseVisualState>();

        // Task42: OnMainVisualUpdate で使い回す発砲イベントのスナップショット（GC回避）。
        // メインスレッド専用アクセス。State.RecentShotsの内容を_stateLock内でここへコピーしてから、
        // ロック解放後にCombatFx.Spawnへ渡す（UnitVisuals向けの_visualSnapshotと同じパターン）。
        private static readonly List<ShotEvent> _shotSnapshot = new List<ShotEvent>();

        // Task51: 同上、State.RecentKillsの内容を_stateLock内でここへコピーしてから、ロック解放後に
        // CombatFx.SpawnKillSoundsへ渡す（_shotSnapshotと全く同じパターン）。
        private static readonly List<KillEvent> _killSnapshot = new List<KillEvent>();

        // Task63: 同上、State.MissilesInFlight向けのスナップショット（MissileVisuals.Sync用）。
        private static readonly List<MissileVisualState> _missileSnapshot = new List<MissileVisualState>();

        // Task63: 同上、State.RecentImpactsの内容を_stateLock内でここへコピーしてから、ロック解放後に
        // MissileVisuals.HandleImpactsへ渡す（_shotSnapshot/_killSnapshotと全く同じパターン）。
        private static readonly List<MissileImpactEvent> _missileImpactSnapshot = new List<MissileImpactEvent>();

        // Task62: 同上、選択中ユニットの進撃/集結先（UI.OrderDestinationMarkers向け）。
        // UI.UnitBoxSelection.SelectedIds（Game層・main-thread専用の状態）を_stateLock内で参照するのは
        // OnMainVisualUpdate自体がメインスレッド専用のため問題ない（他のGame層main-thread状態と同じ扱い）。
        private static readonly List<UI.OrderDestinationState> _orderMarkerSnapshot = new List<UI.OrderDestinationState>();

        // save/loadスレッドとsimスレッド間の State への同時アクセスを防ぐ粗粒度ロック。
        // MVP規模（数十ユニット）では単一ロックで十分。
        private static readonly object _stateLock = new object();

        public static void EnsureInitialized()
        {
            if (State != null) return;
            // ローカルで完全に初期化してから最後に State へ代入する。
            // こうすることで OnSimTick の `if (State == null) return;` が
            // 初期化途中の半端な状態を観測することがない。
            var state = new WarState();
            LandUnitRoster.RegisterAll(state.Types); // 陸上7兵種×Tier1〜5（Task28）
            NavalUnitRoster.RegisterAll(state.Types); // 海上2種(Destroyer/Carrier)×Tier1〜5（Task61）
            AirUnitRoster.RegisterAll(state.Types);   // 航空3種(AirSuperiority/TacticalBomber/SuicideDrone)×Tier1〜5（Task61）

            // 全5勢力を生成する（電力タブの勢力ドロップダウンはどの選択も有効な値を指すようにするため）。
            // 基地はもうここではシードしない（Task18）：プレイヤーが電力タブから基地建物を配置した瞬間に
            // BasePlacementWatcher が論理基地を作成する。開始時の軍資金だけは、配置直後に生産を
            // 始められるようここで与えておく。
            string[] names = WarfrontSettings.FactionNames;
            for (byte i = 0; i < WarfrontSettings.MaxFactions; i++)
            {
                var f = new Faction(i, names[i]);
                f.AddTreasury(200f);
                state.Factions.Add(f);
            }

            // MVPの既定関係は全勢力ペアがHostile。実装はCore.RelationPresets.ApplyAllHostileに委譲する
            // （Task49: Options画面の「全て敵対に戻す」ボタンからも同じ実装を再利用するため）。
            // これは「新規State作成時」のみ適用される既定値であり、既存セーブをロードした場合は
            // シリアライズ済みの関係（WarState.Relations、format v4で25ペア全て永続化済み）がそのまま復元される。
            RelationPresets.ApplyAllHostile(state.Relations, WarfrontSettings.MaxFactions);

            State = state;
        }

        public static void ReplaceState(WarState s) { lock (_stateLock) { State = s; } }

        /// <summary>
        /// 基地情報パネル（Game/UI/BaseInfoPanel）から呼ばれる、基地の所属勢力変更（Task25）。
        /// メインスレッドから呼ばれる想定だが、simスレッド（OnSimTick）も同じ _stateLock を取るため
        /// 排他は保証される。HQ整合性は BasePlacementWatcher.ReassignHqIfCleared を共有利用する
        /// （解体経路と重複させないため）。
        /// </summary>
        /// <returns>baseId の基地または factionId の勢力が見つからない場合は false。</returns>
        public static bool TrySetBaseOwner(ushort baseId, byte factionId)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                MilitaryBase mb = null;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId == baseId) { mb = State.Bases[i]; break; }
                }
                if (mb == null) return false;

                Faction newFaction = State.FindFaction(factionId);
                if (newFaction == null) return false;

                byte? oldOwner = mb.OwnerFactionId;
                if (oldOwner.HasValue && oldOwner.Value == factionId) return true; // 変更なし

                bool wasHq = mb.IsHeadquarters;
                mb.OwnerFactionId = factionId;
                mb.IsHeadquarters = false;

                // 旧所有勢力のHQだった場合はクリアして、その勢力が他に持つ基地があれば昇格する。
                if (oldOwner.HasValue && wasHq)
                {
                    BasePlacementWatcher.ReassignHqIfCleared(State, oldOwner.Value, baseId);
                }

                // 新所有勢力がまだHQを持たない場合、この基地をHQにする。
                if (!newFaction.HomeBaseId.HasValue)
                {
                    newFaction.HomeBaseId = baseId;
                    mb.IsHeadquarters = true;
                }

                ModConfig.Log("MilitaryManager: base " + baseId + " owner changed " +
                    (oldOwner.HasValue ? oldOwner.Value.ToString() : "none") + " -> " + factionId +
                    (mb.IsHeadquarters ? " (new HQ)" : ""));
                return true;
            }
        }

        /// <summary>
        /// Task66: 指定勢力が指定種別の拠点を1つでも所有しているか（AssetAssignPanel/OptionsModelAssignPage
        /// が「基地種別ごとのモデル割り当て」を適用する際、割り当て対象の拠点が現時点で1つも無い場合に
        /// ユーザーへヒントを出すために使う。バグ調査で判明した通り、割り当て自体は正しく保存されていても、
        /// 対応する拠点が存在しなければ見た目には何も反映されないため、ユーザーには「反映されていない」
        /// ように見えてしまう——このメソッドはその状況を明示的に案内するためのものであり、割り当ての
        /// 保存/適用ロジック自体には一切影響しない）。
        /// </summary>
        public static bool HasOwnedBaseOfType(byte factionId, BaseType type)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.Type == type && b.OwnerFactionId.HasValue && b.OwnerFactionId.Value == factionId) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 基地情報パネル表示用の値をロック内でコピーして返す（UIが WarState へ直接触れないため、Task25）。
        /// </summary>
        public static bool TryGetBaseSnapshot(ushort baseId, out BaseUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(BaseUiSnapshot);
                if (State == null) return false;

                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase mb = State.Bases[i];
                    if (mb.BaseId != baseId) continue;

                    snapshot = BaseUiSnapshotBuilder.Build(mb, State);
                    return true;
                }
                return false;
            }
        }

        /// <summary>ユニット情報パネル表示用の値をロック内でコピーして返す（Task31。TryGetBaseSnapshotと
        /// 同じパターン）。死亡済みはまだ残っている可能性があるため見つからない扱いにする。</summary>
        public static bool TryGetUnitSnapshot(uint instanceId, out UnitUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(UnitUiSnapshot);
                if (State == null) return false;

                UnitInstance unit = State.FindUnit(instanceId);
                if (unit == null || unit.State == UnitState.Dead) return false;

                UnitType type = State.Types.Get(unit.TypeKey);
                snapshot = UnitUiSnapshotBuilder.Build(State, unit, type);
                return true;
            }
        }

        /// <summary>
        /// セーブ用：_stateLock を保持したまま WarState をシリアライズする。
        /// OnSimTick が State.Units 等を書き換えている最中の
        /// 「Collection was modified」例外（＝セーブ静かに失敗＝データ消失）を防ぐ。
        /// 呼び出し側（OnSaveData）は _stateLock を保持していないこと（再入不可のため）。
        /// </summary>
        public static byte[] SerializeLocked()
        {
            lock (_stateLock)
            {
                if (State == null) EnsureInitialized();
                // Task54: このMODが立てた戦闘域の道路封鎖(PathFailedビット)をセーブデータへ
                // 焼き込まないよう、シリアライズの前後で一時的に外して戻す（_stateLock保持中なので
                // simスレッドとは競合しない。CombatRoadBlocker.OwnedはRAM上の集合なのでこの間も
                // 覚えたまま＝戻すのは同じセグメント集合）。
                CombatRoadBlocker.UnblockAllForSave();
                try
                {
                    return CSWarfront.Core.WarStateSerializer.Serialize(State);
                }
                finally
                {
                    CombatRoadBlocker.ReblockAfterSave();
                }
            }
        }

        /// <summary>
        /// セーブデータからの復元専用エントリ（save/loadスレッドから呼ばれる）。State差し替えのみを
        /// 行う。生存ユニットの見た目（GameObject）は次回以降の OnMainVisualUpdate が
        /// State.Units をスナップショットして UnitVisuals.Sync に渡すことで自動的に再生成される
        /// （宣言的reconcileのため、respawn用の特別なフラグ・処理は不要＝Task19で削除）。
        /// </summary>
        public static void LoadAndRebuild(WarState restored)
        {
            lock (_stateLock)
            {
                State = restored;
                // セーブから復元＝基地建物はCSが既に復元済み（BaseIdはstable buildingId）。
                // BasePlacementWatcher.ProcessPending は復元済みBaseIdをIdempotencyチェックで
                // スキップするため、EventBuildingCreated の再発火があっても二重登録はしない。
            }
        }

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

                // プレイヤーが電力タブから配置/解体した軍事基地建物を論理基地(WarState.Bases)へ反映する
                // （Task18）。CS建物バッファの読み取りを伴うためsimスレッド専用。新規登録された基地は
                // この直後のProductionPlanningから同tickで生産対象になる。
                BasePlacementWatcher.ProcessPending(State);

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
                    ModConfig.LogError("MilitaryManager: CarrierAirWing.Advance 中に例外: " + e);
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

                // 移動（Moving状態のユニットをOrderTargetPosへキネマティック前進、CoverDestination優先はTask44）
                MovementStep.Advance(State, dt);

                // 戦闘（ユニット同士＋基地攻撃＋外部脅威、Task58）→ 占領 → 勢力状態の再導出（Task46:
                // 拠点の自衛射撃は廃止。Eliminated/HomeBaseIdはOccupationが直接いじらず、
                // FactionStatus.Refreshが毎tick所有基地の有無から導出し直す＝一度Eliminatedになっても
                // 基地を取り戻せば復活する）。ThreatCombatStepは通常の戦闘に「加えて」実行するだけで、
                // ターゲット選定を奪い合わない（射程内なら両方に同時に撃つ、Core/ThreatCombatStep参照）。
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
                        float inc = TerritoryIncome.ForBase(b, samples, IncomeRate);
                        State.FindFaction(b.OwnerFactionId.Value)?.AddTreasury(inc);
                        b.LastIncome = inc; // Task35: UIが基地パネルへ表示するためのキャッシュ（非永続化）
                    }
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
                var unitsPerFaction = new int[WarfrontSettings.MaxFactions];
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

        /// <summary>
        /// メインスレッド（ThreadingExtensionBase.OnUpdate経由）：ユニットの見た目（Unity GameObject）
        /// のみを同期する。CS実体（Vehicle/Building等）には一切触れない。
        /// _stateLock はスナップショットを構築する間だけ保持し、ロックを解放してから
        /// UnitVisuals.Sync（Unity API呼び出し）を行う（ロック保持中の重い/ブロッキング処理を避け、
        /// simスレッドを待たせないため）。
        /// </summary>
        public static void OnMainVisualUpdate()
        {
            if (State == null) return;

            _visualSnapshot.Clear();
            _baseVisualSnapshot.Clear();
            _shotSnapshot.Clear();
            _killSnapshot.Clear();
            _orderMarkerSnapshot.Clear();
            _missileSnapshot.Clear();
            _missileImpactSnapshot.Clear();
            lock (_stateLock)
            {
                for (int i = 0; i < State.Units.Count; i++)
                {
                    var u = State.Units[i];
                    if (u.State == UnitState.Dead) continue;
                    var type = State.Types.Get(u.TypeKey);
                    _visualSnapshot.Add(new UnitVisualState
                    {
                        InstanceId = u.InstanceId,
                        TypeKey = u.TypeKey,
                        FactionId = u.FactionId,
                        Position = new Vector3(u.Position.X, u.Position.Y, u.Position.Z),
                        AssetPrefabName = type != null ? type.AssetPrefabName : ""
                    });
                }

                // Task62: 選択中ユニット（UI.UnitBoxSelection.SelectedIds）の進撃/集結先を同じロック内で
                // 収集する（M&B風の目的地マーカー、UI.OrderDestinationMarkers向け）。Hold中・目的地未設定の
                // ユニットは対象外（マーカーを出さない＝仕様どおり「目的地が無い」ことを意味する）。
                var selectedIds = UI.UnitBoxSelection.SelectedIds;
                for (int i = 0; i < selectedIds.Count; i++)
                {
                    UnitInstance u = State.FindUnit(selectedIds[i]);
                    if (u == null || !u.IsAlive) continue;

                    if (u.Order == UnitOrder.RallyHold)
                    {
                        if (!u.RallyPoint.HasValue) continue;
                        WorldPos p = u.RallyPoint.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Rally
                        });
                    }
                    else if (u.Order != UnitOrder.Hold && u.OrderTargetPos.HasValue)
                    {
                        WorldPos p = u.OrderTargetPos.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Advance
                        });
                    }
                }

                // Task60: 軍事拠点も同じロック内でスナップショットを組み立てる。位置(WorldPos)は
                // Core（State.Bases、基地配置時にBasePlacementWatcherが一度だけ記録した不変値
                // ＝拠点は配置後に移動しないため再読込不要）から、向きはBasePlacementWatcher
                // ._baseAngles（simスレッドがCS建物バッファから既に読み取り済みのキャッシュ、
                // このロックと同じ_stateLockで書き込まれるため、ここで読むのは安全）から取る。
                // BuildingManagerバッファへメインスレッドから直接アクセスすることは一切無い
                // （CS実体はsimスレッド専用というルールを維持する）。
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.OwnerFactionId == null) continue; // 未所属の拠点は勢力別割り当ての対象外

                    float angle;
                    if (!BasePlacementWatcher.TryGetAngle(b.BaseId, out angle)) angle = 0f;

                    _baseVisualSnapshot.Add(new BaseVisualState
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        Position = new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
                        Angle = angle,
                        Type = b.Type // Task66: 基地種別ごとのモデル割り当てキーを解決するために必要
                    });
                }

                // Task42: 発砲エフェクトも同じロック内でコピーする（State.RecentShotsはsimスレッドが
                // 書き込むトランジェント・バッファのため、ロック外で読むとレースになる）。
                for (int i = 0; i < State.RecentShots.Count; i++)
                    _shotSnapshot.Add(State.RecentShots[i]);

                // Task51: 撃破イベントも同じロック内・同じ理由でコピーする。
                for (int i = 0; i < State.RecentKills.Count; i++)
                    _killSnapshot.Add(State.RecentKills[i]);

                // Task63: 飛翔中ミサイルと着弾/迎撃イベントも同じロック内でスナップショットを組み立てる
                // （State.MissilesInFlight/RecentImpactsはsimスレッドが書き込むため、ロック外で読むとレースになる）。
                for (int i = 0; i < State.MissilesInFlight.Count; i++)
                {
                    MissileInFlight m = State.MissilesInFlight[i];
                    _missileSnapshot.Add(new MissileVisualState
                    {
                        Id = m.Id,
                        FactionId = m.FactionId,
                        From = new Vector3(m.From.X, m.From.Y, m.From.Z),
                        To = new Vector3(m.To.X, m.To.Y, m.To.Z),
                        Progress = m.Progress
                    });
                }
                for (int i = 0; i < State.RecentImpacts.Count; i++)
                    _missileImpactSnapshot.Add(State.RecentImpacts[i]);
            }

            UnitVisuals.Sync(_visualSnapshot);
            BaseVisuals.Sync(_baseVisualSnapshot); // Task60: ロック解放後、Unity操作はここで行う
            UI.OrderDestinationMarkers.Sync(_orderMarkerSnapshot); // Task62: 同上
            MissileVisuals.Sync(_missileSnapshot); // Task63: 同上

            // Task42: Unity操作（GameObject生成/破棄/移動）はロック解放後に行う
            // （UnitVisuals.Syncと同じ規約：ロック保持中にUnity APIを呼ぶとsimスレッドを長時間ブロックしうる）。
            CombatFx.Spawn(_shotSnapshot);
            CombatFx.SpawnKillSounds(_killSnapshot); // Task51: 撃破音（視覚エフェクトは無し）
            KillFx.Spawn(_killSnapshot); // Task65: 撃破爆発エフェクト（音とは別のエフェクト専用クラス、同じカテゴリ判定を共有）
            CombatFx.Update(Time.deltaTime);
            KillFx.Update(Time.deltaTime);

            // Task63: 着弾/迎撃の演出（フラッシュ/爆発+音）と、生存中の演出の実時間更新。
            MissileVisuals.HandleImpacts(_missileImpactSnapshot);
            MissileVisuals.UpdateFx(Time.deltaTime);
        }

        /// <summary>
        /// レベルアンロード時（メインメニューへ戻る等）に全セッション状態を初期化する（Task16レビューImportant）。
        /// これが無いと、セーブから復元したセッション由来のセッション状態が同一プロセス内の
        /// 次のゲーム開始へ持ち越されてしまう。呼び出し元（WarfrontLoadingExtension.OnLevelUnloading）は
        /// OnSimTickの外側（CSのロードライフサイクル）で呼ばれるため、_stateLock の再入は発生しない。
        /// BasePlacementWatcher.Unsubscribe は呼び出し元がこのReset()より先に行う想定（イベント購読解除は
        /// レベルライフサイクルに紐づくため、ここでは pending リストのクリアのみ行う）。
        /// </summary>
        public static void Reset()
        {
            // UnitVisuals.DestroyAll はUnity GameObjectを破棄するためメインスレッド専用API。
            // Reset()自体はCSのロードライフサイクル（メインスレッド、OnLevelUnloading経由）から
            // 呼ばれるためここで直接呼んで問題ない（_stateLockはCS実体を持たないState差し替えのみ保護）。
            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: 基地の勢力別オーバーレイもレベルアンロード時に破棄する。
            CombatFx.DestroyAll(); // Task42: 発砲エフェクトもレベルアンロード時に破棄する。
            KillFx.DestroyAll(); // Task65: 撃破爆発エフェクトもレベルアンロード時に破棄する。
            MissileVisuals.DestroyAll(); // Task63: 飛翔中ミサイルの見た目もレベルアンロード時に破棄する。
            MissileVisuals.DestroyAllFx(); // Task63: 着弾/迎撃の演出も同様。
            UI.OrderDestinationMarkers.DestroyAll(); // Task62: 目的地マーカーもレベルアンロード時に破棄する。
            UI.CommandToast.Destroy(); // Task62: トーストラベルもレベルアンロード時に破棄する。
            // Task54: このMODが封鎖した道路(PathFailedビット)を解除する。Reset()自体はOnLevelUnloading
            // （レベル遷移中、simスレッドは既に停止している想定）から呼ばれるため他スレッドとの競合は
            // 想定していない。CombatRoadBlocker.Reset内部は例外を外へ伝播しないガード付き
            // （レベルティアダウン中でNetManagerが無効化されているケースがあり得るため、失敗しても
            // 実害なしとして許容する＝要件通り）。
            // Task56レビュー: このコメントは元々あったが実際の呼び出しが抜けており、レベルアンロードでも
            // PathFailedビットが解除されないまま次セッションへ持ち越されうる欠落だったため追加した。
            CombatRoadBlocker.Reset();
            CombatCollateral.Reset(); // Task65: 抽選間引き用の内部状態もレベルアンロード時にクリアする。

            lock (_stateLock)
            {
                State = null;
                _economyAccum = 0f;
                _roadRebuildAccum = 0f;
                _roadBuildRetryAccum = 0f;
                _hasAttemptedRoadBuild = false;
                _coverRebuildAccum = 0f;
                _coverBuildRetryAccum = 0f;
                _hasAttemptedCoverBuild = false;
                _baseReconcileAccum = 0f;
                _hasLastGameTime = false;
                _lastGameTime = default(DateTime);
                BasePlacementWatcher.ClearPending();
            }

            // 較正診断の積算（Task26）：MilitaryManagerとは別の専用ロックで保護されているため個別にクリアする。
            SpeedCalibrationDiagnostics.Reset();

            // *Panel.Destroy はUnity GameObjectを破棄するためメインスレッド専用API。Reset()自体が
            // CSのロードライフサイクルから呼ばれる点は上の UnitVisuals.DestroyAll と同じ前提のため、
            // ここで直接呼んで問題ない。UnitSelection.Clear は次セッションへ選択IDを持ち越さないため（Task31）。
            UI.BaseInfoPanel.Destroy();
            UI.UnitInfoPanel.Destroy();
            UI.AssetAssignPanel.Destroy(); // Task36
            UI.UnitSelection.Clear();
            UI.UnitBoxSelection.Destroy(); // Task48: 範囲選択の矩形/ハイライトGameObjectと選択状態
            UI.UnitCommandInput.Reset(); // Task48: 集結地点のターゲティング状態を持ち越さない
            UI.MissileLaunchTargeting.Reset(); // Task63: ミサイル発射地点のターゲティング状態を持ち越さない
            UI.PanelChrome.ResetCache(); // Task56: キャッシュ済みPauseMenu/UIView参照を次セッションへ持ち越さない
        }
    }
}
