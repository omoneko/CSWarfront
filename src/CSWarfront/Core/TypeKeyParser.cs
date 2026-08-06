using System;
using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// A small pure-logic helper (Task50) parsing TypeKeys of the "&lt;Category&gt;_T&lt;tier&gt;" format
    /// (the shape LandUnitRoster.TypeKey assembles, e.g. "Tank_T3").
    ///
    /// Used by the Game layer's UnitAssetBindings (the "fall back to another tier of the same category"
    /// rule of the asset assignments — the fix for Task50 feedback 1, "model assignments stop applying at
    /// tier 2"). UnitAssetBindings itself lives in the Game layer (it does not reference UnityEngine
    /// directly, but sits outside the folders compiled into CSWarfront.Core.Tests = only Core\**\*.cs),
    /// so to keep this testable, only the parsing and the fallback-order construction were extracted into
    /// this core class (UnitAssetBindings.TryGet stays a thin glue doing dictionary lookups with these
    /// results).
    /// </summary>
    public static class TypeKeyParser
    {
        /// <summary>The tier range the rosters actually use (LandUnitRoster: tiers 1–5).</summary>
        public const byte MinTier = 1;
        public const byte MaxTier = 5;

        /// <summary>
        /// Parses typeKey as "&lt;Category&gt;_T&lt;tier&gt;". The trailing "_T&lt;digits&gt;" is the
        /// separator; the front half is read as a UnitCategory name and the tail as the tier number (the
        /// inverse of LandUnitRoster.TypeKey's category + "_T" + tier). No UnitCategory member name
        /// contains "_T", so mis-splitting cannot happen. Returns false when unparsable (no separator /
        /// non-numeric tier / unknown category name) — never throws.
        /// </summary>
        public static bool TryParse(string typeKey, out UnitCategory category, out byte tier)
        {
            category = default(UnitCategory);
            tier = 0;
            if (string.IsNullOrEmpty(typeKey)) return false;

            int splitIndex = typeKey.LastIndexOf("_T", StringComparison.Ordinal);
            if (splitIndex <= 0 || splitIndex + 2 >= typeKey.Length) return false;

            string categoryPart = typeKey.Substring(0, splitIndex);
            string tierPart = typeKey.Substring(splitIndex + 2);

            if (!byte.TryParse(tierPart, out tier)) return false;
            return TryParseCategory(categoryPart, out category);
        }

        private static bool TryParseCategory(string value, out UnitCategory category)
        {
            // .NET 3.5 (the Game layer's build target) has no Enum.TryParse<T>, so the known values are
            // scanned linearly (UnitCategory has only 23 members; the cost is negligible).
            foreach (UnitCategory c in (UnitCategory[])Enum.GetValues(typeof(UnitCategory)))
            {
                if (string.Equals(c.ToString(), value, StringComparison.Ordinal))
                {
                    category = c;
                    return true;
                }
            }
            category = default(UnitCategory);
            return false;
        }

        /// <summary>
        /// Returns the fallback order for "searching other tiers of the same category" relative to the
        /// given tier (Task50). Walks down from the nearest lower tier to 1, then up from the nearest
        /// higher tier to 5:
        ///   e.g. tier=4 -&gt; [3, 2, 1, 5]
        ///   e.g. tier=1 -&gt; [2, 3, 4, 5] (nothing below, so upward only)
        ///   e.g. tier=5 -&gt; [4, 3, 2, 1] (nothing above, so downward only)
        /// The tier itself is excluded (callers try the exact-key match first). A tier outside
        /// MinTier(1)–MaxTier(5) does not throw (the walk simply runs in one direction only, or the array
        /// comes back empty).
        /// </summary>
        public static byte[] FallbackTierOrder(byte tier)
        {
            var order = new List<byte>(MaxTier - MinTier);
            for (int t = tier - 1; t >= MinTier; t--) order.Add((byte)t);
            for (int t = tier + 1; t <= MaxTier; t++) order.Add((byte)t);
            return order.ToArray();
        }
    }
}
