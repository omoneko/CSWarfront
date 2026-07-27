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
    public static class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        // 診断ログの間引き用（simスレッドのみが触る）。
        private static int _diagTicks;
        private const int DiagIntervalTicks = 300;
        private const float EconomyIntervalHours = 6f;      // 経済tick間隔（ゲーム内時間、1日4回）
        private const float IncomeRate = 0.01f;

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

            // MVPの既定関係は全勢力ペアがHostile（設定可能な関係UIは将来対応）。
            for (byte i = 0; i < WarfrontSettings.MaxFactions; i++)
                for (byte j = (byte)(i + 1); j < WarfrontSettings.MaxFactions; j++)
                    state.Relations.Set(i, j, Relation.Hostile);

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

                    snapshot = new BaseUiSnapshot
                    {
                        OwnerFactionId = mb.OwnerFactionId,
                        CurrentHP = mb.CurrentHP,
                        MaxHP = mb.MaxHP,
                        CaptureGraceHours = mb.CaptureGraceHours,
                        QueueCount = mb.Queue.Count,
                        IsHeadquarters = mb.IsHeadquarters
                    };
                    return true;
                }
                return false;
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
                return CSWarfront.Core.WarStateSerializer.Serialize(State);
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

                // AI進軍命令（非プレイヤー勢力）
                foreach (var f in State.Factions)
                    if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id, dt);

                // 移動（Moving状態のユニットをOrderTargetPosへキネマティック前進）
                MovementStep.Advance(State, dt);

                // 戦闘（ユニット同士＋基地攻撃）→ 占領
                CombatStep.Advance(State, dt);
                BaseCombatStep.Advance(State, dt);
                Occupation.ResolveCaptures(State);

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
            }

            UnitVisuals.Sync(_visualSnapshot);
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
            lock (_stateLock)
            {
                State = null;
                _economyAccum = 0f;
                _roadRebuildAccum = 0f;
                _roadBuildRetryAccum = 0f;
                _hasAttemptedRoadBuild = false;
                _baseReconcileAccum = 0f;
                _hasLastGameTime = false;
                _lastGameTime = default(DateTime);
                BasePlacementWatcher.ClearPending();
            }

            // 較正診断の積算（Task26）：MilitaryManagerとは別の専用ロックで保護されているため個別にクリアする。
            SpeedCalibrationDiagnostics.Reset();

            // BaseInfoPanel.Destroy はUnity GameObjectを破棄するためメインスレッド専用API。
            // Reset()自体がCSのロードライフサイクル（メインスレッド、OnLevelUnloading経由）から
            // 呼ばれる点は上の UnitVisuals.DestroyAll と同じ前提のため、ここで直接呼んで問題ない。
            UI.BaseInfoPanel.Destroy();
        }
    }

    /// <summary>
    /// 基地情報パネル（Game/UI/BaseInfoPanel）向けの読み取り専用スナップショット（Task25）。
    /// UIが WarState / MilitaryBase へ直接触れずに済むよう、_stateLock 内で値をコピーして渡す。
    /// </summary>
    public struct BaseUiSnapshot
    {
        public byte? OwnerFactionId;
        public float CurrentHP;
        public float MaxHP;
        public float CaptureGraceHours;
        public int QueueCount;
        public bool IsHeadquarters;
    }
}
