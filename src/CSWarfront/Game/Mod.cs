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

        public string Description
        {
            get
            {
                LocaleLoader.EnsureLoaded(); // Task113: earliest UI string the game requests
                return WarfrontStrings.Mod_Description;
            }
        }

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
                LocaleLoader.EnsureLoaded(); // Task113: apply the locale before any label is built

                // Task138 (Workshop request: "split the mod settings into multiple pages, it's pretty
                // cluttered"): the eight groups are dealt onto tabbed pages. The two asset selectors get
                // a page each, because those are where players actually spend time and they used to sit
                // at the bottom of a long scroll. Grouping otherwise follows what a player is trying to
                // do — set up a game, arrange the factions, choose buildings, choose models, bind keys,
                // adjust sound — not which class happens to build the controls.
                OptionsTabs.Begin(helper);

                OptionsTabs.Page(WarfrontStrings.OptionsTab_General);
                UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_BasePlacementGroup);
                group.AddDropdown(
                    WarfrontStrings.Options_BuildFactionDropdown,
                    WarfrontSettings.FactionNames,
                    WarfrontSettings.BuildFactionId,
                    i => WarfrontSettings.SetBuildFactionId(i));
                AddInvasionEventsUI(helper); // Task94: external invasion events (Workshop comment request)
                AddBattlefieldUI(helper);    // Task139: how the city reacts to fighting in it

                OptionsTabs.Page(WarfrontStrings.OptionsTab_Factions);
                OptionsRelationsPage.Build(helper);
                AddFactionIconUI(helper);

                OptionsTabs.Page(WarfrontStrings.OptionsTab_Buildings);
                OptionsBaseBuildingPage.Build(helper); // Task74: scheme where building the designated building makes it function as a base of that kind

                OptionsTabs.Page(WarfrontStrings.OptionsTab_Models);
                OptionsModelAssignPage.Build(helper);

                OptionsTabs.Page(WarfrontStrings.OptionsTab_Controls);
                AddUnitCommandHotkeyUI(helper);

                OptionsTabs.Page(WarfrontStrings.OptionsTab_Audio);
                AddSoundUI(helper);

                OptionsTabs.End();
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
            UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_InvasionGroup);
            group.AddCheckbox(WarfrontStrings.Options_InvasionEnable, WarfrontSettings.InvasionEventsEnabled,
                v => WarfrontSettings.InvasionEventsEnabled = v);
            group.AddDropdown(WarfrontStrings.Options_InvasionFrequency,
                new[] { WarfrontStrings.Options_InvasionFreqLow, WarfrontStrings.Options_InvasionFreqMedium, WarfrontStrings.Options_InvasionFreqHigh },
                WarfrontSettings.InvasionFrequencyIndex,
                i => { if (i >= 0 && i <= 2) WarfrontSettings.InvasionFrequencyIndex = i; });
        }

        /// <summary>Task48: hotkey binding UI for command orders to box-selected units (free advance /
        /// hold / rally-and-wait). Same pattern as the key-binding dropdowns in
        /// MissileDisaster.ModSettings (the selected value is managed as an index into the KeyOptions
        /// array). WarfrontSettings is memory-only (see the comment at the top of that class), so here
        /// we bypass GameSettings/SavedInt entirely and just assign the chosen KeyCode straight to the
        /// property.</summary>
        /// <summary>Task139 (Workshop request from lilMobStick: "civilian buildings should probably
        /// become abandoned... whenever fighting starts in their proximity"): while infantry hold a city
        /// building, it is shown as abandoned — the residents have fled. It goes back to normal when they
        /// leave, and the state is never written into the save, so this is a purely visual consequence of
        /// the battle. Offered as a toggle because it does change how the player's city looks.</summary>
        private static void AddBattlefieldUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_BattlefieldGroup);
            group.AddCheckbox(WarfrontStrings.Options_GarrisonAbandons, WarfrontSettings.GarrisonAbandonsBuildings,
                v => WarfrontSettings.GarrisonAbandonsBuildings = v);
            group.AddDropdown(WarfrontStrings.Options_BattleDamageDropdown,
                WarfrontSettings.BattleDamageNames(), WarfrontSettings.BattleDamageIndex,
                i => WarfrontSettings.BattleDamageIndex = i); // Task146
        }

        private static void AddUnitCommandHotkeyUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_UnitCommandsGroup);

            string[] keyNames = new string[WarfrontSettings.KeyOptions.Length];
            for (int i = 0; i < WarfrontSettings.KeyOptions.Length; i++)
                keyNames[i] = WarfrontSettings.KeyOptions[i].ToString();

            // Task76: ON/OFF toggle key for unit selection mode. Drag-based box selection only works
            // while it is ON (single-click selection is always active; see Game/UI/UnitBoxSelection).
            group.AddDropdown(WarfrontStrings.Options_SelectionModeKey,
                keyNames, IndexOf(WarfrontSettings.SelectionModeKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.SelectionModeKey = WarfrontSettings.KeyOptions[i]; });

            // Task102: open/close key for the military build panel (one-click placement of the 9 military building kinds).
            group.AddDropdown(WarfrontStrings.Options_BuildPanelKey,
                keyNames, IndexOf(WarfrontSettings.BuildPanelKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.BuildPanelKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown(WarfrontStrings.Options_FreeAdvanceKey,
                keyNames, IndexOf(WarfrontSettings.FreeAdvanceKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.FreeAdvanceKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown(WarfrontStrings.Options_HoldKey,
                keyNames, IndexOf(WarfrontSettings.HoldKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.HoldKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown(WarfrontStrings.Options_RallyKey,
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
            UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_FactionIconsGroup);
            group.AddCheckbox(
                WarfrontStrings.Options_FactionIconsToggle,
                WarfrontSettings.ShowFactionIcons,
                v => WarfrontSettings.ShowFactionIcons = v);
        }

        /// <summary>Task51: volume slider and mute toggle for per-branch firing/kill sounds.
        /// Memory-only like WarfrontSettings (resetting to the defaults = 50% / mute OFF across
        /// sessions is acceptable, MVP).</summary>
        private static void AddSoundUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup(WarfrontStrings.Options_SoundGroup);
            group.AddSlider(
                WarfrontStrings.Options_SoundVolume,
                0f, 100f, 1f, WarfrontSettings.SoundVolume,
                v => WarfrontSettings.SoundVolume = (int)v);
            group.AddCheckbox(
                WarfrontStrings.Options_SoundMute,
                WarfrontSettings.SoundMuted,
                v => WarfrontSettings.SoundMuted = v);
        }
    }
}
