using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task102（ユーザー要望「軍事MOD関連の建物を一か所のタブにまとめたい。警察タブ・災害対策タブ・
    /// 港タブから探すのが面倒」）: 軍事建設パネル。
    ///
    /// Optionsで指定済みの軍事建物9種（基地4＋築城5）をボタンとして1つのパネルに並べ、クリックすると
    /// バニラのBuildingToolをそのアセットで起動する（＝どの建設タブにあるアセットかを気にせず、
    /// このパネルが実質的な「軍事タブ」になる）。ツールバーへの本物のカスタムタブ注入は
    /// バニラUI内部構造への依存が強く壊れやすいため採用しない（設計判断。ユーザー承認済み）。
    ///
    /// 開閉: ホットキー（WarfrontSettings.BuildPanelKey、既定Numpad4）と常駐の小ボタン
    /// （ドラッグで移動可）。配置ツールの解除は通常のバニラ操作（Esc/右クリック）そのまま。
    /// 未指定/未購読の種別はグレーアウト表示。パネルを開くたびに指定内容を再読込する。
    /// 全メソッドはメインスレッド専用（WarfrontThreadingExtension.OnUpdateから駆動）。
    /// </summary>
    internal static class MilitaryBuildPanel
    {
        private static readonly BaseType[] RowTypes =
        {
            BaseType.Army, BaseType.Navy, BaseType.AirForce, BaseType.MissileBase,
            BaseType.Bunker, BaseType.ArtilleryPost, BaseType.SupplyDepot, BaseType.Trench, BaseType.CargoStation
        };
        private static readonly string[] RowDisplayNames =
        {
            "Army Base", "Naval Base", "Air Base", "Missile Base",
            "Bunker", "Artillery Position", "Supply Depot", "Trench", "Cargo Station"
        };

        private const float PanelWidth = 240f;
        private const float RowHeight = 30f;
        private const float Pad = 8f;

        private static UIPanel _panel;
        private static UIButton[] _rowButtons;
        private static UIButton _toggleButton;

        /// <summary>このパネル経由で建設ツールを起動中か（Escキャンセル処理用）。</summary>
        private static bool _placementActive;

        /// <summary>毎フレーム（メインスレッド）: 生成・ホットキー・メニュー連動・Escキャンセル。</summary>
        public static void Update()
        {
            if (!PanelChrome.IsGameReadyForUi()) return;

            EnsureCreated();

            // 実機バグ修正（ユーザー報告「Escを押しても建築状態からキャンセルされない」）:
            // バニラでは建設メニュー（GeneratedGroupPanel）がEscを受けてツールを解除するが、
            // 本パネルはメニュー外からSetToolしているためその経路が無い。ここでEscを検知して
            // 明示的にDefaultToolへ戻し、同フレームで開いてしまったポーズメニューは閉じる
            // （＝バニラ同様「1回目のEscは配置キャンセル、2回目でメニュー」という体感にする）。
            if (_placementActive)
            {
                ToolBase current = ToolsModifierControl.toolController != null
                    ? ToolsModifierControl.toolController.CurrentTool : null;
                if (!(current is BuildingTool))
                {
                    _placementActive = false; // 配置完了/他ツールへ切替済み
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    _placementActive = false;
                    try { UIView.library.Hide("PauseMenu"); } catch (Exception) { /* 開いていなければ無視 */ }
                    return;
                }
            }

            if (_panel != null && _panel.isVisible && PanelChrome.IsGameMenuOpen())
            {
                _panel.Hide(); // ESCメニュー中は他パネルと同じく非表示
                return;
            }

            if (UIView.HasInputFocus()) return;
            if (Input.GetKeyDown(WarfrontSettings.BuildPanelKey)) Toggle();
        }

        public static void Toggle()
        {
            if (_panel == null) return;
            if (_panel.isVisible) _panel.Hide();
            else
            {
                RefreshRows(); // 開くたびに指定内容を再読込（Optionsで変更した直後も正しく反映）
                _panel.Show();
                _panel.BringToFront();
            }
        }

        private static void EnsureCreated()
        {
            if (_panel != null && _toggleButton != null) return;
            UIView view = PanelChrome.GetCachedView();
            if (view == null) return;

            if (_toggleButton == null)
            {
                _toggleButton = view.AddUIComponent(typeof(UIButton)) as UIButton;
                _toggleButton.name = "WarfrontBuildToggle";
                _toggleButton.text = "WF";
                _toggleButton.tooltip = "CS:WARFRONT military construction (Numpad 4)";
                _toggleButton.textScale = 0.8f;
                _toggleButton.size = new Vector2(36f, 36f);
                _toggleButton.normalBgSprite = "ButtonMenu";
                _toggleButton.hoveredBgSprite = "ButtonMenuHovered";
                _toggleButton.pressedBgSprite = "ButtonMenuPressed";
                // Task110: 画面最上段のアイコンの並び（左上の丸ボタン2つの右隣）へ置く（ユーザー要望）。
                _toggleButton.relativePosition = new Vector3(150f, 10f);
                // 実機バグ修正: ボタン全面を覆うUIDragHandleがクリックを奪うことがあるため、
                // 常駐ボタンはドラッグ不可の固定位置にする（クリックの確実性を優先）。
                _toggleButton.eventClick += (c, e) => Toggle();
            }

            if (_panel == null)
            {
                _panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
                _panel.name = "WarfrontBuildPanel";
                _panel.backgroundSprite = "MenuPanel2";
                _panel.width = PanelWidth;
                _panel.height = Pad + 28f + RowTypes.Length * (RowHeight + 4f) + Pad;
                _panel.relativePosition = new Vector3(150f, 55f); // Task110: 最上段のボタンの直下に開く
                _panel.isVisible = false;

                // 実機バグ修正: ドラッグハンドルが×ボタンまで覆ってクリックを奪っていたため、
                // ハンドル幅を×ボタンの手前までに縮める。
                UIDragHandle drag = _panel.AddUIComponent<UIDragHandle>();
                drag.target = _panel;
                drag.size = new Vector2(PanelWidth - 34f, 28f);
                drag.relativePosition = Vector3.zero;

                UILabel title = _panel.AddUIComponent<UILabel>();
                title.text = "Military Construction";
                title.textScale = 0.9f;
                title.relativePosition = new Vector3(Pad, 8f);

                UIButton close = _panel.AddUIComponent<UIButton>();
                close.text = "x";
                close.textScale = 0.8f;
                close.size = new Vector2(22f, 22f);
                close.normalBgSprite = "ButtonMenu";
                close.hoveredBgSprite = "ButtonMenuHovered";
                close.pressedBgSprite = "ButtonMenuPressed";
                close.relativePosition = new Vector3(PanelWidth - 22f - 4f, 4f);
                close.eventClick += (c, e) => _panel.Hide();
                close.BringToFront();

                _rowButtons = new UIButton[RowTypes.Length];
                for (int i = 0; i < RowTypes.Length; i++)
                {
                    UIButton b = _panel.AddUIComponent<UIButton>();
                    b.size = new Vector2(PanelWidth - Pad * 2f, RowHeight);
                    b.textScale = 0.85f;
                    b.textHorizontalAlignment = UIHorizontalAlignment.Left;
                    b.textPadding = new RectOffset(8, 4, 6, 0);
                    b.normalBgSprite = "ButtonMenu";
                    b.hoveredBgSprite = "ButtonMenuHovered";
                    b.pressedBgSprite = "ButtonMenuPressed";
                    b.disabledBgSprite = "ButtonMenuDisabled";
                    b.relativePosition = new Vector3(Pad, Pad + 28f + i * (RowHeight + 4f));
                    int rowIndex = i; // クロージャ用コピー
                    b.eventClick += (c, e) => OnRowClick(rowIndex);
                    _rowButtons[i] = b;
                }
                RefreshRows();
            }
        }

        private static void RefreshRows()
        {
            if (_rowButtons == null) return;
            for (int i = 0; i < RowTypes.Length; i++)
            {
                UIButton b = _rowButtons[i];
                if (b == null) continue;

                string assetName;
                bool designated = BaseBuildingDesignation.TryGet(RowTypes[i], out assetName);
                BuildingInfo info = designated ? PrefabCollection<BuildingInfo>.FindLoaded(assetName) : null;

                if (info != null)
                {
                    b.text = RowDisplayNames[i];
                    b.tooltip = assetName;
                    b.isEnabled = true;
                }
                else
                {
                    // 未指定（Optionsで建物を選んでいない）/ 指定アセットが未ロード。
                    b.text = RowDisplayNames[i] + (designated ? " (asset missing)" : " (not set)");
                    b.tooltip = "Assign a building in Options > Base Buildings";
                    b.isEnabled = false;
                }
            }
        }

        private static void OnRowClick(int rowIndex)
        {
            try
            {
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;
                string assetName;
                if (!BaseBuildingDesignation.TryGet(RowTypes[rowIndex], out assetName)) return;
                BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(assetName);
                if (info == null)
                {
                    CommandToast.Show("Asset not loaded: " + assetName);
                    return;
                }

                // Task106: 塹壕はライン敷設モード（2点右クリックで連続配置。バニラ配置ツールを
                // 使わないため「道路に接して配置」要件を受けない）。
                if (RowTypes[rowIndex] == BaseType.Trench)
                {
                    TrenchLineTargeting.Begin();
                    if (_panel != null) _panel.Hide(); // 地面クリックの邪魔にならないよう閉じる
                    return;
                }

                // バニラの建設ツールを直接このプレハブで起動する（BuildingToolは常設のバニラツール
                // なのでSetTool<T>の事前登録は不要）。以後の配置・回転・解除は通常の建設操作そのまま。
                BuildingTool tool = ToolsModifierControl.SetTool<BuildingTool>();
                if (tool == null)
                {
                    ModConfig.LogError("MilitaryBuildPanel: BuildingTool unavailable");
                    return;
                }
                tool.m_prefab = info;
                tool.m_relocate = 0;
                _placementActive = true; // Escキャンセル処理（Update）の対象にする
                CommandToast.Show("Placing: " + RowDisplayNames[rowIndex] + "  (Esc to cancel)");
            }
            catch (Exception e)
            {
                ModConfig.LogError("MilitaryBuildPanel.OnRowClick error: " + e);
            }
        }

        /// <summary>レベルアンロード時（WarfrontLoadingExtension経由）: 参照を破棄して次のロードで再生成。</summary>
        public static void Reset()
        {
            _panel = null;
            _rowButtons = null;
            _toggleButton = null;
            _placementActive = false;
        }
    }
}
