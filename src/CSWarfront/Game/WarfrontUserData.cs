using System;
using System.IO;
using ColossalFramework.IO;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task137 (Workshop reports "the recognition of the assets keeps resetting" — lilMobStick — and
    /// "the trenches and the bunker are missing even though I subscribed" — siddyskylines1989): where
    /// the mod keeps the player's own settings.
    ///
    /// They used to live in the mod's own folder (base-buildings.txt, unit-assets.txt,
    /// unit-assets-set&lt;n&gt;.txt, unit-stats.xml). For a Workshop subscription that folder belongs to
    /// Steam: every time the item is updated Steam replaces its contents with the published payload, so
    /// each update silently reset the player's asset assignments. Worse, if a settings file is ever
    /// present in the payload — which is exactly what happened — every subscriber has their own
    /// assignments overwritten by the author's, pointing at assets that only exist on the author's
    /// machine, which is precisely the "not recognized / missing" symptom.
    ///
    /// Settings therefore live under the game's own user directory, which no mod update can touch:
    ///   %LOCALAPPDATA%\Colossal Order\Cities_Skylines\CSWarfront\
    /// (DataLocation.localApplicationData — the same place the game keeps its .cgs settings).
    ///
    /// Migration: a settings file still sitting in the mod folder is adopted once, but only for a local
    /// install (Addons\Mods). Files in a Workshop folder are Steam's copy of the published payload, not
    /// the player's own work, and importing one would re-apply the very corruption this change exists to
    /// stop.
    /// </summary>
    internal static class WarfrontUserData
    {
        private const string FolderName = "CSWarfront";

        /// <summary>Marker inside a mod path that means "this is Steam's copy, not the player's".</summary>
        private const string WorkshopPathMarker = "workshop";

        private static string _directory;
        private static bool _resolved;

        /// <summary>The settings directory, created on demand. Null when it cannot be resolved or
        /// created, in which case callers keep their existing "in-memory only" behaviour.</summary>
        public static string Directory
        {
            get
            {
                if (_resolved) return _directory;
                _resolved = true;
                try
                {
                    string root = DataLocation.localApplicationData;
                    if (string.IsNullOrEmpty(root))
                    {
                        ModConfig.LogError("WarfrontUserData: localApplicationData unavailable; settings will not be saved");
                        return null;
                    }
                    string dir = Path.Combine(root, FolderName);
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    _directory = dir;
                }
                catch (Exception e)
                {
                    ModConfig.LogError("WarfrontUserData: could not prepare the settings directory: " + e);
                    _directory = null;
                }
                return _directory;
            }
        }

        /// <summary>The path this settings file should be read from and written to, migrating a
        /// pre-Task137 copy out of a local mod folder on the way. Null when no writable location could be
        /// resolved.</summary>
        public static string ResolvePath(string fileName, string modDirectory)
        {
            string dir = Directory;
            if (string.IsNullOrEmpty(dir)) return null;

            string path = Path.Combine(dir, fileName);
            TryMigrate(fileName, modDirectory, path);
            return path;
        }

        private static void TryMigrate(string fileName, string modDirectory, string destination)
        {
            try
            {
                if (File.Exists(destination)) return;            // already migrated, or written since
                if (string.IsNullOrEmpty(modDirectory)) return;
                if (IsWorkshopPath(modDirectory)) return;        // Steam's copy of the payload; never adopt it

                string legacy = Path.Combine(modDirectory, fileName);
                if (!File.Exists(legacy)) return;

                File.Copy(legacy, destination);
                ModConfig.Log("WarfrontUserData: migrated '" + fileName + "' from the mod folder to '"
                    + destination + "' (mod updates can no longer overwrite it)");
            }
            catch (Exception e)
            {
                // A failed migration only costs the old values; never let it stop loading.
                ModConfig.LogError("WarfrontUserData: could not migrate '" + fileName + "': " + e);
            }
        }

        private static bool IsWorkshopPath(string modDirectory)
        {
            return modDirectory.Replace('/', '\\').ToLowerInvariant().Contains(WorkshopPathMarker);
        }
    }
}
