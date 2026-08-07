using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Player designation of a ballistic missile launch target (Task63). A single-base scaled-down
    /// port of exactly the same pattern as UnitCommandInput's rally-point targeting
    /// (HandleRallyTargeting): "record the press position; if the release position is within a
    /// threshold, commit it as a click, otherwise treat it as a camera-rotation drag and ignore it".
    ///
    /// The "designate launch target" button on BaseInfoPanel calls Arm(baseId) to enter targeting
    /// mode. Until a subsequent right click confirms the point (or Esc cancels), other operations
    /// are not obstructed (it holds state independent of UnitCommandInput, so there is no mutual
    /// exclusion with unit-command targeting — the UI itself offers no way to arm both at once, so
    /// there is no practical harm).
    ///
    /// Main thread only. Call it from WarfrontThreadingExtension.OnUpdate, after
    /// UnitCommandInput.Update.
    /// </summary>
    internal static class MissileLaunchTargeting
    {
        private const float ClickMoveThresholdPixels = 10f; // same value as UnitCommandInput

        private static bool _awaiting;
        private static ushort _armedBaseId;
        private static bool _rightMouseDownPending;
        private static Vector2 _rightMouseDownScreen;

        /// <summary>Whether launch-point targeting is in progress (for a future hint display; unused as of Task63).</summary>
        public static bool IsAwaiting { get { return _awaiting; } }

        /// <summary>Called from the "designate launch target" button on the base info panel. The
        /// next valid right click launches at that point (an arming notice is shown via CommandToast).</summary>
        public static void Arm(ushort baseId)
        {
            _awaiting = true;
            _armedBaseId = baseId;
            _rightMouseDownPending = false;
            ModConfig.Log("MissileLaunchTargeting: armed for base " + baseId + " - right-click a target (Esc cancels)");
            CommandToast.Show("Please set a missile launch target");
        }

        /// <summary>Called on level unload (via MilitaryManager.Reset). Leaves no targeting state behind.</summary>
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

                if (!PanelChrome.IsGameReadyForUi()) { Reset(); return; } // Task56: do not touch the UI library while loading/unloading
                if (PanelChrome.IsGameMenuOpen()) { Reset(); return; } // abort targeting once the menu opens
                if (UIView.HasInputFocus()) return; // ignore while a text input field has focus

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
                        _rightMouseDownPending = false; // clicks pressed down over UI do not count
                    }
                    else
                    {
                        _rightMouseDownPending = true;
                        _rightMouseDownScreen = Input.mousePosition;
                    }
                    return;
                }

                if (!Input.GetMouseButtonUp(1)) return; // wait until the right button is released

                bool wasPending = _rightMouseDownPending;
                _rightMouseDownPending = false;
                if (!wasPending) return;

                if (Vector2.Distance(Input.mousePosition, _rightMouseDownScreen) > ClickMoveThresholdPixels)
                {
                    return; // treated as a camera-rotation drag. Targeting continues; wait for the next click.
                }
                if (UIInput.hoveredComponent != null) return;

                // Task77: point resolution is delegated to GroundClickRaycast (Physics.Raycast →
                // terrain-intersection fallback).
                Vector3 clicked;
                string reason;
                if (!GroundClickRaycast.TryGetPoint(out clicked, out reason))
                {
                    ModConfig.Log("MissileLaunchTargeting: click rejected - " + reason);
                    return; // targeting continues. Retry on the next click.
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
                    // Even on failure (out of range / no stockpile etc.), arming is NOT cleared:
                    // this lets the player re-designate a different point within range (targeting
                    // continues until explicitly cancelled with Esc).
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
