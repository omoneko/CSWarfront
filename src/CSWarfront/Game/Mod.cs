using System;
using CSWarfront.Game.UI;
using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// Entry point providing the mod info and Mod Options. ICities.IUserMod offers few lifecycle
    /// hooks, so the actual initialization (MilitaryManager.EnsureInitialized) happens in
    /// <see cref="WarfrontThreadingExtension"/>.OnAfterSimulationTick (a ThreadingExtensionBase the
    /// game auto-registers by scanning the assembly; sim thread).
    /// </summary>
    public class Mod : IUserMod
    {
        public string Name => "CS:WARFRONT"; // Task93: user-specified mod title (unified with the Workshop title)
        public string Description =>
            "A tier-based military simulation with 5 factions (land/sea/air, bases, territory, occupation). Building the building designated in Options turns it into a military base.";

        /// <summary>The Mod Options screen (build-target faction selection, plus the model-assignment
        /// UI that Task47 turned into an Options subpage). Auto-detected and called by the game.
        /// Building the model-assignment UI itself is delegated to OptionsModelAssignPage.Build
        /// (Game/UI/OptionsModelAssignPage.cs) (as of Task40 it was a button that opened a floating
        /// panel, but Task47 changed it to a "subpage" style that lays the full set of controls
        /// directly inside an Options group).</summary>
        public void OnSettingsUI(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup("Base placement");
                group.AddDropdown(
                    "Faction to build for (designated base building)",
                    WarfrontSettings.FactionNames,
                    WarfrontSettings.BuildFactionId,
                    i => WarfrontSettings.SetBuildFactionId(i));

                AddUnitCommandHotkeyUI(helper);

                AddInvasionEventsUI(helper); // Task94: external invasion events (Workshop comment request)

                OptionsRelationsPage.Build(helper);

                AddFactionIconUI(helper);

                AddSoundUI(helper);

                OptionsBaseBuildingPage.Build(helper); // Task74: scheme where building the designated building makes it function as a base of that kind

                OptionsModelAssignPage.Build(helper);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OnSettingsUI error: " + e);
            }
        }

        /// <summary>Task94 (Workshop comment request): ON/OFF and frequency of external invasion
        /// events. When ON, raid squads (belonging to an unused faction, automatically hostile to the
        /// defender) spawn at the map edge at random times and attack the nearest base. Aimed at a
        /// playstyle of building your own bases and defending them.</summary>
        private static void AddInvasionEventsUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Invasion events (waves attack from outside the city)");
            group.AddCheckbox("Enable invasion events", WarfrontSettings.InvasionEventsEnabled,
                v => WarfrontSettings.InvasionEventsEnabled = v);
            group.AddDropdown("Invasion frequency",
                new[] { "Low (about every 5 days)", "Medium (about every 2-3 days)", "High (about every day)" },
                WarfrontSettings.InvasionFrequencyIndex,
                i => { if (i >= 0 && i <= 2) WarfrontSettings.InvasionFrequencyIndex = i; });
        }

        /// <summary>Task48: hotkey binding UI for command orders to box-selected units (free advance /
        /// hold / rally-and-wait). Same pattern as the key-binding dropdowns in
        /// MissileDisaster.ModSettings (the selected value is managed as an index into the KeyOptions
        /// array). WarfrontSettings is memory-only (see the comment at the top of that class), so here
        /// we bypass GameSettings/SavedInt entirely and just assign the chosen KeyCode straight to the
        /// property.</summary>
        private static void AddUnitCommandHotkeyUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Unit commands (select units with a box drag, then press)");

            string[] keyNames = new string[WarfrontSettings.KeyOptions.Length];
            for (int i = 0; i < WarfrontSettings.KeyOptions.Length; i++)
                keyNames[i] = WarfrontSettings.KeyOptions[i].ToString();

            // Task76: ON/OFF toggle key for unit selection mode. Drag-based box selection only works
            // while it is ON (single-click selection is always active; see Game/UI/UnitBoxSelection).
            group.AddDropdown("Toggle unit selection mode (drag-box select; single click always works)",
                keyNames, IndexOf(WarfrontSettings.SelectionModeKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.SelectionModeKey = WarfrontSettings.KeyOptions[i]; });

            // Task102: open/close key for the military build panel (one-click placement of the 9 military building kinds).
            group.AddDropdown("Toggle military construction panel",
                keyNames, IndexOf(WarfrontSettings.BuildPanelKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.BuildPanelKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Free advance (march at full speed toward the nearest hostile base)",
                keyNames, IndexOf(WarfrontSettings.FreeAdvanceKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.FreeAdvanceKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Hold (stop in place, still fires at anything in range)",
                keyNames, IndexOf(WarfrontSettings.HoldKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.HoldKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Rally (then right-click a destination; units move there, stop, and fight defensively only)",
                keyNames, IndexOf(WarfrontSettings.RallyKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.RallyKey = WarfrontSettings.KeyOptions[i]; });
        }

        private static int IndexOf(UnityEngine.KeyCode key)
        {
            for (int i = 0; i < WarfrontSettings.KeyOptions.Length; i++)
                if (WarfrontSettings.KeyOptions[i] == key) return i;
            return 0;
        }

        /// <summary>Task49: toggle for the faction icons above units (small spheres, see
        /// Game/UnitVisuals). Memory-only like WarfrontSettings (resetting to the default = ON across
        /// sessions is acceptable, MVP). When turned OFF, already-created spheres are destroyed on the
        /// next UnitVisuals.Sync() (UnitVisuals.Sync checks WarfrontSettings.ShowFactionIcons and
        /// destroys them individually).</summary>
        private static void AddFactionIconUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Faction icons");
            group.AddCheckbox(
                "Show a small faction-colored marker above each unit",
                WarfrontSettings.ShowFactionIcons,
                v => WarfrontSettings.ShowFactionIcons = v);
        }

        /// <summary>Task51: volume slider and mute toggle for per-branch firing/kill sounds.
        /// Memory-only like WarfrontSettings (resetting to the defaults = 50% / mute OFF across
        /// sessions is acceptable, MVP).</summary>
        private static void AddSoundUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Firing sounds");
            group.AddSlider(
                "Sound volume",
                0f, 100f, 1f, WarfrontSettings.SoundVolume,
                v => WarfrontSettings.SoundVolume = (int)v);
            group.AddCheckbox(
                "Mute all firing/kill sounds",
                WarfrontSettings.SoundMuted,
                v => WarfrontSettings.SoundMuted = v);
        }
    }
}
