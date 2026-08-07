using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class that splits out from OptionsModelAssignPage only the construction of the
    /// "current assignment" preview (label + thumbnail) and the rebuilding of the asset dropdown,
    /// the current-assignment display, and the thumbnail (newly created in Task70 because the
    /// OptionsModelAssignPage.cs side reached the 500-line limit; pure code move only, no logic
    /// changes). The fields (_currentBindingLabel/_thumbnailSprite/_assetDropdown/
    /// _countLabel/_filteredAssetNames etc.) are all declared on the OptionsModelAssignPage.cs side
    /// (fine, since a partial class shares private members across all parts).
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        /// <summary>Adds the "current assignment" label and thumbnail directly to the group panel
        /// (calling this after AddDropdown etc. places them in the position shown right after those;
        /// same dimensions and idea as AssetAssignPanelFaction.BuildThumbnailSprite).</summary>
        private static void BuildBindingPreview(UIComponent groupPanel)
        {
            _currentBindingLabel = groupPanel.AddUIComponent<UILabel>();
            _currentBindingLabel.textScale = 0.8f;
            _currentBindingLabel.textColor = new Color32(200, 200, 200, 255);
            _currentBindingLabel.wordWrap = true;
            _currentBindingLabel.autoHeight = true;
            _currentBindingLabel.width = 500f;
            _currentBindingLabel.text = "";

            _thumbnailSprite = groupPanel.AddUIComponent<UISprite>();
            _thumbnailSprite.size = new Vector2(96f, 96f);
            _thumbnailSprite.isVisible = false;
        }

        private static void RefreshAssetDropdown()
        {
            if (_assetDropdown == null) return;

            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(SelectedAssetKind, _customOnly, filter);

            _filteredAssetNames.Clear();
            bool truncated = names.Count > AssetAssignPanel.MaxListItems;
            int count = truncated ? AssetAssignPanel.MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredAssetNames.Add(names[i]);

            string[] items = new string[_filteredAssetNames.Count + 1];
            items[0] = NoSelectionLabel;
            for (int i = 0; i < _filteredAssetNames.Count; i++) items[i + 1] = _filteredAssetNames[i];

            _suppressEvents = true;
            try
            {
                _assetDropdown.items = items;
                _assetDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }

            if (_countLabel != null)
            {
                _countLabel.text = truncated
                    ? "* Showing " + AssetAssignPanel.MaxListItems + " of " + names.Count + " (narrow your search)"
                    : names.Count + " item(s)";
            }
        }

        private static void RefreshCurrentBinding()
        {
            if (_currentBindingLabel == null || _typeKeyDropdown == null || _typeKeys == null) return;

            int idx = _typeKeyDropdown.selectedIndex;
            if (idx < 0 || idx >= _typeKeys.Length)
            {
                _currentBindingLabel.text = "";
                RefreshThumbnail(AssetKind.Prop, null);
                return;
            }

            AssetKind kind;
            string name;
            bool bound = UnitAssetBindings.TryGetEffective(SelectedFactionId, _typeKeys[idx], out kind, out name);
            _currentBindingLabel.text = "Current binding: " + (bound ? AssetKindUtil.Describe(kind, name) : "(default model)");
            RefreshThumbnail(bound ? kind : AssetKind.Prop, bound ? name : null);
        }

        private static void RefreshThumbnail(AssetKind kind, string name)
        {
            if (_thumbnailSprite == null) return;

            UITextureAtlas atlas;
            string spriteName;
            if (!string.IsNullOrEmpty(name) && AssetCatalog.TryGetThumbnail(kind, name, out atlas, out spriteName))
            {
                _thumbnailSprite.atlas = atlas;
                _thumbnailSprite.spriteName = spriteName;
                _thumbnailSprite.isVisible = true;
            }
            else
            {
                _thumbnailSprite.isVisible = false;
            }
        }
    }
}
