using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task106 (user request "I want trenches to be a repeating model I can use like roads"):
    /// trench line placement.
    ///
    /// The Trench button on the Military Construction panel enters this mode; specifying
    ///   1st right-click = start point → 2nd right-click = end point
    /// makes MilitaryManager.RequestTrenchLine place trench buildings continuously between the two
    /// points at 32m intervals (the actual building creation happens on the sim thread, see
    /// MilitaryManagerTrenchLine.cs).
    ///
    /// Because we call CreateBuilding directly instead of using the vanilla BuildingTool, the
    /// vanilla "must be placed adjacent to a road" placement requirement never applies = trench
    /// lines can be dug freely in open fields (works around the behavior the user flagged as
    /// troublesome; the post-placement "road not connected" warning is icon-suppressed by
    /// MilitaryManager for all fortification types).
    ///
    /// Click detection uses the same "press → release within movement threshold = click" pattern as
    /// MissileLaunchTargeting (to distinguish from camera-rotation drags). Esc cancels. Main
    /// thread only.
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

        /// <summary>Called from the Trench button on the Military Construction panel.</summary>
        public static void Begin()
        {
            _awaiting = true;
            _hasStart = false;
            _rightMouseDownPending = false;
            ModConfig.Log("TrenchLineTargeting: armed - right-click start point (Esc cancels)");
            CommandToast.Show(WarfrontStrings.Trench_StartPrompt);
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
                    CommandToast.Show(WarfrontStrings.Trench_CancelledToast);
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
                    CommandToast.Show(WarfrontStrings.Trench_EndPrompt);
                    return;
                }

                MilitaryManager.RequestTrenchLine(_start, clicked);
                CommandToast.Show(WarfrontStrings.Trench_DiggingToast);
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
