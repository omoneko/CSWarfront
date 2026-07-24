namespace CSWarfront.Core
{
    /// <summary>ユニットが敵対基地を射程内で攻撃する（純ロジック）。</summary>
    public static class BaseCombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                var type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (state.Relations.Get(u.FactionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f);
                    if (b.CurrentHP < 0f) b.CurrentHP = 0f;
                }
            }
        }
    }
}
