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
        /// <summary>当該勢力の非交戦ユニットに、各自位置から最寄りの敵基地へ進軍命令を与える。</summary>
        public static void AssignAdvance(WarState state, byte factionId)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.State == UnitState.Engaging) continue;
                var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position);
                if (target == null) continue;
                u.OrderTargetPos = target.Position;
                u.State = UnitState.Moving;
            }
        }
    }
}
