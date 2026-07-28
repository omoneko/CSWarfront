using System;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// クリック選択したユニットのステータスパネル（Task31/Task32）。UnitSelection.SelectedInstanceIdが0以外、
    /// かつ MilitaryManager.TryGetUnitSnapshot がそのidの生存ユニットを返す間だけ表示する。
    ///
    /// Task32: 画面上の対象ユニットへ追従する（CSのバニラ車両/市民ワールド情報パネルと同様）。
    /// BaseInfoPanel（Game/UI/BaseInfoPanel.cs）と異なり「バニラパネルの隣に追従」する方式ではない
    /// —— ユニット選択はCSのWorldInfoPanelシステムと無関係な自前クリック判定（UnitSelection）のため、
    /// 追従すべきバニラパネルが存在しない。代わりに UnitVisuals.TryGetPosition で取得した実際の
    /// 描画位置（ワールド座標）を毎フレーム画面座標へ変換し、その真上にパネルを配置する。
    ///
    /// スレッド注記: このクラスの public メソッドは全てメインスレッド専用（Unity UI API呼び出しのため）。
    /// WarfrontThreadingExtension.OnUpdate から毎フレーム呼ばれる想定。WarState へは一切直接触れず、
    /// MilitaryManager.TryGetUnitSnapshot / UnitVisuals.TryGetPosition 経由でのみ読む（_stateLock は
    /// 前者の内部で短時間だけ取られ、ここでは保持しない）。
    /// </summary>
    internal static class UnitInfoPanel
    {
        private const string PanelName = "CSWarfrontUnitInfoPanel";

        // Task33: 旧240pxではステータス行が wordWrap 前提の幅に収まらず、単語単位の折り返しで
        // 「状態」「目標」「経路」がパネル外/下にはみ出していた。ラベルは wordWrap=false に固定し、
        // パネル幅は最長行（「装甲: 100    速度: 50km/h」「目標: ユニット#<uint>」等）がtextScale=0.75
        // で収まる幅まで拡張する（全角≈16px/半角≈9px @ scale1.0 の概算＋安全マージンで最長行は約163px、
        // 実測フォントとの誤差に備え260px（内側幅244px）を確保）。
        private const float PanelWidth = 260f;
        private const float Pad = 8f;
        private const float TitleRowHeight = 22f;
        private const float CloseButtonSize = 20f;
        private const float ButtonGap = 4f;

        /// <summary>
        /// Task32: パネルをユニットの真上に置くための、ワールド座標での上方オフセット。
        /// 可視性マーカー（UnitVisuals.MarkerSize=8、地面から MarkerHeight=5 持ち上げて中心配置なので
        /// 上端はおよそ地上+9）より確実に高い位置を狙い、+12 とした（上端との間に約3ユニットの余白）。
        /// </summary>
        private static readonly Vector3 VerticalOffset = new Vector3(0f, 12f, 0f);

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _closeButton;
        private static UIButton _collapseButton;
        private static PanelChrome.Handles _chrome; // Task40: タイトル行の最小化ボタン+ドラッグハンドル
        private static UILabel _statusLabel;
        private static bool _loggedCreated;

        /// <summary>Task40: このパネルには元々最小化機能が無かったため新設。BaseInfoPanel.ApplyCollapsedState
        /// と同じ考え方（タイトル行だけを残して畳む）。セッション中は選択解除・再選択をまたいで保持する。</summary>
        private static bool _collapsed;

        /// <summary>Task40: 展開時の全体高さキャッシュ（BaseInfoPanel._expandedHeightと同じ役割）。</summary>
        private static float _expandedHeight;

        /// <summary>
        /// Task40: ユーザーがタイトル行をドラッグした後は true になり、その間は UpdateTrackingPosition
        /// による毎フレームのユニット追従を止める（ドラッグ位置を維持するため）。パネルが「閉じる」
        /// （選択解除・×ボタン・対象ユニット消失）と false に戻り、次に選択したユニットには
        /// 通常どおり追従する。
        /// </summary>
        private static bool _detached;

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

                // Task47: バニラのEscメニューが開いている間はこのフレームの処理を丸ごとスキップし、
                // 生のUIPanelだけを隠す（_detached等のロジック状態には一切触れない）。private Hide()を
                // 経由しない理由はBaseInfoPanel.UpdateVisibilityと同じ（Hide()は「選択解除」相当で
                // _detached=falseにリセットしてしまい、メニューを閉じた次のフレームで同じユニットへ
                // 通常どおり追従復帰できなくなる）。
                if (PanelChrome.IsGameMenuOpen())
                {
                    if (_panel.isVisible) _panel.Hide();
                    return;
                }

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

                // Task40: 折りたたみ中はタイトル行しか見えないため再構築は無駄（BaseInfoPanelと同じ最適化）。
                if (!_collapsed) RefreshContents(snapshot);
                UpdateTrackingPosition(selected);
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
                PanelChrome.Unsubscribe(_chrome, OnCollapseClick); // Task40
                if (_chrome != null && _chrome.DragHandle != null) _chrome.DragHandle.eventMouseDown -= OnTitleBarMouseDown;
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
                _collapseButton = null;
                _chrome = null;
                _statusLabel = null;
                _collapsed = false;
                _expandedHeight = 0f;
                _detached = false;
            }
        }

        /// <summary>パネルを隠す。Task40: 「閉じる」（選択解除・×ボタン・対象ユニット消失）相当のため、
        /// 切り離しモードも解除し次の選択には通常どおり追従させる。UpdateTrackingPosition内の
        /// 一時的な非表示経路（可視表現未生成/カメラ後方等）は、_detached=true の間は別枝で早期returnし
        /// ここを通らないため、ドラッグ中に誤ってリセットされる心配はない。</summary>
        private static void Hide()
        {
            if (_panel != null && _panel.isVisible) _panel.Hide();
            _detached = false;
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

            // Task40: タイトル行全体を覆うドラッグハンドル(target=_panel)を先に追加し、その後に
            // タイトルラベル(非対話的)・最小化ボタン・×(閉じる)ボタン(いずれも対話的)を重ねる。
            // 後から追加したコンポーネントが前面に来るため、ボタンのクリックはドラッグハンドルに
            // 横取りされない（BaseInfoPanelと同じ方式、PanelChrome.AddTitleBarChrome参照）。
            _chrome = PanelChrome.AddTitleBarChrome(_panel, PanelWidth, y, Pad, OnCollapseClick);
            _chrome.DragHandle.eventMouseDown += OnTitleBarMouseDown;
            _collapseButton = _chrome.CollapseButton;
            // ×ボタンの左に並べるため、最小化ボタンをさらに左へ動かす（AddTitleBarChromeの既定位置は
            // パネル右端＝×ボタンと同じ場所のため、ここで詰め直す）。
            _collapseButton.relativePosition = new Vector3(
                PanelWidth - Pad - CloseButtonSize - ButtonGap - PanelChrome.CollapseButtonSize, y);

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = "";
            _titleLabel.textScale = 0.9f;
            _titleLabel.width = w - CloseButtonSize - PanelChrome.CollapseButtonSize - ButtonGap * 2f;
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
            // Task33: autoSize(既定true)とwordWrapの組み合わせがラベル幅を単語単位で縮めてしまう不具合の
            // 直接原因だったため、autoSize=false・wordWrap=false に固定して幅wを維持する。
            // 代わりに autoHeight=true でラベル自身の高さを実際の行数（"\n"の数）に追従させ、
            // RecomputePanelHeight() でパネル全体の高さをそこから毎更新算出する。
            _statusLabel.wordWrap = false;
            _statusLabel.autoSize = false;
            _statusLabel.autoHeight = true;
            _statusLabel.width = w;
            _statusLabel.text = "";
            _statusLabel.relativePosition = new Vector3(Pad, y);

            RecomputePanelHeight();
            _panel.isVisible = false;
            ApplyCollapsedState(); // Task40: 展開/折りたたみの初期反映（BaseInfoPanel.Buildと同じ方式）

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

        /// <summary>Task40: _collapsed の現在値をUIに反映する（BaseInfoPanel.ApplyCollapsedStateと同じ方式）。
        /// このパネルには折りたたみ対象のセクションがステータスラベル1つしか無いため単純。</summary>
        private static void ApplyCollapsedState()
        {
            if (_panel == null) return;

            if (_statusLabel != null) _statusLabel.isVisible = !_collapsed;
            _panel.height = _collapsed ? (Pad + TitleRowHeight + Pad) : _expandedHeight;

            if (_collapseButton != null)
            {
                _collapseButton.text = PanelChrome.CollapseGlyph(_collapsed);
            }
        }

        /// <summary>最小化トグルボタンのクリックハンドラ（BaseInfoPanel.OnCollapseClickと同じ方式）。</summary>
        private static void OnCollapseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _collapsed = !_collapsed;
                ApplyCollapsedState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.OnCollapseClick error: " + e);
            }
        }

        /// <summary>Task40: タイトル行(ドラッグハンドル)への最初のマウスダウンで「切り離し」モードに入る。
        /// 以降はUpdateTrackingPositionによる自動追従を止め、UIDragHandle自身がパネルを自由に動かせる
        /// ようにする（毎フレームの位置上書きとドラッグ操作の競合を避けるため）。Hide()（選択解除・
        /// ×ボタン・対象ユニット消失）で false に戻る。</summary>
        private static void OnTitleBarMouseDown(UIComponent component, UIMouseEventParameter eventParam)
        {
            _detached = true;
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
                sb.Append("\n命中: ").Append((snapshot.Accuracy * 100f).ToString("0")).Append("%");
                if (snapshot.AccuracyBoosted) sb.Append("（観測支援）");
                sb.Append("\n状態: ").Append(StateLabel(snapshot.State));
                sb.Append("\n目標: ").Append(snapshot.TargetId.HasValue ? "ユニット#" + snapshot.TargetId.Value : "なし");
                sb.Append("\n経路: ").Append(snapshot.PathCount > 0 ? snapshot.PathIndex + "/" + snapshot.PathCount : "直進");

                _statusLabel.text = sb.ToString();
                RecomputePanelHeight();
            }
        }

        /// <summary>
        /// Task33: ステータスラベル（autoHeight有効）の実際の高さからパネル全体高さを算出し、
        /// 変化があった場合のみ書き換える（毎フレームの無駄なレイアウト再計算を避ける）。
        /// Task40: 最小化機能の追加により、展開時の高さを _expandedHeight にキャッシュするようになった
        /// （BaseInfoPanel.RecomputeExpandedHeightと同じ方式。このメソッドは _collapsed==false の時にだけ
        /// RefreshContents経由で呼ばれるため、常に「展開時の高さ」を計算している前提で問題ない）。
        /// </summary>
        private static void RecomputePanelHeight()
        {
            if (_statusLabel == null || _panel == null) return;

            float newHeight = _statusLabel.relativePosition.y + _statusLabel.height + Pad;
            if (Mathf.Abs(newHeight - _expandedHeight) > 0.01f)
            {
                _expandedHeight = newHeight;
            }

            if (!_collapsed && Mathf.Abs(_panel.height - _expandedHeight) > 0.01f)
            {
                _panel.height = _expandedHeight;
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

        /// <summary>
        /// Task32: 選択中ユニットの実際の描画位置を毎フレーム画面座標へ変換し、その真上にパネルを追従させる。
        /// 位置決めに必要な前提（可視表現・メインカメラ・UIView）のいずれかが今フレーム揃わない場合、
        /// または対象がカメラの後方にある場合は、選択を維持したままこのフレームだけパネルを隠す
        /// （反転表示や誤った位置での表示を避けるため）。
        ///
        /// 座標変換API（ColossalManaged.dll をリフレクション/IL逆アセンブルで確認済み）:
        ///   - UIView.GetAView(): static UIView
        ///   - UIView.WorldPointToGUI(Camera cam, Vector3 worldPoint): Vector2
        ///     実装は「Camera.WorldToScreenPoint → x/yをそれぞれ (GetScreenResolution() / uiCamera.pixelWidth・
        ///     pixelHeight) でスケール → UIView.ScreenPointToGUI」という順で、Unity画面座標（左下原点、実ピクセル）
        ///     を UIView の仮想GUI解像度（GetScreenResolution()、relativePosition等が使う座標系）へ正しく
        ///     変換してくれる。
        ///   - UIView.ScreenPointToGUI(Vector2): Vector2 単体では
        ///     `result.y = GetScreenResolution().y - result.y` という単純なY反転のみで、実画面ピクセルと
        ///     UIViewの仮想解像度が異なる場合（UIスケール設定等）にX/Yのスケール補正を行わない。
        ///     そのため本メソッドでは「カメラ背後判定」にのみ Camera.WorldToScreenPoint を自前で呼び、
        ///     実際のGUI座標への変換は上記の理由から WorldPointToGUI に委ねる
        ///     （どちらも UIView.GetAView() 経由で得た同一 UIView インスタンス上のメソッド）。
        /// </summary>
        private static void UpdateTrackingPosition(uint instanceId)
        {
            if (_panel == null) return;

            // Task40: ユーザーがタイトル行をドラッグ済み（切り離しモード）の間は、ここで完全に
            // スキップして毎フレームの位置上書きを止める（UIDragHandle自身が動かした位置を維持する）。
            // 世界座標・カメラ・UIViewが今フレーム揃わない場合の一時非表示（下記のHide()呼び出し群）も
            // 併せて不要になる：追従自体をしないので、それらの前提が無くても表示を維持してよい。
            if (_detached)
            {
                if (!_panel.isVisible) _panel.Show();
                _panel.BringToFront();
                return;
            }

            Vector3 unitPos;
            if (!UnitVisuals.TryGetPosition(instanceId, out unitPos))
            {
                Hide(); // 見た目が今フレーム未生成/破棄済み。選択は維持し次フレーム再試行。
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                Hide(); // カメラ未準備（レベルロード中等）。選択は維持し次フレーム再試行。
                return;
            }

            UIView view = UIView.GetAView();
            if (view == null)
            {
                Hide();
                return;
            }

            Vector3 targetPos = unitPos + VerticalOffset;

            // カメラ後方判定専用。GUI座標そのものはこの下で WorldPointToGUI に委ねる
            // （クラスコメント参照：ScreenPointToGUI単体はスケール未補正のため）。
            Vector3 screenPoint = cam.WorldToScreenPoint(targetPos);
            if (screenPoint.z <= 0f)
            {
                // カメラの後方＝そのままではミラー表示されてしまうため、このフレームは隠す（選択は維持）。
                Hide();
                return;
            }

            Vector2 guiPoint = view.WorldPointToGUI(cam, targetPos);
            Vector2 res = view.GetScreenResolution();

            // 水平方向はユニット中心、垂直方向はパネル全体をguiPointの上に来るように配置。
            float x = guiPoint.x - _panel.width * 0.5f;
            float y = guiPoint.y - _panel.height;

            // 画面のどの辺からもパネル全体がはみ出さないようクランプする。
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, res.x - _panel.width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, res.y - _panel.height));

            _panel.relativePosition = new Vector3(x, y);

            if (!_panel.isVisible) _panel.Show();
            _panel.BringToFront();
        }
    }
}
