using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class of AssetAssignPanel that isolates only the construction of the asset-kind
    /// (prop/building/vehicle/tree) dropdown, search field, subscribed-only toggle, and list, plus
    /// their event handlers (new in Task41, because of the 500-line limit. It used to live in the old
    /// AssetAssignPanelControls.cs but was split off in Task41 when the search-field readability fix
    /// and the asset-kind dropdown were added).
    /// All fields are declared on the AssetAssignPanel.cs side (fine because a partial class shares
    /// private members across all parts). All methods are main-thread only (they call Unity UI APIs).
    ///
    /// Search field (UITextField) readability fix: the old implementation layered textColor left at
    /// black (0,0,0) over normalBgSprite="TextFieldPanel" (the CS default dark panel sprite), so the
    /// black text sank into the dark background and was unreadable.
    /// Of all UITextField properties confirmed via reflection against ColossalManaged.dll (see the
    /// Task41 task-41-report.md), the following are set explicitly to fix this: textColor (Color32, to
    /// near-white), color (Color32, kept white = do not darken-tint the sprite),
    /// selectionBackgroundColor (Color32, makes the selection visible),
    /// padding (RectOffset, 2x scale), horizontalAlignment/verticalAlignment (left/middle),
    /// textScale (1.5x scale), cursorWidth (Int32). normalBgSprite/hoveredBgSprite/
    /// focusedBgSprite/disabledBgSprite/selectionSprite stay as-is (the "TextFieldPanel" family are
    /// sprite names with an actual usage track record in this project, so they are not changed to
    /// unverified alternatives).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        /// <summary>The currently selected asset kind. Returns the default Prop (0) while the dropdown
        /// has not been created yet (relies on the dropdown's selection indices 0..3 having the same
        /// order as AssetKindUtil.All).</summary>
        internal static AssetKind SelectedAssetKind
        {
            get { return _assetKindDropdown != null ? (AssetKind)_assetKindDropdown.selectedIndex : AssetKind.Prop; }
        }

        private static UITextField BuildSearchField(float x, float y, float width)
        {
            UITextField tf = _panel.AddUIComponent<UITextField>();
            tf.size = new Vector2(width, RowHeight);
            tf.relativePosition = new Vector3(x, y);
            tf.padding = new RectOffset(9, 9, 9, 9); // old (6,6,6,6) x1.5
            tf.builtinKeyNavigation = true;
            tf.isInteractive = true;
            tf.readOnly = false;
            tf.selectOnFocus = true;
            tf.horizontalAlignment = UIHorizontalAlignment.Left;
            tf.verticalAlignment = UIVerticalAlignment.Middle; // verified, newly set (vertically centered for readability)
            tf.normalBgSprite = "TextFieldPanel";
            tf.hoveredBgSprite = "TextFieldPanelHovered";
            tf.focusedBgSprite = "TextFieldPanelFocused";
            tf.disabledBgSprite = "TextFieldPanel";
            tf.color = Color.white; // verified, newly set. Do not darken-tint the sprite (display it as-is, white).
            tf.textColor = new Color32(240, 240, 240, 255); // old (0,0,0) = black -> changed to near-white (the core of the readability fix)
            tf.disabledTextColor = new Color32(150, 150, 150, 255); // verified, newly set
            tf.textScale = 0.8f; // unified with the other panels
            tf.cursorWidth = 2; // old 1
            tf.cursorBlinkTime = 0.45f;
            tf.selectionSprite = "EmptySprite";
            tf.selectionBackgroundColor = new Color32(70, 130, 200, 160); // verified, newly set (makes the selection visible)
            tf.text = "";
            tf.eventTextChanged += OnSearchTextChanged;
            return tf;
        }

        /// <summary>Task41: Dropdown that switches between prop/building/vehicle/tree. Default index 0
        /// (prop). The selection indices are kept aligned with the order of AssetKindUtil.All / AssetKind.</summary>
        private static UIDropDown BuildAssetKindDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, RowHeight);
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
            dd.textScale = 0.75f;
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0);
            dd.itemPadding = new RectOffset(12, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            string[] labels = new string[AssetKindUtil.All.Length];
            for (int i = 0; i < AssetKindUtil.All.Length; i++) labels[i] = AssetKindUtil.DisplayNameJa(AssetKindUtil.All[i]);
            dd.items = labels;
            dd.selectedIndex = 0; // default: prop

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(40f, RowHeight);
            trigger.relativePosition = new Vector3(width - 40f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnAssetKindChanged;
            return dd;
        }

        private static void OnAssetKindChanged(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnAssetKindChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(UIComponent component, string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnSearchTextChanged error: " + e);
            }
        }

        private static void OnCustomOnlyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _customOnly = !_customOnly;
                UpdateCustomOnlyLabel();
                RefreshAssetList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCustomOnlyClick error: " + e);
            }
        }

        private static void UpdateCustomOnlyLabel()
        {
            if (_customOnlyToggle != null)
            {
                _customOnlyToggle.text = "Subscribed only: " + (_customOnly ? "ON" : "OFF");
            }
        }

        /// <summary>Task40: Updates the thumbnail every time the list selection changes (requirement 3).
        /// On deselection (-1, e.g. the reset done by RefreshAssetList), reverts to the thumbnail of the
        /// currently applied asset.
        /// Task41: Thumbnail resolution uses the current kind-dropdown selection (SelectedAssetKind)
        /// (because the list only contains assets of that kind).</summary>
        private static void OnAssetSelected(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;

                if (value >= 0 && value < _filteredAssetNames.Count)
                {
                    RefreshThumbnail(SelectedAssetKind, _filteredAssetNames[value]);
                }
                else
                {
                    RefreshCurrentBindingLabel(); // internally also updates RefreshThumbnail (default/current binding)
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnAssetSelected error: " + e);
            }
        }

        /// <summary>Rebuilds the list based on the search string, the subscribed-only toggle, and the
        /// asset kind. If more than MaxListItems match, the list is truncated and a notice to that
        /// effect is shown in _truncatedLabel.</summary>
        private static void RefreshAssetList()
        {
            if (_propListBox == null) return;

            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(SelectedAssetKind, _customOnly, filter);

            _filteredAssetNames.Clear();
            bool truncated = names.Count > MaxListItems;
            int count = truncated ? MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredAssetNames.Add(names[i]);

            _suppressEvents = true;
            try
            {
                _propListBox.items = _filteredAssetNames.ToArray();
                _propListBox.selectedIndex = -1;
            }
            finally
            {
                _suppressEvents = false;
            }

            if (_truncatedLabel != null)
            {
                _truncatedLabel.text = truncated
                    ? "* Showing " + MaxListItems + " of " + names.Count + " (narrow your search)"
                    : names.Count + " item(s)";
            }
        }
    }
}
