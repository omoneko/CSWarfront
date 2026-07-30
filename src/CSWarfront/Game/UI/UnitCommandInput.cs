using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// 部隊コマンドのホットキー入力（Task48）。WarfrontSettings.FreeAdvanceKey/HoldKey/RallyKey を
    /// 毎フレームポーリングし、Game/UI/UnitBoxSelection.SelectedIds を対象に MilitaryManager の
    /// コマンドラッパー（Game/MilitaryManagerUnitCommands.cs）を呼ぶ。
    ///
    /// RallyKeyは即座に命令を出さず、「次の右クリックで集結地点を指定する」ターゲティングモードへ入る
    /// （プレイヤーがまず地点を選ぶ必要があるため）。ターゲティング中はEscでキャンセルできる。
    /// 地点のワールド座標は Game/UI/GroundClickRaycast で解決する（Task77）:
    /// Physics.Raycast（ユニット/建物などMOD自前コライダーへの精密ヒット）→外れたら
    /// Core.TerrainRaycast（TerrainManager高さサンプリングとの交差計算）の順。
    /// CS1の地形はUnity物理コライダーを持たないため、Physics.Raycast単独では開けた地面の
    /// クリックが全て失敗する（Task62時点の「地形コライダーも反応する」という前提は誤りだった。
    /// ユニット選択が動いていたのはUnitVisualsが自前コライダーを付けているため）。
    ///
    /// Task62（実機ログで右クリックによる集結地点の指定が一度も成功していなかった不具合の修正）:
    /// 旧実装は Input.GetMouseButtonDown(1) の立ち上がりフレームだけで即座にraycastしていた。これだと
    /// 「カメラを回転させるための右クリック押しっぱなし+ドラッグ」の押し始めがたまたま開けた地面の
    /// 上だった場合、プレイヤーがカメラ回転のつもりで押した瞬間に意図せず集結地点が確定してしまう
    /// （＝Downだけを見ると「クリック」と「ドラッグの開始」を区別できない）。新実装は押し下げ位置を
    /// 記録しておき、Input.GetMouseButtonUp(1) で「離した位置が押し下げ位置からClickMoveThresholdPixels
    /// 以内」であることを確認してから初めてraycastする（＝カメラ回転ドラッグは無視し、その場でのクリック
    /// のみ地点として確定する）。副次的な効果として、何らかの理由で押し下げフレームの検知を取りこぼしても
    /// 離す瞬間のフレームでも判定できるため、単一フレームの検知漏れにも強くなる。
    /// 却下されたクリックは理由ごとに1回だけログする（UI上で押した/UI上で離した/カメラ回転とみなした/
    /// カメラ未準備/raycastが何にも当たらなかった）。実機ログだけで原因を切り分けられるようにするため。
    ///
    /// ホットキー/右クリックのどちらも、バニラのEscメニューが開いている間・何らかのテキスト入力欄に
    /// フォーカスがある間は完全に無視する（ColossalFramework.UI.UIView.HasInputFocus()、
    /// ColossalManaged.dll をリフレクションで確認済みの public static bool メソッド）。
    ///
    /// メインスレッド専用。WarfrontThreadingExtension.OnUpdate から、UnitBoxSelection.Update の後に呼ぶこと
    /// （同じフレームで確定した選択を対象にコマンドを出せるようにするため）。
    /// </summary>
    public static class UnitCommandInput
    {
        /// <summary>右クリックの押し下げ位置からこの距離（実スクリーンピクセル）を超えて動いてから
        /// 離した場合は「カメラ回転ドラッグ」とみなし、集結地点としては確定しない（Task62）。
        /// UnitBoxSelection.DragThresholdPixelsと同じ考え方・同じ値を採用する。</summary>
        private const float ClickMoveThresholdPixels = 10f;

        private static bool _awaitingRallyClick;

        // Task62: 右クリックの押し下げ〜離すまでを追跡するための状態（HandleRallyTargeting専用）。
        private static bool _rightMouseDownPending; // UI外で右ボタンが押し下げられ、まだ離されていない
        private static Vector2 _rightMouseDownScreen;

        /// <summary>集結地点のターゲティング中か（Game/UI/UnitInfoPanel等がヒント表示に使ってよい、Task48時点は未使用）。</summary>
        public static bool IsAwaitingRallyClick { get { return _awaitingRallyClick; } }

        public static void Update()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi())
                {
                    _awaitingRallyClick = false; // Task56: ロード/アンロード中はUIライブラリに触れない
                    _rightMouseDownPending = false;
                    return;
                }

                if (PanelChrome.IsGameMenuOpen())
                {
                    _awaitingRallyClick = false; // メニューが開いたらターゲティングは打ち切る
                    _rightMouseDownPending = false;
                    return;
                }
                if (UIView.HasInputFocus()) return; // テキスト入力欄にフォーカスがある間はホットキーを一切拾わない

                if (_awaitingRallyClick)
                {
                    HandleRallyTargeting();
                    return; // ターゲティング中は他のホットキーを無視（誤操作防止）
                }

                if (IsHotkeyDown(WarfrontSettings.FreeAdvanceKey))
                {
                    IssueFreeAdvance();
                }
                else if (IsHotkeyDown(WarfrontSettings.HoldKey))
                {
                    IssueHold();
                }
                else if (IsHotkeyDown(WarfrontSettings.RallyKey))
                {
                    BeginRallyTargeting();
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitCommandInput.Update error: " + e);
                _awaitingRallyClick = false;
                _rightMouseDownPending = false;
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。ターゲティング状態を残さない。</summary>
        public static void Reset()
        {
            _awaitingRallyClick = false;
            _rightMouseDownPending = false;
        }

        /// <summary>Task62（NumLock対策）: WarfrontSettings.KeyOptionsのテンキー候補(Keypad0〜9)が
        /// 割り当てられている間、NumLockがOFFのWindows環境ではOS側がテンキーの物理キーを別のキー
        /// （矢印/Home/End等）として送るため、Unityの Input.GetKeyDown(KeyCode.KeypadN) が
        /// 一切反応しない既知の問題がある。対策として、テンキーが割り当てられている場合は対応する
        /// 最上段の数字キー（Alpha0〜9）も常にフォールバックとして受け付ける
        /// （両方のキーで反応する＝害はなく、NumLock状態を問わず必ずどちらかで発火する）。
        /// テンキー以外（F5〜F12等）が割り当てられている場合は従来通り単純に GetKeyDown するだけ。
        /// Task76: internal化してUnitBoxSelectionの部隊選択モードキー（既定Numpad0、同じKeyOptions
        /// テンキー候補群）からも再利用する（NumLock対策ロジックを重複させないため）。</summary>
        internal static bool IsHotkeyDown(KeyCode key)
        {
            if (Input.GetKeyDown(key)) return true;

            KeyCode fallback;
            if (TryGetTopRowFallback(key, out fallback) && Input.GetKeyDown(fallback)) return true;

            return false;
        }

        private static bool TryGetTopRowFallback(KeyCode key, out KeyCode fallback)
        {
            switch (key)
            {
                case KeyCode.Keypad0: fallback = KeyCode.Alpha0; return true;
                case KeyCode.Keypad1: fallback = KeyCode.Alpha1; return true;
                case KeyCode.Keypad2: fallback = KeyCode.Alpha2; return true;
                case KeyCode.Keypad3: fallback = KeyCode.Alpha3; return true;
                case KeyCode.Keypad4: fallback = KeyCode.Alpha4; return true;
                case KeyCode.Keypad5: fallback = KeyCode.Alpha5; return true;
                case KeyCode.Keypad6: fallback = KeyCode.Alpha6; return true;
                case KeyCode.Keypad7: fallback = KeyCode.Alpha7; return true;
                case KeyCode.Keypad8: fallback = KeyCode.Alpha8; return true;
                case KeyCode.Keypad9: fallback = KeyCode.Alpha9; return true;
                default: fallback = key; return false;
            }
        }

        private static void IssueFreeAdvance()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            int n = MilitaryManager.CommandFreeAdvance(UnitBoxSelection.SelectedIds);
            CommandToast.Show("Advance x" + n);
        }

        private static void IssueHold()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            int n = MilitaryManager.CommandHold(UnitBoxSelection.SelectedIds);
            CommandToast.Show("Hold x" + n);
        }

        private static void BeginRallyTargeting()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            _awaitingRallyClick = true;
            _rightMouseDownPending = false;
            ModConfig.Log("UnitCommandInput: rally targeting armed for " + UnitBoxSelection.SelectedIds.Count +
                " unit(s) - right-click a destination (Esc cancels)");
            CommandToast.Show("Rally & Hold (right-click to set a destination)");
        }

        private static void HandleRallyTargeting()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _awaitingRallyClick = false;
                _rightMouseDownPending = false;
                ModConfig.Log("UnitCommandInput: rally targeting cancelled");
                CommandToast.Show("Cancelled rally targeting");
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                if (UIInput.hoveredComponent != null)
                {
                    // UI上で押し下げたクリックは対象外（ターゲティング自体は継続、離すまで待つ必要も無い）。
                    _rightMouseDownPending = false;
                    ModConfig.Log("UnitCommandInput: rally click rejected - pressed over UI");
                }
                else
                {
                    _rightMouseDownPending = true;
                    _rightMouseDownScreen = Input.mousePosition;
                }
                return;
            }

            if (!Input.GetMouseButtonUp(1)) return; // 右ボタンが離されるまで待つ。それまでターゲティング状態を維持する。

            bool wasPending = _rightMouseDownPending;
            _rightMouseDownPending = false;
            if (!wasPending) return; // 押し下げがUI上だった、またはこのモードに入る前から押されていた分は無視。

            if (Vector2.Distance(Input.mousePosition, _rightMouseDownScreen) > ClickMoveThresholdPixels)
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - treated as camera drag");
                return; // カメラ回転ドラッグとみなす。ターゲティングは継続し、次のクリックを待つ。
            }

            if (UIInput.hoveredComponent != null)
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - released over UI");
                return;
            }

            // Task77: 地点の解決はGroundClickRaycastへ委譲（Physics.Raycast→地形交差フォールバック）。
            // CS1の地形はコライダーを持たないため、従来のPhysics.Raycast単独では開けた地面の
            // クリックが全て「raycast hit nothing」で却下されていた。
            Vector3 clicked;
            string reason;
            if (!GroundClickRaycast.TryGetPoint(out clicked, out reason))
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - " + reason);
                return; // ターゲティング継続。次のクリックで再試行。
            }

            WorldPos point = new WorldPos(clicked.x, clicked.y, clicked.z);
            int n = MilitaryManager.CommandRally(UnitBoxSelection.SelectedIds, point);
            ModConfig.Log("UnitCommandInput: rally point set at " + point.X.ToString("0") + "," +
                point.Z.ToString("0") + " for " + n + " unit(s)");
            CommandToast.Show("Rally point set x" + n);
            _awaitingRallyClick = false;
        }
    }
}
