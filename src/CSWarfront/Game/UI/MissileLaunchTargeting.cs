using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// プレイヤーによる弾道ミサイルの発射地点指定（Task63）。UnitCommandInputの集結地点指定
    /// （HandleRallyTargeting）と全く同じ「押し下げ位置を記録し、離した位置がしきい値以内なら
    /// クリックとして確定、それ以外はカメラ回転ドラッグとみなして無視する」パターンを1基地専用に
    /// 縮小移植したもの。
    ///
    /// BaseInfoPanel の「発射地点を指定」ボタンが Arm(baseId) を呼んでターゲティングモードへ入る。
    /// 以後の右クリックで地点を確定するまで（またはEscでキャンセルするまで）他の操作を妨げない
    /// （UnitCommandInputとは独立した状態を持つため、部隊コマンドのターゲティングと同時に排他制御は
    /// しない——同時に両方を武装する操作自体をUIが提供しないため実害は無い）。
    ///
    /// メインスレッド専用。WarfrontThreadingExtension.OnUpdate から、UnitCommandInput.Update の後に
    /// 呼ぶこと。
    /// </summary>
    internal static class MissileLaunchTargeting
    {
        private const float ClickMoveThresholdPixels = 10f; // UnitCommandInputと同じ値

        private static bool _awaiting;
        private static ushort _armedBaseId;
        private static bool _rightMouseDownPending;
        private static Vector2 _rightMouseDownScreen;

        /// <summary>発射地点のターゲティング中か（将来のヒント表示用、Task63時点は未使用）。</summary>
        public static bool IsAwaiting { get { return _awaiting; } }

        /// <summary>基地情報パネルの「発射地点を指定」ボタンから呼ばれる。次の有効な右クリックで
        /// その地点へ発射する（CommandToastで武装通知を出す）。</summary>
        public static void Arm(ushort baseId)
        {
            _awaiting = true;
            _armedBaseId = baseId;
            _rightMouseDownPending = false;
            ModConfig.Log("MissileLaunchTargeting: armed for base " + baseId + " - right-click a target (Esc cancels)");
            CommandToast.Show("Please set a missile launch target");
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。ターゲティング状態を残さない。</summary>
        public static void Reset()
        {
            _awaiting = false;
            _rightMouseDownPending = false;
            _armedBaseId = 0;
        }

        public static void Update()
        {
            try
            {
                if (!_awaiting) return;

                if (!PanelChrome.IsGameReadyForUi()) { Reset(); return; } // Task56: ロード/アンロード中はUIライブラリに触れない
                if (PanelChrome.IsGameMenuOpen()) { Reset(); return; } // メニューが開いたらターゲティングは打ち切る
                if (UIView.HasInputFocus()) return; // テキスト入力欄にフォーカスがある間は無視

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    ModConfig.Log("MissileLaunchTargeting: cancelled");
                    CommandToast.Show("Cancelled launch targeting");
                    Reset();
                    return;
                }

                if (Input.GetMouseButtonDown(1))
                {
                    if (UIInput.hoveredComponent != null)
                    {
                        _rightMouseDownPending = false; // UI上で押し下げたクリックは対象外
                    }
                    else
                    {
                        _rightMouseDownPending = true;
                        _rightMouseDownScreen = Input.mousePosition;
                    }
                    return;
                }

                if (!Input.GetMouseButtonUp(1)) return; // 右ボタンが離されるまで待つ

                bool wasPending = _rightMouseDownPending;
                _rightMouseDownPending = false;
                if (!wasPending) return;

                if (Vector2.Distance(Input.mousePosition, _rightMouseDownScreen) > ClickMoveThresholdPixels)
                {
                    return; // カメラ回転ドラッグとみなす。ターゲティングは継続し、次のクリックを待つ。
                }
                if (UIInput.hoveredComponent != null) return;

                // Task77: 地点の解決はGroundClickRaycastへ委譲（Physics.Raycast→地形交差フォールバック）。
                Vector3 clicked;
                string reason;
                if (!GroundClickRaycast.TryGetPoint(out clicked, out reason))
                {
                    ModConfig.Log("MissileLaunchTargeting: click rejected - " + reason);
                    return; // ターゲティング継続。次のクリックで再試行。
                }

                LaunchResult result = MilitaryManager.TryLaunchMissile(_armedBaseId, clicked);
                if (result == LaunchResult.Ok)
                {
                    ModConfig.Log("MissileLaunchTargeting: launched from base " + _armedBaseId + " at " +
                        clicked.x.ToString("0") + "," + clicked.z.ToString("0"));
                    CommandToast.Show("Launched");
                    Reset();
                }
                else
                {
                    ModConfig.Log("MissileLaunchTargeting: launch failed base=" + _armedBaseId + " result=" + result);
                    CommandToast.Show(FailMessage(result));
                    // 失敗（射程外/備蓄なし等）した場合も武装は解除しない: プレイヤーが射程内の別地点へ
                    // 指定し直せるようにする（Escで明示的にキャンセルするまで継続）。
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileLaunchTargeting.Update error: " + e);
                Reset();
            }
        }

        private static string FailMessage(LaunchResult r)
        {
            switch (r)
            {
                case LaunchResult.NoStockpile: return "No missiles in stockpile";
                case LaunchResult.OutOfRange: return "Out of range";
                case LaunchResult.NoOwner: return "No owner";
                case LaunchResult.NotMissileBase: return "Not a missile base";
                case LaunchResult.BaseNotFound: return "Base not found";
                default: return "";
            }
        }
    }
}
