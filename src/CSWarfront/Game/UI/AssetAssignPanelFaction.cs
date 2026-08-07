using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class of AssetAssignPanel that isolates only the "Faction" dropdown and thumbnail display
    /// added in Task40 (split off because of the 500-line limit on the AssetAssignPanel.cs side; same
    /// policy as BaseInfoPanel/BaseInfoPanelProduction). All fields are intended for use on the
    /// AssetAssignPanel.cs side, but the declarations live here (fine because a partial class shares
    /// private members across all parts). All methods are main-thread only
    /// (they call Unity UI APIs).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UILabel _factionSectionLabel;
        private static UIDropDown _factionDropdown;

        /// <summary>The currently selected faction ID (0..WarfrontSettings.MaxFactions-1). Returns the
        /// default 0 (Red) while the dropdown has not been created yet. Passed to
        /// UnitAssetBindings.TryGet/Set/Clear.</summary>
        internal static byte SelectedFactionId
        {
            get { return _factionDropdown != null ? (byte)_factionDropdown.selectedIndex : (byte)0; }
        }

        /// <summary>Task40: Whether at least one asset is currently loaded (all 4 kinds are considered;
        /// the scan is a forced Rescan()). Used by Mod Options (Game/Mod.cs) to detect situations where
        /// the feature is effectively unusable, e.g. on the main menu. Task41: Now covers not only props
        /// but also buildings/vehicles/trees (for example, on the main menu all 4 kinds should be 0, but
        /// after a level load it is fine to judge "usable" if even one kind has at least 1 asset).</summary>
        internal static bool HasAnyProps()
        {
            try
            {
                AssetCatalog.Rescan();
                for (int i = 0; i < AssetKindUtil.All.Length; i++)
                {
                    if (AssetCatalog.GetNames(AssetKindUtil.All[i], false, null).Count > 0) return true;
                }
                return false;
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.HasAnyProps error: " + e);
                return false;
            }
        }

        private static void BuildFactionDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 26; // text at 1x; line spacing slightly wider
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 300; // old 200 x1.5
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.8f; // unified with the other panels
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0); // old (8,8,6,0) x1.5
            dd.itemPadding = new RectOffset(12, 0, 5, 0); // old (8,0,3,0) x1.5
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = WarfrontSettings.FactionNames;
            dd.selectedIndex = 0; // default Red. Same default as BaseInfoPanel.BuildFactionDropdown.

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f; // unified with the other panels
            trigger.size = new Vector2(36f, DropdownHeight); // old 24f x1.5
            trigger.relativePosition = new Vector3(width - 48f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnFactionChanged;
            _factionDropdown = dd;
        }

        /// <summary>When the faction selection changes, rebuilds the unit-type dropdown labels
        /// ("TypeKey → propName") with that faction's bindings, and also updates the current-binding
        /// display and thumbnail accordingly.</summary>
        private static void OnFactionChanged(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                RefreshCurrentBindingLabel();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnFactionChanged error: " + e);
            }
        }

        /// <summary>Task40: A 128x128 (old 64x64; scaled by 1.5x in Task41 = 96x96) UISprite for showing
        /// a thumbnail image to the right of the "current binding" label. UISprite.atlas(UITextureAtlas)/
        /// spriteName(String) and PrefabInfo.m_Atlas/m_Thumbnail were verified via reflection against
        /// Assembly-CSharp.dll/ColossalManaged.dll (both public, field/property). Hidden by default
        /// (RefreshThumbnail shows it only when a valid thumbnail is found).</summary>
        private static void BuildThumbnailSprite(float x, float y)
        {
            UISprite sprite = _panel.AddUIComponent<UISprite>();
            sprite.size = new Vector2(ThumbnailSize, ThumbnailSize);
            sprite.relativePosition = new Vector3(x, y);
            sprite.isVisible = false;
            _thumbnailSprite = sprite;
        }

        /// <summary>Resolves the thumbnail of the asset with the given kind and name and applies it to
        /// the UISprite. Many assets have no thumbnail (AssetCatalog.TryGetThumbnail returns false), in
        /// which case the sprite is hidden (requirement: hide the thumbnail area when there is no
        /// thumbnail / nothing selected). Callers: OnAssetSelected (when the list selection changes),
        /// RefreshCurrentBindingLabel (when the faction/type changes, as a preview of the currently
        /// bound asset). Task41: added the kind (AssetKind) argument (building/vehicle/tree thumbnails
        /// can be resolved via the same path as props; see AssetCatalog.TryGetThumbnail).</summary>
        private static void RefreshThumbnail(AssetKind kind, string assetName)
        {
            if (_thumbnailSprite == null) return;

            UITextureAtlas atlas;
            string spriteName;
            if (!string.IsNullOrEmpty(assetName) && AssetCatalog.TryGetThumbnail(kind, assetName, out atlas, out spriteName))
            {
                _thumbnailSprite.atlas = atlas;
                _thumbnailSprite.spriteName = spriteName;
                _hasThumbnail = true;
                _thumbnailSprite.isVisible = !_collapsed;
            }
            else
            {
                _hasThumbnail = false;
                _thumbnailSprite.isVisible = false;
            }
        }

        /// <summary>Called from Destroy(). Only unsubscribes events and resets fields
        /// (same policy as BaseInfoPanelModelButton.DestroyModelButtonSection).</summary>
        private static void DestroyFactionSection()
        {
            if (_factionDropdown != null) _factionDropdown.eventSelectedIndexChanged -= OnFactionChanged;
            _factionSectionLabel = null;
            _factionDropdown = null;
        }
    }
}
