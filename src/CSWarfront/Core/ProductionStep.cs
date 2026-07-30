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
        /// <summary>Task78:「海上ユニットが敵拠点へ移動せず自拠点にこもったまま」不具合の一因の対策。
        /// 海軍基地(BaseType.Navy)の建物位置は水上とは限らない（岸に建てられる）ため、これをそのまま
        /// 艦艇のSpawnPosにすると、MovementStep.AdvanceSeaの最初の一歩から着地点が水域外と判定され、
        /// 生まれた瞬間から永遠に動けなくなる（岸に立ち往生する不具合の直接原因）。これを避けるため、
        /// 基地位置を中心に同心円状（半径NavySpawnSearchStepずつ拡大、各半径でNavySpawnSearchRaysの
        /// 方向を均等に走査）で最も近い水域点を探し、見つかればそこをSpawnPosにする。既に水上、または
        /// NavySpawnSearchMaxRadius以内に水域が見つからなければ、従来通り基地位置そのままを返す
        /// （ベストエフォート・決定的・乱数不使用）。</summary>
        private const float NavySpawnSearchStep = 20f;
        private const float NavySpawnSearchMaxRadius = 200f;
        private const int NavySpawnSearchRays = 16;

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
                        SpawnPos = ResolveSpawnPos(b, state.Water)
                    });
                    b.Queue.RemoveAt(0);
                }
            }
            return completed;
        }

        private static WorldPos ResolveSpawnPos(MilitaryBase b, IWaterSampler water)
        {
            if (b.Type != BaseType.Navy || water == null || water.IsWater(b.Position.X, b.Position.Z))
                return b.Position;

            for (float r = NavySpawnSearchStep; r <= NavySpawnSearchMaxRadius; r += NavySpawnSearchStep)
            {
                for (int a = 0; a < NavySpawnSearchRays; a++)
                {
                    double rad = a * (360.0 / NavySpawnSearchRays) * System.Math.PI / 180.0;
                    float x = b.Position.X + r * (float)System.Math.Cos(rad);
                    float z = b.Position.Z + r * (float)System.Math.Sin(rad);
                    if (water.IsWater(x, z)) return new WorldPos(x, b.Position.Y, z);
                }
            }
            return b.Position; // 探索半径内に水域が見つからなかった: 従来通りベストエフォートで基地位置を使う。
        }
    }
}
