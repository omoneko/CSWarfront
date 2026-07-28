using System;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task40: BaseInfoPanel/UnitInfoPanel/AssetAssignPanel の3パネルに共通する「タイトル行の最小化トグル」
    /// と「ドラッグ移動」を構築する小さな共有ヘルパー。各パネルの個別ロジック（何を畳む/どこに追従するか等）
    /// には一切関与せず、UIコンポーネントの生成と最小限の見た目定数だけを提供する。
    ///
    /// ドラッグ移動: <see cref="ColossalFramework.UI.UIDragHandle"/>（`target`(UIComponent), `size`(Vector2),
    /// `relativePosition`(Vector3) をリフレクションで存在確認済み。ColossalManaged.dll）をタイトル行全体
    /// （パネル幅×TitleRowHeight）を覆う透明な子コンポーネントとして先に追加し、`target` にパネル自身を
    /// 設定する。タイトルラベル（非対話的、既定でクリックを素通しする）と最小化ボタン（対話的、後から
    /// 追加してドラッグハンドルの上に重ねる）は呼び出し側が生成するため、このヘルパーが返した
    /// ドラッグハンドルより後にラベル/ボタンを追加すること（UIコンポーネントは追加順で前面に来るため、
    /// ボタンのクリックがドラッグハンドルに横取りされない）。
    ///
    /// 最小化トグル: グリフ文字列だけを提供する（"–"=展開中/畳むと–→+、"+"=折りたたみ中）。実際の
    /// 折りたたみ対象（どのコンポーネントを隠すか、パネルの高さをどう戻すか）はパネルごとに大きく異なる
    /// ため、ボタンの生成とクリックハンドラの購読だけをこのヘルパーで行い、ApplyCollapsedState相当の
    /// ロジックは呼び出し側（各パネル）に残す。
    /// </summary>
    internal static class PanelChrome
    {
        public const float TitleRowHeight = 22f;
        public const float CollapseButtonSize = 20f;

        private const string CollapseGlyphExpanded = "–"; // – (最小化する = クリックすると畳む)
        private const string CollapseGlyphCollapsed = "+";     // + (展開する = クリックすると開く)

        // Task47: バニラの一時停止/ESCメニュー（PauseMenu、Escで開く「終了」「オプション」等の一覧画面）の
        // コンポーネント名。UIView.library.Get&lt;T&gt;(name) 経由での取得は、BaseInfoPanel が
        // CityServiceWorldInfoPanel を取得する際と全く同じ確立済みパターン（型名=登録名）を踏襲する。
        // 検証方法と選定理由（PowerShellでColossalManaged.dll/Assembly-CSharp.dllをリフレクションし確認）:
        //   - PauseMenu : MenuPanel : ColossalFramework.UI.UICustomControl。UICustomControlは
        //     `UIComponent component { get; }` を公開しており、そのisVisibleがバニラの「Escで開く
        //     一時停止/オプション選択メニュー」の表示状態そのもの（このパネル自体がEscトグルの実体）。
        //   - 候補として検討したが不採用の他API:
        //     ・Singleton&lt;SimulationManager&gt;.instance.SimulationPaused … ユーザーが手動で「一時停止」
        //       ボタンを押した場合も true になり、Escメニューを開いていない場合と区別できない
        //       （タスク要件が明示的に「不十分」と指定）。
        //     ・UIView.HasModalInput() … PushModalスタックに何か積まれていれば真になる、より広い概念。
        //       UIDropDown（本MOD含め多用）のポップアップはUIDropDown.OpenPopup等の実装
        //       （リフレクションでPushModal呼び出しの痕跡なしを確認）でモーダルスタックを使わないため
        //       直ちに競合するわけではないが、バニラの他のモーダルUI（保存/読み込みダイアログ等）でも
        //       true になり「Escメニューが開いている」より意味が広すぎる。本タスクは「Escメニュー」に
        //       限定した挙動を要求しているため、より的を絞ったPauseMenu.component.isVisibleを採用する。
        private const string PauseMenuName = "PauseMenu";

        /// <summary>タイトル行の構築結果。各パネルはこれをフィールドに保持し、Destroy時に
        /// CollapseButton.eventClick の購読解除に使う（DragHandleは自前イベントを持たないため解除不要）。</summary>
        public sealed class Handles
        {
            public UIDragHandle DragHandle;
            public UIButton CollapseButton;
        }

        /// <summary>
        /// パネル直下に、タイトル行全体(x=0..panelWidth, y=titleRowY..+TitleRowHeight)を覆う
        /// UIDragHandle（target=panel、パネル全体をドラッグ移動可能にする）と、その右端に重ねる
        /// 最小化トグルボタンを追加する。ボタンのクリックハンドラは呼び出し側が渡す
        /// （各パネルの _collapsed フィールドをトグルし ApplyCollapsedState 相当を呼ぶだけの薄い処理を想定）。
        /// </summary>
        public static Handles AddTitleBarChrome(UIPanel panel, float panelWidth, float titleRowY, float pad, MouseEventHandler onCollapseClick)
        {
            Handles h = new Handles();

            UIDragHandle handle = panel.AddUIComponent<UIDragHandle>();
            handle.size = new Vector2(panelWidth, TitleRowHeight);
            handle.relativePosition = new Vector3(0f, titleRowY);
            handle.target = panel;
            h.DragHandle = handle;

            UIButton collapse = panel.AddUIComponent<UIButton>();
            collapse.size = new Vector2(CollapseButtonSize, CollapseButtonSize);
            collapse.relativePosition = new Vector3(panelWidth - pad - CollapseButtonSize, titleRowY);
            collapse.textScale = 0.8f;
            collapse.normalBgSprite = "ButtonMenu";
            collapse.hoveredBgSprite = "ButtonMenuHovered";
            collapse.pressedBgSprite = "ButtonMenuPressed";
            collapse.text = CollapseGlyphExpanded;
            collapse.eventClick += onCollapseClick;
            h.CollapseButton = collapse;

            return h;
        }

        /// <summary>指定した折りたたみ状態に対応するボタングリフを返す（呼び出し側の
        /// ApplyCollapsedState相当が _collapseButton.text へ設定するだけでよいようにする）。</summary>
        public static string CollapseGlyph(bool collapsed)
        {
            return collapsed ? CollapseGlyphCollapsed : CollapseGlyphExpanded;
        }

        /// <summary>Destroy()から呼ぶ。CollapseButtonのイベント購読を解除する
        /// （DragHandleは呼び出し側でイベントを購読していない前提のため対象外。パネル自体の破棄で
        /// GameObjectごと消える）。</summary>
        public static void Unsubscribe(Handles h, MouseEventHandler onCollapseClick)
        {
            if (h == null) return;
            if (h.CollapseButton != null) h.CollapseButton.eventClick -= onCollapseClick;
        }

        /// <summary>
        /// Task47: バニラのEsc（一時停止/オプション選択）メニューが開いているか。BaseInfoPanel/
        /// UnitInfoPanel/AssetAssignPanel の毎フレーム更新から呼ばれ、trueの間は各パネルを
        /// （内部の「選択中の基地/ユニット」等のロジック状態には触れず）見た目だけ隠すために使う。
        /// UIView.library.Get&lt;T&gt; は未登録/未生成の場合nullを返す（例外ではない）ため、
        /// ゲーム起動直後でPauseMenuがまだ無い状況は「メニューは開いていない」として扱う
        /// （BaseInfoPanel.TryGetVanillaPanelと同じ「未準備は通常経路」という方針）。
        /// </summary>
        public static bool IsGameMenuOpen()
        {
            try
            {
                PauseMenu pauseMenu = UIView.library.Get<PauseMenu>(PauseMenuName);
                return pauseMenu != null && pauseMenu.component != null && pauseMenu.component.isVisible;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PanelChrome.IsGameMenuOpen error: " + e);
                return false;
            }
        }
    }
}
