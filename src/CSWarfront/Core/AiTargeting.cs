namespace CSWarfront.Core
{
    public static class AiTargeting
    {
        public static MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from)
        {
            MilitaryBase best = null; float bestDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (state.Relations.Get(factionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                float d = from.HorizontalDistanceTo(b.Position);
                if (d < bestDist) { bestDist = d; best = b; }
            }
            return best;
        }
    }

    public static class InvasionOrders
    {
        /// <summary>道路スナップ判定に使う最大距離（水平）。これより道路から離れているユニット/基地は直線移動にフォールバック。</summary>
        public const float PathSnapRadius = 200f;

        /// <summary>目的地が変わったとみなす閾値（X/Z）。これ未満の差は同一目的地として扱い経路を再利用する。</summary>
        private const float TargetChangeEpsilon = 1f;

        /// <summary>当該勢力の非交戦ユニットに、各自位置から最寄りの敵基地へ進軍命令を与える。
        /// state.Roadsが供給されていれば道路経路(A*)も計算する（1回の呼び出しでmaxPathComputations件まで）。</summary>
        public static void AssignAdvance(WarState state, byte factionId, int maxPathComputations = 4)
        {
            int pathComputations = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.State == UnitState.Engaging) continue;
                var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position);
                if (target == null) continue;

                u.OrderTargetPos = target.Position;
                u.State = UnitState.Moving;

                if (u.PathTarget.HasValue && !IsSameTarget(u.PathTarget.Value, u.OrderTargetPos.Value))
                    u.ClearPath();

                if (state.Roads != null && u.Path == null)
                {
                    if (pathComputations >= maxPathComputations) continue; // 予算超過。次回に持ち越し

                    pathComputations++;
                    var path = state.Roads.FindPath(u.Position, u.OrderTargetPos.Value, PathSnapRadius);
                    u.Path = path;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                }
            }
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b)
        {
            return System.Math.Abs(a.X - b.X) < TargetChangeEpsilon && System.Math.Abs(a.Z - b.Z) < TargetChangeEpsilon;
        }
    }
}
