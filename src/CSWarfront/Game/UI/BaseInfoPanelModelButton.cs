using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanel のうち「モデル設定」ボタン（サブスクライブ済みプロップのユニットモデル割り当てUIを開く、
    /// Task36）だけを分離した partial class。BaseInfoPanel.cs 側の500行制限のため分離した
    /// （BaseInfoPanelProduction.cs と同じ方針）。
    /// このボタン自体はMilitaryManager/WarStateへ一切触れない（AssetAssignPanel.Toggleを呼ぶだけ）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float ModelButtonHeight = 22f;

        private static UIButton _modelButton;

        /// <summary>「所属勢力」ドロップダウンの下にボタンを1つ追加する（Build()から一度だけ呼ばれる）。
        /// 戻り値は次のコントロール（ステータスラベル）を配置するための更新済みY座標。</summary>
        private static float BuildModelButtonSection(float x, float y, float width)
        {
            if (_panel == null) return y;

            _modelButton = _panel.AddUIComponent<UIButton>();
            _modelButton.text = "Model Settings";
            _modelButton.textScale = 0.8f;
            _modelButton.size = new Vector2(width, ModelButtonHeight);
            _modelButton.relativePosition = new Vector3(x, y);
            _modelButton.normalBgSprite = "ButtonMenu";
            _modelButton.hoveredBgSprite = "ButtonMenuHovered";
            _modelButton.pressedBgSprite = "ButtonMenuPressed";
            _modelButton.eventClick += OnModelButtonClick;

            return y + ModelButtonHeight + 6f;
        }

        private static void OnModelButtonClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                AssetAssignPanel.Toggle();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnModelButtonClick error: " + e);
            }
        }

        /// <summary>_collapsed の反映（BaseInfoPanel.ApplyCollapsedStateから呼ばれる）。</summary>
        private static void ApplyModelButtonCollapsedState(bool collapsed)
        {
            if (_modelButton != null) _modelButton.isVisible = !collapsed;
        }

        /// <summary>Destroy()から呼ばれる。イベント購読解除とフィールドリセットのみ
        /// （Task34のDestroyProductionSectionと同じ方針）。</summary>
        private static void DestroyModelButtonSection()
        {
            if (_modelButton != null) _modelButton.eventClick -= OnModelButtonClick;
            _modelButton = null;
        }
    }
}
