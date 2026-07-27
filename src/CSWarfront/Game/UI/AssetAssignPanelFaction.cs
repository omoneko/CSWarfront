using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、Task40で新設した「勢力」ドロップダウンとサムネイル表示だけを分離した
    /// partial class（AssetAssignPanel.cs 側の500行制限のため。BaseInfoPanel/BaseInfoPanelProduction と
    /// 同じ方針）。フィールドは全て AssetAssignPanel.cs 側で使う想定だが、宣言はここに置く（partial class は
    /// private メンバーも全パーツで共有するため問題ない）。全メソッドはメインスレッド専用
    /// （Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UILabel _factionSectionLabel;
        private static UIDropDown _factionDropdown;

        /// <summary>現在選択中の勢力ID（0..WarfrontSettings.MaxFactions-1）。ドロップダウンが未生成の間は
        /// 既定の0(Red)を返す。UnitAssetBindings.TryGet/Set/Clearへ渡す。</summary>
        internal static byte SelectedFactionId
        {
            get { return _factionDropdown != null ? (byte)_factionDropdown.selectedIndex : (byte)0; }
        }

        /// <summary>Task40: 現在ロードされているプロップが1件以上あるか（走査は強制的にRescan()する）。
        /// Mod Options（Game/Mod.cs）がメインメニュー等で機能が実質使えない状況を検出するために使う。</summary>
        internal static bool HasAnyProps()
        {
            try
            {
                PropCatalog.Rescan();
                return PropCatalog.GetNames(false, null).Count > 0;
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
            dd.itemHeight = 22;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 200;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.8f;
            dd.textFieldPadding = new RectOffset(8, 8, 6, 0);
            dd.itemPadding = new RectOffset(8, 0, 3, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = WarfrontSettings.FactionNames;
            dd.selectedIndex = 0; // 既定 Red。BaseInfoPanel.BuildFactionDropdownと同じ既定値。

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(24f, DropdownHeight);
            trigger.relativePosition = new Vector3(width - 24f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnFactionChanged;
            _factionDropdown = dd;
        }

        /// <summary>勢力選択が変わったら、ユニット種別ドロップダウンのラベル（"TypeKey → propName"）を
        /// その勢力の割り当てで再構築し、現在の割り当て表示・サムネイルも合わせて更新する。</summary>
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

        /// <summary>Task40: 「現在の割り当て」ラベルの右にサムネイル画像を表示するための64x64 UISprite。
        /// UISprite.atlas(UITextureAtlas)/spriteName(String) と PropInfo.m_Atlas/m_Thumbnail は
        /// Assembly-CSharp.dll/ColossalManaged.dllをリフレクションで検証済み（両方public、フィールド/プロパティ）。
        /// 既定では非表示（有効なサムネイルが見つかった時だけ RefreshThumbnail が表示する）。</summary>
        private static void BuildThumbnailSprite(float x, float y)
        {
            UISprite sprite = _panel.AddUIComponent<UISprite>();
            sprite.size = new Vector2(ThumbnailSize, ThumbnailSize);
            sprite.relativePosition = new Vector3(x, y);
            sprite.isVisible = false;
            _thumbnailSprite = sprite;
        }

        /// <summary>指定プロップのサムネイルを解決してUISpriteへ反映する。多くのアセットはサムネイルを
        /// 持たない（PropCatalog.TryGetThumbnailがfalseを返す）ため、その場合はスプライトを隠す
        /// （要件: サムネイル無し/未選択時はサムネイル領域を非表示にする）。呼び出し元:
        /// OnPropSelected（一覧選択変更時）、RefreshCurrentBindingLabel（勢力/種別切り替え時、
        /// 現在の割り当て済みプロップのプレビューとして）。</summary>
        private static void RefreshThumbnail(string propName)
        {
            if (_thumbnailSprite == null) return;

            UITextureAtlas atlas;
            string spriteName;
            if (!string.IsNullOrEmpty(propName) && PropCatalog.TryGetThumbnail(propName, out atlas, out spriteName))
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

        /// <summary>Destroy()から呼ばれる。イベント購読解除とフィールドリセットのみ
        /// （BaseInfoPanelModelButton.DestroyModelButtonSectionと同じ方針）。</summary>
        private static void DestroyFactionSection()
        {
            if (_factionDropdown != null) _factionDropdown.eventSelectedIndexChanged -= OnFactionChanged;
            _factionSectionLabel = null;
            _factionDropdown = null;
        }
    }
}
