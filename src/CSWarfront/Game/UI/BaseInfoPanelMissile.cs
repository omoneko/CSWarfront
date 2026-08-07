using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out only the section of BaseInfoPanel dedicated to ballistic missile
    /// bases (BaseType.MissileBase) (Task63). Split off because of the 500-line limit on
    /// BaseInfoPanel.cs / BaseInfoPanelProduction.cs (same policy as the Task34
    /// BaseInfoPanelProduction.cs split).
    ///
    /// This section and the unit production section in BaseInfoPanelProduction.cs are mutually
    /// exclusive: if the selected base is a MissileBase, show this one and hide the unit production
    /// section (and vice versa). Both Refresh*Section methods are called every frame (except while
    /// collapsed) and decide their own visibility each time by looking at snapshot.Type
    /// (ApplyProductionCollapsedState/ApplyMissileSectionCollapsedState; they track correctly even on
    /// the frame where the selected base was swapped).
    ///
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float MissileButtonHeight = 24f;
        private const float MissileRowGap = 6f;

        private static UILabel _missileStatusLabel;
        private static UIButton _buildMissileButton;
        private static UIButton _launchMissileButton;
        private static UIButton _missileAutoProduceButton;
        private static UIButton _missileAutoLaunchButton;
        private static UILabel _missileMessageLabel;

        /// <summary>AutoProduce/AutoLaunchMissiles from the most recent snapshot (used to compute the
        /// inverted value on click, same pattern as BaseInfoPanelProduction._lastAutoProduce).</summary>
        private static bool _lastMissileAutoProduce = true;
        private static bool _lastMissileAutoLaunch = true;

        /// <summary>Bottom Y of the missile section (0 means it is hidden = RecomputeExpandedHeight ignores it).</summary>
        private static float _missileSectionBottomY;

        /// <summary>Whether the selected base was a MissileBase in the most recent RefreshContents.
        /// Remembered so ApplyCollapsedState (on toggle click) can decide which section — unit
        /// production or missile — should be shown (the same handling of "cross-section state" as
        /// Task34's BuildProductionSection).</summary>
        private static bool _lastIsMissileBase;

        private static void BuildMissileSection(float width)
        {
            if (_panel == null) return;

            _missileStatusLabel = _panel.AddUIComponent<UILabel>();
            _missileStatusLabel.textScale = 0.75f;
            _missileStatusLabel.textColor = new Color32(220, 220, 220, 255);
            _missileStatusLabel.wordWrap = false;
            _missileStatusLabel.autoSize = false;
            _missileStatusLabel.autoHeight = true;
            _missileStatusLabel.width = width;
            _missileStatusLabel.text = "";
            _missileStatusLabel.relativePosition = new Vector3(Pad, 0f);

            float halfWidth = (width - MissileRowGap) / 2f;

            _buildMissileButton = _panel.AddUIComponent<UIButton>();
            _buildMissileButton.text = "Build Missile (¥" + MissileStockpile.MissileCost.ToString("0") + ")";
            _buildMissileButton.textScale = 0.75f;
            _buildMissileButton.size = new Vector2(halfWidth, MissileButtonHeight);
            _buildMissileButton.normalBgSprite = "ButtonMenu";
            _buildMissileButton.hoveredBgSprite = "ButtonMenuHovered";
            _buildMissileButton.pressedBgSprite = "ButtonMenuPressed";
            _buildMissileButton.relativePosition = new Vector3(Pad, 0f);
            _buildMissileButton.eventClick += OnBuildMissileClick;

            _launchMissileButton = _panel.AddUIComponent<UIButton>();
            _launchMissileButton.text = "Set Launch Target";
            _launchMissileButton.textScale = 0.75f;
            _launchMissileButton.size = new Vector2(halfWidth, MissileButtonHeight);
            _launchMissileButton.normalBgSprite = "ButtonMenu";
            _launchMissileButton.hoveredBgSprite = "ButtonMenuHovered";
            _launchMissileButton.pressedBgSprite = "ButtonMenuPressed";
            _launchMissileButton.relativePosition = new Vector3(Pad + halfWidth + MissileRowGap, 0f);
            _launchMissileButton.eventClick += OnLaunchMissileClick;

            // Task90: auto/manual toggles for production/launch (auto-production reuses the existing
            // MilitaryBase.AutoProduce; auto-launch is the newly added AutoLaunchMissiles. Both are
            // toggle buttons).
            _missileAutoProduceButton = _panel.AddUIComponent<UIButton>();
            _missileAutoProduceButton.text = "Auto-build: ON";
            _missileAutoProduceButton.textScale = 0.75f;
            _missileAutoProduceButton.size = new Vector2(halfWidth, MissileButtonHeight);
            _missileAutoProduceButton.normalBgSprite = "ButtonMenu";
            _missileAutoProduceButton.hoveredBgSprite = "ButtonMenuHovered";
            _missileAutoProduceButton.pressedBgSprite = "ButtonMenuPressed";
            _missileAutoProduceButton.relativePosition = new Vector3(Pad, 0f);
            _missileAutoProduceButton.eventClick += OnMissileAutoProduceClick;

            _missileAutoLaunchButton = _panel.AddUIComponent<UIButton>();
            _missileAutoLaunchButton.text = "Auto-launch: ON";
            _missileAutoLaunchButton.textScale = 0.75f;
            _missileAutoLaunchButton.size = new Vector2(halfWidth, MissileButtonHeight);
            _missileAutoLaunchButton.normalBgSprite = "ButtonMenu";
            _missileAutoLaunchButton.hoveredBgSprite = "ButtonMenuHovered";
            _missileAutoLaunchButton.pressedBgSprite = "ButtonMenuPressed";
            _missileAutoLaunchButton.relativePosition = new Vector3(Pad + halfWidth + MissileRowGap, 0f);
            _missileAutoLaunchButton.eventClick += OnMissileAutoLaunchClick;

            _missileMessageLabel = _panel.AddUIComponent<UILabel>();
            _missileMessageLabel.textScale = 0.7f;
            _missileMessageLabel.textColor = new Color32(230, 140, 140, 255);
            _missileMessageLabel.wordWrap = false;
            _missileMessageLabel.autoSize = false;
            _missileMessageLabel.width = width;
            _missileMessageLabel.text = "";
            _missileMessageLabel.relativePosition = new Vector3(Pad, 0f);
        }

        /// <summary>Called every frame (except while collapsed). Looks at snapshot.Type and shows and
        /// re-places this section only while the selected base is a MissileBase. Otherwise hides it and
        /// resets _missileSectionBottomY to 0 (so RecomputeExpandedHeight excludes it from the height
        /// calculation).</summary>
        private static void RefreshMissileSection(BaseUiSnapshot snapshot, float y)
        {
            _lastIsMissileBase = snapshot.Type == BaseType.MissileBase;
            ApplyMissileSectionCollapsedState(!_lastIsMissileBase);

            if (!_lastIsMissileBase)
            {
                _missileSectionBottomY = 0f;
                return;
            }

            if (_missileStatusLabel != null)
            {
                string statusText = "Stockpile: " + snapshot.StockpiledMissiles + " / " + MissileStockpile.MaxStockpile;
                if (snapshot.IsBuildingMissile)
                {
                    float pct = Mathf.Clamp01(snapshot.MissileBuildProgress) * 100f;
                    float remainHours = (1f - Mathf.Clamp01(snapshot.MissileBuildProgress)) * MissileStockpile.MissileBuildHours;
                    if (remainHours < 0f) remainHours = 0f;
                    statusText += "\nBuilding: " + pct.ToString("0") + "%  (" + remainHours.ToString("0.0") + "h left)";
                }
                else
                {
                    statusText += "\nBuilding: none";
                }
                _missileStatusLabel.text = statusText;
                _missileStatusLabel.relativePosition = new Vector3(Pad, y);
            }
            y += (_missileStatusLabel != null ? _missileStatusLabel.height : 0f) + MissileRowGap;

            float width = PanelWidth - Pad * 2f;
            float halfWidth = (width - MissileRowGap) / 2f;
            if (_buildMissileButton != null) _buildMissileButton.relativePosition = new Vector3(Pad, y);
            if (_launchMissileButton != null) _launchMissileButton.relativePosition = new Vector3(Pad + halfWidth + MissileRowGap, y);
            y += MissileButtonHeight + MissileRowGap;

            // Task90: the auto-build/auto-launch toggle row. The display text tracks the snapshot every
            // frame (there is no other-player input, but this keeps consistency for future extensions
            // where the AI side changes the values, and right after loading).
            _lastMissileAutoProduce = snapshot.AutoProduce;
            _lastMissileAutoLaunch = snapshot.AutoLaunchMissiles;
            if (_missileAutoProduceButton != null)
            {
                _missileAutoProduceButton.text = snapshot.AutoProduce ? "Auto-build: ON" : "Auto-build: OFF";
                _missileAutoProduceButton.relativePosition = new Vector3(Pad, y);
            }
            if (_missileAutoLaunchButton != null)
            {
                _missileAutoLaunchButton.text = snapshot.AutoLaunchMissiles ? "Auto-launch: ON" : "Auto-launch: OFF";
                _missileAutoLaunchButton.relativePosition = new Vector3(Pad + halfWidth + MissileRowGap, y);
            }
            y += MissileButtonHeight + MissileRowGap;

            if (_missileMessageLabel != null) _missileMessageLabel.relativePosition = new Vector3(Pad, y);
            y += SmallLabelHeight;

            _missileSectionBottomY = y;
        }

        private static void ApplyMissileSectionCollapsedState(bool hidden)
        {
            if (_missileStatusLabel != null) _missileStatusLabel.isVisible = !hidden;
            if (_buildMissileButton != null) _buildMissileButton.isVisible = !hidden;
            if (_launchMissileButton != null) _launchMissileButton.isVisible = !hidden;
            if (_missileAutoProduceButton != null) _missileAutoProduceButton.isVisible = !hidden;
            if (_missileAutoLaunchButton != null) _missileAutoLaunchButton.isVisible = !hidden;
            if (_missileMessageLabel != null) _missileMessageLabel.isVisible = !hidden;
        }

        private static void OnMissileAutoProduceClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                bool newValue = !_lastMissileAutoProduce;
                if (MilitaryManager.TrySetAutoProduce(_currentBaseId, newValue))
                {
                    _lastMissileAutoProduce = newValue;
                    if (_missileAutoProduceButton != null)
                        _missileAutoProduceButton.text = newValue ? "Auto-build: ON" : "Auto-build: OFF";
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnMissileAutoProduceClick error: " + e);
            }
        }

        private static void OnMissileAutoLaunchClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                bool newValue = !_lastMissileAutoLaunch;
                if (MilitaryManager.TrySetMissileAutoLaunch(_currentBaseId, newValue))
                {
                    _lastMissileAutoLaunch = newValue;
                    if (_missileAutoLaunchButton != null)
                        _missileAutoLaunchButton.text = newValue ? "Auto-launch: ON" : "Auto-launch: OFF";
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnMissileAutoLaunchClick error: " + e);
            }
        }

        private static void OnBuildMissileClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                MissileBuildResult r = MilitaryManager.TryQueueMissileBuild(_currentBaseId);
                SetMissileMessage(r == MissileBuildResult.Ok ? "" : BuildResultMessage(r));
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnBuildMissileClick error: " + e);
            }
        }

        private static void OnLaunchMissileClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                MissileLaunchTargeting.Arm(_currentBaseId);
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnLaunchMissileClick error: " + e);
            }
        }

        private static void SetMissileMessage(string text)
        {
            if (_missileMessageLabel != null) _missileMessageLabel.text = text;
        }

        private static string BuildResultMessage(MissileBuildResult r)
        {
            switch (r)
            {
                case MissileBuildResult.BaseNotFound: return "Base not found";
                case MissileBuildResult.NotMissileBase: return "Not a missile base";
                case MissileBuildResult.NoOwner: return "No owner";
                case MissileBuildResult.AlreadyBuilding: return "Already building";
                case MissileBuildResult.StockpileFull: return "Stockpile full";
                case MissileBuildResult.NotAffordable: return "Insufficient funds";
                default: return "";
            }
        }

        /// <summary>Called from BaseInfoPanel.Destroy. Only unsubscribes events and resets fields.</summary>
        private static void DestroyMissileSection()
        {
            if (_buildMissileButton != null) _buildMissileButton.eventClick -= OnBuildMissileClick;
            if (_launchMissileButton != null) _launchMissileButton.eventClick -= OnLaunchMissileClick;
            if (_missileAutoProduceButton != null) _missileAutoProduceButton.eventClick -= OnMissileAutoProduceClick;
            if (_missileAutoLaunchButton != null) _missileAutoLaunchButton.eventClick -= OnMissileAutoLaunchClick;

            _missileStatusLabel = null;
            _buildMissileButton = null;
            _launchMissileButton = null;
            _missileAutoProduceButton = null;
            _missileAutoLaunchButton = null;
            _missileMessageLabel = null;
            _missileSectionBottomY = 0f;
            _lastIsMissileBase = false;
            _lastMissileAutoProduce = true;
            _lastMissileAutoLaunch = true;
        }
    }
}
