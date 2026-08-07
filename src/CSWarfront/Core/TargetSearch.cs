using System.Collections.Generic;
namespace CSWarfront.Core
{
    public static class TargetSearch
    {
        /// <summary>Returns the enemy with the smallest horizontal distance among in-range, hostile
        /// (Hostile/Nemesis), living units; null if none. Task59: if even one Nemesis-relation enemy is
        /// in range, nemeses take priority over regular Hostiles and the nearest nemesis is returned.
        /// With no nemesis present, the nearest regular Hostile is returned as before (when no nemesis
        /// exists the behavior is exactly the legacy one = backward compatible with existing
        /// tests).</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range)
        {
            return FindNearestHostile(self, all, rel, range, DomainMask.All, null);
        }

        /// <summary>Task61: the domain-filtered version. Candidates whose Domain (resolved via types)
        /// is not in attackerCanTarget are excluded even when the other conditions (range, hostility)
        /// hold. This is what makes AntiAir the only land category with anti-air capability and keeps
        /// regular land categories from ever targeting aircraft. When types is null or a candidate's
        /// TypeKey cannot be resolved, the domain filter is skipped (= the same behavior as the
        /// 4-argument version, the backward-compatible fallback for existing callers and
        /// tests).</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range,
            DomainMask attackerCanTarget, UnitTypeRegistry types)
        {
            UnitInstance bestHostile = null;
            float bestHostileDist = float.MaxValue;
            UnitInstance bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            // Task101: the anti-helicopter rule (TargetingRules.CanTargetHelicopter) needs the attacker's category.
            UnitCategory? selfCategory = null;
            if (types != null)
            {
                UnitType selfType = types.Get(self.TypeKey);
                if (selfType != null) selfCategory = selfType.Category;
            }

            foreach (var u in all)
            {
                if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                if (u.IsCarried) continue; // Task101: units being carried (inside a heli/train) cannot be targeted
                Relation r = rel.Get(self.FactionId, u.FactionId);
                if (!r.IsHostile()) continue;
                float d = self.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;

                if (types != null)
                {
                    UnitType targetType = types.Get(u.TypeKey);
                    if (targetType != null)
                    {
                        // Task101: helicopters have their own rule (only tanks, AA and fighters may
                        // attack them). Tanks keep CanTargetDomains=Land yet may target helicopters
                        // as the one exception (a two-way exception).
                        if (TargetingRules.IsHelicopter(targetType.Category))
                        {
                            if (!selfCategory.HasValue || !TargetingRules.CanTargetHelicopter(selfCategory.Value))
                                continue;
                        }
                        else if (!DomainMaskUtil.Contains(attackerCanTarget, targetType.Domain))
                        {
                            continue;
                        }
                    }
                }

                if (r == Relation.Nemesis)
                {
                    if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = u; }
                }
                else
                {
                    if (d < bestHostileDist) { bestHostileDist = d; bestHostile = u; }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }

        /// <summary>Task97: the spatial-grid version. The result is exactly identical to the
        /// brute-force version above; narrowing candidates to "cells overlapping the range circle"
        /// takes O(N²) to roughly O(N) (see UnitSpatialGrid's class comment). Callers must have run
        /// grid.Build(state.Units) at the top of the tick.</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self, UnitSpatialGrid grid,
            RelationMatrix rel, float range, DomainMask attackerCanTarget, UnitTypeRegistry types)
        {
            return grid.FindNearestHostile(self, rel, range, attackerCanTarget, types);
        }
    }
}
