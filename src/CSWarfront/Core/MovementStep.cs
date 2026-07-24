namespace CSWarfront.Core
{
    /// <summary>Moving状態のユニットをOrderTargetPosへ速度分だけ水平前進させる（キネマティック・純ロジック）。Yは維持。</summary>
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
                WorldPos target = u.OrderTargetPos.Value;
                float dist = u.Position.HorizontalDistanceTo(target);
                float stepLen = type.Speed * dt;
                if (dist <= stepLen || dist <= 0.01f)
                    u.Position = new WorldPos(target.X, u.Position.Y, target.Z);       // 到達
                else
                {
                    float t = stepLen / dist;
                    float nx = u.Position.X + (target.X - u.Position.X) * t;
                    float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
                    u.Position = new WorldPos(nx, u.Position.Y, nz);
                }
            }
        }
    }
}
