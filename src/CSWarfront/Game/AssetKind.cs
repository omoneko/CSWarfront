using System;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task41: the kind of asset that can be assigned as a unit's visual model. An identifier that
    /// allows borrowing not only props but also building/vehicle/tree Workshop assets
    /// (<see cref="AssetCatalog"/> switches between PrefabCollection&lt;T&gt; per kind for
    /// enumeration and resolution).
    /// The numeric values are tied to the file save format (UnitAssetBindings) and to the selection
    /// indices of the UI dropdown, so never reorder the existing entries (appending at the end only).
    /// </summary>
    internal enum AssetKind : byte
    {
        Prop = 0,
        Building = 1,
        Vehicle = 2,
        Tree = 3
    }

    /// <summary>
    /// Small helper bundling the AssetKind &lt;-&gt; string conversions (save-format prefix / UI
    /// display label). Used both by UnitAssetBindings (the kind prefix of the file format) and by
    /// AssetAssignPanel (kind-dropdown labels and current-binding display).
    /// </summary>
    internal static class AssetKindUtil
    {
        /// <summary>Must match the order of kind-dropdown selection indices 0..3 exactly.</summary>
        public static readonly AssetKind[] All = { AssetKind.Prop, AssetKind.Building, AssetKind.Vehicle, AssetKind.Tree };

        /// <summary>Lowercase prefix used in the UnitAssetBindings file save format ("kind:assetName").</summary>
        public static string ToPrefix(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Building: return "building";
                case AssetKind.Vehicle: return "vehicle";
                case AssetKind.Tree: return "tree";
                default: return "prop";
            }
        }

        /// <summary>Resolves a save-format prefix string (case-insensitive) to an AssetKind.
        /// Returns false for an unknown prefix (the caller must treat it as a legacy line without a
        /// kind prefix).</summary>
        public static bool TryParsePrefix(string prefix, out AssetKind kind)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                if (string.Equals(prefix, "prop", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Prop; return true; }
                if (string.Equals(prefix, "building", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Building; return true; }
                if (string.Equals(prefix, "vehicle", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Vehicle; return true; }
                if (string.Equals(prefix, "tree", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Tree; return true; }
            }
            kind = AssetKind.Prop;
            return false;
        }

        /// <summary>Japanese label for the UI (kind dropdown, current-binding display).</summary>
        public static string DisplayNameJa(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Building: return "Building";
                case AssetKind.Vehicle: return "Vehicle";
                case AssetKind.Tree: return "Tree";
                default: return "Prop";
            }
        }

        /// <summary>Builds the label for the "current binding" display. Props keep the name-only
        /// form as before; every other kind gets a kind tag like "[Building]" prepended to the name
        /// so it can be told apart.</summary>
        public static string Describe(AssetKind kind, string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return assetName;
            return kind == AssetKind.Prop ? assetName : "[" + DisplayNameJa(kind) + "]" + assetName;
        }
    }
}
