using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class of AssetAssignPanel that isolates only the "Reset All" button and the "preset" row
    /// (save/load for slots 1-3) added in Task70 (split off because of the 500-line limit on the
    /// AssetAssignPanel.cs side; same policy as AssetAssignPanelCopy.cs). The actual persistence is
    /// handled centrally by UnitAssetBindings.ClearAll/SaveToSlot/LoadFromSlot
    /// (Game/UnitAssetBindingsPresets.cs); this file holds only the UI (button/dropdown construction,
    /// click handlers, feedback display). The Options subpage
    /// (Game/UI/OptionsModelAssignPagePresets.cs) calls the same APIs, so the persistence logic itself
    /// is not implemented twice. All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UIButton _clearAllButton;
        private static UILabel _presetSectionLabel;
        private static UIDropDown _presetSlotDropdown;
        private static UIButton _presetSaveButton;
        private static UIButton _presetLoadButton;
        private static UILabel _presetMessageLabel;

        /// <summary>Converts the dropdown's selection index (0..2) to a slot number (1..3).</summary>
        private static int SelectedPresetSlot
        {
            get { return _presetSlotDropdown != null ? _presetSlotDropdown.selectedIndex + 1 : 1; }
        }

        private static UIButton BuildClearAllButton(float x, float y, float width)
        {
            _clearAllButton = BuildButton("Reset All", x, y, width, OnClearAllClick);
            return _clearAllButton;
        }

        /// <summary>The "preset" row: a slot-selection dropdown (1/2/3, with "(empty)" appended to empty
        /// slots) + Save/Load buttons. The dropdown takes about 1/3 of the row width; the remainder is
        /// split evenly between the Save/Load buttons.</summary>
        private static void BuildPresetRow(float x, float y, float width)
        {
            float dropdownWidth = width * 0.34f;
            float buttonWidth = (width - dropdownWidth - SectionGap * 2f) / 2f;

            _presetSlotDropdown = BuildPresetSlotDropdown(x, y, dropdownWidth);
            _presetSaveButton = BuildButton("Save", x + dropdownWidth + SectionGap, y, buttonWidth, OnSaveSlotClick);
            _presetLoadButton = BuildButton("Load", x + dropdownWidth + SectionGap + buttonWidth + SectionGap, y, buttonWidth, OnLoadSlotClick);
        }

        private static UIDropDown BuildPresetSlotDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 26;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 90;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f; // unified with the other panels
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0);
            dd.itemPadding = new RectOffset(12, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = BuildPresetSlotLabels();
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(36f, DropdownHeight);
            trigger.relativePosition = new Vector3(width - 36f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            return dd;
        }

        /// <summary>Builds labels like "1", "2 (empty)": "(empty)" is appended to slots that do not
        /// exist per UnitAssetBindings.SlotExists (a cheap File.Exists only).</summary>
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

        /// <summary>Rebuilds only the slot dropdown's labels while preserving the selection. Called from
        /// Show() and right after save/load (since the contents may have changed).</summary>
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

        /// <summary>The "Reset All" button. Resets all bindings to default via
        /// UnitAssetBindings.ClearAll and shows the number of removed entries as a message. Only when
        /// at least one entry was removed does it run the same apply processing as the existing
        /// Apply/Reset/CopyApply (label rebuild + visual destruction).</summary>
        private static void OnClearAllClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                int removed = UnitAssetBindings.ClearAll();
                SetPresetMessage(removed > 0
                    ? "Reset all bindings (" + removed + " entries)"
                    : "Nothing to reset (already at default)");

                if (removed > 0)
                {
                    RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                    RefreshCurrentBindingLabel();
                    UnitVisuals.DestroyAll();
                    BaseVisuals.DestroyAll();
                    ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the full reset");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnClearAllClick error: " + e);
            }
        }

        private static void OnSaveSlotClick(UIComponent component, UIMouseEventParameter eventParam)
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
                ModConfig.LogError("AssetAssignPanel.OnSaveSlotClick error: " + e);
            }
        }

        /// <summary>Loading replaces the whole table (the REPLACE semantics of
        /// UnitAssetBindings.LoadFromSlot; see the comment at the top of that class), so on success the
        /// faction/type dropdown labels, the current-binding display, and the visuals are all
        /// resynchronized (same apply processing as Apply/Reset/CopyApply).</summary>
        private static void OnLoadSlotClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.LoadFromSlot(slot);
                SetPresetMessage(ok ? "Loaded preset " + slot + " (replaced the existing bindings)" : "Preset " + slot + " is empty");

                if (ok)
                {
                    RefreshPresetSlotLabels();
                    RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                    RefreshCurrentBindingLabel();
                    UnitVisuals.DestroyAll();
                    BaseVisuals.DestroyAll();
                    ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the preset load");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnLoadSlotClick error: " + e);
            }
        }

        private static void SetPresetMessage(string text)
        {
            if (_presetMessageLabel != null) _presetMessageLabel.text = text;
        }

        /// <summary>Called from Destroy(). Only unsubscribes events and resets fields
        /// (same policy as AssetAssignPanelFaction.DestroyFactionSection).</summary>
        private static void DestroyPresetsSection()
        {
            if (_clearAllButton != null) _clearAllButton.eventClick -= OnClearAllClick;
            if (_presetSaveButton != null) _presetSaveButton.eventClick -= OnSaveSlotClick;
            if (_presetLoadButton != null) _presetLoadButton.eventClick -= OnLoadSlotClick;
            _clearAllButton = null;
            _presetSectionLabel = null;
            _presetSlotDropdown = null;
            _presetSaveButton = null;
            _presetLoadButton = null;
            _presetMessageLabel = null;
        }
    }
}
