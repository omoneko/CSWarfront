namespace CSWarfront.Core
{
    /// <summary>Moving状態のユニットを前進させる（キネマティック・純ロジック）。Yは維持。
    /// Path（道路経路）があればウェイポイントを順に消化し、尽きたらOrderTargetPosへの直線移動にフォールバックする。</summary>
    public static class MovementStep
    {
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.State != UnitState.Moving || !u.OrderTargetPos.HasValue) continue;
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                float stepLen = type.Speed * dt;
                if (stepLen <= 0f) continue;

                stepLen = ConsumePath(u, stepLen);
                if (stepLen > 0f)
                    AdvanceStraight(u, u.OrderTargetPos.Value, stepLen);
            }
        }

        /// <summary>Pathが残っていればウェイポイントを順に消化する。残ったstepLen（直線フォールバック用）を返す。</summary>
        private static float ConsumePath(UnitInstance u, float stepLen)
        {
            if (u.Path == null) return stepLen;

            while (stepLen > 0f && u.PathIndex < u.Path.Count)
            {
                WorldPos waypoint = u.Path[u.PathIndex];
                float dist = u.Position.HorizontalDistanceTo(waypoint);

                if (dist <= stepLen || dist <= 0.01f)
                {
                    u.Position = new WorldPos(waypoint.X, u.Position.Y, waypoint.Z);
                    stepLen -= dist;
                    u.PathIndex++;
                }
                else
                {
                    MoveToward(u, waypoint, stepLen);
                    return 0f;
                }
            }

            return stepLen;
        }

        private static void AdvanceStraight(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= stepLen || dist <= 0.01f)
                u.Position = new WorldPos(target.X, u.Position.Y, target.Z); // 到達
            else
                MoveToward(u, target, stepLen);
        }

        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            u.Position = new WorldPos(nx, u.Position.Y, nz);
        }
    }
}
