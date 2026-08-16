using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task47: The range of copy destinations selectable in "copy apply". Values must match the
    /// selection indices of the scope dropdown in both UIs (floating panel / Options subpage)
    /// (reordering or inserting entries requires re-checking both UIs).
    /// </summary>
    internal enum CopyScope
    {
        /// <summary>All tiers of the same category (same faction, e.g. Tank_T1 through T5).</summary>
        SameCategoryAllTiers = 0,
        /// <summary>All unit types (all 35 keys of the same faction).</summary>
        AllUnitTypes = 1,
        /// <summary>All factions (same type, faction 0..4).</summary>
        AllFactionsSameType = 2,
        /// <summary>All factions and all types.</summary>
        AllFactionsAllTypes = 3
    }

    /// <summary>CopyScope &lt;=&gt; display label conversion. A small helper following the same policy as AssetKindUtil.</summary>
    internal static class CopyScopeUtil
    {
        /// <summary>Must exactly match the ordering of the scope dropdown's selection indices 0..3.</summary>
        public static readonly CopyScope[] All =
        {
            CopyScope.SameCategoryAllTiers, CopyScope.AllUnitTypes,
            CopyScope.AllFactionsSameType, CopyScope.AllFactionsAllTypes
        };

        public static string DisplayNameJa(CopyScope scope)
        {
            switch (scope)
            {
                case CopyScope.SameCategoryAllTiers: return "All tiers of same category";
                case CopyScope.AllUnitTypes: return "All unit types";
                case CopyScope.AllFactionsSameType: return "All factions (same type)";
                case CopyScope.AllFactionsAllTypes: return "All factions & all types";
                default: return scope.ToString();
            }
        }
    }

    /// <summary>
    /// Holds and persists the "(faction ID, unit type TypeKey) -&gt; subscribed asset (kind + name)"
    /// assignments (introduced in Task36, extended per-faction in Task40, and extended in Task41 to
    /// asset kinds other than props (buildings/vehicles/trees)).
    /// This is a global setting saved to a simple text file directly under the MOD directory, not in
    /// the savegame (matching the player's mental model of "the assets I own", shared across saves).
    ///
    /// File format (one entry per line, UTF-8):
    ///   "factionId|typeKey=kind:assetName"  ... per-faction assignment (Task41 added the kind prefix
    ///                                          to the value)
    ///   "factionId|typeKey=assetName"       ... value without a kind prefix. Loaded as
    ///                                          AssetKind.Prop for backward compatibility.
    ///   "typeKey=kind:assetName" / "typeKey=assetName" ... legacy lines (no factionId prefix).
    ///                                          Loaded as an "all-factions common fallback"
    ///                                          (backward compatibility).
    ///
    /// Resolution order (tier fallback added in Task50):
    ///   1. Per-faction, exact-key (faction + the typeKey itself) assignment
    ///   2. Legacy / all-factions-common, exact-key assignment
    ///   3. Fallback to other tiers of the same category (only when the typeKey can be parsed as
    ///      "&lt;Category&gt;_T&lt;tier&gt;"). Tries the nearest lower tiers down to 1, then the nearest higher
    ///      tiers up to 5 (see TypeKeyParser.FallbackTierOrder; e.g. if Tank_T4 is unassigned, the
    ///      order is T3 -&gt; T2 -&gt; T1 -&gt; T5), checking "per-faction -&gt; legacy/all-factions-common" for
    ///      each tier candidate.
    ///   4. None (default model)
    /// This means "assigning a model only to Tier1 automatically applies that model to Tier2 and
    /// above" (no need to manually assign all 5 tiers). However, an explicit assignment to a specific
    /// tier (steps 1/2) always takes precedence over this fallback (step 3).
    ///
    /// Base (military installation)-specific resolution lives separately in <see cref="TryGetForBase"/>
    /// (it does not go through TryGet because tier fallback is irrelevant to bases). Task60 had only a
    /// single key that did not distinguish base types (<see cref="BaseTypeKey"/>, "MilitaryBase");
    /// Task66 split it into four per-type keys for army/navy/air-force/missile
    /// (<see cref="ArmyBaseTypeKey"/> etc.). Resolution order: 1. per-type key, per-faction exact -&gt;
    /// 2. per-type key, legacy/all-factions-common exact -&gt; 3. old unified key ("MilitaryBase"),
    /// per-faction exact -&gt; 4. old unified key, legacy/all-factions-common exact -&gt; 5. none.
    /// This way, "faction|MilitaryBase=..." lines saved before Task66 continue to work as a "common
    /// fallback for all base types that have no explicit per-type assignment" (no need to rewrite
    /// unit-assets.txt).
    ///
    /// Kind prefix parsing is done by AssetKindUtil.TryParsePrefix. If the value does not start with a
    /// known kind name followed by ':', the whole value is treated as an AssetKind.Prop name (as if it
    /// had no kind prefix) — so an existing prop name that happens to contain ':' is not misparsed.
    ///
    /// Set() always writes the new format ("kind:assetName"). Legacy lines are never created by
    /// Set/Clear (new saves are always per-faction with kind prefix), but if they remain in an
    /// existing file they continue to be loaded, and on save they are written back in the key format
    /// they were loaded with (with or without the factionId prefix) — they are not removed, for
    /// compatibility. The value side is always normalized to include the kind prefix when written
    /// back, so after a re-save every line has a new-format value.
    ///
    /// A corrupted/missing file is always treated as "no assignments" and never throws outward
    /// (a failure here must not stop loading itself).
    /// There is no main-thread-only constraint, but all calls are expected to come from the main
    /// thread (UI / load processing).
    /// </summary>
    internal static partial class UnitAssetBindings
    {
        // Task66: The base-type key constants (BaseTypeKey/ArmyBaseTypeKey etc.), display names,
        // BaseTypeKeyFor/TryGetBaseTypeForKey/DisplayNameForBaseKey, TryGetForBase, and CopyBaseTo were
        // split out into UnitAssetBindingsBaseTypes.cs (same partial class,
        // Game/UnitAssetBindingsBaseTypes.cs) due to the 500-line limit.

        private const string FileName = "unit-assets.txt";

        private struct Binding
        {
            public AssetKind Kind;
            public string Name;
        }

        // Per-faction assignments. Key is MakeKey(factionId, typeKey) = "factionId|typeKey".
        private static readonly Dictionary<string, Binding> _bindings = new Dictionary<string, Binding>();

        // Legacy lines (no factionId prefix). Key is the typeKey itself. All-factions common fallback.
        private static readonly Dictionary<string, Binding> _anyFactionBindings = new Dictionary<string, Binding>();

        // Resolved file path. Remains null if modDirectory could not be obtained, in which case
        // Set() keeps the binding in memory only and skips saving (like EnsureRegistered, loading
        // itself is never stopped).
        private static string _filePath;

        // Task70: The modDirectory itself, kept for building preset slot paths
        // (unit-assets-set1 through 3.txt). (_filePath is the full path including FileName, so the
        // directory alone is kept separately.)
        // If Load received a null mod directory, this also stays null, and all slot operations in
        // UnitAssetBindingsPresets.cs treat it as "save destination unresolved" and return false
        // without doing anything.
        private static string _modDirectory;

        public static int Count { get { return _bindings.Count + _anyFactionBindings.Count; } }

        /// <summary>Called once at startup (WarfrontLoadingExtension.OnLevelLoaded). Not idempotent
        /// (re-reads from file every time; the caller is expected to call it only once per level
        /// load).</summary>
        public static void Load(string modDirectory)
        {
            _bindings.Clear();
            _anyFactionBindings.Clear();
            _filePath = null;
            _modDirectory = null;

            try
            {
                // Task137: bindings live in the player's own settings directory. In the mod folder a
                // Workshop update replaced them with whatever the payload happened to contain.
                _filePath = WarfrontUserData.ResolvePath(FileName, modDirectory);
                if (string.IsNullOrEmpty(_filePath))
                {
                    ModConfig.LogError("UnitAssetBindings.Load: no writable settings directory, running in-memory only (bindings will not be saved)");
                    return;
                }

                _modDirectory = modDirectory; // Task70/137: preset slots resolve through WarfrontUserData too
                if (!File.Exists(_filePath))
                {
                    ModConfig.Log("UnitAssetBindings.Load: '" + _filePath + "' not found, starting with 0 bindings");
                    return;
                }

                int parsed;
                ParseFileInto(_filePath, _bindings, _anyFactionBindings, out parsed);

                ModConfig.Log("UnitAssetBindings.Load: loaded " + parsed + " binding(s) from '" + _filePath + "' (per-faction " +
                    _bindings.Count + " / all-factions (legacy) " + _anyFactionBindings.Count + ")");
            }
            catch (Exception e)
            {
                // A corrupted file, access permission error, etc. is treated as "no assignments" and
                // execution continues (loading is not stopped).
                ModConfig.LogError("UnitAssetBindings.Load error (continuing with no bindings): " + e);
                _bindings.Clear();
                _anyFactionBindings.Clear();
            }
        }

        /// <summary>Task70: Core file-format parser (shared helper for Load and
        /// UnitAssetBindingsPresets.LoadFromSlot). The caller must verify the existence of
        /// <paramref name="path"/> beforehand (if it does not exist, File.ReadAllLines throws and the
        /// caller's try/catch is expected to catch it). See the file format section in the class-level
        /// comment for the per-line parsing spec.</summary>
        private static void ParseFileInto(string path, Dictionary<string, Binding> bindings, Dictionary<string, Binding> anyFactionBindings, out int parsedCount)
        {
            parsedCount = 0;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0 || eq >= line.Length - 1) continue; // ignore lines where key or value is empty

                string key = line.Substring(0, eq);
                string rawValue = line.Substring(eq + 1);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(rawValue)) continue;

                Binding binding;
                ParseValue(rawValue, out binding);

                byte factionId;
                string typeKey;
                if (TryParseFactionKey(key, out factionId, out typeKey))
                {
                    bindings[MakeKey(factionId, typeKey)] = binding;
                }
                else
                {
                    // Legacy line (Task36 format). Treated as an all-factions common fallback.
                    anyFactionBindings[key] = binding;
                }
                parsedCount++;
            }
        }

        /// <summary>
        /// Resolves the assignment for the given faction and type. Resolution order (see the
        /// class-level comment; tier fallback added in Task50): per-faction exact -&gt;
        /// legacy/all-factions-common exact -&gt; same-category other-tier fallback (per-faction takes
        /// precedence) -&gt; none.
        /// </summary>
        public static bool TryGet(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            kind = AssetKind.Prop;
            assetName = null;
            if (string.IsNullOrEmpty(typeKey)) return false;

            if (TryGetExact(factionId, typeKey, out kind, out assetName)) return true;

            // Task50: If there is no exact-key match, fall back to other tiers of the same category
            // (this realizes "assign a model only to Tier1 and it applies to all tiers". Parsing and
            // building the search order are delegated to CSWarfront.Core.TypeKeyParser (pure logic,
            // tested in Core.Tests)).
            // If typeKey cannot be parsed as "<Category>_T<tier>" (base-type keys etc.), TryParse
            // simply returns false and this block is skipped — no exceptions or false matches occur.
            UnitCategory category;
            byte tier;
            if (TypeKeyParser.TryParse(typeKey, out category, out tier))
            {
                byte[] fallbackTiers = TypeKeyParser.FallbackTierOrder(tier);
                for (int i = 0; i < fallbackTiers.Length; i++)
                {
                    string fallbackKey = LandUnitRoster.TypeKey(category, fallbackTiers[i]);
                    if (TryGetExact(factionId, fallbackKey, out kind, out assetName)) return true;
                }
            }

            kind = AssetKind.Prop;
            assetName = null;
            return false;
        }

        // Task66: TryGetEffective/TryGetForBase were split out into UnitAssetBindingsBaseTypes.cs
        // (same partial class) due to the 500-line limit. TryGetExact (below) stays here because it is
        // a shared helper called from both.

        /// <summary>Internal helper that checks only the two stages per-faction exact -&gt;
        /// legacy/all-factions-common exact (no fallback). The minimal unit shared by both TryGet
        /// (with tier fallback) and TryGetForBase (with old-unified-key fallback).</summary>
        private static bool TryGetExact(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            kind = AssetKind.Prop;
            assetName = null;

            Binding binding;
            if (_bindings.TryGetValue(MakeKey(factionId, typeKey), out binding))
            {
                kind = binding.Kind;
                assetName = binding.Name;
                return true;
            }
            if (_anyFactionBindings.TryGetValue(typeKey, out binding))
            {
                kind = binding.Kind;
                assetName = binding.Name;
                return true;
            }
            return false;
        }

        /// <summary>Assigns an asset (kind + name) to the given (faction, TypeKey) and saves
        /// immediately. Always saves in the per-faction, kind-prefixed format (legacy /
        /// all-factions-common entries are neither created nor modified here).</summary>
        public static void Set(byte factionId, string typeKey, AssetKind kind, string assetName)
        {
            if (string.IsNullOrEmpty(typeKey) || string.IsNullOrEmpty(assetName)) return;
            _bindings[MakeKey(factionId, typeKey)] = new Binding { Kind = kind, Name = assetName };
            ModConfig.Log("UnitAssetBindings.Set: faction=" + factionId + " " + typeKey + " = " + AssetKindUtil.ToPrefix(kind) + ":" + assetName);
            Save();
        }

        /// <summary>Removes only the per-faction assignment for the given (faction, TypeKey)
        /// (reverting to the all-factions-common / default fallback) and saves immediately.
        /// All-factions-common (legacy) entries are not modified.</summary>
        public static void Clear(byte factionId, string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return;
            if (_bindings.Remove(MakeKey(factionId, typeKey)))
            {
                ModConfig.Log("UnitAssetBindings.Clear: faction=" + factionId + " " + typeKey + " reset to default");
                Save();
            }
        }

        /// <summary>
        /// Task47: "Copy apply". Copies the current assignment of the given (faction, TypeKey) in bulk
        /// to every (faction, TypeKey) in the range specified by scope. The copy source itself is
        /// excluded from the write targets (it already has the same value, so this avoids a pointless
        /// write/log). Saving is done once after all changes are finished, rather than calling Set()
        /// inside the loop (to avoid one disk I/O per written entry). The caller (AssetAssignPanel /
        /// Options page) must call UnitVisuals.DestroyAll() itself (this method is responsible only
        /// for persistence and is not involved in triggering visual regeneration — since it is called
        /// from both the floating panel and the Options page, the side effect is uniformly made
        /// explicit in each UI).
        /// </summary>
        /// <returns>The number of (faction, TypeKey) entries actually written. Returns 0 and changes
        /// nothing if the copy source has no assignment or the TypeKey is unknown.</returns>
        public static int CopyTo(byte fromFaction, string fromTypeKey, CopyScope scope)
        {
            // Task66: If the copy source is a base-type key (Army/Navy/Air/MissileBaseTypeKey), branch
            // into the dedicated copy routine. The unit-oriented logic below, which linearly scans
            // LandUnitRoster.All(), never contains this virtual "type", so passing it through would
            // always yield 0 entries (TryGetCategory failure).
            // So that the same effective value shown by the "current assignment" display (TryGetForBase,
            // including the fallback to the old unified key) can be copied, the copy source is also
            // resolved with TryGetForBase instead of TryGet.
            BaseType fromBaseType;
            bool isBaseKey = TryGetBaseTypeForKey(fromTypeKey, out fromBaseType);

            AssetKind kind;
            string name;
            bool hasSource = isBaseKey
                ? TryGetForBase(fromFaction, fromBaseType, out kind, out name)
                : TryGet(fromFaction, fromTypeKey, out kind, out name);

            if (!hasSource)
            {
                ModConfig.Log("UnitAssetBindings.CopyTo: skipped, source faction=" + fromFaction + " " + fromTypeKey + " has no binding");
                return 0;
            }

            if (isBaseKey)
            {
                return CopyBaseTo(fromFaction, fromTypeKey, kind, name, scope);
            }

            UnitCategory fromCategory;
            if (!TryGetCategory(fromTypeKey, out fromCategory))
            {
                ModConfig.LogError("UnitAssetBindings.CopyTo: skipped, unknown TypeKey '" + fromTypeKey + "'");
                return 0;
            }

            bool allTypes = scope == CopyScope.AllUnitTypes || scope == CopyScope.AllFactionsAllTypes;
            bool allFactions = scope == CopyScope.AllFactionsSameType || scope == CopyScope.AllFactionsAllTypes;

            int written = 0;
            foreach (UnitType t in LandUnitRoster.All())
            {
                if (!allTypes)
                {
                    bool sameCategory = scope == CopyScope.SameCategoryAllTiers && t.Category == fromCategory;
                    bool sameType = scope == CopyScope.AllFactionsSameType && t.TypeKey == fromTypeKey;
                    if (!sameCategory && !sameType) continue;
                }

                if (allFactions)
                {
                    for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
                    {
                        if (f == fromFaction && t.TypeKey == fromTypeKey) continue; // skip the copy source itself
                        _bindings[MakeKey(f, t.TypeKey)] = new Binding { Kind = kind, Name = name };
                        written++;
                    }
                }
                else
                {
                    if (t.TypeKey == fromTypeKey) continue; // skip the copy source itself
                    _bindings[MakeKey(fromFaction, t.TypeKey)] = new Binding { Kind = kind, Name = name };
                    written++;
                }
            }

            if (written > 0) Save();
            ModConfig.Log("UnitAssetBindings.CopyTo: faction=" + fromFaction + " " + fromTypeKey + " (" +
                AssetKindUtil.ToPrefix(kind) + ":" + name + ") copied to scope=" + scope + ", wrote " + written + " entries");
            return written;
        }

        // Task66: CopyBaseTo (the dedicated copy routine used when the copy source is a base-type key)
        // was split out into UnitAssetBindingsBaseTypes.cs (same partial class) due to the 500-line
        // limit. _bindings/MakeKey/Save are private static, but partial class members are shared across
        // all parts, so they can be called from there without issue.

        /// <summary>Reverse-looks up the UnitCategory from a TypeKey (linear scan of
        /// LandUnitRoster.All(); only 35 entries, so the cost is negligible). Returns false if not
        /// found.</summary>
        private static bool TryGetCategory(string typeKey, out UnitCategory category)
        {
            foreach (UnitType t in LandUnitRoster.All())
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

        /// <summary>Parses the value part ("kind:assetName", or just "assetName" for backward
        /// compatibility). If it starts with a known kind name followed by ':', it is treated as that
        /// kind; otherwise the whole value is treated as an AssetKind.Prop name (backward
        /// compatibility: files from the Task36/Task40 era have no kind prefix).</summary>
        private static void ParseValue(string rawValue, out Binding binding)
        {
            int colon = rawValue.IndexOf(':');
            if (colon > 0)
            {
                string prefix = rawValue.Substring(0, colon);
                AssetKind parsedKind;
                if (AssetKindUtil.TryParsePrefix(prefix, out parsedKind))
                {
                    binding = new Binding { Kind = parsedKind, Name = rawValue.Substring(colon + 1) };
                    return;
                }
            }

            binding = new Binding { Kind = AssetKind.Prop, Name = rawValue };
        }

        /// <summary>Parses a key in "factionId|typeKey" format. Returns false if the factionId prefix
        /// is missing or not numeric (the caller then treats that line as
        /// legacy/all-factions-common).</summary>
        private static bool TryParseFactionKey(string key, out byte factionId, out string typeKey)
        {
            factionId = 0;
            typeKey = null;

            int bar = key.IndexOf('|');
            if (bar <= 0 || bar >= key.Length - 1) return false;

            string prefix = key.Substring(0, bar);
            byte parsed;
            if (!byte.TryParse(prefix, out parsed)) return false;

            factionId = parsed;
            typeKey = key.Substring(bar + 1);
            return !string.IsNullOrEmpty(typeKey);
        }

        private static string MakeKey(byte factionId, string typeKey)
        {
            return factionId.ToString() + "|" + typeKey;
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                {
                    ModConfig.LogError("UnitAssetBindings.Save: modDirectory unresolved, skipping save (valid for this session only)");
                    return;
                }

                WriteBindingsToFile(_filePath, _bindings, _anyFactionBindings);

                ModConfig.Log("UnitAssetBindings.Save: saved " + _bindings.Count +
                    " per-faction + " + _anyFactionBindings.Count + " all-factions entries to '" + _filePath + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.Save error: " + e);
            }
        }

        /// <summary>Task70: Core serializer (shared helper for Save and
        /// UnitAssetBindingsPresets.SaveToSlot). See the class-level comment for the file format.
        /// Exceptions are expected to be caught by the caller's try/catch (this method itself does not
        /// swallow them).</summary>
        private static void WriteBindingsToFile(string path, Dictionary<string, Binding> bindings, Dictionary<string, Binding> anyFactionBindings)
        {
            // File.WriteAllLines(path, lines, encoding) is an overload added in .NET 4.0 and later and
            // is not guaranteed to exist in this project's TargetFrameworkVersion v3.5 environment, so
            // we explicitly use StreamWriter, which has existed since .NET 1.1 (existing code such as
            // WarStateSerializer only goes as far as File.ReadAllText/File.Exists; for writes we choose
            // the more conservative path).
            using (StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                // Legacy / all-factions-common lines are written back in the key format they were
                // loaded with (no factionId prefix). The value is always normalized to the new
                // kind-prefixed format.
                foreach (KeyValuePair<string, Binding> kv in anyFactionBindings)
                {
                    writer.WriteLine(kv.Key + "=" + AssetKindUtil.ToPrefix(kv.Value.Kind) + ":" + kv.Value.Name);
                }
                // Per-faction lines already have keys in "factionId|typeKey" format.
                foreach (KeyValuePair<string, Binding> kv in bindings)
                {
                    writer.WriteLine(kv.Key + "=" + AssetKindUtil.ToPrefix(kv.Value.Kind) + ":" + kv.Value.Name);
                }
            }
        }
    }
}
