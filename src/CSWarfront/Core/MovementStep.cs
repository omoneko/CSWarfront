namespace CSWarfront.Core
{
    /// <summary>Moving状態のユニットを前進させる（キネマティック・純ロジック）。
    /// Task37: Yはもはや維持しない（旧仕様）。X/Zと同じ補間係数でウェイポイント/目標のYへ向けて
    /// 補間することで、道路の勾配（橋・坂）に沿って高さが変化するようにする（路面から浮くバグの修正）。
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
                    // Task37: ウェイポイントに到達したらそのウェイポイントのYをそのまま採用する
                    // （旧: u.Position.Yを維持 → 路面から浮く原因だった）。
                    u.Position = new WorldPos(waypoint.X, waypoint.Y, waypoint.Z);
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
                u.Position = new WorldPos(target.X, target.Y, target.Z); // 到達: targetのYをそのまま採用（Task37、旧:u.Position.Y維持）
            else
                MoveToward(u, target, stepLen);
        }

        /// <summary>X/Zと同じ補間係数(t = stepLen/dist)でYも目標へ向けて補間する（Task37）。
        /// 旧仕様は常に u.Position.Y を維持していたため、道路の勾配（橋・坂）を無視して水平飛行して
        /// しまい「路面から浮いている」ように見えるバグの原因だった。X/Zの補間ロジック自体は変更していない
        /// （オーバーシュートは発生しない、既存のAdvance_stops_at_target_without_overshootで保証）。</summary>
        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
