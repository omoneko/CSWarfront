using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class of AssetAssignPanel that isolates only the scope dropdown and button dedicated to
    /// "Apply to Multiple" (copying the currently selected (faction, unit type) assignment to other
    /// (faction, type) pairs in one operation), added in Task47 (split off because of the 500-line limit
    /// on the AssetAssignPanel.cs side; same policy as AssetAssignPanelFaction.cs, with the fields also
    /// declared here). The actual copy processing is handled by UnitAssetBindings.CopyTo
    /// (Game/UnitAssetBindings.cs); this file holds only the UI (dropdown construction, click handler).
    /// The Options subpage (Game/UI/OptionsModelAssignPage.cs) calls the same UnitAssetBindings.CopyTo,
    /// so the copy logic itself is not implemented twice. All methods are main-thread only
    /// (they call Unity UI APIs).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UILabel _copyScopeSectionLabel;
        private static UIDropDown _copyScopeDropdown;
        private static UIButton _copyApplyButton;

        private static UIDropDown BuildCopyScopeDropdown(float x, float y, float width)
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
            dd.listHeight = 200;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.7f; // unified with the other panels (slightly smaller because the labels are long)
            dd.textFieldPadding = new RectOffset(10, 10, 9, 0);
            dd.itemPadding = new RectOffset(10, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            string[] labels = new string[CopyScopeUtil.All.Length];
            for (int i = 0; i < CopyScopeUtil.All.Length; i++) labels[i] = CopyScopeUtil.DisplayNameJa(CopyScopeUtil.All[i]);
            dd.items = labels;
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

        /// <summary>Click handler of the "Apply to Multiple" button. Copies the current (faction, unit
        /// type) assignment to the selected scope, then runs the same "apply" processing as
        /// UnitAssetBindings.CopyTo (label rebuild + visual destruction). If the copy source has no
        /// assignment (still at default), CopyTo skips with 0 entries and only leaves a log entry.</summary>
        private static void OnCopyApplyClick(UIComponent component, UIMouseEventParameter eventParam)
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
                if (written > 0)
                {
                    ApplyBindingChange(typeIdx); // the shared Apply/Reset apply processing (label rebuild + UnitVisuals.DestroyAll)
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCopyApplyClick error: " + e);
            }
        }
    }
}
