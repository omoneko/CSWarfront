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
    /// Task47: "Model Assignment" subpage built directly inside Mod Options (Game/Mod.cs,
    /// OnSettingsUI). Up through Task40-41, pressing the "Open model assignment" button opened an
    /// independent floating panel (AssetAssignPanel), but per user request this was changed to a
    /// scheme (subpage) that lays out the full set of controls directly inside a group on the
    /// Options screen.
    ///
    /// ICities.UIHelperBase only has 6 methods (AddGroup/AddDropdown/AddCheckbox/AddTextfield/
    /// AddButton/AddSpace) and offers no scrollable list (the equivalent of AssetAssignPanel's
    /// UIListBox), so the asset list degrades to a "narrow down with the search field, then pick
    /// from a dropdown" scheme (the policy explicitly stated in the requirements: "if no matching
    /// control exists, substitute a dropdown"). A count label tells the user how many entries did
    /// not fit into the dropdown (beyond AssetAssignPanel.MaxListItems=300).
    ///
    /// The concrete UIHelperBase implementation is the global-namespace class "UIHelper" in
    /// Assembly-CSharp; the return value of `object AddXxx(...)` is in fact the created UI
    /// component itself (UIDropDown/UITextField etc.), and `public object self { get; }` returns
    /// the "group content panel" (UIComponent) wrapped by the child helper that AddGroup returns
    /// (both verified by disassembling the IL of ColossalManaged.dll/Assembly-CSharp.dll via
    /// PowerShell reflection and checking the target types of the newobj/callvirt instructions).
    /// This lets us cast the dropdown return values and rewrite items/selectedIndex later, and add
    /// a "current assignment" preview display such as UILabel/UISprite as direct children starting
    /// from group.self, outside the helper (a generalization of the same technique as
    /// CreateHintLabel in Mod.cs as of Task40). If a call that AddUIComponents a raw UIComponent is
    /// interleaved between other Add* calls, the children line up on the same parent panel "in call
    /// order", so layout order can still be controlled with this scheme.
    ///
    /// This page itself may be opened with no map loaded (main menu), in which case
    /// PrefabCollection&lt;PropInfo/BuildingInfo/VehicleInfo/TreeInfo&gt; is not yet populated and
    /// the list has 0 entries. AssetAssignPanel.HasAnyProps() (the 4-kind cross-check including a
    /// Rescan) is reused to show that fact on the hint label (following the Mod.cs behavior as of
    /// Task40).
    ///
    /// The actual copy logic (Task47 "apply copy") is centrally owned by UnitAssetBindings.CopyTo;
    /// this class merely passes the selected value of the scope dropdown (exactly the same call
    /// pattern as the floating panel side in AssetAssignPanelCopy.cs).
    ///
    /// All methods are main-thread only (they call Unity UI APIs).
    ///
    /// Task52 bug fix: the paragraph above was written under the incorrect assumption that
    /// "OnSettingsUI may be re-run every time the Options screen is opened". In reality, CS's
    /// OptionsMainPanel (Assembly-CSharp.dll, verified by decompiling with ILSpy; see the
    /// class-header comment of OptionsRelationsPage.cs for details) calls each mod's OnSettingsUI
    /// exactly once in OptionsMainPanel.Awake() (normally at the main menu, before a city is
    /// loaded), and afterwards rebuilds only on a locale change or a mod enable/disable change.
    /// In other words, Build() runs exactly once while AssetCatalog is empty (no city loaded), and
    /// no matter how many times a city is reloaded and the Options screen reopened afterwards,
    /// Build() itself is never called again.
    ///
    /// The fix follows the same pattern as OptionsRelationsPage: subscribe to the group panel's
    /// eventVisibilityChanged (it fires every time this mod's tab is selected on the Options
    /// screen, propagated to all descendant components; UIComponent, verified by decompiling
    /// ColossalManaged.dll), and on each firing RefreshFromState() (1) re-scans AssetCatalog and
    /// determines whether a city is loaded, (2) rebuilds the unit-type/asset dropdown contents and
    /// the "current assignment" display, (3) syncs isEnabled of all controls to the determination
    /// result, and (4) updates the hint label. No new controls are ever created (only the existing
    /// fields created once in Build() are updated; prevents double creation).
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private static readonly string GroupTitle = WarfrontStrings.OptionsModel_GroupTitle;
        private static readonly string NoSelectionLabel = WarfrontStrings.OptionsModel_NoSelection;

        private static UIDropDown _factionDropdown;
        private static UIDropDown _typeKeyDropdown;
        private static UIDropDown _assetKindDropdown;
        private static UICheckBox _customOnlyCheckbox;
        private static UITextField _searchField;
        private static UIDropDown _assetDropdown;
        private static UIDropDown _copyScopeDropdown;
        private static UIButton _applyButton;
        private static UIButton _resetButton;
        private static UIButton _copyApplyButton;
        private static UILabel _currentBindingLabel;
        private static UISprite _thumbnailSprite;
        private static UILabel _countLabel;
        private static UILabel _hintLabel;

        private static string[] _typeKeys;
        private static readonly List<string> _filteredAssetNames = new List<string>();
        private static bool _customOnly = true;
        private static bool _suppressEvents;

        private static byte SelectedFactionId
        {
            get { return _factionDropdown != null ? (byte)_factionDropdown.selectedIndex : (byte)0; }
        }

        private static AssetKind SelectedAssetKind
        {
            get { return _assetKindDropdown != null ? (AssetKind)_assetKindDropdown.selectedIndex : AssetKind.Prop; }
        }

        /// <summary>Called from Mod.OnSettingsUI. Builds the "Model Assignment" group under the given helper.</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;
                if (groupPanel == null)
                {
                    ModConfig.LogError("OptionsModelAssignPage.Build: failed to get group panel, omitting current binding display/thumbnail.");
                }

                _factionDropdown = group.AddDropdown(WarfrontStrings.OptionsModel_FactionDropdown, WarfrontSettings.FactionNames, 0, OnFactionChanged) as UIDropDown;

                BuildTypeKeys();
                _typeKeyDropdown = group.AddDropdown(WarfrontStrings.OptionsModel_UnitTypeDropdown, BuildTypeKeyLabels(), 0, OnTypeKeyChanged) as UIDropDown;

                if (groupPanel != null) BuildBindingPreview(groupPanel);

                string[] kindLabels = new string[AssetKindUtil.All.Length];
                for (int i = 0; i < AssetKindUtil.All.Length; i++) kindLabels[i] = AssetKindUtil.DisplayNameJa(AssetKindUtil.All[i]);
                _assetKindDropdown = group.AddDropdown(WarfrontStrings.OptionsModel_AssetKindDropdown, kindLabels, 0, OnAssetKindChanged) as UIDropDown;

                _customOnlyCheckbox = group.AddCheckbox(WarfrontStrings.OptionsModel_SubscribedOnlyCheckbox, _customOnly, OnCustomOnlyChanged) as UICheckBox;
                // Task47: AddTextfield's OnTextSubmitted is unused (OnTextChanged alone is enough), but it is
                // unverified whether the UIHelper implementation null-guards the callback, so pass a no-op
                // instead of null (in the IL the eventTextSubmitted subscription looked unconditional, so err
                // on the safe side).
                _searchField = group.AddTextfield(WarfrontStrings.OptionsModel_SearchField, "", OnSearchTextChanged, OnSearchTextSubmitted) as UITextField;
                _assetDropdown = group.AddDropdown(WarfrontStrings.OptionsModel_AssetDropdown, new[] { NoSelectionLabel }, 0, OnAssetSelected) as UIDropDown;

                if (groupPanel != null)
                {
                    _countLabel = groupPanel.AddUIComponent<UILabel>();
                    _countLabel.textScale = 0.75f;
                    _countLabel.textColor = new Color32(200, 200, 200, 255);
                    _countLabel.text = "";
                }

                string[] copyLabels = new string[CopyScopeUtil.All.Length];
                for (int i = 0; i < CopyScopeUtil.All.Length; i++) copyLabels[i] = CopyScopeUtil.DisplayNameJa(CopyScopeUtil.All[i]);
                // Only the selectedIndex is read at the moment OnCopyApplyClick is pressed, so no change
                // notification is needed, but for the same reason as above pass a no-op callback rather than null.
                _copyScopeDropdown = group.AddDropdown(WarfrontStrings.OptionsModel_CopyScopeDropdown, copyLabels, 0, OnCopyScopeChanged) as UIDropDown;

                _applyButton = group.AddButton(WarfrontStrings.OptionsModel_ApplyButton, OnApplyClick) as UIButton;
                _resetButton = group.AddButton(WarfrontStrings.OptionsModel_ResetButton, OnResetClick) as UIButton;
                object copyButtonObj = group.AddButton(WarfrontStrings.OptionsModel_CopyApplyButton, OnCopyApplyClick);
                _copyApplyButton = copyButtonObj as UIButton;

                _hintLabel = CreateNoteLabel(copyButtonObj);

                // Task70: the "clear all" button and the "set" preset row (implemented in
                // OptionsModelAssignPagePresets.cs; the newly added part was placed there from the start
                // because of the 500-line limit).
                BuildPresetsSection(group);

                // Task52 bug fix: subscribe to eventVisibilityChanged, which fires every time this mod's
                // Options tab is selected (i.e. an ancestor component's isVisible flips to true and that
                // propagates down to this group panel), and call RefreshFromState with the state at that
                // moment (see the class-header comment). Build() itself is only called once, in
                // OptionsMainPanel.Awake() etc., so without this the disabled UI built once at the main menu
                // would never be updated after a city is loaded.
                if (groupPanel != null) groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;

                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.Build error: " + e);
            }
        }

        // Task70: BuildBindingPreview/RefreshAssetDropdown/RefreshCurrentBinding/RefreshThumbnail were
        // split out into OptionsModelAssignPageBinding.cs (same partial class) due to the 500-line limit.

        /// <summary>Adds a UILabel for status display to the button's parent panel (same technique as
        /// Mod.CreateHintLabel as of Task40). Failure to obtain it is not fatal (it merely becomes
        /// log-only display).</summary>
        private static UILabel CreateNoteLabel(object afterObj)
        {
            try
            {
                UIComponent after = afterObj as UIComponent;
                if (after == null || after.parent == null) return null;

                UILabel label = after.parent.AddUIComponent<UILabel>();
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
                ModConfig.LogError("OptionsModelAssignPage.CreateNoteLabel error: " + e);
                return null;
            }
        }

        /// <summary>Task66: prepends the 4 per-base-type keys (army/navy/air/missile,
        /// UnitAssetBindings.ArmyBaseTypeKey etc.) and then builds the 35 TypeKeys from
        /// LandUnitRoster.All() (category declaration order, then Tier1-5), for 39 entries total. Same
        /// policy as AssetAssignPanelControls.BuildTypeKeys (both the floating panel and the Options
        /// subpage show the same 4 entries; as of Task60 there was a single key).</summary>
        private static void BuildTypeKeys()
        {
            List<string> keys = new List<string>
            {
                UnitAssetBindings.ArmyBaseTypeKey,
                UnitAssetBindings.NavyBaseTypeKey,
                UnitAssetBindings.AirBaseTypeKey,
                UnitAssetBindings.MissileBaseTypeKey
            };
            foreach (UnitType t in LandUnitRoster.All()) keys.Add(t.TypeKey);
            // Task155 (tea32: "is it possible to add an option to also change the models for
            // aircraft?"). The list was ground vehicles only, for no reason beyond the order things
            // were built in - a binding is resolved by type key alone (UnitMeshSource.TryResolve), so
            // aircraft and ships work through exactly the same path.
            foreach (UnitType t in AirUnitRoster.All()) keys.Add(t.TypeKey);
            foreach (UnitType t in NavalUnitRoster.All()) keys.Add(t.TypeKey);
            _typeKeys = keys.ToArray();
        }

        /// <summary>Task66: for the base-type keys (army/navy/air/missile), display the Japanese label
        /// from UnitAssetBindings.DisplayNameForBaseKey instead of the raw key string (same policy as
        /// AssetAssignPanelControls.BuildDropdownLabels). Assignment resolution also goes per-type key
        /// first, then the legacy unified key (UnitAssetBindings.TryGetEffective).</summary>
        private static string[] BuildTypeKeyLabels()
        {
            if (_typeKeys == null) return new string[0];

            byte factionId = SelectedFactionId;
            string[] labels = new string[_typeKeys.Length];
            for (int i = 0; i < _typeKeys.Length; i++)
            {
                AssetKind kind;
                string name;
                string suffix = UnitAssetBindings.TryGetEffective(factionId, _typeKeys[i], out kind, out name)
                    ? AssetKindUtil.Describe(kind, name)
                    : WarfrontStrings.OptionsModel_DefaultSuffix;
                string displayKey = UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[i]);
                labels[i] = string.Format(WarfrontStrings.OptionsModel_TypeKeyLabelFormat, displayKey, suffix);
            }
            return labels;
        }

        private static void RefreshTypeKeyLabels(int selectedIndex)
        {
            if (_typeKeyDropdown == null || _typeKeys == null) return;

            _suppressEvents = true;
            try
            {
                _typeKeyDropdown.items = BuildTypeKeyLabels();
                if (selectedIndex >= 0 && selectedIndex < _typeKeys.Length) _typeKeyDropdown.selectedIndex = selectedIndex;
                else if (_typeKeys.Length > 0) _typeKeyDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>Shows on the hint label the situation where no assets exist at all because no map is
        /// loaded (main menu etc.). stateReady is received from the caller (RefreshFromState) as the
        /// result of AssetAssignPanel.HasAnyProps() (the 4-kind cross-check including a Rescan, following
        /// the Task40 behavior). Calling HasAnyProps() again here would run AssetCatalog.Rescan() twice
        /// (Rescan merely empties the cache, so the subsequent GetNames would re-scan all 4 kinds again,
        /// which is wasteful), so the caller performs the check exactly once.</summary>
        private static void RefreshHint(bool stateReady)
        {
            if (_hintLabel == null) return;
            _hintLabel.text = stateReady
                ? ""
                : WarfrontStrings.OptionsModel_NoAssetsHint;
        }

        /// <summary>eventVisibilityChanged handler for the group panel (Task52 bug fix).
        /// Calls RefreshFromState only when isVisible==true (i.e. only when this mod's tab is
        /// selected/shown inside Options). Does nothing on hiding (false)
        /// (same policy as OptionsRelationsPage.OnGroupVisibilityChanged).</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>Task52 bug fix: common re-sync routine called on initial construction in Build() and
        /// thereafter every time this mod's tab is selected inside Options. Uses
        /// AssetAssignPanel.HasAnyProps() (the existing method used since Task40 that runs
        /// AssetCatalog.Rescan() internally and then cross-checks all 4 kinds) to determine whether a city
        /// is currently loaded, brings the unit-type/asset dropdown contents, the current-assignment
        /// display, and the hint label up to date, then syncs isEnabled of all controls to the
        /// determination result. No new controls are ever created (only the existing fields created once
        /// in Build() are updated = prevents double creation).
        /// Exceptions are swallowed here so they never propagate from a UI callback into the game loop.</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = AssetAssignPanel.HasAnyProps();

                RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                RefreshAssetDropdown();
                RefreshCurrentBinding();
                RefreshHint(stateReady);
                RefreshPresetSlotLabels(); // Task70: refresh the empty-slot display each time the tab is shown

                SetControlsEnabled(stateReady);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.RefreshFromState error: " + e);
            }
        }

        /// <summary>Syncs isEnabled of all controls in one batch according to the city-loaded state
        /// (stateReady) (the same idea as the isEnabled sync in OptionsRelationsPage.RefreshFromState,
        /// extended to the full set of controls). While stateReady==false (no assets at all, e.g. main
        /// menu), user interaction would produce no meaningful result, so the controls are disabled and
        /// the hint label (RefreshHint) explains why.</summary>
        private static void SetControlsEnabled(bool enabled)
        {
            if (_factionDropdown != null) _factionDropdown.isEnabled = enabled;
            if (_typeKeyDropdown != null) _typeKeyDropdown.isEnabled = enabled;
            if (_assetKindDropdown != null) _assetKindDropdown.isEnabled = enabled;
            if (_customOnlyCheckbox != null) _customOnlyCheckbox.isEnabled = enabled;
            if (_searchField != null) _searchField.isEnabled = enabled;
            if (_assetDropdown != null) _assetDropdown.isEnabled = enabled;
            if (_copyScopeDropdown != null) _copyScopeDropdown.isEnabled = enabled;
            if (_applyButton != null) _applyButton.isEnabled = enabled;
            if (_resetButton != null) _resetButton.isEnabled = enabled;
            if (_copyApplyButton != null) _copyApplyButton.isEnabled = enabled;
            if (_clearAllButton != null) _clearAllButton.isEnabled = enabled; // Task70
            if (_presetSlotDropdown != null) _presetSlotDropdown.isEnabled = enabled;
            if (_presetSaveButton != null) _presetSaveButton.isEnabled = enabled;
            if (_presetLoadButton != null) _presetLoadButton.isEnabled = enabled;
        }

        // Task70: OnFactionChanged/OnTypeKeyChanged/OnAssetKindChanged/OnCustomOnlyChanged/
        // OnSearchTextChanged/OnSearchTextSubmitted/OnCopyScopeChanged/OnAssetSelected were split out
        // into OptionsModelAssignPageEvents.cs (same partial class) due to the 500-line limit.

        private static void OnApplyClick()
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _assetDropdown == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int assetIdx = _assetDropdown.selectedIndex;
                if (assetIdx < 1 || assetIdx - 1 >= _filteredAssetNames.Count)
                {
                    ModConfig.Log("OptionsModelAssignPage: apply skipped (no asset selected)");
                    return;
                }

                string typeKey = _typeKeys[typeIdx];
                string assetName = _filteredAssetNames[assetIdx - 1];
                AssetKind kind = SelectedAssetKind;

                UnitAssetBindings.Set(SelectedFactionId, typeKey, kind, assetName);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnApplyClick error: " + e);
            }
        }

        private static void OnResetClick()
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                UnitAssetBindings.Clear(SelectedFactionId, _typeKeys[typeIdx]);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnResetClick error: " + e);
            }
        }

        /// <summary>"Apply to Multiple" button. Copies the assignment of the currently selected
        /// (faction, unit type) to all (faction, type) pairs in the selected scope
        /// (UnitAssetBindings.CopyTo, same call pattern as AssetAssignPanelCopy.cs).</summary>
        private static void OnCopyApplyClick()
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _copyScopeDropdown == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int scopeIdx = _copyScopeDropdown.selectedIndex;
                if (scopeIdx < 0 || scopeIdx >= CopyScopeUtil.All.Length) return;
                CopyScope scope = CopyScopeUtil.All[scopeIdx];

                int written = UnitAssetBindings.CopyTo(SelectedFactionId, _typeKeys[typeIdx], scope);
                if (written > 0) ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnCopyApplyClick error: " + e);
            }
        }

        /// <summary>Reflection routine shared by binding changes (apply / reset to default / apply copy).
        /// After rebuilding the labels and updating the current-assignment display, destroys all existing
        /// visuals (same policy as AssetAssignPanelControls.ApplyBindingChange: the next Sync re-resolves
        /// the new assignment via UnitMeshSource).</summary>
        private static void ApplyBindingChange(int typeIdx)
        {
            RefreshTypeKeyLabels(typeIdx);
            RefreshCurrentBinding();

            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: also destroy the per-faction base overlays so the next Sync re-resolves them
            ModConfig.Log("OptionsModelAssignPage: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the binding change");

            WarnIfNoOwnedBaseForKey(typeIdx);
        }

        /// <summary>Task66 bug-investigation follow-up: same purpose as
        /// AssetAssignPanelControls.WarnIfNoOwnedBaseForKey (when the target is a base-type key and the
        /// selected faction owns no base of that type, the assignment is saved but nothing changes
        /// visually, so notify the user explicitly).</summary>
        private static void WarnIfNoOwnedBaseForKey(int typeIdx)
        {
            if (_typeKeys == null || typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

            BaseType baseType;
            if (!UnitAssetBindings.TryGetBaseTypeForKey(_typeKeys[typeIdx], out baseType)) return;
            if (MilitaryManager.HasOwnedBaseOfType(SelectedFactionId, baseType)) return;

            string note = string.Format(WarfrontStrings.OptionsModel_NoOwnedBaseNoteFormat, UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[typeIdx]));
            ModConfig.Log("OptionsModelAssignPage: " + note);
            if (_currentBindingLabel != null) _currentBindingLabel.text += "\n" + note;
        }
    }
}
