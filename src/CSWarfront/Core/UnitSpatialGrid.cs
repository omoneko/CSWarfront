using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task97 (playtest feedback "the game slows down as the battle grows"): the spatial grid for
    /// engagement checks.
    ///
    /// The old TargetSearch.FindNearestHostile brute-forced every unit (called once per unit by its
    /// callers, hence O(N²)); at a measured 690 units that was ~480k distance computations per tick and
    /// the dominant bottleneck. This grid buckets units into CellSize squares at the top of each tick
    /// (O(N)) and narrows the search to "only the candidates in cells overlapping the range circle"
    /// (neighbors only — near O(N)).
    ///
    /// Results are exactly identical to the brute-force version (preserving the deterministic
    /// simulation):
    ///  - Cells are used only to narrow candidates; range, hostility, domain and liveness are all
    ///    checked at search time (so a unit killed earlier in the same tick vanishing from later
    ///    searches behaves the same as in the brute-force version).
    ///  - Distance ties go to the smaller state.Units index (indices are compared explicitly to match
    ///    the brute-force version's "first in the list wins" exactly).
    ///
    /// Usage: each step (CombatStep/KamikazeStep) calls Build() at the top of Advance and passes the
    /// grid to TargetSearch.FindNearestHostile's grid overload. Unit positions only change in
    /// MovementStep (a different step), so the grid never goes stale within a step.
    /// A runtime-only member of WarState (not persisted, sim-thread only).
    /// </summary>
    public class UnitSpatialGrid
    {
        /// <summary>Cell edge length (m). Large enough that even the longest-ranged class's range circle
        /// spans only a few cells, yet small enough that no cell packs dozens of units.</summary>
        public const float CellSize = 256f;

        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private List<UnitInstance> _units;

        /// <summary>Re-buckets the unit list into cells (called every tick at the top of each step).
        /// Only living units are registered (the dead can never be candidates). Lists are reused to
        /// avoid per-tick allocations.</summary>
        public void Build(List<UnitInstance> units)
        {
            _units = units;
            foreach (List<int> cell in _cells.Values) cell.Clear();

            for (int i = 0; i < units.Count; i++)
            {
                UnitInstance u = units[i];
                if (!u.IsAlive) continue;
                long key = KeyFor(u.Position.X, u.Position.Z);
                List<int> cell;
                if (!_cells.TryGetValue(key, out cell))
                {
                    cell = new List<int>();
                    _cells[key] = cell;
                }
                cell.Add(i);
            }
        }

        /// <summary>Grid search returning the exact same result as the brute-force
        /// TargetSearch.FindNearestHostile. See the class comment for details.</summary>
        public UnitInstance FindNearestHostile(UnitInstance self, RelationMatrix rel, float range,
            DomainMask attackerCanTarget, UnitTypeRegistry types)
        {
            if (_units == null) return null;

            UnitInstance bestHostile = null;
            float bestHostileDist = float.MaxValue;
            int bestHostileIdx = int.MaxValue;
            UnitInstance bestNemesis = null;
            float bestNemesisDist = float.MaxValue;
            int bestNemesisIdx = int.MaxValue;

            // Task101: the anti-helicopter rules need the attacker's category (same as the brute-force
            // version in TargetSearch).
            UnitCategory? selfCategory = null;
            if (types != null)
            {
                UnitType selfType = types.Get(self.TypeKey);
                if (selfType != null) selfCategory = selfType.Category;
            }

            int cxMin = CellOf(self.Position.X - range), cxMax = CellOf(self.Position.X + range);
            int czMin = CellOf(self.Position.Z - range), czMax = CellOf(self.Position.Z + range);
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                for (int cz = czMin; cz <= czMax; cz++)
                {
                    List<int> cell;
                    if (!_cells.TryGetValue(Key(cx, cz), out cell)) continue;

                    for (int c = 0; c < cell.Count; c++)
                    {
                        int idx = cell[c];
                        UnitInstance u = _units[idx];
                        if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                        if (u.IsCarried) continue; // Task101: never targetable while aboard a carrier
                        Relation r = rel.Get(self.FactionId, u.FactionId);
                        if (!r.IsHostile()) continue;
                        float d = self.Position.HorizontalDistanceTo(u.Position);
                        if (d > range) continue;

                        if (types != null)
                        {
                            UnitType targetType = types.Get(u.TypeKey);
                            if (targetType != null)
                            {
                                // Task101: the dedicated helicopter-target rules (identical to the
                                // brute-force TargetSearch).
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
                            if (d < bestNemesisDist || (d == bestNemesisDist && idx < bestNemesisIdx))
                            { bestNemesisDist = d; bestNemesis = u; bestNemesisIdx = idx; }
                        }
                        else
                        {
                            if (d < bestHostileDist || (d == bestHostileDist && idx < bestHostileIdx))
                            { bestHostileDist = d; bestHostile = u; bestHostileIdx = idx; }
                        }
                    }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }

        private static int CellOf(float v)
        {
            return (int)System.Math.Floor(v / CellSize);
        }

        private static long Key(int cx, int cz)
        {
            return ((long)cx << 32) ^ (uint)cz;
        }

        private static long KeyFor(float x, float z)
        {
            return Key(CellOf(x), CellOf(z));
        }
    }
}
