using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Task157: every unit type there is, in one sequence - land, then air, then sea.
    ///
    /// The three rosters were written one at a time and callers reached for whichever they needed, so
    /// code that meant "all units" tended to say LandUnitRoster.All() and quietly mean "all ground
    /// units". That was harmless while only ground vehicles could be given a model, and stopped being
    /// harmless the moment aircraft and ships could too (Task155): copying a model to another type
    /// looked up the source's category in the land roster alone, so an aircraft key was rejected as
    /// unknown and the copy silently did nothing.
    ///
    /// Anything that means "every unit type" should ask here. Ordering is land, air, sea - the order
    /// the model assignment list shows them in, and stable, because callers index into it.</summary>
    public static class UnitRosters
    {
        /// <summary>The 80 unit types: 45 land, 25 air, 10 sea.</summary>
        public static IEnumerable<UnitType> All()
        {
            foreach (UnitType t in LandUnitRoster.All()) yield return t;
            foreach (UnitType t in AirUnitRoster.All()) yield return t;
            foreach (UnitType t in NavalUnitRoster.All()) yield return t;
        }

        /// <summary>Assembles a "&lt;Category&gt;_T&lt;tier&gt;" key. All three rosters spell it the
        /// same way, so this works whichever one the category belongs to.</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>The category a type key names, looked up across all three rosters. False for a key
        /// no roster has - a base-type key, or a category that exists in the enum but is not built.
        /// </summary>
        public static bool TryGetCategory(string typeKey, out UnitCategory category)
        {
            foreach (UnitType t in All())
            {
                if (t.TypeKey == typeKey)
                {
                    category = t.Category;
                    return true;
                }
            }
            category = default(UnitCategory);
            return false;
        }
    }
}
