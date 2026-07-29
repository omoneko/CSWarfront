using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// OptionsModelAssignPage のうち、「現在の割り当て」プレビュー（ラベル+サムネイル）の構築と、
    /// アセットドロップダウン・現在の割り当て表示・サムネイルの再構築だけを分離した partial class
    /// （OptionsModelAssignPage.cs 側が500行制限に達したため、Task70で新設。純粋なコード移動のみで
    /// ロジックの変更は無い）。フィールド（_currentBindingLabel/_thumbnailSprite/_assetDropdown/
    /// _countLabel/_filteredAssetNames等）は全て OptionsModelAssignPage.cs 側で宣言されている
    /// （partial class は private メンバーも全パーツで共有するため問題ない）。
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        /// <summary>「現在の割り当て」ラベルとサムネイルをグループパネルへ直接追加する（AddDropdown等の
        /// 後に呼ぶことで、その直後に表示される位置に並ぶ。AssetAssignPanelFaction.BuildThumbnailSpriteと
        /// 同じ寸法・考え方）。</summary>
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
                    ? "※ " + names.Count + " 件中 " + AssetAssignPanel.MaxListItems + " 件のみ表示（検索で絞り込んでください）"
                    : names.Count + " 件";
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
            _currentBindingLabel.text = "現在の割り当て: " + (bound ? AssetKindUtil.Describe(kind, name) : "(既定のモデル)");
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
