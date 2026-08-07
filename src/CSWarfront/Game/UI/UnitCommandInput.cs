using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Hotkey input for unit commands (Task48). Polls WarfrontSettings.FreeAdvanceKey/HoldKey/RallyKey
    /// every frame and calls MilitaryManager's command wrappers (Game/MilitaryManagerUnitCommands.cs)
    /// targeting Game/UI/UnitBoxSelection.SelectedIds.
    ///
    /// RallyKey does not issue an order immediately; it enters a targeting mode where "the next right-click
    /// designates the rally point" (because the player must pick a location first). Targeting can be
    /// cancelled with Esc while active.
    /// The location's world coordinates are resolved by Game/UI/GroundClickRaycast (Task77):
    /// Physics.Raycast (precise hits against the mod's own colliders such as units/buildings) → on a miss,
    /// Core.TerrainRaycast (intersection against TerrainManager height sampling).
    /// CS1's terrain has no Unity physics colliders, so with Physics.Raycast alone every click on open
    /// ground fails (the Task62-era assumption that "the terrain collider also responds" was wrong;
    /// unit selection worked only because UnitVisuals attaches its own colliders).
    ///
    /// Task62 (fix for the bug where, per in-game logs, right-click rally point designation never succeeded
    /// even once): the old implementation raycast immediately, only on the Input.GetMouseButtonDown(1)
    /// rising-edge frame. With that, if the start of a "hold right button + drag to rotate the camera"
    /// gesture happened to be over open ground, a rally point was unintentionally confirmed the moment the
    /// player pressed the button intending to rotate the camera (= looking at Down alone cannot distinguish
    /// a "click" from the "start of a drag"). The new implementation records the press-down position and
    /// only raycasts once Input.GetMouseButtonUp(1) confirms that "the release position is within
    /// ClickMoveThresholdPixels of the press-down position" (= camera-rotation drags are ignored and only an
    /// in-place click confirms the location). As a side benefit, even if detection of the press-down frame
    /// is somehow missed, the check can still fire on the release frame, making it robust against
    /// single-frame detection misses.
    /// Rejected clicks are logged once per reason (pressed over UI / released over UI / treated as camera
    /// rotation / camera not ready / raycast hit nothing), so the cause can be isolated from in-game logs
    /// alone.
    ///
    /// Both hotkeys and right-clicks are ignored entirely while the vanilla Esc menu is open or while any
    /// text input field has focus (ColossalFramework.UI.UIView.HasInputFocus(), a public static bool method
    /// verified via reflection on ColossalManaged.dll).
    ///
    /// Main thread only. Call from WarfrontThreadingExtension.OnUpdate, after UnitBoxSelection.Update
    /// (so commands can target a selection confirmed in the same frame).
    /// </summary>
    public static class UnitCommandInput
    {
        /// <summary>If the mouse moved farther than this distance (in real screen pixels) from the
        /// right-click press-down position before being released, it is treated as a "camera-rotation drag"
        /// and not confirmed as a rally point (Task62).
        /// Same rationale and same value as UnitBoxSelection.DragThresholdPixels.</summary>
        private const float ClickMoveThresholdPixels = 10f;

        private static bool _awaitingRallyClick;

        // Task62: state for tracking a right-click from press-down to release (used only by HandleRallyTargeting).
        private static bool _rightMouseDownPending; // The right button was pressed outside UI and has not been released yet
        private static Vector2 _rightMouseDownScreen;

        /// <summary>Whether rally-point targeting is active (may be used by Game/UI/UnitInfoPanel etc. for hint display; unused as of Task48).</summary>
        public static bool IsAwaitingRallyClick { get { return _awaitingRallyClick; } }

        public static void Update()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi())
                {
                    _awaitingRallyClick = false; // Task56: do not touch the UI library while loading/unloading
                    _rightMouseDownPending = false;
                    return;
                }

                if (PanelChrome.IsGameMenuOpen())
                {
                    _awaitingRallyClick = false; // Abort targeting when the menu opens
                    _rightMouseDownPending = false;
                    return;
                }
                if (UIView.HasInputFocus()) return; // Do not pick up any hotkey while a text input field has focus

                if (_awaitingRallyClick)
                {
                    HandleRallyTargeting();
                    return; // Ignore the other hotkeys while targeting (prevents mis-operation)
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

        /// <summary>Call at level unload (via MilitaryManager.Reset). Leaves no targeting state behind.</summary>
        public static void Reset()
        {
            _awaitingRallyClick = false;
            _rightMouseDownPending = false;
        }

        /// <summary>Task62 (NumLock workaround): while one of the numpad candidates (Keypad0-9) from
        /// WarfrontSettings.KeyOptions is assigned, there is a known problem on Windows environments with
        /// NumLock OFF where the OS sends the numpad's physical keys as different keys (arrows/Home/End
        /// etc.), so Unity's Input.GetKeyDown(KeyCode.KeypadN) never responds at all. As a workaround, when
        /// a numpad key is assigned, the corresponding top-row digit key (Alpha0-9) is always accepted as a
        /// fallback too (responding to both keys does no harm, and one of the two is guaranteed to fire
        /// regardless of the NumLock state).
        /// When something other than the numpad (F5-F12 etc.) is assigned, it is a plain GetKeyDown as
        /// before.
        /// Task76: made internal and reused by UnitBoxSelection's unit selection mode key (default Numpad0,
        /// from the same KeyOptions numpad candidate group) to avoid duplicating the NumLock workaround
        /// logic.</summary>
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
                    // A click pressed down over UI is excluded (targeting itself continues; no need to wait for the release either).
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

            if (!Input.GetMouseButtonUp(1)) return; // Wait until the right button is released. Targeting state is kept until then.

            bool wasPending = _rightMouseDownPending;
            _rightMouseDownPending = false;
            if (!wasPending) return; // Ignore presses that were over UI, or that were already held before entering this mode.

            if (Vector2.Distance(Input.mousePosition, _rightMouseDownScreen) > ClickMoveThresholdPixels)
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - treated as camera drag");
                return; // Treated as a camera-rotation drag. Targeting continues; wait for the next click.
            }

            if (UIInput.hoveredComponent != null)
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - released over UI");
                return;
            }

            // Task77: location resolution is delegated to GroundClickRaycast (Physics.Raycast → terrain
            // intersection fallback). CS1's terrain has no colliders, so with the previous Physics.Raycast
            // alone every click on open ground was rejected with "raycast hit nothing".
            Vector3 clicked;
            string reason;
            if (!GroundClickRaycast.TryGetPoint(out clicked, out reason))
            {
                ModConfig.Log("UnitCommandInput: rally click rejected - " + reason);
                return; // Targeting continues. Retry on the next click.
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
