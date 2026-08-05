using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task106（ユーザー要望「塹壕を繰り返しモデルにして道路のように使いたい」）: 塹壕ライン敷設。
    ///
    /// Military ConstructionパネルのTrenchボタンでこのモードに入り、
    ///   右クリック1回目=起点 → 右クリック2回目=終点
    /// を指定すると、MilitaryManager.RequestTrenchLineが2点間に塹壕建物を32m間隔で連続配置する
    /// （実際の建物生成はsimスレッド、MilitaryManagerTrenchLine.cs参照）。
    ///
    /// バニラのBuildingToolを使わず直接CreateBuildingするため、「道路に接して配置」という
    /// バニラの配置要件を通らない＝野原に自由に塹壕線を掘れる（ユーザーが厄介と指摘した仕様の回避。
    /// 配置後の「道路未接続」警告はMilitaryManager側が築城全種でアイコン抑制する）。
    ///
    /// クリック判定はMissileLaunchTargetingと同じ「押下→移動閾値以内で離す＝クリック」パターン
    /// （カメラ回転ドラッグと区別する）。Escでキャンセル。メインスレッド専用。
    /// </summary>
    internal static class TrenchLineTargeting
    {
        private const float ClickMoveThresholdPixels = 10f;

        private static bool _awaiting;
        private static bool _hasStart;
        private static Vector3 _start;
        private static bool _rightMouseDownPending;
        private static Vector2 _rightMouseDownScreen;

        public static bool IsAwaiting { get { return _awaiting; } }

        /// <summary>Military ConstructionパネルのTrenchボタンから呼ばれる。</summary>
        public static void Begin()
        {
            _awaiting = true;
            _hasStart = false;
            _rightMouseDownPending = false;
            ModConfig.Log("TrenchLineTargeting: armed - right-click start point (Esc cancels)");
            CommandToast.Show("Trench line: right-click the START point (Esc to cancel)");
        }

        public static void Reset()
        {
            _awaiting = false;
            _hasStart = false;
            _rightMouseDownPending = false;
        }

        public static void Update()
        {
            try
            {
                if (!_awaiting) return;

                if (!PanelChrome.IsGameReadyForUi()) { Reset(); return; }
                if (PanelChrome.IsGameMenuOpen()) { Reset(); return; }
                if (UIView.HasInputFocus()) return;

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CommandToast.Show("Cancelled trench line");
                    Reset();
                    return;
                }

                if (Input.GetMouseButtonDown(1))
                {
                    if (UIInput.hoveredComponent != null) _rightMouseDownPending = false;
                    else
                    {
                        _rightMouseDownPending = true;
                        _rightMouseDownScreen = Input.mousePosition;
                    }
                    return;
                }

                if (!Input.GetMouseButtonUp(1)) return;

                bool wasPending = _rightMouseDownPending;
                _rightMouseDownPending = false;
                if (!wasPending) return;
                if (Vector2.Distance(Input.mousePosition, _rightMouseDownScreen) > ClickMoveThresholdPixels) return;
                if (UIInput.hoveredComponent != null) return;

                Vector3 clicked;
                string reason;
                if (!GroundClickRaycast.TryGetPoint(out clicked, out reason))
                {
                    ModConfig.Log("TrenchLineTargeting: click rejected - " + reason);
                    return;
                }

                if (!_hasStart)
                {
                    _start = clicked;
                    _hasStart = true;
                    CommandToast.Show("Trench line: right-click the END point");
                    return;
                }

                MilitaryManager.RequestTrenchLine(_start, clicked);
                CommandToast.Show("Digging trench line...");
                Reset();
            }
            catch (Exception e)
            {
                ModConfig.LogError("TrenchLineTargeting.Update error: " + e);
                Reset();
            }
        }
    }
}
