using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using ICities;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class that splits out from OptionsModelAssignPage only the "Reset All" button and the
    /// "Set" preset row (save/load for slots 1-3) newly added in Task70 (the OptionsModelAssignPage.cs
    /// side has already reached the 500-line limit, so the new additions are placed here from the
    /// start; this gives the Options subpage the same role as AssetAssignPanelPresets.cs). The actual
    /// persistence work is centrally handled by UnitAssetBindings.ClearAll/
    /// SaveToSlot/LoadFromSlot (Game/UnitAssetBindingsPresets.cs); this file only holds the UI
    /// (button/dropdown construction, click handlers, feedback display). Because it calls exactly the
    /// same API as the floating-panel side (AssetAssignPanelPresets.cs), the persistence logic itself
    /// is not implemented twice. All methods are main-thread only (they call Unity UI APIs).
    ///
    /// ICities.UIHelperBase has no UIListBox equivalent, so the same composition as the floating panel
    /// (dropdown + save/load buttons) is built plainly with AddDropdown/AddButton. Using the same
    /// technique as the "current assignment" preview (OptionsModelAssignPage.BuildBindingPreview),
    /// a raw UILabel is added directly after the buttons and used to show feedback messages
    /// (reusing CreateNoteLabel).
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private static UIButton _clearAllButton;
        private static UIDropDown _presetSlotDropdown;
        private static UIButton _presetSaveButton;
        private static UIButton _presetLoadButton;
        private static UILabel _presetMessageLabel;

        /// <summary>Converts the dropdown's selected index (0..2) to a slot number (1..3).</summary>
        private static int SelectedPresetSlot
        {
            get { return _presetSlotDropdown != null ? _presetSlotDropdown.selectedIndex + 1 : 1; }
        }

        /// <summary>Called from Build(). Appends the "Reset All" button, the "Set" dropdown +
        /// save/load buttons, and the feedback label to the end of the given group (the model
        /// assignment group).</summary>
        internal static void BuildPresetsSection(UIHelperBase group)
        {
            _clearAllButton = group.AddButton("Reset All", OnClearAllClick) as UIButton;

            string[] presetLabels = BuildPresetSlotLabels();
            _presetSlotDropdown = group.AddDropdown("Preset", presetLabels, 0, OnPresetSlotChanged) as UIDropDown;
            _presetSaveButton = group.AddButton("Save", OnSaveSlotClick) as UIButton;
            object loadButtonObj = group.AddButton("Load", OnLoadSlotClick);
            _presetLoadButton = loadButtonObj as UIButton;

            _presetMessageLabel = CreateNoteLabel(loadButtonObj);
        }

        /// <summary>Builds labels like "1", "2 (empty)" — appending "(empty)" for slots that do not
        /// exist per UnitAssetBindings.SlotExists (a cheap File.Exists only) (same content as
        /// AssetAssignPanelPresets.BuildPresetSlotLabels).</summary>
        private static string[] BuildPresetSlotLabels()
        {
            string[] labels = new string[3];
            for (int i = 0; i < 3; i++)
            {
                int slot = i + 1;
                labels[i] = UnitAssetBindings.SlotExists(slot) ? slot.ToString() : slot + " (empty)";
            }
            return labels;
        }

        /// <summary>Rebuilds only the labels of the slot dropdown while preserving the selection.
        /// Called from RefreshFromState() (every time this mod's tab is selected inside Options), and
        /// immediately after save/load.</summary>
        private static void RefreshPresetSlotLabels()
        {
            if (_presetSlotDropdown == null) return;

            int selected = _presetSlotDropdown.selectedIndex;
            _suppressEvents = true;
            try
            {
                _presetSlotDropdown.items = BuildPresetSlotLabels();
                _presetSlotDropdown.selectedIndex = (selected >= 0 && selected < 3) ? selected : 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>No-op for AddDropdown's eventCallback (reading selectedIndex at the moment the
        /// save/load button is pressed is sufficient, same policy as OnCopyScopeChanged).</summary>
        private static void OnPresetSlotChanged(int value)
        {
        }

        private static void SetPresetMessage(string text)
        {
            if (_presetMessageLabel != null) _presetMessageLabel.text = text;
        }

        /// <summary>"Reset All" button. Resets all assignments to the defaults via
        /// UnitAssetBindings.ClearAll and shows the removal count as a message. Only when 1 or more
        /// entries were removed does it perform the same reflection routine as the existing
        /// Apply/Reset/CopyApply (label rebuild + visual destruction) (WarnIfNoOwnedBaseForKey is
        /// typeIdx-specific and does not apply to a bulk operation, so it is not called).</summary>
        private static void OnClearAllClick()
        {
            try
            {
                int removed = UnitAssetBindings.ClearAll();
                SetPresetMessage(removed > 0
                    ? "Reset all bindings (" + removed + " entries)"
                    : "Nothing to reset (already at default)");

                if (removed > 0) RefreshAfterBulkChange();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnClearAllClick error: " + e);
            }
        }

        private static void OnSaveSlotClick()
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.SaveToSlot(slot);
                SetPresetMessage(ok ? "Saved to preset " + slot : "Failed to save to preset " + slot);
                if (ok) RefreshPresetSlotLabels();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnSaveSlotClick error: " + e);
            }
        }

        /// <summary>Loading replaces the whole table (the REPLACE semantics of
        /// UnitAssetBindings.LoadFromSlot), so on success re-syncs everything: the faction/type
        /// dropdown labels, the current-assignment display, and the visuals.</summary>
        private static void OnLoadSlotClick()
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.LoadFromSlot(slot);
                SetPresetMessage(ok ? "Loaded preset " + slot + " (replaced the existing bindings)" : "Preset " + slot + " is empty");

                if (ok)
                {
                    RefreshPresetSlotLabels();
                    RefreshAfterBulkChange();
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnLoadSlotClick error: " + e);
            }
        }

        /// <summary>Reflection routine shared by "Reset All" and preset load (the bulk-operation
        /// version of ApplyBindingChange. WarnIfNoOwnedBaseForKey, which is tied to a single typeIdx,
        /// is not called = multiple keys may change, so a per-key warning would be meaningless).</summary>
        private static void RefreshAfterBulkChange()
        {
            RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshCurrentBinding();

            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll();
            ModConfig.Log("OptionsModelAssignPage: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the bulk change");
        }
    }
}
