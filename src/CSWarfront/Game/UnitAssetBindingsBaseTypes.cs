using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// The portion of UnitAssetBindings split out to hold only the constants, resolution, and copy
    /// logic related to the "per-base-type (army/navy/air-force/missile) model assignment keys"
    /// introduced in Task66, as a partial class (due to the 500-line limit on UnitAssetBindings.cs;
    /// same policy as AssetAssignPanel/AssetAssignPanelControls).
    /// All fields are declared on the UnitAssetBindings.cs side (a partial class shares even private
    /// members across all parts, so _bindings/MakeKey/Save/TryGetExact etc. can be used as-is).
    /// </summary>
    internal static partial class UnitAssetBindings
    {
        /// <summary>
        /// The special TypeKey used for military-installation model assignment as of Task60.
        /// <b>Not a unit type</b> (it is not included in LandUnitRoster.All() and has no
        /// UnitType/UnitCategory backing it).
        /// After Task66 split it into four per-type keys for army/navy/air-force/missile
        /// (<see cref="ArmyBaseTypeKey"/> etc.), it is no longer directly selectable from the UI
        /// (not included in _typeKeys), but it is still used in resolution as a
        /// <b>backward-compatibility fallback</b>: when the new keys have no assignment, this old
        /// key's assignment is used as a "fallback common to all base types" (see
        /// <see cref="TryGetForBase"/>).
        /// This way, "MilitaryBase" lines in existing unit-assets.txt files created before Task66 do
        /// not have to be discarded.
        /// </summary>
        public const string BaseTypeKey = "MilitaryBase";

        /// <summary>Task66: Per-base-type model assignment keys. These four entries are shown at the
        /// top of the UI dropdown (in place of <see cref="BaseTypeKey"/>). The string values are used
        /// as-is as key names in saved files, so do not change them after release.</summary>
        public const string ArmyBaseTypeKey = "ArmyBase";
        public const string NavyBaseTypeKey = "NavyBase";
        public const string AirBaseTypeKey = "AirBase";
        public const string MissileBaseTypeKey = "MissileBase";

        /// <summary>Human-readable labels shown in the UI dropdowns (AssetAssignPanel/
        /// OptionsModelAssignPage) instead of the raw key strings. Showing the raw key string as-is
        /// would give the misleading impression that "this is one of the 35 unit types", so it is
        /// always replaced with this label for display.</summary>
        public const string ArmyBaseDisplayName = "Army Base";
        public const string NavyBaseDisplayName = "Naval Base";
        public const string AirBaseDisplayName = "Air Base";
        public const string MissileBaseDisplayName = "Missile Base";

        /// <summary>Returns the assignment key (<see cref="ArmyBaseTypeKey"/> etc.) corresponding to the given <see cref="BaseType"/>.</summary>
        public static string BaseTypeKeyFor(BaseType type)
        {
            switch (type)
            {
                case BaseType.Navy: return NavyBaseTypeKey;
                case BaseType.AirForce: return AirBaseTypeKey;
                case BaseType.MissileBase: return MissileBaseTypeKey;
                case BaseType.Army:
                default:
                    return ArmyBaseTypeKey;
            }
        }

        /// <summary>If typeKey is a base-type key (one of Army/Navy/Air/MissileBaseTypeKey), returns
        /// the corresponding BaseType. Returns false for unit TypeKeys and the old legacy key
        /// (<see cref="BaseTypeKey"/> itself) — the legacy key is fallback-only and never selected
        /// from the UI, so it is not included in the "this is a base selection entry"
        /// determination.</summary>
        public static bool TryGetBaseTypeForKey(string typeKey, out BaseType type)
        {
            switch (typeKey)
            {
                case ArmyBaseTypeKey: type = BaseType.Army; return true;
                case NavyBaseTypeKey: type = BaseType.Navy; return true;
                case AirBaseTypeKey: type = BaseType.AirForce; return true;
                case MissileBaseTypeKey: type = BaseType.MissileBase; return true;
                default: type = default(BaseType); return false;
            }
        }

        /// <summary>Display label for a base-type key (returns typeKey as-is if it is not a base-type key).</summary>
        public static string DisplayNameForBaseKey(string typeKey)
        {
            switch (typeKey)
            {
                case ArmyBaseTypeKey: return ArmyBaseDisplayName;
                case NavyBaseTypeKey: return NavyBaseDisplayName;
                case AirBaseTypeKey: return AirBaseDisplayName;
                case MissileBaseTypeKey: return MissileBaseDisplayName;
                default: return typeKey;
            }
        }

        /// <summary>Task66: Common dispatch helper used by the UI (AssetAssignPanel/
        /// OptionsModelAssignPage) for label display and current-assignment display. If typeKey is a
        /// base-type key (<see cref="TryGetBaseTypeForKey"/> returns true), use
        /// <see cref="TryGetForBase"/> (including the fallback to the old unified key); otherwise
        /// (a unit TypeKey) use the normal <see cref="TryGet"/> (including tier fallback).
        /// This lets the UI side always display the effective value (the value actually applied,
        /// including fallbacks) without caring "which kind of key am I looking at right now".</summary>
        public static bool TryGetEffective(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            BaseType baseType;
            if (TryGetBaseTypeForKey(typeKey, out baseType))
            {
                return TryGetForBase(factionId, baseType, out kind, out assetName);
            }
            return TryGet(factionId, typeKey, out kind, out assetName);
        }

        /// <summary>
        /// Task66: Base (military installation)-specific resolution. Resolution order (the key to
        /// backward compatibility):
        ///   1. The dedicated key for the given <paramref name="baseType"/>
        ///      (<see cref="BaseTypeKeyFor"/>), per-faction exact
        ///   2. Same key, legacy/all-factions-common exact
        ///   3. Old unified key (<see cref="BaseTypeKey"/>, "MilitaryBase"), per-faction exact
        ///   4. Old unified key, legacy/all-factions-common exact
        ///   5. None
        /// This fallback exists so that "MilitaryBase" lines created before Task60's split (when base
        /// types were not distinguished) are not wasted; the per-type keys introduced in Task66
        /// (steps 1 and 2) always take precedence.
        /// Tier fallback (see TryGet) is irrelevant to bases, so none is performed here.
        /// (Base-type keys are not in "&lt;Category&gt;_T&lt;tier&gt;" format, so TypeKeyParser.TryParse would
        /// fail to parse them and simply pass through with no harm anyway, but making this a dedicated
        /// method states the intent explicitly.)
        /// </summary>
        public static bool TryGetForBase(byte factionId, BaseType baseType, out AssetKind kind, out string assetName)
        {
            string typeKey = BaseTypeKeyFor(baseType);
            if (TryGetExact(factionId, typeKey, out kind, out assetName)) return true;
            if (TryGetExact(factionId, BaseTypeKey, out kind, out assetName)) return true;

            kind = AssetKind.Prop;
            assetName = null;
            return false;
        }

        /// <summary>
        /// Task66: Dedicated copy routine used when the copy source is a base-type key
        /// (ArmyBaseTypeKey etc.) — called via a branch from UnitAssetBindings.CopyTo.
        /// Bases have no "category", no "tier", and no "other unit types", so of the CopyScope values
        /// only the two with a "faction" dimension (AllFactionsSameType = all factions (same type) /
        /// AllFactionsAllTypes = all factions &amp; all types) are supported, reinterpreted as "copy the
        /// same base type to all other factions" (no copying across base types, e.g. Army -&gt; Navy.
        /// Per the requirement "all factions (same type) operates per base type",
        /// <paramref name="fromTypeKey"/> itself is used directly as the destination key).
        /// The two with a "type" dimension (SameCategoryAllTiers = all tiers of same category /
        /// AllUnitTypes = all unit types) are meaningless for bases (they are not unit types), so per
        /// the requirement nothing is written and 0 is returned.
        /// (When written==0 the calling UI does not invoke the ApplyBindingChange-equivalent refresh,
        /// so the user only sees "nothing happened" and no accidental writes to other unit types can
        /// occur.)
        /// </summary>
        private static int CopyBaseTo(byte fromFaction, string fromTypeKey, AssetKind kind, string name, CopyScope scope)
        {
            bool allFactions = scope == CopyScope.AllFactionsSameType || scope == CopyScope.AllFactionsAllTypes;
            if (!allFactions)
            {
                ModConfig.Log("UnitAssetBindings.CopyTo: skipped, " + fromTypeKey + " does not support scope=" + scope +
                    " (same-category/all-unit-types scopes are unit-only)");
                return 0;
            }

            int written = 0;
            for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
            {
                if (f == fromFaction) continue; // skip the copy source itself
                _bindings[MakeKey(f, fromTypeKey)] = new Binding { Kind = kind, Name = name };
                written++;
            }

            if (written > 0) Save();
            ModConfig.Log("UnitAssetBindings.CopyTo: faction=" + fromFaction + " " + fromTypeKey + " (" +
                AssetKindUtil.ToPrefix(kind) + ":" + name + ") copied to scope=" + scope + " (same base type, all other factions), " +
                "wrote " + written + " entries");
            return written;
        }
    }
}
