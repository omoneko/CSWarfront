using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>One registered fortification position (type + world position + building angle in
    /// radians, the CS Building.m_angle convention). Persisted in WarState.DefenseLayout (v11).</summary>
    public struct DefenseLayoutEntry
    {
        public BaseType Type;
        public WorldPos Position;
        public float Angle;
    }

    /// <summary>
    /// Task114 (Workshop request "a UI panel to reset all destroyed defensive positions"): the saved
    /// defense layout and its pure matching logic.
    ///
    /// "Save Defense Layout" snapshots every eligible fortification currently on the map into
    /// WarState.DefenseLayout (overwriting the previous snapshot). "Rebuild Defenses" walks the
    /// snapshot and re-places only the entries that are no longer satisfied — intact positions are
    /// left untouched. The actual building placement/demolition and cost payment are the Game
    /// layer's job (MilitaryManagerDefenseRebuild); this class only decides WHICH entries qualify,
    /// WHICH are missing, and WHICH dead wreck blocks a spot.
    /// </summary>
    public static class DefenseLayout
    {
        /// <summary>How close (m, horizontal) a same-type facility must be to a saved entry to count
        /// as "this position still exists". Well under the smallest fortification footprint (16m),
        /// so neighbouring trench segments never satisfy each other's entries.</summary>
        public const float MatchRadius = 6f;

        /// <summary>Whether this facility belongs in the snapshot: fortifications only; trenches
        /// always qualify (they are ownerless terrain by design), everything else must be alive and
        /// owned by the registering faction.</summary>
        public static bool IsEligible(MilitaryBase b, byte factionId)
        {
            if (b == null || !FortificationRules.IsFortification(b.Type)) return false;
            if (b.Type == BaseType.Trench) return true;
            return b.OwnerFactionId != null && b.OwnerFactionId.Value == factionId && b.CurrentHP > 0f;
        }

        /// <summary>Whether a saved position is still standing: a same-type facility within
        /// MatchRadius that is present (trenches) or owned-by-anyone and alive (everything else).
        /// An enemy-captured depot/station counts as satisfied — the spot is physically occupied, so
        /// the answer is to recapture it, not to build a duplicate on top.</summary>
        public static bool IsSatisfied(WarState state, DefenseLayoutEntry e)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != e.Type) continue;
                if (b.Position.HorizontalDistanceTo(e.Position) > MatchRadius) continue;
                if (e.Type == BaseType.Trench) return true;
                if (b.OwnerFactionId != null && b.CurrentHP > 0f) return true;
            }
            return false;
        }

        /// <summary>The saved entries that need rebuilding (order preserved).</summary>
        public static List<DefenseLayoutEntry> FindMissing(WarState state)
        {
            var missing = new List<DefenseLayoutEntry>();
            for (int i = 0; i < state.DefenseLayout.Count; i++)
            {
                DefenseLayoutEntry e = state.DefenseLayout[i];
                if (!IsSatisfied(state, e)) missing.Add(e);
            }
            return missing;
        }

        /// <summary>A defunct same-type wreck still registered at the spot (e.g. a bunker at 0 HP
        /// keeps its building with Owner nulled). The Game layer demolishes it before re-placing.
        /// False when the spot is genuinely empty.</summary>
        public static bool TryFindBlocker(WarState state, DefenseLayoutEntry e, out ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != e.Type) continue;
                if (b.Position.HorizontalDistanceTo(e.Position) > MatchRadius) continue;
                baseId = b.BaseId;
                return true;
            }
            baseId = 0;
            return false;
        }
    }
}
