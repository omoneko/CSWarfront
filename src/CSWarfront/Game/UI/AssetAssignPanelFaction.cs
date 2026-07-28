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

        /// <summary>Task40: 現在ロードされているアセットが1件以上あるか（4種類全てを対象、走査は
        /// 強制的にRescan()する）。Mod Options（Game/Mod.cs）がメインメニュー等で機能が実質使えない
        /// 状況を検出するために使う。Task41: プロップだけでなく建物/車両/樹木も対象に含めた
        /// （例えばメインメニューでは4種類とも0件のはずだが、レベルロード後はどれか1種類でも
        /// 1件あれば「使える」と判定してよいため）。</summary>
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
            dd.itemHeight = 33; // 旧22 ×1.5
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 300; // 旧200 ×1.5
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 1.2f; // 旧0.8f ×1.5
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0); // 旧(8,8,6,0) ×1.5
            dd.itemPadding = new RectOffset(12, 0, 5, 0); // 旧(8,0,3,0) ×1.5
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = WarfrontSettings.FactionNames;
            dd.selectedIndex = 0; // 既定 Red。BaseInfoPanel.BuildFactionDropdownと同じ既定値。

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 1.05f; // 旧0.7f ×1.5
            trigger.size = new Vector2(36f, DropdownHeight); // 旧24f ×1.5
            trigger.relativePosition = new Vector3(width - 48f, 0f);
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

        /// <summary>Task40: 「現在の割り当て」ラベルの右にサムネイル画像を表示するための128x128
        /// （旧64x64、Task41で1.5倍化＝96x96） UISprite。UISprite.atlas(UITextureAtlas)/spriteName(String) と
        /// PrefabInfo.m_Atlas/m_Thumbnail は Assembly-CSharp.dll/ColossalManaged.dllをリフレクションで
        /// 検証済み（両方public、フィールド/プロパティ）。既定では非表示（有効なサムネイルが見つかった時
        /// だけ RefreshThumbnail が表示する）。</summary>
        private static void BuildThumbnailSprite(float x, float y)
        {
            UISprite sprite = _panel.AddUIComponent<UISprite>();
            sprite.size = new Vector2(ThumbnailSize, ThumbnailSize);
            sprite.relativePosition = new Vector3(x, y);
            sprite.isVisible = false;
            _thumbnailSprite = sprite;
        }

        /// <summary>指定種類・名前のアセットのサムネイルを解決してUISpriteへ反映する。多くのアセットは
        /// サムネイルを持たない（AssetCatalog.TryGetThumbnailがfalseを返す）ため、その場合はスプライトを
        /// 隠す（要件: サムネイル無し/未選択時はサムネイル領域を非表示にする）。呼び出し元:
        /// OnAssetSelected（一覧選択変更時）、RefreshCurrentBindingLabel（勢力/種別切り替え時、
        /// 現在の割り当て済みアセットのプレビューとして）。Task41: 種類(AssetKind)を引数に追加した
        /// （建物/車両/樹木のサムネイルもプロップと同じ経路で解決できる、AssetCatalog.TryGetThumbnail参照）。</summary>
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
