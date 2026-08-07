using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using CSWarfront.Game.Audio;
using CSWarfront.Game.Models;
using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// Initializes the static state of MilitaryManager/BasePlacementWatcher in step with the level
    /// (game session) lifecycle. CS auto-detects subclasses of LoadingExtensionBase, so no explicit
    /// registration is needed (same as WarfrontThreadingExtension).
    /// Resetting in OnLevelUnloading (called when returning to the main menu / switching to another
    /// save) prevents session state from carrying over into the next game in the
    /// restore-save-then-start-new-game transition within the same process (Task16 review Important).
    /// No heavy work is done here.
    /// </summary>
    public class WarfrontLoadingExtension : LoadingExtensionBase
    {
        /// <summary>
        /// Only in playable modes (NewGame/LoadGame and the corresponding scenario-derived modes) do we
        /// start subscribing to base-building placement/demolition events (not needed in the
        /// asset/theme/map editors, etc.). The LoadMode members were verified against ICities.dll
        /// (research-power-tab-building.md §3).
        ///
        /// Task82: the duplicated electricity-tab prefab (WarfrontBasePrefab) used to be registered at
        /// runtime here, but it was removed when we consolidated on the Options-designated-building
        /// (BaseBuildingDesignation) approach (the duplicated-prefab mechanism itself was deleted.
        /// Buildings using the old clone prefab that remain in existing saves are no longer registered
        /// and thus not recognized as bases — CS itself merely treats them as unknown prefabs, and
        /// loading does not crash).
        /// </summary>
        public override void OnLevelLoaded(LoadMode mode)
        {
            try
            {
                if (mode == LoadMode.NewGame || mode == LoadMode.LoadGame ||
                    mode == LoadMode.NewGameFromScenario)
                {
                    LoadModAssets(); // Task36: unit model assignments / Task51: firing & kill sounds / Task57: default models

                    // Task109: now that prefabs are all in place, detect (by name) the subscribed
                    // building assets intended for CS:WARFRONT and use them as defaults for base kinds
                    // that have no assignment (manual assignments always take precedence).
                    BaseBuildingDesignation.ApplyAutoDetected(BaseBuildingAutoAssign.Detect());

                    // Task109: resolve movement sounds (loop sounds borrowed from CS's own vehicle effects).
                    Audio.EngineSounds.ResolveAll();

                    BasePlacementWatcher.Subscribe();
                }
            }
            catch (System.Exception e) { ModConfig.LogError("OnLevelLoaded: " + e); }
        }

        public override void OnLevelUnloading()
        {
            try
            {
                // Unsubscribe from events first, then clear the pending lists etc. via
                // MilitaryManager.Reset() (this order avoids an event firing after Reset and being
                // queued onto pending).
                BasePlacementWatcher.Unsubscribe();
                MilitaryManager.Reset();
                UI.MilitaryBuildPanel.Reset(); // Task102: do not carry over references to destroyed UI
                UI.TrenchLineTargeting.Reset(); // Task106: do not carry over targeting state
                Audio.EngineSounds.Reset();     // Task109: re-resolve clips in the next session
            }
            catch (System.Exception e) { ModConfig.LogError("OnLevelUnloading: " + e); }
        }

        /// <summary>
        /// Task36: loads/initializes UnitAssetBindings (TypeKey → subscribed prop name assignments),
        /// Task51: WarfrontSounds (loading firing/kill sound wavs), and Task74: BaseBuildingDesignation
        /// (base kind → designated building asset name assignments), each from the mod directory.
        /// This is done here rather than in Mod.cs (IUserMod.OnEnabled, the same pattern as
        /// MissileDisaster) because this class already has the OnLevelLoaded timing in a playable
        /// LoadMode, and loading assets at that same timing is sufficient. If modPath cannot be
        /// obtained, we just log and continue; both operate in-memory only (the UnitAssetBindings
        /// assignments and WarfrontSounds audio are unavailable, but loading itself is not blocked).
        /// </summary>
        private static void LoadModAssets()
        {
            try
            {
                PluginManager.PluginInfo info =
                    Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly());
                if (info == null || string.IsNullOrEmpty(info.modPath))
                {
                    ModConfig.LogError("LoadModAssets: could not get modPath from PluginManager (running in-memory only)");
                }
                string modPath = info != null ? info.modPath : null;
                UnitAssetBindings.Load(modPath);
                BaseBuildingDesignation.Load(modPath); // Task74: assignments of base building assets designated in Options
                WarfrontSounds.Initialize(modPath); // Task51
                WarfrontModelProvider.Initialize(modPath); // Task57
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LoadModAssets error: " + e);
            }
        }
    }
}
