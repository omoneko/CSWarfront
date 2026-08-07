using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task74: designation store for the "build the building asset specified in Options and that
    /// building functions as a base of that type" scheme. For each base type (<see cref="BaseType"/>,
    /// Army/Navy/AirForce/MissileBase) it holds exactly one entry: the name of the existing building
    /// asset the player designated in Options as "the one that should function as this base".
    ///
    /// Saved to its own dedicated file (&lt;modDir&gt;\base-buildings.txt), fully independent from
    /// UnitAssetBindings (per-faction visual model bindings for units/bases; file: unit-assets.txt).
    /// Reason: this is not a "visual binding" but a placement-recognition rule — "which asset, when
    /// built, becomes a base by itself" — and there is no concept of splitting it per faction
    /// (just like the electricity-tab cloned prefabs, it is always registered as a base belonging to
    /// WarfrontSettings.BuildFactionId; see BasePlacementWatcher.ProcessCreated). There is no need
    /// to drag in UnitAssetBindings' complex Tier/faction/clone fallback machinery, so it was kept
    /// deliberately independent with a simple format of one asset name per type only.
    ///
    /// File format (one line each, UTF-8): "baseType=assetName" (baseType is the enum name of
    /// CSWarfront.Core.BaseType, e.g. "Army=Some Custom Building"). Of the 4 types, only those
    /// actually designated exist as lines (undesignated types have no line at all, and such a type
    /// can still only be placed via the electricity-tab cloned building as before =
    /// coexistence: designations in this class are strictly an additional placement path and do not
    /// change the electricity-tab cloned prefab registration/behavior in any way).
    ///
    /// A corrupt/missing file is always treated as "no designations" and no exception escapes
    /// (a failure here must never stop the load itself; same policy as UnitAssetBindings).
    /// There is no main-thread-only constraint, but all calls are expected to come from the main
    /// thread (Options UI / load processing). BasePlacementWatcher (sim thread) calls only the
    /// read-only methods <see cref="TryGet"/>/<see cref="TryMatch"/> (writes to the Dictionary occur
    /// only during Options UI interaction, restricted to the main thread, with the assumption that
    /// no concurrent writes with the sim thread happen. UnitAssetBindings operates under the same
    /// assumption).
    /// </summary>
    internal static class BaseBuildingDesignation
    {
        private const string FileName = "base-buildings.txt";

        private static readonly Dictionary<BaseType, string> _designations = new Dictionary<BaseType, string>();

        /// <summary>Task109: automatic designations (subscribed CS:WARFRONT building assets detected
        /// by name, <see cref="BaseBuildingAutoAssign"/>). Manual designations (_designations)
        /// always take precedence; this is used as the default only for types with no manual entry.
        /// Not saved to file — it is re-detected every time, so unsubscribing/replacing an asset can
        /// never leave a stale name silently held on to.</summary>
        private static readonly Dictionary<BaseType, string> _auto = new Dictionary<BaseType, string>();

        private static string _filePath;

        /// <summary>Whether any base type has at least one designation. Used by
        /// BasePlacementWatcher for its early-return check "nothing can be done if there is not a
        /// single designated building" (now that Task82 removed the electricity-tab cloned prefab
        /// mechanism, designated buildings are the only base placement path).</summary>
        public static bool HasAny { get { return _designations.Count > 0 || _auto.Count > 0; } }

        /// <summary>Task109: replaces the auto-detection results (once at level load, after prefabs
        /// are available).</summary>
        public static void ApplyAutoDetected(Dictionary<BaseType, string> detected)
        {
            _auto.Clear();
            if (detected == null) return;
            foreach (KeyValuePair<BaseType, string> kv in detected) _auto[kv.Key] = kv.Value;
        }

        /// <summary>Task109: whether this type's value comes from auto-assignment (no manual
        /// designation, only an auto-detected one). Used by the Options UI to display "auto".</summary>
        public static bool IsAutoAssigned(BaseType type)
        {
            return !_designations.ContainsKey(type) && _auto.ContainsKey(type);
        }

        /// <summary>Called once at startup (WarfrontLoadingExtension.LoadModAssets, same spot as
        /// UnitAssetBindings.Load). Not idempotent (re-reads from the file every time).</summary>
        public static void Load(string modDirectory)
        {
            _designations.Clear();
            _filePath = null;

            try
            {
                if (string.IsNullOrEmpty(modDirectory))
                {
                    ModConfig.LogError("BaseBuildingDesignation.Load: modDirectory is empty, running in-memory only (designations will not be saved)");
                    return;
                }

                _filePath = Path.Combine(modDirectory, FileName);
                if (!File.Exists(_filePath))
                {
                    ModConfig.Log("BaseBuildingDesignation.Load: '" + _filePath + "' not found, starting with 0 designations");
                    return;
                }

                string[] lines = File.ReadAllLines(_filePath, Encoding.UTF8);
                int parsed = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq >= line.Length - 1) continue; // ignore if either key or value is empty

                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    if (string.IsNullOrEmpty(value)) continue;

                    BaseType type;
                    if (!TryParseBaseType(key, out type)) continue; // ignore unknown keys (forward compatibility)

                    _designations[type] = value;
                    parsed++;
                }

                ModConfig.Log("BaseBuildingDesignation.Load: loaded " + parsed + " designated building(s) from '" + _filePath + "'");
            }
            catch (Exception e)
            {
                // Corrupt files, access permission errors, etc. continue as "no designations" (never stop the load).
                ModConfig.LogError("BaseBuildingDesignation.Load error (continuing with no designations): " + e);
                _designations.Clear();
            }
        }

        /// <summary>Returns the designated building asset name for <paramref name="type"/>; false if
        /// undesignated. Task109: types with no manual designation fall back to the auto-detected
        /// default (a subscribed CS:WARFRONT asset).</summary>
        public static bool TryGet(BaseType type, out string assetName)
        {
            if (_designations.TryGetValue(type, out assetName)) return true;
            return _auto.TryGetValue(type, out assetName);
        }

        /// <summary>Sets the designated building asset for <paramref name="type"/> and saves immediately.</summary>
        public static void Set(BaseType type, string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return;
            _designations[type] = assetName;
            ModConfig.Log("BaseBuildingDesignation.Set: " + type + " = " + assetName);
            Save();
        }

        /// <summary>Clears the designation for <paramref name="type"/> (reverting to the default =
        /// electricity-tab cloned building only) and saves immediately. Does nothing if no
        /// designation exists (no-op, the Save call is skipped too).</summary>
        public static void Clear(BaseType type)
        {
            if (_designations.Remove(type))
            {
                ModConfig.Log("BaseBuildingDesignation.Clear: cleared designation for " + type + " (only the Electricity tab duplicate building remains usable)");
                Save();
            }
        }

        /// <summary>
        /// Whether a building's Info.name (<paramref name="assetName"/>) matches the designated
        /// building of any base type. Returns that BaseType on a match. Used by
        /// BasePlacementWatcher.ProcessCreated/ReconcileBases, BaseHiddenSync.ApplyPending, and
        /// CoverMapBuilder.Build as the sole path for base identification
        /// (Task82: matching against the electricity-tab cloned prefab = WarfrontBasePrefab.TryMatch
        /// has been removed).
        /// Multiple of the 4 types designating the same asset name is not expected (the UI allows
        /// one asset = one type only), but even if duplicated it merely returns the first type found
        /// rather than throwing.
        /// </summary>
        public static bool TryMatch(string assetName, out BaseType type)
        {
            type = default(BaseType);
            if (string.IsNullOrEmpty(assetName)) return false;

            foreach (KeyValuePair<BaseType, string> kv in _designations)
            {
                if (kv.Value == assetName) { type = kv.Key; return true; }
            }
            // Task109: auto-assigned buildings are also recognized for placement (types overridden
            // by a manual designation match first in the loop above, so precedence is preserved).
            foreach (KeyValuePair<BaseType, string> kv in _auto)
            {
                if (_designations.ContainsKey(kv.Key)) continue;
                if (kv.Value == assetName) { type = kv.Key; return true; }
            }
            return false;
        }

        /// <summary>Task109: previously only the 4 types Army/Navy/AirForce/MissileBase were
        /// interpreted, so fortification designations (Bunker/ArtilleryPost/SupplyDepot/Trench/
        /// CargoStation), even when written to the file by Save, were discarded on the next Load
        /// (= they had to be re-designated after every restart). Interpret BaseType enum names
        /// directly so all types can be restored.</summary>
        private static bool TryParseBaseType(string key, out BaseType type)
        {
            type = default(BaseType);
            if (string.IsNullOrEmpty(key)) return false;
            if (!Enum.IsDefined(typeof(BaseType), key)) return false;
            type = (BaseType)Enum.Parse(typeof(BaseType), key);
            return true;
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                {
                    ModConfig.LogError("BaseBuildingDesignation.Save: modDirectory unresolved, skipping save (valid for this session only)");
                    return;
                }

                // File.WriteAllLines(path, lines, encoding) is not guaranteed to exist in a
                // TargetFrameworkVersion v3.5 environment, so use StreamWriter just like
                // UnitAssetBindings.WriteBindingsToFile.
                using (StreamWriter writer = new StreamWriter(_filePath, false, Encoding.UTF8))
                {
                    foreach (KeyValuePair<BaseType, string> kv in _designations)
                    {
                        writer.WriteLine(kv.Key + "=" + kv.Value);
                    }
                }

                ModConfig.Log("BaseBuildingDesignation.Save: saved " + _designations.Count + " designation(s) to '" + _filePath + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseBuildingDesignation.Save error: " + e);
            }
        }
    }
}
