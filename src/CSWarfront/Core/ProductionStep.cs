using System.Collections.Generic;
namespace CSWarfront.Core
{
    public struct CompletedUnit
    {
        public ushort BaseId;
        public byte FactionId;
        public string TypeKey;
        public WorldPos SpawnPos;
    }

    /// <summary>生産tick（純ロジック）。完成分を CompletedUnit として返す（スポーンはGame層）。</summary>
    public static class ProductionStep
    {
        public static List<CompletedUnit> Advance(WarState state, float dt)
        {
            var completed = new List<CompletedUnit>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                var b = state.Bases[i];
                if (b.OwnerFactionId == null || b.Queue.Count == 0) continue;
                var order = b.Queue[0];
                if (order.BuildTime <= 0f) order.Progress = 1f;
                else order.Progress += dt / order.BuildTime;
                if (order.Progress >= 1f)
                {
                    completed.Add(new CompletedUnit
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        TypeKey = order.TypeKey,
                        SpawnPos = b.Position
                    });
                    b.Queue.RemoveAt(0);
                }
            }
            return completed;
        }
    }
}
