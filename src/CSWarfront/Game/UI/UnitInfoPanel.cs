using System;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// クリック選択したユニットのステータスパネル（Task31）。UnitSelection.SelectedInstanceIdが0以外、
    /// かつ MilitaryManager.TryGetUnitSnapshot がそのidの生存ユニットを返す間だけ表示する。
    ///
    /// BaseInfoPanel（Game/UI/BaseInfoPanel.cs）と異なり「バニラパネルに追従」する方式ではない
    /// —— ユニット選択はCSのWorldInfoPanelシステムと無関係な自前クリック判定（UnitSelection）のため、
    /// 追従すべきバニラパネルが存在しない。代わりに画面右上隅に固定位置の常設パネルとして生成する。
    /// BaseInfoPanelは通常バニラの建物情報パネル（画面下寄りに出ることが多い）の隣に出るため、
    /// 反対側（右上）に固定することで両パネルの重なりを避ける。
    ///
    /// スレッド注記: このクラスの public メソッドは全てメインスレッド専用（Unity UI API呼び出しのため）。
    /// WarfrontThreadingExtension.OnUpdate から毎フレーム呼ばれる想定。WarState へは一切直接触れず、
    /// MilitaryManager.TryGetUnitSnapshot 経由でのみ読む（_stateLock はその内部で短時間だけ取られ、
    /// ここでは保持しない）。
    /// </summary>
    internal static class UnitInfoPanel
    {
        private const string PanelName = "CSWarfrontUnitInfoPanel";

        private const float PanelWidth = 240f;
        private const float Pad = 8f;
        private const float TitleRowHeight = 22f;
        private const float CloseButtonSize = 20f;
        // ステータス表示（所属/体力/攻撃・射程/装甲・速度/状態/目標/経路の最大7行）を
        // 1行あたり約16pxで見積もった予約高さ。
        private const float StatusLabelReserveHeight = 120f;
        private const float ScreenMargin = 16f;

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _closeButton;
        private static UILabel _statusLabel;
        private static bool _loggedCreated;

        /// <summary>RefreshContents で毎フレーム再利用するバッファ（BaseInfoPanelと同じ理由：
        /// 文字列連結の毎フレームアロケーションを避けるため、StringBuilder.Clear() で使い回す）。</summary>
        private static readonly StringBuilder _statusBuilder = new StringBuilder(256);

        /// <summary>
        /// 冪等。まだ生成していなければ UIView が準備できた時点で自前パネルを構築する
        /// （BaseInfoPanel.EnsureCreated と同じ「毎フレームポーリングして条件が揃ったら一度だけ作る」方式）。
        /// </summary>
        public static void EnsureCreated()
        {
            try
            {
                if (_panel != null) return;
                UIView view = UIView.GetAView();
                if (view == null) return; // UI未初期化。次フレーム再試行。
                Build(view);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>
        /// 毎メインスレッドフレーム呼ぶ。選択中ユニットが存在する間だけ表示・内容更新し、それ以外は隠す。
        /// 選択中ユニットが死亡/削除等で見つからなくなった場合は、パネルを隠すと同時に選択も解除する
        /// （消えたユニットを選択したまま次のクリックまで待たされる状態を避けるため）。
        /// </summary>
        public static void UpdateVisibility()
        {
            try
            {
                if (_panel == null) return; // EnsureCreated 待ち

                uint selected = UnitSelection.SelectedInstanceId;
                if (selected == 0)
                {
                    Hide();
                    return;
                }

                UnitUiSnapshot snapshot;
                if (!MilitaryManager.TryGetUnitSnapshot(selected, out snapshot))
                {
                    Hide();
                    UnitSelection.Clear();
                    return;
                }

                RefreshContents(snapshot);
                PositionTopRight(_panel);
                if (!_panel.isVisible) _panel.Show();
                _panel.BringToFront();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.UpdateVisibility error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。パネルを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                if (_closeButton != null) _closeButton.eventClick -= OnCloseClick;
                if (_panel != null) UnityEngine.Object.Destroy(_panel.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.Destroy error: " + e);
            }
            finally
            {
                _panel = null;
                _titleLabel = null;
                _closeButton = null;
                _statusLabel = null;
            }
        }

        private static void Hide()
        {
            if (_panel != null && _panel.isVisible) _panel.Hide();
        }

        private static void Build(UIView view)
        {
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // 二重生成防止

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("UnitInfoPanel.Build: UIPanel の生成に失敗");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = "";
            _titleLabel.textScale = 0.9f;
            _titleLabel.width = w - CloseButtonSize - 4f;
            _titleLabel.relativePosition = new Vector3(Pad, y);

            _closeButton = _panel.AddUIComponent<UIButton>();
            _closeButton.text = "×"; // ×（閉じるボタン、必須）
            _closeButton.size = new Vector2(CloseButtonSize, CloseButtonSize);
            _closeButton.relativePosition = new Vector3(PanelWidth - Pad - CloseButtonSize, y - 2f);
            _closeButton.textScale = 0.9f;
            _closeButton.normalBgSprite = "ButtonMenu";
            _closeButton.hoveredBgSprite = "ButtonMenuHovered";
            _closeButton.pressedBgSprite = "ButtonMenuPressed";
            _closeButton.eventClick += OnCloseClick;
            y += TitleRowHeight + 4f;

            _statusLabel = _panel.AddUIComponent<UILabel>();
            _statusLabel.textScale = 0.75f;
            _statusLabel.textColor = new Color32(220, 220, 220, 255);
            _statusLabel.wordWrap = true;
            _statusLabel.width = w;
            _statusLabel.text = "";
            _statusLabel.relativePosition = new Vector3(Pad, y);
            y += StatusLabelReserveHeight;

            _panel.height = y + Pad;
            _panel.isVisible = false;

            if (!_loggedCreated)
            {
                _loggedCreated = true;
                ModConfig.Log("UnitInfoPanel: created");
            }
        }

        /// <summary>×ボタンのクリックハンドラ。選択を解除してパネルを隠す（ユーザーの明示的な閉じる操作）。</summary>
        private static void OnCloseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                UnitSelection.Clear();
                Hide();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.OnCloseClick error: " + e);
            }
        }

        /// <summary>タイトル・ステータスラベルの文言を、ロック内でコピー済みのスナップショットから更新する。</summary>
        private static void RefreshContents(UnitUiSnapshot snapshot)
        {
            if (_titleLabel != null)
            {
                _titleLabel.text = snapshot.TypeKey + "  (Tier " + snapshot.Tier + ")";
            }

            if (_statusLabel != null)
            {
                string[] names = WarfrontSettings.FactionNames;
                string factionName = snapshot.FactionId < names.Length ? names[snapshot.FactionId] : "?";

                StringBuilder sb = _statusBuilder;
                sb.Length = 0;

                sb.Append("所属: ").Append(factionName);
                sb.Append("\n体力: ").Append(snapshot.CurrentHP.ToString("0")).Append(" / ").Append(snapshot.MaxHP.ToString("0"));
                sb.Append("\n攻撃: ").Append(snapshot.Attack.ToString("0")).Append("/h  射程: ").Append(snapshot.Range.ToString("0"));
                sb.Append("\n装甲: ").Append(snapshot.Armor.ToString("0")).Append("    速度: ").Append(snapshot.SpeedKmh.ToString("0")).Append("km/h");
                sb.Append("\n状態: ").Append(StateLabel(snapshot.State));
                sb.Append("\n目標: ").Append(snapshot.TargetId.HasValue ? "ユニット#" + snapshot.TargetId.Value : "なし");
                sb.Append("\n経路: ").Append(snapshot.PathCount > 0 ? snapshot.PathIndex + "/" + snapshot.PathCount : "直進");

                _statusLabel.text = sb.ToString();
            }
        }

        private static string StateLabel(UnitState state)
        {
            switch (state)
            {
                case UnitState.Idle: return "待機";
                case UnitState.Moving: return "移動中";
                case UnitState.Engaging: return "交戦中";
                case UnitState.Dead: return "戦死";
                default: return state.ToString();
            }
        }

        /// <summary>画面右上隅に固定位置で表示する（クラスコメント参照：BaseInfoPanelとの重なりを避けるため）。</summary>
        private static void PositionTopRight(UIPanel panel)
        {
            UIView view = UIView.GetAView();
            if (view == null || panel == null) return;

            Vector2 res = view.GetScreenResolution();
            float x = Mathf.Max(0f, res.x - panel.width - ScreenMargin);
            float y = ScreenMargin;
            panel.relativePosition = new Vector3(x, y);
        }
    }
}
