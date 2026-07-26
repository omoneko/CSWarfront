namespace CSWarfront.Core
{
    /// <summary>ユニットが敵対基地を射程内で攻撃する（純ロジック）。</summary>
    public static class BaseCombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            // 新設基地の占領猶予を先に消化する。猶予中の基地はこのtickのダメージループから完全に除外する
            // （プレイヤーが両陣営を配置し終える前に一方的に占領されるのを防ぐ）。
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.CaptureGraceHours <= 0f) continue;
                b.CaptureGraceHours -= dt;
                if (b.CaptureGraceHours < 0f) b.CaptureGraceHours = 0f;
            }

            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                var type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (b.CaptureGraceHours > 0f) continue; // 猶予中は無敵
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (state.Relations.Get(u.FactionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;
                    // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)に比例する。
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f) * dt;
                    if (b.CurrentHP < 0f) b.CurrentHP = 0f;
                }
            }
        }
    }
}
