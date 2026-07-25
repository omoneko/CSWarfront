using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。
    /// スレッド境界（重要）:
    ///  - CSの実体（車両/建物）操作（CreateVehicle/CreateBuilding/ReleaseVehicle、
    ///    m_vehicles.m_buffer[]への書込等）は必ず sim スレッドで行う。これらの配列・空きスロット
    ///    割当・空間グリッドは sim スレッドが所有し、CSの内部シミュレーションも sim スレッドで
    ///    駆動される。メインスレッドから同時に触るとバッファが破壊され、CS自身のsimコードが
    ///    IndexOutOfRangeException（捕捉されないポップアップ）を投げる。
    ///  - そのためOnSimTick（sim スレッド、ThreadingExtensionBase.OnAfterSimulationTick経由）を
    ///    唯一のCS実体操作の場所とし、判断ロジック（Core）とCS API呼び出しを同一tick・同一ロックで
    ///    直列に実行する。メインスレッド（OnUpdate）はCS実体には一切触れない。
    ///  - 注意: ゲームが一時停止中は OnAfterSimulationTick が発火しないため、基地配置やスポーンは
    ///    停止解除まで待機する（MVPとして許容）。
    ///  - _stateLock は、save/loadスレッド（SerializeLocked/LoadAndRebuild）とsimスレッド(OnSimTick)
    ///    の State への同時アクセスを防ぐために引き続き必要（net35のため
    ///    System.Collections.Concurrent は使用不可）。
    /// </summary>
    public static class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        private const float EconomyIntervalSeconds = 5f;   // 経済tick間隔
        private const float IncomeRate = 0.01f;

        // セーブロード直後、生存ユニットの表現（車両）をsimスレッドで再生成する必要があることを示す
        // フラグ（load/saveスレッドからCS車両APIを呼ばないため、実処理はOnSimTickへ委譲する）。
        private static bool _respawnLoadedUnits;

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
            state.Types.Register(MvpUnitTypes.Tank_T1());

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
        /// 行い、生存ユニットの表現（車両）再生成はここでは行わない（LandUnitSpawner.Spawn＝CS車両API
        /// はsimスレッド専用のため）。代わりに _respawnLoadedUnits フラグを立て、次回以降の
        /// OnSimTick で表現を再生成させる。
        /// </summary>
        public static void LoadAndRebuild(WarState restored)
        {
            lock (_stateLock)
            {
                State = restored;
                // セーブから復元＝基地建物はCSが既に復元済み（BaseIdはstable buildingId）。
                // BasePlacementWatcher.ProcessPending は復元済みBaseIdをIdempotencyチェックで
                // スキップするため、EventBuildingCreated の再発火があっても二重登録はしない。
                _respawnLoadedUnits = true;
            }
        }

        /// <summary>
        /// simスレッド（ThreadingExtensionBase.OnAfterSimulationTick経由）：判断ロジック（Core）と
        /// CS実体操作（車両/建物の生成・バッファ書込）をこの一箇所に集約する。
        /// 理由: VehicleManager/BuildingManagerのバッファ・空きスロット割当・空間グリッドは
        /// simスレッドが所有しているため、これらへの書込（CreateVehicle/CreateBuilding/
        /// ReleaseVehicle/m_vehicles.m_buffer[]直接書込）を他スレッド（メイン等）から行うと
        /// simスレッドと競合してバッファが破壊され、CS自身のシミュレーションコードが
        /// IndexOutOfRangeException（捕捉されないポップアップ）を投げる。
        /// 注意: ゲームが一時停止中はOnAfterSimulationTickが発火しないため、基地配置・スポーンは
        /// 停止解除まで待機する（MVPとして許容）。
        /// </summary>
        public static void OnSimTick()
        {
            EnsureInitialized();
            if (State == null) return;
            float dt = 1f; // 1 sim tick ≈ 固定dt（MVP簡略）

            lock (_stateLock)
            {
                // セーブロード直後：生存ユニットのうち表現（車両）を持たないものを再生成する。
                // CreateVehicleはsimスレッド専用APIのため、load完了を示すこのフラグをここで消化する。
                if (_respawnLoadedUnits)
                {
                    foreach (var u in State.Units)
                    {
                        if (u.State == UnitState.Dead) continue;
                        if (LandUnitSpawner.HasRepresentation(u.InstanceId)) continue;
                        LandUnitSpawner.Spawn(u.InstanceId, new CompletedUnit
                        {
                            BaseId = 0,
                            FactionId = u.FactionId,
                            TypeKey = u.TypeKey,
                            SpawnPos = u.Position
                        });
                    }
                    _respawnLoadedUnits = false;
                }

                // プレイヤーが電力タブから配置/解体した軍事基地建物を論理基地(WarState.Bases)へ反映する
                // （Task18）。CS建物バッファの読み取りを伴うためsimスレッド専用。新規登録された基地は
                // この直後のProductionPlanningから同tickで生産対象になる。
                BasePlacementWatcher.ProcessPending(State);

                // 生産計画（軍資金消費でキュー補充）→ 生産 → 完成分を直接スポーン（simスレッドのため
                // キューを介さずその場でCreateVehicleしてよい）。
                ProductionPlanning.Advance(State);
                var completed = ProductionStep.Advance(State, dt);
                foreach (var c in completed)
                {
                    uint id = State.AllocInstanceId();
                    var type = State.Types.Get(c.TypeKey);
                    State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                    LandUnitSpawner.Spawn(id, c);
                }

                // AI進軍命令（非プレイヤー勢力）
                foreach (var f in State.Factions)
                    if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id);

                // 移動（Moving状態のユニットをOrderTargetPosへキネマティック前進）
                MovementStep.Advance(State, dt);

                // 戦闘（ユニット同士＋基地攻撃）→ 占領
                CombatStep.Advance(State, dt);
                BaseCombatStep.Advance(State, dt);
                Occupation.ResolveCaptures(State);

                // 経済（低頻度）
                _economyAccum += dt;
                if (_economyAccum >= EconomyIntervalSeconds)
                {
                    _economyAccum = 0f;
                    var samples = DevelopmentSampler.Sample(); // Task 12
                    foreach (var b in State.Bases)
                    {
                        if (b.OwnerFactionId == null) continue;
                        float inc = TerritoryIncome.ForBase(b, samples, IncomeRate);
                        State.FindFaction(b.OwnerFactionId.Value)?.AddTreasury(inc);
                    }
                }

                // 移動更新・撃破表現の撤去（車両バッファ書込・ReleaseVehicleはsimスレッド専用API）。
                LandUnitSpawner.UpdateMovementAndCleanup(State);

                // 死亡ユニットの掃除（表現撤去済みのもののみ）
                State.Units.RemoveAll(u => u.State == UnitState.Dead && !LandUnitSpawner.HasRepresentation(u.InstanceId));
            }
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
            lock (_stateLock)
            {
                State = null;
                _respawnLoadedUnits = false;
                _economyAccum = 0f;
                LandUnitSpawner.ResetAll();
                BasePlacementWatcher.ClearPending();
            }
        }
    }
}
