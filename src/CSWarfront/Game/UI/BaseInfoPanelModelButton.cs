using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out of BaseInfoPanel only the "Model Settings" button (opens the
    /// unit-model assignment UI for subscribed props, Task36). Split off because of the 500-line limit
    /// on BaseInfoPanel.cs (same policy as BaseInfoPanelProduction.cs).
    /// The button itself never touches MilitaryManager/WarState (it only calls AssetAssignPanel.Toggle).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float ModelButtonHeight = 22f;

        private static UIButton _modelButton;

        /// <summary>Adds one button below the "Faction" dropdown (called once from Build()).
        /// Returns the updated Y coordinate for placing the next control (the status label).</summary>
        private static float BuildModelButtonSection(float x, float y, float width)
        {
            if (_panel == null) return y;

            _modelButton = _panel.AddUIComponent<UIButton>();
            _modelButton.text = WarfrontStrings.BaseInfo_ModelSettingsButton;
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

        /// <summary>Applies _collapsed (called from BaseInfoPanel.ApplyCollapsedState).</summary>
        private static void ApplyModelButtonCollapsedState(bool collapsed)
        {
            if (_modelButton != null) _modelButton.isVisible = !collapsed;
        }

        /// <summary>Called from Destroy(). Only unsubscribes events and resets fields
        /// (same policy as Task34's DestroyProductionSection).</summary>
        private static void DestroyModelButtonSection()
        {
            if (_modelButton != null) _modelButton.eventClick -= OnModelButtonClick;
            _modelButton = null;
        }
    }
}
