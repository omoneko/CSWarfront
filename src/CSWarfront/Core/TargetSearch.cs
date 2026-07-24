using System.Collections.Generic;
namespace CSWarfront.Core
{
    public static class TargetSearch
    {
        /// <summary>射程内・敵対・生存のうち水平距離最小の敵を返す。無ければ null。</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            foreach (var u in all)
            {
                if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                if (rel.Get(self.FactionId, u.FactionId) != Relation.Hostile) continue;
                float d = self.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best;
        }
    }
}
