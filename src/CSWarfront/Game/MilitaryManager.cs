using System.Collections.Generic;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。</summary>
    public static class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        private const float EconomyIntervalSeconds = 5f;   // 経済tick間隔
        private const float IncomeRate = 0.01f;

        public static void EnsureInitialized()
        {
            if (State != null) return;
            State = new WarState();
            State.Types.Register(MvpUnitTypes.Tank_T1());
            // 勢力2つ（MVP）。基地/所有はTask13のシナリオ配線で設定。
            State.Factions.Add(new Faction(0, "Red"));
            State.Factions.Add(new Faction(1, "Blue"));
            State.Relations.Set(0, 1, Relation.Hostile);
        }

        public static void ReplaceState(WarState s) { State = s; }

        /// <summary>simスレッド：判断ロジックを回す。</summary>
        public static void OnSimTick()
        {
            if (State == null) return;
            float dt = 1f; // 1 sim tick ≈ 固定dt（MVP簡略）

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

        /// <summary>メインスレッド：スポーン要求消化・移動・撃破表現の撤去。</summary>
        public static void OnMainUpdate(float dt)
        {
            if (State == null) return;
            while (SpawnQueue.Count > 0)
            {
                var c = SpawnQueue.Dequeue();
                uint id = State.AllocInstanceId();
                var type = State.Types.Get(c.TypeKey);
                State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                LandUnitSpawner.Spawn(id, c);
            }
            LandUnitSpawner.UpdateMovementAndCleanup(State);
        }

        internal static readonly Queue<CompletedUnit> SpawnQueue = new Queue<CompletedUnit>();
    }
}
