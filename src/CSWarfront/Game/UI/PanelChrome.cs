using System;
using ColossalFramework;
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

        // Task56: クラッシュ後調査で、UIView.library.Get&lt;T&gt;（実体は ColossalFramework.UI.UIDynamicPanels.Get,
        // ilspycmdでColossalManaged.dllを逆コンパイルし確認）自体は m_CachedPanels（Dictionary）へのルックアップ
        // だけで、呼ぶたびにプレハブをインスタンス化することは無いと判明した（単一インスタンスパネルは全て
        // UIView.Awake→m_PanelsLibrary.Init(this)で起動時に一括生成済み、Getはキャッシュ済みインスタンスを
        // 返すだけ）。とはいえ「毎フレーム・複数パネルから」ライブラリ経由の型解決を行うこと自体は無駄な
        // GetComponent呼び出しを繰り返すだけでなく、UIView自体が未登録（ロード中等）だと
        // UIView.library がnullを返し UIDynamicPanels.Get の呼び出しがNullReferenceExceptionになる
        // （try/catchで既に握ってはいるが、ロード中は毎フレーム例外→ログの温床になる）。防御的に一度だけ
        // 解決してキャッシュし、以後は使い回す。Unity側でこのインスタンスが破棄された場合はUnityEngine.Object
        // のoperator==オーバーロードにより自動的に「null相当」に戻るが、念のためMilitaryManager.Reset()
        // （レベルアンロード時）からも明示的に ResetCache() で null 化する。
        private static PauseMenu _cachedPauseMenu;

        // Task56: UIView.GetAView()（static Dictionary&lt;string,UIView&gt;.Values.FirstOrDefault()、
        // ilspycmdで確認済み・こちらもインスタンス化はしない）を複数箇所が毎フレーム呼んでいたため、
        // 同じ考え方でキャッシュを共有する（BaseInfoPanelDrag.PositionNextToVanilla / UnitInfoPanel.
        // UpdateTrackingPosition / UnitBoxSelection.UpdateRectVisual が使用）。
        private static UIView _cachedView;

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
                // Task56: 毎フレームUIView.library.Get&lt;T&gt;を呼び直すのではなく、一度解決できたら
                // キャッシュを使い回す（上のフィールドコメント参照。Get自体は非破壊なルックアップだが、
                // ロード中はUIView.libraryがnullになりうるため、キャッシュ済みなら再解決自体を省略できる）。
                if (_cachedPauseMenu == null)
                {
                    UIDynamicPanels lib = UIView.library;
                    if (lib != null) _cachedPauseMenu = lib.Get<PauseMenu>(PauseMenuName);
                }
                return _cachedPauseMenu != null && _cachedPauseMenu.component != null && _cachedPauseMenu.component.isVisible;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PanelChrome.IsGameMenuOpen error: " + e);
                return false;
            }
        }

        /// <summary>Task56: UIView.GetAView()のキャッシュ済みアクセサ（上のフィールドコメント参照）。
        /// 毎フレーム呼ぶ複数箇所（BaseInfoPanelDrag/UnitInfoPanel/UnitBoxSelection）はこちらを使う。</summary>
        public static UIView GetCachedView()
        {
            if (_cachedView == null)
            {
                _cachedView = UIView.GetAView();
            }
            return _cachedView;
        }

        /// <summary>
        /// Task56: ゲームがUIライブラリ（バニラUI・自パネル問わず）に触れてよい状態か。
        /// レベルロード中/アンロード中は false を返し、呼び出し側（各パネルのEnsureCreated/
        /// UpdateVisibility、UnitSelection/UnitBoxSelection/UnitCommandInput等の毎フレームUI入口）は
        /// このフレームの処理を丸ごとスキップする（MilitaryManager.OnMainVisualUpdateのユニット見た目
        /// 同期＝Unity GameObjectのみを触る処理は対象外。UIライブラリに触れないため継続してよい）。
        ///
        /// 判定に使うシグナル（ilspycmdでAssembly-CSharp.dllのLoadingManagerを逆コンパイルし確認済み）:
        ///   - public volatile bool LoadingManager.m_loadingComplete: レベルロードのコルーチンが
        ///     全工程を終えた最後（OnLevelLoadedをMOD拡張へ配信する直前）にtrueへセットされる
        ///     （LoadingManager.cs 1813行目）。ロード開始時・アンロード開始時にはfalseへ戻す
        ///     （391/401, 429/439, 467/477行目）。
        ///   - public volatile bool LoadingManager.m_applicationQuitting: アプリ終了シーケンス開始でtrue。
        ///   - 存在確認は Singleton&lt;LoadingManager&gt;.exists（ = 内部static fieldのnullチェックのみ、
        ///     .instance と違いオブジェクトを新規生成しない）を先に見る。これはLoadingManager自身が
        ///     AutoSaveTimer内で使っている既存パターン（LoadingManager.cs 52行目）と同じ。
        /// いずれもvolatile boolの読み取りのみでアロケーションなし。
        /// </summary>
        public static bool IsGameReadyForUi()
        {
            try
            {
                return Singleton<LoadingManager>.exists
                    && Singleton<LoadingManager>.instance.m_loadingComplete
                    && !Singleton<LoadingManager>.instance.m_applicationQuitting;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PanelChrome.IsGameReadyForUi error: " + e);
                return false;
            }
        }

        /// <summary>Task56: MilitaryManager.Reset()（レベルアンロード時）から呼ぶ。キャッシュ済みの
        /// PauseMenu/UIView参照を破棄し、次セッションで改めて解決させる（テアダウン中にUnity側で
        /// 実際に破棄されるかどうかに関わらず、古い参照を持ち越さないための明示的なクリア）。</summary>
        public static void ResetCache()
        {
            _cachedPauseMenu = null;
            _cachedView = null;
        }
    }
}
