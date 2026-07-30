using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanel のうち、弾道ミサイル基地（BaseType.MissileBase）専用のセクション（Task63）だけを
    /// 分離した partial class。BaseInfoPanel.cs / BaseInfoPanelProduction.cs の500行制限のため分離した
    /// （Task34のBaseInfoPanelProduction.cs分離と同じ方針）。
    ///
    /// このセクションと BaseInfoPanelProduction.cs のユニット生産セクションは排他表示: 選択中の基地が
    /// MissileBaseならこちらを表示しユニット生産セクションを隠す（逆も同様）。どちらの
    /// Refresh*Section も毎フレーム（折りたたみ中を除く）呼ばれ、snapshot.Type を見て自分自身の
    /// 表示/非表示を都度判定する（ApplyProductionCollapsedState/ApplyMissileSectionCollapsedState、
    /// 選択基地が入れ替わったフレームでも正しく追従する）。
    ///
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float MissileButtonHeight = 24f;
        private const float MissileRowGap = 6f;

        private static UILabel _missileStatusLabel;
        private static UIButton _buildMissileButton;
        private static UIButton _launchMissileButton;
        private static UILabel _missileMessageLabel;

        /// <summary>ミサイルセクションの最下端Y（0なら非表示中＝RecomputeExpandedHeightが無視する）。</summary>
        private static float _missileSectionBottomY;

        /// <summary>直近のRefreshContentsで選択中の基地がMissileBaseだったか。ApplyCollapsedState
        /// （トグルクリック時）がユニット生産/ミサイルのどちらのセクションを表示すべきか判断するために
        /// 覚えておく（Task34のBuildProductionSectionと同じ「セクション横断の状態」の扱い）。</summary>
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

            _missileMessageLabel = _panel.AddUIComponent<UILabel>();
            _missileMessageLabel.textScale = 0.7f;
            _missileMessageLabel.textColor = new Color32(230, 140, 140, 255);
            _missileMessageLabel.wordWrap = false;
            _missileMessageLabel.autoSize = false;
            _missileMessageLabel.width = width;
            _missileMessageLabel.text = "";
            _missileMessageLabel.relativePosition = new Vector3(Pad, 0f);
        }

        /// <summary>毎フレーム（折りたたみ中を除く）呼ばれる。snapshot.Typeを見て、選択中の基地が
        /// MissileBaseの間だけこのセクションを表示・再配置する。それ以外は非表示にし
        /// _missileSectionBottomY を0へ戻す（RecomputeExpandedHeightが高さ計算から除外するため）。</summary>
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

            if (_missileMessageLabel != null) _missileMessageLabel.relativePosition = new Vector3(Pad, y);
            y += SmallLabelHeight;

            _missileSectionBottomY = y;
        }

        private static void ApplyMissileSectionCollapsedState(bool hidden)
        {
            if (_missileStatusLabel != null) _missileStatusLabel.isVisible = !hidden;
            if (_buildMissileButton != null) _buildMissileButton.isVisible = !hidden;
            if (_launchMissileButton != null) _launchMissileButton.isVisible = !hidden;
            if (_missileMessageLabel != null) _missileMessageLabel.isVisible = !hidden;
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

        /// <summary>BaseInfoPanel.Destroyから呼ばれる。イベント購読解除とフィールドのリセットのみ行う。</summary>
        private static void DestroyMissileSection()
        {
            if (_buildMissileButton != null) _buildMissileButton.eventClick -= OnBuildMissileClick;
            if (_launchMissileButton != null) _launchMissileButton.eventClick -= OnLaunchMissileClick;

            _missileStatusLabel = null;
            _buildMissileButton = null;
            _launchMissileButton = null;
            _missileMessageLabel = null;
            _missileSectionBottomY = 0f;
            _lastIsMissileBase = false;
        }
    }
}
