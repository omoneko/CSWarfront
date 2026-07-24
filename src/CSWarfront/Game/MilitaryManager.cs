using System.Collections.Generic;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。
    /// スレッド境界（重要）:
    ///  - OnSimTick は sim スレッドから呼ばれ、State.Units/State.Bases/factionを読み書きし、
    ///    SpawnQueue へ Enqueue する。
    ///  - OnMainUpdate は メインスレッドから呼ばれ、SpawnQueue を Dequeue して State.Units へ Add し、
    ///    LandUnitSpawner（CSの車両APIはメインスレッド専用）を呼ぶ。
    ///  Queue&lt;T&gt;/List&lt;T&gt; はスレッドセーフではないため、両コールバックの状態接触区間を
    ///  _stateLock で直列化する（net35のため System.Collections.Concurrent は使用不可）。
    /// </summary>
    public static class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        private const float EconomyIntervalSeconds = 5f;   // 経済tick間隔
        private const float IncomeRate = 0.01f;

        /// <summary>trueならセーブから復元済み＝実建物は既にCSが復元済みのため新規配置しない（Task16）。</summary>
        public static bool LoadedFromSave = false;
        // 新規ゲームでの基地実建物配置が完了したか（全基地成功で true。以後このセッションでは再実行しない）。
        private static bool _baseBuildingsPlaced;
        // State.Bases のインデックスごとの配置済みフラグ。BuildingManager未準備等での失敗時、
        // 次フレームで未配置分のみ再試行するために保持する。
        private static bool[] _basePlacementDone;

        // sim/mainスレッド間で SpawnQueue と State(Units/Bases/faction) の同時アクセスを防ぐ粗粒度ロック。
        // MVP規模（数十ユニット）では単一ロックで十分。Task12でCS API呼び出し（LandUnitSpawner.Spawn等）が
        // 重くなり競合が問題になる場合は、ロック内では値の受け渡しのみに留め、重い処理をロック外に出すこと。
        private static readonly object _stateLock = new object();

        public static void EnsureInitialized()
        {
            if (State != null) return;
            // ローカルで完全に初期化してから最後に State へ代入する。
            // こうすることで OnSimTick の `if (State == null) return;` が
            // 初期化途中の半端な状態を観測することがない。
            var state = new WarState();
            state.Types.Register(MvpUnitTypes.Tank_T1());
            // 勢力2つ（MVP）。基地/所有はTask13のシナリオ配線で設定。
            state.Factions.Add(new Faction(0, "Red"));
            state.Factions.Add(new Faction(1, "Blue"));
            state.Relations.Set(0, 1, Relation.Hostile);
            BaseBuildingBinder.SeedTwoBaseScenario(state);
            State = state;
        }

        public static void ReplaceState(WarState s) { lock (_stateLock) { State = s; } }

        /// <summary>
        /// セーブ用：_stateLock を保持したまま WarState をシリアライズする。
        /// OnMainUpdate/OnSimTick が State.Units 等を書き換えている最中の
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
        /// セーブデータからの復元専用エントリ。State差し替えと、生存ユニットの表現（車両）再生成を
        /// 同一ロック内で行う。これにより ReplaceState 直後に別スレッド（sim/main tick）が
        /// 「State はあるが表現がまだ無い」中間状態を観測することを防ぐ。
        /// 表現再生成（LandUnitSpawner.Spawn＝CS車両API呼び出し）はMVP規模の数十体想定であり、
        /// OnMainUpdate 同様ロック内実行を許容する（重くなる場合は値の受け渡しのみロック内に残し、
        /// 実処理をロック外に出すこと）。
        /// </summary>
        public static void LoadAndRebuild(WarState restored)
        {
            lock (_stateLock)
            {
                State = restored;
                // セーブから復元＝実建物はCSが既に復元済み（BaseIdはstable buildingId）。新規配置は行わない。
                LoadedFromSave = true;
                _baseBuildingsPlaced = true;
                foreach (var u in restored.Units)
                {
                    if (u.State == UnitState.Dead) continue;
                    LandUnitSpawner.Spawn(u.InstanceId, new CompletedUnit
                    {
                        BaseId = 0,
                        FactionId = u.FactionId,
                        TypeKey = u.TypeKey,
                        SpawnPos = u.Position
                    });
                }
            }
        }

        /// <summary>simスレッド：判断ロジックを回す。</summary>
        public static void OnSimTick()
        {
            if (State == null) return;
            float dt = 1f; // 1 sim tick ≈ 固定dt（MVP簡略）

            lock (_stateLock)
            {
                // 生産計画（軍資金消費でキュー補充）→ 生産 → 完成分をスポーン要求へ
                ProductionPlanning.Advance(State);
                var completed = ProductionStep.Advance(State, dt);
                foreach (var c in completed) SpawnQueue.Enqueue(c);

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

                // 死亡ユニットの掃除（表現撤去はメインスレッドで）
                State.Units.RemoveAll(u => u.State == UnitState.Dead && !LandUnitSpawner.HasRepresentation(u.InstanceId));
            }
        }

        /// <summary>メインスレッド：スポーン要求消化・移動・撃破表現の撤去。</summary>
        public static void OnMainUpdate(float dt)
        {
            if (State == null) return;
            lock (_stateLock)
            {
                // 新規ゲームの初回のみ：論理基地の座標へ実建物を配置し、BaseId/HomeBaseIdを
                // 実建物IDへ紐付ける（可視化 Task16）。BuildingManager未準備時は失敗するため、
                // 全基地成功するまで毎フレーム再試行する。sim/main両方が同一ロックで直列化される
                // ため、紐付け中に BaseCombatStep/Occupation（simスレッド）が中間状態を読むことはない。
                if (!LoadedFromSave && !_baseBuildingsPlaced) PlaceBaseBuildingsOnce();

                // まずキューをローカルへ排出（sim側のEnqueueと競合しないようロック内で完結させる）。
                List<CompletedUnit> toSpawn = null;
                if (SpawnQueue.Count > 0)
                {
                    toSpawn = new List<CompletedUnit>(SpawnQueue.Count);
                    while (SpawnQueue.Count > 0) toSpawn.Add(SpawnQueue.Dequeue());
                }

                if (toSpawn != null)
                {
                    foreach (var c in toSpawn)
                    {
                        uint id = State.AllocInstanceId();
                        var type = State.Types.Get(c.TypeKey);
                        State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                        // Task12注記: LandUnitSpawner.Spawn は現状no-opスタブのためロック内で許容。
                        // 実装後にCS API呼び出しが重くなり競合するようなら、値だけロック内で受け渡し、
                        // 実際のスポーン処理はロック外に出すこと。
                        LandUnitSpawner.Spawn(id, c);
                    }
                }

                // 移動更新・撃破表現の撤去（State.Units を読むためロック内で実行）。
                LandUnitSpawner.UpdateMovementAndCleanup(State);
            }
        }

        /// <summary>
        /// 新規ゲームの初回のみ実行：各 MilitaryBase の論理座標へ実建物を配置し、
        /// 成功した基地について BaseId を実建物IDへ差し替え、旧IDを参照していた
        /// Faction.HomeBaseId も新IDへ更新する（呼び出し元が _stateLock 保持済み）。
        /// 一部の基地でBuildingManager等が未準備なら _baseBuildingsPlaced を立てず、
        /// 未配置分だけ次フレームで再試行する。
        /// </summary>
        private static void PlaceBaseBuildingsOnce()
        {
            if (_basePlacementDone == null || _basePlacementDone.Length != State.Bases.Count)
                _basePlacementDone = new bool[State.Bases.Count];

            bool allDone = true;
            for (int i = 0; i < State.Bases.Count; i++)
            {
                if (_basePlacementDone[i]) continue;

                MilitaryBase b = State.Bases[i];
                ushort newId;
                if (BaseBuildingPlacer.TryPlace(b.Position, out newId))
                {
                    ushort oldId = b.BaseId;
                    b.BaseId = newId;

                    foreach (var f in State.Factions)
                        if (f.HomeBaseId.HasValue && f.HomeBaseId.Value == oldId) f.HomeBaseId = newId;

                    _basePlacementDone[i] = true;
                }
                else
                {
                    allDone = false; // 未準備（BuildingManager等）。次フレームで再試行。
                }
            }

            if (allDone) _baseBuildingsPlaced = true;
        }

        internal static readonly Queue<CompletedUnit> SpawnQueue = new Queue<CompletedUnit>();
    }
}
