using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using ICities;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task49: "Faction Relations" subpage built directly inside Mod Options (Game/Mod.cs,
    /// OnSettingsUI). For every distinct pair of the 5 factions (the 10 combinations
    /// 0-1, 0-2, 0-3, 0-4, 1-2, 1-3, 1-4, 2-3, 2-4, 3-4), one row of hostile/neutral/allied
    /// dropdowns is laid out. Values read/write Core.WarState.Relations directly via
    /// MilitaryManager.TryGetRelation/TrySetRelation (Relations is already persisted for all 25
    /// pairs by the existing serializer (format v4), so changes made here are persisted with the
    /// save as-is. There is no data-format change).
    ///
    /// This page can also be opened from the main menu (MilitaryManager.State is null, no city
    /// loaded yet). In that case TryGetRelation/TrySetRelation return false (there is no WarState
    /// to read or write), so no exception is thrown here; the dropdowns are simply disabled with
    /// isEnabled=false while still displaying "Hostile", and a note label explaining the situation
    /// is shown.
    ///
    /// Same convention as OptionsModelAssignPage: all methods are main-thread only (they call
    /// Unity UI APIs).
    ///
    /// Task52 bug fix: the root cause of the "faction relations cannot be changed from Options"
    /// defect was that CS's OptionsMainPanel (Assembly-CSharp.dll, verified by decompiling with
    /// ILSpy) calls each mod's OnSettingsUI not "every time the Options screen is opened" but
    /// exactly once in OptionsMainPanel.Awake() (= when the Options screen prefab is first
    /// instantiated, normally at the main menu before a city is loaded), and afterwards rebuilds
    /// only on a locale change (OnLocaleChanged) or a mod enable/disable change (RefreshPlugins,
    /// eventPluginsChanged/eventPluginsStateChanged). In other words, Build() (= this OnSettingsUI
    /// call) runs exactly once while MilitaryManager.State is still null (no city loaded), and the
    /// 10 dropdowns get pinned to isEnabled=false based on stateReady(false) at that moment.
    /// Afterwards, even once a city is loaded and State becomes available, merely reopening the
    /// Options screen never calls Build() again (as stated above, it is only re-run on
    /// Awake/locale change/mod enable-disable change), so the dropdowns stay disabled = to the
    /// user it keeps looking as if "they cannot be changed from Hostile".
    /// The old implementation, including this very comment, was written under the incorrect
    /// assumption that "Build() may be re-run every time the Options screen is opened"
    /// (OptionsModelAssignPage.cs had a comment with the same incorrect assumption, but that one
    /// is out of scope for this task).
    ///
    /// Fix: Unity/CS UIComponent recursively propagates OnVisibilityChanged() down to descendants
    /// when an ancestor's isVisible changes, firing eventVisibilityChanged at each level
    /// (UIComponent, verified by decompiling ColossalManaged.dll). When switching tabs on the
    /// Options screen, UITabContainer.SelectPageByIndex merely toggles isVisible true/false on the
    /// single selected child (= the single UIComponent containing all of this mod's groups), but
    /// that propagates down to the "Faction Relations" group's panel, so subscribing to the group
    /// panel's own eventVisibilityChanged reliably hooks "every time this mod's Options tab is
    /// selected" (independent of whether Build() itself is re-run).
    /// On each such event, RefreshFromState() (1) re-reads the selected values of the 10 dropdowns
    /// from the current State, (2) syncs isEnabled to stateReady, and (3) updates the note label.
    /// As a result, even if "the stale UI built once with no city loaded" remains, it is updated
    /// to the correct state the moment this Options tab is next opened after loading a city.
    /// </summary>
    internal static class OptionsRelationsPage
    {
        private const string GroupTitle = "Faction Relations";
        // Task59: appended Nemesis at the end. Kept consistent with the declaration order of the Relation enum (Hostile, Neutral, Allied, Nemesis).
        private static readonly string[] RelationLabels = { "Hostile", "Neutral", "Allied", "Nemesis" };
        private static readonly Relation[] RelationValues = { Relation.Hostile, Relation.Neutral, Relation.Allied, Relation.Nemesis };

        // The 10 rows of dropdowns created during Build() and their corresponding (a,b) pairs.
        // Kept so the selected values can be re-synced when the "Reset All to Hostile" button
        // (OnResetAllClick) is pressed.
        private static readonly List<UIDropDown> _dropdowns = new List<UIDropDown>();
        private static readonly List<byte> _pairA = new List<byte>();
        private static readonly List<byte> _pairB = new List<byte>();

        // Task59: relation dropdowns for KAIJU/Alien. Only when the Godzilla Disaster / Alien
        // Invasion mods are actually installed, up to 5 rows each (one per faction) are built
        // (determined via ExternalThreatBridge.IsGodzillaModPresent / IsAlienModPresent; checking
        // once at Build() time is sufficient = the mod installation state cannot change without a
        // game restart, so there is no temporal change like faction relations (State) being
        // "disabled while no city is loaded").
        private static readonly List<UIDropDown> _threatDropdowns = new List<UIDropDown>();
        private static readonly List<byte> _threatFactionId = new List<byte>();
        private static readonly List<ThreatKind> _threatKind = new List<ThreatKind>();

        private static UILabel _noteLabel;

        /// <summary>Called from Mod.OnSettingsUI. Builds the "Faction Relations" group under the given helper.</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                _dropdowns.Clear();
                _pairA.Clear();
                _pairB.Clear();
                _threatDropdowns.Clear();
                _threatFactionId.Clear();
                _threatKind.Clear();

                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;

                string[] names = WarfrontSettings.FactionNames;
                bool stateReady = MilitaryManager.State != null;

                for (byte a = 0; a < WarfrontSettings.MaxFactions; a++)
                {
                    for (byte b = (byte)(a + 1); b < WarfrontSettings.MaxFactions; b++)
                    {
                        byte pairA = a; // guard against closure capture of the loop variable (for reuses one variable, so always copy to a local)
                        byte pairB = b;

                        Relation current;
                        if (!MilitaryManager.TryGetRelation(pairA, pairB, out current)) current = Relation.Hostile;

                        string label = names[pairA] + " ↔ " + names[pairB]; // "Red ↔ Blue"
                        UIDropDown dd = group.AddDropdown(label, RelationLabels, IndexOfRelation(current),
                            i => OnRelationChanged(pairA, pairB, i)) as UIDropDown;

                        if (dd != null)
                        {
                            dd.isEnabled = stateReady;
                            _dropdowns.Add(dd);
                            _pairA.Add(pairA);
                            _pairB.Add(pairB);
                        }
                    }
                }

                // Task59: relations with KAIJU/Alien. Adds rows only for the installed mods (0/1/2 of them).
                bool godzillaPresent = ExternalThreatBridge.IsGodzillaModPresent;
                bool alienPresent = ExternalThreatBridge.IsAlienModPresent;

                if (godzillaPresent) BuildThreatRows(group, names, ThreatKind.Kaiju, "KAIJU", stateReady);
                if (alienPresent) BuildThreatRows(group, names, ThreatKind.Alien, "Alien", stateReady);

                object resetButtonObj = group.AddButton("Reset All to Hostile", OnResetAllClick);

                if (groupPanel != null)
                {
                    _noteLabel = groupPanel.AddUIComponent<UILabel>();
                    _noteLabel.textScale = 0.8f;
                    _noteLabel.textColor = new Color32(255, 190, 120, 255);
                    _noteLabel.wordWrap = true;
                    _noteLabel.autoHeight = true;
                    _noteLabel.width = 500f;
                    _noteLabel.text = stateReady
                        ? ""
                        : "No city is loaded, so faction relations cannot be edited. Please open this again after loading a city.";

                    // Task52 bug fix: CS does not re-run this whole mod's OnSettingsUI every time the
                    // Options screen is opened (see the class-header comment). Instead, subscribe to
                    // eventVisibilityChanged, which fires every time this mod's tab is selected inside
                    // Options (= an ancestor component's isVisible flips to true and that propagates
                    // down to this group panel), and re-sync the dropdowns' selected values,
                    // enabled/disabled state, and the note label based on MilitaryManager.State at
                    // that moment. This resolves the "disabled once with no city loaded and pinned
                    // that way" defect without relying on Build() itself being re-run.
                    groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.Build error: " + e);
            }
        }

        /// <summary>Task59: for the given ThreatKind, builds one "faction name ↔ display name" row per
        /// faction (WarfrontSettings.MaxFactions rows). Only called when the caller (Build) has already
        /// confirmed the mod is installed.</summary>
        private static void BuildThreatRows(UIHelperBase group, string[] names, ThreatKind kind, string displayName, bool stateReady)
        {
            for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
            {
                byte factionId = f; // guard against closure capture
                ThreatKind capturedKind = kind;

                Relation current;
                if (!MilitaryManager.TryGetThreatRelation(factionId, capturedKind, out current)) current = Relation.Hostile;

                string label = names[factionId] + " ↔ " + displayName;
                UIDropDown dd = group.AddDropdown(label, RelationLabels, IndexOfRelation(current),
                    i => OnThreatRelationChanged(factionId, capturedKind, i)) as UIDropDown;

                if (dd != null)
                {
                    dd.isEnabled = stateReady;
                    _threatDropdowns.Add(dd);
                    _threatFactionId.Add(factionId);
                    _threatKind.Add(capturedKind);
                }
            }
        }

        private static int IndexOfRelation(Relation r)
        {
            for (int i = 0; i < RelationValues.Length; i++)
                if (RelationValues[i] == r) return i;
            return 0;
        }

        private static void OnRelationChanged(byte a, byte b, int selectedIndex)
        {
            try
            {
                if (selectedIndex < 0 || selectedIndex >= RelationValues.Length) return;
                MilitaryManager.TrySetRelation(a, b, RelationValues[selectedIndex]);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnRelationChanged error: " + e);
            }
        }

        private static void OnThreatRelationChanged(byte factionId, ThreatKind kind, int selectedIndex)
        {
            try
            {
                if (selectedIndex < 0 || selectedIndex >= RelationValues.Length) return;
                MilitaryManager.TrySetThreatRelation(factionId, kind, RelationValues[selectedIndex]);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnThreatRelationChanged error: " + e);
            }
        }

        /// <summary>eventVisibilityChanged handler for the group panel (Task52 bug fix).
        /// Calls RefreshFromState only when isVisible==true (i.e. only when this mod's tab is
        /// selected/shown inside Options). Does nothing on hiding (false).</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>Re-syncs the selected values and isEnabled of the 10 dropdown rows plus the note
        /// label based on the current MilitaryManager.State (Task52 bug fix). Shared routine used both
        /// when this Options tab is reopened after loading a city, and for the re-sync after the
        /// "Reset All to Hostile" button is pressed.
        /// Exceptions are swallowed here so they never propagate from a UI callback into the game loop.</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = MilitaryManager.State != null;

                for (int i = 0; i < _dropdowns.Count; i++)
                {
                    UIDropDown dd = _dropdowns[i];
                    if (dd == null) continue;

                    dd.isEnabled = stateReady;

                    Relation current;
                    if (!MilitaryManager.TryGetRelation(_pairA[i], _pairB[i], out current)) current = Relation.Hostile;
                    int idx = IndexOfRelation(current);
                    // Writing back to selectedIndex when the value has not changed would needlessly
                    // re-fire OnRelationChanged via eventSelectedIndexChanged (only extra log noise,
                    // no real harm, but avoid it).
                    if (dd.selectedIndex != idx) dd.selectedIndex = idx;
                }

                // Task59: re-sync the KAIJU/Alien rows under the same convention (only the rows already
                // built = the mod-presence determination is fixed at Build() time, so the number of
                // dropdowns itself never grows or shrinks here).
                for (int i = 0; i < _threatDropdowns.Count; i++)
                {
                    UIDropDown dd = _threatDropdowns[i];
                    if (dd == null) continue;

                    dd.isEnabled = stateReady;

                    Relation current;
                    if (!MilitaryManager.TryGetThreatRelation(_threatFactionId[i], _threatKind[i], out current)) current = Relation.Hostile;
                    int idx = IndexOfRelation(current);
                    if (dd.selectedIndex != idx) dd.selectedIndex = idx;
                }

                if (_noteLabel != null)
                {
                    _noteLabel.text = stateReady
                        ? ""
                        : "No city is loaded, so faction relations cannot be edited. Please open this again after loading a city.";
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.RefreshFromState error: " + e);
            }
        }

        /// <summary>"Reset All to Hostile" button. Calls MilitaryManager.TryResetRelationsToAllHostile
        /// (a thin wrapper over Core.RelationPresets.ApplyAllHostile) and then re-syncs the selection
        /// display of the 10 dropdown rows via RefreshFromState to the current values (which should now
        /// all be Hostile). If State is uninitialized, does nothing (the wrapper just returns false).</summary>
        private static void OnResetAllClick()
        {
            try
            {
                if (!MilitaryManager.TryResetRelationsToAllHostile()) return;
                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnResetAllClick error: " + e);
            }
        }
    }
}
