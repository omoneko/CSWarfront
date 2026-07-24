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
            State = state;
        }

        public static void ReplaceState(WarState s) { State = s; }

        /// <summary>simスレッド：判断ロジックを回す。</summary>
        public static void OnSimTick()
        {
            if (State == null) return;
            float dt = 1f; // 1 sim tick ≈ 固定dt（MVP簡略）

            lock (_stateLock)
            {
                // 生産 → 完成分をスポーン要求へ
                var completed = ProductionStep.Advance(State, dt);
                foreach (var c in completed) SpawnQueue.Enqueue(c);

                // AI進軍命令（非プレイヤー勢力）
                foreach (var f in State.Factions)
                    if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id);

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

        internal static readonly Queue<CompletedUnit> SpawnQueue = new Queue<CompletedUnit>();
    }
}
