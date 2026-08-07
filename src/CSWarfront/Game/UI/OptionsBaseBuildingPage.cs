using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using ICities;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task74: "Base Buildings" subpage built directly inside Mod Options (Game/Mod.cs, OnSettingsUI).
    ///
    /// Previously (Task18 onward), bases could only be created by placing a dedicated prefab cloned
    /// into the electricity tab (WarfrontBasePrefab, still visually a wind turbine with a confusing
    /// toolbar thumbnail). This page instead adds a scheme where "if you designate one existing
    /// building asset per base type in Options, then the player simply builds that building anywhere
    /// (from that building's own normal menu) and it automatically functions as a base of that type"
    /// (no visual swapping or Hidden-flag tricks at all; the asset always keeps its native
    /// appearance). Persistence is handled by <see cref="BaseBuildingDesignation"/>
    /// (&lt;modDir&gt;\base-buildings.txt), and the actual recognition (new-placement event ->
    /// logical MilitaryBase registration) is done by BasePlacementWatcher.ProcessCreated.
    ///
    /// In Task81 the electricity-tab clone prefab (WarfrontBasePrefab) was retired as a fallback
    /// placement path from the toolbar (m_availableIn = ItemClass.Availability.None), and in Task82
    /// the clone-prefab machinery itself (registration, runtime cloning, visual swapping, the whole
    /// set) was completely removed. Buildings of the old clone prefab placed in existing saves are no
    /// longer registered as logical bases after this removal (this was accepted by the user's
    /// explicit decision as an abandonment of existing-save compatibility). Base placement now goes
    /// solely through the designation on this page (BaseBuildingDesignation) — base types with no
    /// designation end up with no placement means at all (stated explicitly on this page's hint
    /// label).
    ///
    /// UI composition: follows the same constraint as OptionsModelAssignPage (ICities.UIHelperBase
    /// has no scrollable list, so it is the "narrow down with the search field, then pick from a
    /// dropdown" scheme). The search field and the "Subscribed only" toggle are shared by the 4 rows
    /// (army/navy/air/missile), and each row consists of 3 parts: a "dropdown labeled with the type",
    /// a "current designation" display label, and a "Reset to Default" button (no extra concepts like
    /// apply-copy or per-faction settings = there is a single global designation per base type, so
    /// neither the faction dropdown nor the TypeKey concept is brought in).
    ///
    /// The dropdown selection takes effect immediately (unlike OptionsModelAssignPage's "Apply"
    /// button, each row on this page is a single independent dropdown, so the act of selecting is
    /// itself the only expression of intent "this asset for this type". Selecting "(none selected)"
    /// means the same as the "Reset to Default" button = clearing the designation).
    ///
    /// Same pattern as the Task52 bug fix: OptionsMainPanel does not re-run a mod's OnSettingsUI
    /// every time the Options screen is opened (see the class-header comment of
    /// OptionsRelationsPage.cs for details), so subscribe to the group panel's
    /// eventVisibilityChanged and, every time this mod's Options tab is selected, re-sync the
    /// dropdown contents, the current-designation display, isEnabled, and the hint label via
    /// RefreshFromState.
    ///
    /// "Existing buildings are not covered": BasePlacementWatcher.ProcessCreated reacts only to CS's
    /// EventBuildingCreated event (fired only on new placement / re-creation including road moves),
    /// so buildings of the same-named asset already standing at the moment of designation are never
    /// retroactively converted into bases (stated explicitly on this page's hint label).
    ///
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static class OptionsBaseBuildingPage
    {
        private static readonly string GroupTitle = WarfrontStrings.OptionsBaseBuilding_GroupTitle;
        private static readonly string NoSelectionLabel = WarfrontStrings.OptionsBaseBuilding_NoSelection;

        // Row order. Reuses the display-label constants of UnitAssetBindingsBaseTypes as-is (single source for UI wording).
        // Task101: added designation rows for the 5 field-fortification types (Bunker/ArtilleryPost/SupplyDepot/Trench/CargoStation).
        private static readonly BaseType[] RowTypes =
        {
            BaseType.Army, BaseType.Navy, BaseType.AirForce, BaseType.MissileBase,
            BaseType.Bunker, BaseType.ArtilleryPost, BaseType.SupplyDepot, BaseType.Trench, BaseType.CargoStation
        };
        private static readonly string[] RowDisplayNames =
        {
            UnitAssetBindings.ArmyBaseDisplayName,
            UnitAssetBindings.NavyBaseDisplayName,
            UnitAssetBindings.AirBaseDisplayName,
            UnitAssetBindings.MissileBaseDisplayName,
            WarfrontStrings.OptionsBaseBuilding_BunkerRow, WarfrontStrings.OptionsBaseBuilding_ArtilleryPostRow, WarfrontStrings.OptionsBaseBuilding_SupplyDepotRow, WarfrontStrings.OptionsBaseBuilding_TrenchRow, WarfrontStrings.OptionsBaseBuilding_CargoStationRow
        };

        private static UICheckBox _customOnlyCheckbox;
        private static UITextField _searchField;
        private static UILabel _countLabel;
        private static UILabel _hintLabel;

        private static readonly UIDropDown[] _rowDropdowns = new UIDropDown[9];
        private static readonly UILabel[] _rowLabels = new UILabel[9];
        private static readonly UIButton[] _rowResetButtons = new UIButton[9];

        private static readonly List<string> _filteredNames = new List<string>();
        private static bool _customOnly = true;
        private static bool _suppressEvents;

        /// <summary>Called from Mod.OnSettingsUI. Builds the "Base Buildings" group under the given helper.</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;

                _customOnlyCheckbox = group.AddCheckbox(WarfrontStrings.OptionsBaseBuilding_SubscribedOnlyCheckbox, _customOnly, OnCustomOnlyChanged) as UICheckBox;
                _searchField = group.AddTextfield(WarfrontStrings.OptionsBaseBuilding_SearchField, "", OnSearchTextChanged, OnSearchTextSubmitted) as UITextField;

                if (groupPanel != null)
                {
                    _countLabel = groupPanel.AddUIComponent<UILabel>();
                    _countLabel.textScale = 0.75f;
                    _countLabel.textColor = new Color32(200, 200, 200, 255);
                    _countLabel.text = "";
                }

                object lastControl = null;
                for (int i = 0; i < RowTypes.Length; i++)
                {
                    lastControl = BuildRow(group, groupPanel, i);
                }

                _hintLabel = CreateNoteLabel(lastControl, groupPanel);

                if (groupPanel != null) groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;

                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.Build error: " + e);
            }
        }

        /// <summary>Builds one row (dropdown labeled with the type + current-designation label +
        /// Reset to Default button). Returns the last control created (Build uses it to place the hint
        /// label right after it).</summary>
        private static object BuildRow(UIHelperBase group, UIComponent groupPanel, int index)
        {
            BaseType type = RowTypes[index];
            string displayName = RowDisplayNames[index];
            int rowIndex = index; // local copy for closure capture

            UIDropDown dd = group.AddDropdown(displayName, new[] { NoSelectionLabel }, 0,
                i => OnAssetSelected(rowIndex, i)) as UIDropDown;
            _rowDropdowns[index] = dd;

            if (groupPanel != null)
            {
                UILabel label = groupPanel.AddUIComponent<UILabel>();
                label.textScale = 0.8f;
                label.textColor = new Color32(200, 200, 200, 255);
                label.wordWrap = true;
                label.autoHeight = true;
                label.width = 500f;
                label.text = "";
                _rowLabels[index] = label;
            }

            object resetObj = group.AddButton(WarfrontStrings.OptionsBaseBuilding_ResetButton, () => OnResetClick(rowIndex));
            _rowResetButtons[index] = resetObj as UIButton;

            return resetObj;
        }

        /// <summary>Adds the hint label right after the last control (inside the same parent panel)
        /// (same technique as OptionsModelAssignPage.CreateNoteLabel).</summary>
        private static UILabel CreateNoteLabel(object afterObj, UIComponent fallbackParent)
        {
            try
            {
                UIComponent after = afterObj as UIComponent;
                UIComponent parent = after != null && after.parent != null ? after.parent : fallbackParent;
                if (parent == null) return null;

                UILabel label = parent.AddUIComponent<UILabel>();
                label.textScale = 0.8f;
                label.textColor = new Color32(255, 190, 120, 255);
                label.wordWrap = true;
                label.autoHeight = true;
                label.width = 500f;
                label.text = "";
                return label;
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.CreateNoteLabel error: " + e);
                return null;
            }
        }

        /// <summary>eventVisibilityChanged handler for the group panel (same pattern as the Task52
        /// bug fix). Calls RefreshFromState only when isVisible==true.</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>Common re-sync routine called on initial construction in Build() and thereafter
        /// every time this mod's tab is selected inside Options. No new controls are ever created.
        /// Exceptions are swallowed here.</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = AssetAssignPanel.HasAnyProps();

                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
                RefreshAllRowLabels();
                RefreshHint(stateReady);

                SetControlsEnabled(stateReady);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.RefreshFromState error: " + e);
            }
        }

        private static void SetControlsEnabled(bool enabled)
        {
            if (_customOnlyCheckbox != null) _customOnlyCheckbox.isEnabled = enabled;
            if (_searchField != null) _searchField.isEnabled = enabled;
            for (int i = 0; i < RowTypes.Length; i++)
            {
                if (_rowDropdowns[i] != null) _rowDropdowns[i].isEnabled = enabled;
                if (_rowResetButtons[i] != null) _rowResetButtons[i].isEnabled = enabled;
            }
        }

        /// <summary>Notice for the situation where no building assets exist at all because no map is
        /// loaded (main menu etc.). Task93 (user request): the always-shown usage note (the yellow
        /// text about the electricity tab etc.) was removed; only the notice for when the asset list
        /// is empty remains.</summary>
        private static void RefreshHint(bool stateReady)
        {
            if (_hintLabel == null) return;

            _hintLabel.text = stateReady
                ? ""
                : WarfrontStrings.OptionsBaseBuilding_NoAssetsHint;
        }

        private static void RefreshFilteredNames()
        {
            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(AssetKind.Building, _customOnly, filter);

            _filteredNames.Clear();
            bool truncated = names.Count > AssetAssignPanel.MaxListItems;
            int count = truncated ? AssetAssignPanel.MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredNames.Add(names[i]);

            if (_countLabel != null)
            {
                _countLabel.text = truncated
                    ? string.Format(WarfrontStrings.OptionsBaseBuilding_ListTruncatedFormat, AssetAssignPanel.MaxListItems, names.Count)
                    : string.Format(WarfrontStrings.OptionsBaseBuilding_ListCountFormat, names.Count);
            }
        }

        /// <summary>Rebuilds items/selectedIndex of all 4 row dropdowns to match the current filter
        /// result plus each row's current designation (BaseBuildingDesignation.TryGet). Because this is
        /// wrapped in _suppressEvents, "a designation dropped from the filter due to search/toggle
        /// state" is never mistakenly Cleared here via OnAssetSelected (the actual designation trusts
        /// only the value on the BaseBuildingDesignation side and is treated independently of the
        /// dropdown's visual selectedIndex).</summary>
        private static void RefreshAllRowDropdownItems()
        {
            string[] items = new string[_filteredNames.Count + 1];
            items[0] = NoSelectionLabel;
            for (int i = 0; i < _filteredNames.Count; i++) items[i + 1] = _filteredNames[i];

            _suppressEvents = true;
            try
            {
                for (int r = 0; r < RowTypes.Length; r++)
                {
                    UIDropDown dd = _rowDropdowns[r];
                    if (dd == null) continue;

                    dd.items = items;

                    string current;
                    int selectedIndex = 0;
                    if (BaseBuildingDesignation.TryGet(RowTypes[r], out current))
                    {
                        int idx = _filteredNames.IndexOf(current);
                        if (idx >= 0) selectedIndex = idx + 1;
                    }
                    dd.selectedIndex = selectedIndex;
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static void RefreshAllRowLabels()
        {
            for (int i = 0; i < RowTypes.Length; i++) RefreshRowLabel(i);
        }

        private static void RefreshRowLabel(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;
            UILabel label = _rowLabels[rowIndex];
            if (label == null) return;

            string current;
            if (!BaseBuildingDesignation.TryGet(RowTypes[rowIndex], out current))
            {
                label.text = WarfrontStrings.OptionsBaseBuilding_NotSetLabel;
                return;
            }

            // Task109: when there is no manual designation and a subscribed CS:WARFRONT asset was
            // auto-detected and is in use, say so (it works as-is, but the dropdown can switch to a
            // different asset at any time).
            label.text = BaseBuildingDesignation.IsAutoAssigned(RowTypes[rowIndex])
                ? string.Format(WarfrontStrings.OptionsBaseBuilding_CurrentAutoFormat, current)
                : string.Format(WarfrontStrings.OptionsBaseBuilding_CurrentFormat, current);
        }

        private static void OnCustomOnlyChanged(bool value)
        {
            try
            {
                _customOnly = value;
                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnCustomOnlyChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnSearchTextChanged error: " + e);
            }
        }

        /// <summary>No-op for AddTextfield's OnTextSubmitted (OnTextChanged alone already filters).</summary>
        private static void OnSearchTextSubmitted(string value)
        {
        }

        /// <summary>Handler for when a row's dropdown selection changes. Selecting index 0 (none
        /// selected) is treated the same as the "Reset to Default" button (clearing the designation).
        /// Otherwise the matching asset name from the filtered list is saved as the designation
        /// immediately (this page has no "Apply" button = the act of selecting is itself the only
        /// expression of intent).</summary>
        private static void OnAssetSelected(int rowIndex, int selectedIndex)
        {
            try
            {
                if (_suppressEvents) return;
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;

                BaseType type = RowTypes[rowIndex];
                if (selectedIndex <= 0)
                {
                    BaseBuildingDesignation.Clear(type);
                }
                else if (selectedIndex - 1 < _filteredNames.Count)
                {
                    BaseBuildingDesignation.Set(type, _filteredNames[selectedIndex - 1]);
                }

                RefreshRowLabel(rowIndex);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnAssetSelected error: " + e);
            }
        }

        private static void OnResetClick(int rowIndex)
        {
            try
            {
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;

                BaseBuildingDesignation.Clear(RowTypes[rowIndex]);
                RefreshAllRowDropdownItems(); // also revert the dropdown's visual selection to "(none selected)"
                RefreshRowLabel(rowIndex);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnResetClick error: " + e);
            }
        }
    }
}
