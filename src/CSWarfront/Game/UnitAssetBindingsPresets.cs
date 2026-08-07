using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task70: The portion of UnitAssetBindings split out to hold only "reset all"
    /// (<see cref="ClearAll"/>) and "preset registration (1-3)"
    /// (<see cref="SaveToSlot"/>/<see cref="LoadFromSlot"/>/<see cref="SlotExists"/>), as a partial
    /// class (due to the 500-line limit on UnitAssetBindings.cs; same policy as
    /// UnitAssetBindingsBaseTypes).
    /// The fields (_bindings/_anyFactionBindings/_filePath/_modDirectory) and file I/O helpers
    /// (ParseFileInto/WriteBindingsToFile/Save) are all declared on the UnitAssetBindings.cs side,
    /// but a partial class shares even private members across all parts, so they can be used as-is.
    ///
    /// Preset slot files: saved directly under modDirectory as "unit-assets-set&lt;slot&gt;.txt"
    /// (slot=1..3), in exactly the same line format as the main file (unit-assets.txt) (no separate
    /// format is invented; parsing/serialization share ParseFileInto/WriteBindingsToFile).
    ///
    /// Loading (LoadFromSlot) replaces the entire table (REPLACE semantics): all current assignments
    /// (both per-faction and all-factions-common (legacy)) are discarded and replaced with only the
    /// slot file's contents. It is not a merge (i.e. not a scheme where the slot's contents are
    /// overwritten/added on top of the existing assignments). After replacement, unit-assets.txt is
    /// also updated immediately (Save() is called), so this state persists across the next level load.
    ///
    /// If the slot file does not exist or is corrupted (exception during parsing), false is returned
    /// and neither the current in-memory state nor unit-assets.txt is modified in any way (the
    /// two-stage approach — parse into temporary dictionaries first and apply to
    /// _bindings/_anyFactionBindings only on success — means a parse failure never leaves a
    /// half-updated state).
    ///
    /// No method throws exceptions outward (callers are UI = main-thread event handlers, so a failure
    /// here must not stop the game loop).
    /// </summary>
    internal static partial class UnitAssetBindings
    {
        private const int MinSlot = 1;
        private const int MaxSlot = 3;
        private const string SlotFileNamePrefix = "unit-assets-set";
        private const string SlotFileNameSuffix = ".txt";

        /// <summary>Clears all assignments (all keys, including per-faction, all-factions-common
        /// (legacy), and base-type keys) and reverts to the default models (the bulk version of the
        /// individual "reset to default" = <see cref="Clear"/>). Saves immediately.</summary>
        /// <returns>The number of entries actually removed (per-faction + all-factions-common total).
        /// If there were originally 0 entries, saving is skipped and 0 is returned.</returns>
        public static int ClearAll()
        {
            try
            {
                int removed = _bindings.Count + _anyFactionBindings.Count;
                if (removed == 0) return 0;

                _bindings.Clear();
                _anyFactionBindings.Clear();
                Save();

                ModConfig.Log("UnitAssetBindings.ClearAll: reset all bindings (" + removed + " entries) to default models");
                return removed;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.ClearAll error: " + e);
                return 0;
            }
        }

        /// <summary>Saves the entire current assignment table to the file of the given slot (1..3)
        /// (a separate file from the existing unit-assets.txt; like a "gear loadout", it can be
        /// restored later with <see cref="LoadFromSlot"/>).</summary>
        /// <returns>Whether it succeeded. Returns false if slot is out of range, modDirectory is
        /// unresolved, or an I/O error occurs (the current assignments are not affected at
        /// all).</returns>
        public static bool SaveToSlot(int slot)
        {
            try
            {
                if (!IsValidSlot(slot))
                {
                    ModConfig.LogError("UnitAssetBindings.SaveToSlot: invalid slot=" + slot + " (only 1-3 are valid)");
                    return false;
                }
                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("UnitAssetBindings.SaveToSlot: modDirectory unresolved, cannot save (valid for this session only)");
                    return false;
                }

                string path = SlotPath(slot);
                WriteBindingsToFile(path, _bindings, _anyFactionBindings);

                ModConfig.Log("UnitAssetBindings.SaveToSlot: saved current bindings (per-faction " + _bindings.Count + " + all-factions " +
                    _anyFactionBindings.Count + ") to preset " + slot + " ('" + path + "')");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.SaveToSlot(slot=" + slot + ") error: " + e);
                return false;
            }
        }

        /// <summary>Loads the entire assignment table from the file of the given slot (1..3) and
        /// replaces the current table wholesale (REPLACE, not a merge). After replacement,
        /// unit-assets.txt is updated as well.</summary>
        /// <returns>Whether it succeeded. Returns false — and leaves the current assignments entirely
        /// unchanged — if slot is out of range, modDirectory is unresolved, the slot file does not
        /// exist, or it is corrupted (exception during parsing).</returns>
        public static bool LoadFromSlot(int slot)
        {
            try
            {
                if (!IsValidSlot(slot))
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: invalid slot=" + slot + " (only 1-3 are valid)");
                    return false;
                }
                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: modDirectory unresolved, cannot load");
                    return false;
                }

                string path = SlotPath(slot);
                if (!File.Exists(path))
                {
                    ModConfig.Log("UnitAssetBindings.LoadFromSlot: preset " + slot + " ('" + path + "') does not exist, skipping load (keeping current bindings)");
                    return false;
                }

                // Parse into temporary dictionaries first, and apply to the real tables only on
                // success (so a corrupted file cannot drag down the current state. ParseFileInto is
                // expected to propagate exceptions from File.ReadAllLines etc. straight to the caller,
                // so we catch here and fail safe).
                Dictionary<string, Binding> newBindings = new Dictionary<string, Binding>();
                Dictionary<string, Binding> newAnyFactionBindings = new Dictionary<string, Binding>();
                int parsed;
                try
                {
                    ParseFileInto(path, newBindings, newAnyFactionBindings, out parsed);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: failed to load preset " + slot + " ('" + path + "'), keeping current bindings: " + e);
                    return false;
                }

                // Only once we get here is the entire table replaced (REPLACE semantics).
                _bindings.Clear();
                _anyFactionBindings.Clear();
                foreach (KeyValuePair<string, Binding> kv in newBindings) _bindings[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, Binding> kv in newAnyFactionBindings) _anyFactionBindings[kv.Key] = kv.Value;

                Save(); // also update unit-assets.txt (so this preset's contents persist across the next level load)

                ModConfig.Log("UnitAssetBindings.LoadFromSlot: loaded " + parsed + " entries from preset " + slot +
                    " ('" + path + "') and replaced the entire binding table");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.LoadFromSlot(slot=" + slot + ") error (keeping current bindings): " + e);
                return false;
            }
        }

        /// <summary>Whether the file of the given slot (1..3) exists (used to decide whether to append
        /// "(empty)" to the UI dropdown label, Task70). Also returns false if slot is out of range or
        /// modDirectory is unresolved.</summary>
        public static bool SlotExists(int slot)
        {
            try
            {
                if (!IsValidSlot(slot)) return false;
                if (string.IsNullOrEmpty(_modDirectory)) return false;
                return File.Exists(SlotPath(slot));
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.SlotExists(slot=" + slot + ") error: " + e);
                return false;
            }
        }

        private static bool IsValidSlot(int slot)
        {
            return slot >= MinSlot && slot <= MaxSlot;
        }

        private static string SlotPath(int slot)
        {
            return Path.Combine(_modDirectory, SlotFileNamePrefix + slot + SlotFileNameSuffix);
        }
    }
}
