using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Box selection of units (Task48). The existing single-click selection (Game/UI/UnitSelection.Update,
    /// which raycasts immediately on the GetMouseButtonDown rising-edge frame and, on a hit, writes to
    /// SelectedInstanceId) is left completely untouched. This class only overwrites the selection with the
    /// box-selection result at mouse up, and only when "that click actually turned out to be the start of a
    /// drag". This satisfies the requirement that "an ordinary click that never becomes a drag keeps the
    /// existing single-click selection behavior" without duplicating or modifying any of the existing raycast
    /// logic (UnitSelection.Update itself keeps being called every frame as before).
    ///
    /// Conditions for a drag: if, at the moment the left button is pressed down, the cursor is over UI
    /// (UIInput.hoveredComponent!=null) or the vanilla Esc menu is open, the entire drag is ignored (no
    /// rectangle is shown and the selection is not changed). Only once the cursor has moved more than
    /// DragThresholdPixels from the press-down position is the drag considered "confirmed" and rectangle
    /// drawing begins. If the button is released before that, it is treated as "just a click" and the result
    /// of UnitSelection.Update is left as-is.
    ///
    /// Where the selection result goes: both SelectedIds (all box-selected IDs, used by the command input
    /// Game/UI/UnitCommandInput) and UnitSelection.SelectedInstanceId (the first entry, referenced by the
    /// existing unit info panel for the single-unit case; 0 = nothing selected when the list is empty).
    ///
    /// The rectangle hit test is done entirely in screen coordinates (Camera.WorldToScreenPoint and
    /// Input.mousePosition, both bottom-left origin, real pixels) and never goes through the
    /// ColossalFramework UIView virtual GUI resolution (which may not match real pixels depending on the UI
    /// scale setting). This keeps the selection test itself independent of the UI scale setting.
    ///
    /// The rectangle's "visual" (UpdateRectVisual below) uses UIPanel.relativePosition, so it must be
    /// converted into the UIView GUI coordinate space (a resolution reflecting UIView.fixedHeight/UI scale,
    /// top-left origin).
    /// Task76 (fix for the bug reported in-game as "the rectangle is offset from where I dragged"): the old
    /// implementation called view.ScreenPointToGUI(rawScreenPos) directly, but decompiling
    /// ColossalManaged.dll with ilspycmd shows that ScreenPointToGUI itself is only a Y-flip —
    /// `position.y = GetScreenResolution().y - position.y; return position;` — and performs no scale
    /// conversion from real screen pixels to the GUI resolution. That scale conversion is done separately
    /// inside UIView.WorldPointToGUI
    /// (`screenResolution.x * (rawX / uiCamera.pixelWidth)` etc.). In other words the old implementation
    /// did "Y-flip only, no scale conversion", so on any environment where fixedHeight (default 1080) does
    /// not match the real resolution, or the UI scale is not at its default (100%), it was always offset
    /// (the error grows in proportion to distance from the origin, so it becomes more pronounced the farther
    /// you drag). The hit-test side (FinishBoxSelect, above) works entirely in real screen-pixel space and
    /// never goes through this conversion, so the selection itself was unaffected by this bug.
    /// Fix: ScreenToGuiPoint below performs the same scale conversion as UIView.WorldPointToGUI
    /// (the GetScreenResolution() ÷ uiCamera.pixelWidth/pixelHeight ratio) before calling ScreenPointToGUI
    /// (unifying on the same, verified formula UIView itself uses). Both the start point and the current
    /// point in UpdateRectVisual now go through this helper.
    ///
    /// Highlighting selected units: a thin cylinder primitive that tracks each selected unit's position every
    /// frame (with the collider removed, so it never interferes with click hit-testing via Physics.Raycast).
    /// Adopted as a cheap visual cue. A single shared material is created and reused by every highlight (the
    /// units' own materials/faction colors are never modified).
    ///
    /// Unit selection mode (Task76, WarfrontSettings.SelectionModeKey, default Numpad0): each press of this
    /// hotkey toggles ON/OFF. Box-drag selection (this class's main feature) only works while ON.
    /// Single-click selection (UnitSelection.Update, plus the SelectedIds follow-up in the mouse down branch
    /// above) always works regardless of the mode's state. A mouse down over UI while the mode is ON is,
    /// as before, not treated as a drag candidate (_pendingDragCandidate, below). Pressing SelectionModeKey
    /// again, or Esc, returns it to OFF (switching to OFF mid-drag also discards the in-progress drag
    /// immediately).
    ///
    /// Main thread only (because it calls Unity/ColossalFramework UI APIs). Call from
    /// WarfrontThreadingExtension.OnUpdate, after position sync (MilitaryManager.OnMainVisualUpdate) and
    /// before UnitInfoPanel.
    /// </summary>
    public static class UnitBoxSelection
    {
        private const string RectPanelName = "CSWarfrontBoxSelectRect";

        /// <summary>Movement beyond this distance (in real screen pixels) is what first counts as a "drag".
        /// Slack so that a "plain click" with hand-tremor-level movement is not mistakenly treated as a drag.
        /// Task62: this value was the root cause of the repeated "selected 0 unit(s) via drag" entries in
        /// in-game logs. The old value (6px) was easily exceeded even by an ordinary click on high-DPI setups
        /// or due to mouse sensor jitter, so a normal single click was mistakenly classified as a drag and,
        /// if the rectangle contained nothing, wiped the selection as collateral damage (mitigated twice
        /// over, together with FinishBoxSelect's "an empty drag does not clear the selection" rule described
        /// below). Raised to 10px, with 8px+ as the recommended range.</summary>
        private const float DragThresholdPixels = 10f;

        private const float MaxCameraDistanceCheck = 100000f; // Used only for the z>0 check of WorldToScreenPoint (no distance clamping)

        // Visual constants for the highlight (selection marker). Same family as
        // UnitVisuals.AttachVisibilityMarker, but a separate thin cylinder so it is visually distinct from
        // the unit's own marker/mesh.
        private const float HighlightRadius = 5f;
        private const float HighlightThinHeight = 0.15f;
        private const float HighlightYOffset = 0.3f;

        public static readonly List<uint> SelectedIds = new List<uint>();

        private static UIPanel _rectPanel;

        private static bool _pendingDragCandidate; // Mouse down happened outside UI = may develop into a drag
        private static bool _dragging;              // Confirmed by exceeding DragThresholdPixels
        private static Vector2 _dragStartScreen;

        private static bool _selectionModeActive; // Task76: toggled by WarfrontSettings.SelectionModeKey. Box drag is allowed only while ON

        /// <summary>Whether unit selection mode (Task76) is currently ON. Does not affect single-click
        /// selection (always active, see the class-level comment above). May be used by UI for hint display
        /// etc.; unused as of Task76.</summary>
        public static bool IsSelectionModeActive { get { return _selectionModeActive; } }

        /// <summary>UnitSelection.SelectedInstanceId as of the end of the previous frame (Task48). If it
        /// differs at mouse down time, we can conclude "this press caused UnitSelection.Update (already run
        /// earlier in the same frame) to newly hit a unit". Used to keep SelectedIds in sync even for single
        /// clicks (see the comment at the top of Update below).</summary>
        private static uint _lastSeenSelectedInstanceId;

        private static readonly List<uint> _idBuffer = new List<uint>();
        private static readonly List<Vector3> _posBuffer = new List<Vector3>();
        private static readonly List<uint> _foundBuffer = new List<uint>(); // Task62: scratch area holding FinishBoxSelect results before committing (avoids GC)

        private static readonly Dictionary<uint, GameObject> _highlightMarkers = new Dictionary<uint, GameObject>();
        private static readonly List<uint> _staleHighlightIds = new List<uint>();
        private static Material _highlightMaterial;

        /// <summary>Idempotent. Creates the rectangle panel exactly once, when the UIView is ready (same
        /// approach as the other panels).</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_rectPanel != null) return;
                UIView view = PanelChrome.GetCachedView();
                if (view == null) return;
                if (view.FindUIComponent<UIPanel>(RectPanelName) != null) return;

                UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
                if (panel == null)
                {
                    ModConfig.LogError("UnitBoxSelection.EnsureCreated: failed to create UIPanel");
                    return;
                }
                panel.name = RectPanelName;
                // "EmptySprite": a solid 1x1 sprite included in the vanilla UI atlas. Tinted via color and
                // used as a translucent rectangle (a classic combination widely used in CS modding for
                // solid-color rectangle overlays). Even if this sprite name did not exist in some
                // environment, ColossalFramework would simply draw nothing rather than throw, so only the
                // rectangle would be invisible — the selection logic itself (screen-space hit test) is
                // unaffected.
                panel.backgroundSprite = "EmptySprite";
                panel.color = new Color32(120, 170, 255, 90);
                panel.isInteractive = false; // Do not intercept clicks/drags
                panel.isVisible = false;
                _rectPanel = panel;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.EnsureCreated error: " + e);
            }
        }

        /// <summary>Call every main-thread frame. Must run in the same frame as UnitSelection.Update and
        /// after it (the call order is guaranteed by WarfrontThreadingExtension.OnUpdate).</summary>
        public static void Update()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi())
                {
                    // Task56: do not touch the UI library while loading/unloading. Also discard any drag candidate in progress.
                    CancelDrag();
                    return;
                }

                if (PanelChrome.IsGameMenuOpen())
                {
                    CancelDrag();
                    _lastSeenSelectedInstanceId = UnitSelection.SelectedInstanceId;
                    return;
                }

                HandleSelectionModeToggle(); // Task76: fine to evaluate before single-click selection (applying the toggle immediately within the same frame is not a problem)

                if (Input.GetMouseButtonDown(0))
                {
                    // A press that started over UI is not a drag candidate (same guard as UnitSelection.Update).
                    _pendingDragCandidate = UIInput.hoveredComponent == null;
                    _dragging = false;
                    _dragStartScreen = Input.mousePosition;

                    if (_pendingDragCandidate)
                    {
                        // UnitSelection.Update has already run earlier in this frame. If this press newly
                        // hit a unit (i.e. the value differs from what it was at the end of the previous
                        // frame), keep SelectedIds in sync even if the press never develops into a drag
                        // (to keep SelectedIds/SelectedInstanceId consistent for single clicks too). If
                        // nothing was hit / it is the same as before, do nothing — preserving
                        // UnitSelection's original contract that "a miss keeps the current selection".
                        uint clicked = UnitSelection.SelectedInstanceId;
                        if (clicked != 0 && clicked != _lastSeenSelectedInstanceId)
                        {
                            SelectedIds.Clear();
                            SelectedIds.Add(clicked);
                        }
                    }
                }
                else if (_selectionModeActive && _pendingDragCandidate && Input.GetMouseButton(0))
                {
                    // Task76: only advance to drag confirmation (rectangle drawing) while unit selection
                    // mode is ON. While OFF, the recording of _pendingDragCandidate/_dragStartScreen itself
                    // still happens, but this branch is never entered, so no rectangle appears and _dragging
                    // never becomes true = FinishBoxSelect is not called at mouse up either
                    // (single-click selection was fully handled in the mouse down branch above, so it is
                    // unaffected by the mode).
                    Vector2 cur = Input.mousePosition;
                    if (!_dragging && Vector2.Distance(cur, _dragStartScreen) >= DragThresholdPixels)
                    {
                        _dragging = true;
                    }
                    if (_dragging) UpdateRectVisual(_dragStartScreen, cur);
                }
                else if (Input.GetMouseButtonUp(0))
                {
                    if (_pendingDragCandidate && _dragging)
                    {
                        FinishBoxSelect(_dragStartScreen, Input.mousePosition);
                    }
                    // If the drag never got confirmed (it was just a click), do nothing here =
                    // keep the single-click selection already applied in the mouse down branch above (or
                    // UnitSelection's "keep the current selection" behavior on a miss) as-is.
                    CancelDrag();
                }

                // Task88 (fix for the "green rim remains after deselection" bug): when the single selection
                // is cleared externally (UnitInfoPanel's close button / ESC / death of the selected unit
                // etc. called UnitSelection.Clear), clear the multi-selection list along with it. Previously
                // there was no code path here that cleared SelectedIds, so SyncHighlights kept drawing
                // highlights even after deselection.
                if (UnitSelection.SelectedInstanceId == 0 && _lastSeenSelectedInstanceId != 0 && SelectedIds.Count > 0)
                {
                    SelectedIds.Clear();
                }

                SyncHighlights();
                _lastSeenSelectedInstanceId = UnitSelection.SelectedInstanceId;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.Update error: " + e);
                CancelDrag();
            }
        }

        /// <summary>Call at level unload (via MilitaryManager.Reset). Destroys the panel/markers and leaves no static state behind.</summary>
        public static void Destroy()
        {
            try
            {
                if (_rectPanel != null) UnityEngine.Object.Destroy(_rectPanel.gameObject);
                foreach (var kv in _highlightMarkers)
                {
                    if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.Destroy error: " + e);
            }
            finally
            {
                _rectPanel = null;
                _highlightMarkers.Clear();
                SelectedIds.Clear();
                _pendingDragCandidate = false;
                _dragging = false;
                _lastSeenSelectedInstanceId = 0;
                _selectionModeActive = false; // Task76: resetting to the default (OFF) across sessions is acceptable (same MVP policy as the other WarfrontSettings)
            }
        }

        private static void CancelDrag()
        {
            _pendingDragCandidate = false;
            _dragging = false;
            if (_rectPanel != null && _rectPanel.isVisible) _rectPanel.Hide();
        }

        /// <summary>Task76: polls WarfrontSettings.SelectionModeKey (default Numpad0) and handles the
        /// toggle. Ignored while a text input field has focus (same reason as the UIView.HasInputFocus()
        /// guard in Game/UI/UnitCommandInput.Update). Pressing Esc while ON also returns it to OFF
        /// immediately (like HandleRallyTargeting, this does not consume/interfere with the vanilla Esc
        /// menu itself).</summary>
        private static void HandleSelectionModeToggle()
        {
            if (UIView.HasInputFocus()) return;

            if (UnitCommandInput.IsHotkeyDown(WarfrontSettings.SelectionModeKey))
            {
                SetSelectionModeActive(!_selectionModeActive);
                return;
            }
            if (_selectionModeActive && Input.GetKeyDown(KeyCode.Escape))
            {
                SetSelectionModeActive(false);
            }
        }

        private static void SetSelectionModeActive(bool active)
        {
            if (_selectionModeActive == active) return;
            _selectionModeActive = active;
            if (!active) CancelDrag(); // Switching OFF mid-drag discards the drag immediately (also hides the rectangle)
            ModConfig.Log("UnitBoxSelection: selection mode " + (active ? "ON" : "OFF"));
            CommandToast.Show(active ? WarfrontStrings.Toast_SelectionModeOn : WarfrontStrings.Toast_SelectionModeOff);
        }

        /// <summary>Converts real screen-pixel coordinates (same space as Input.mousePosition, bottom-left
        /// origin) into the UIView GUI coordinates (the space UIPanel.relativePosition expects, top-left
        /// origin). See the class-level comment: UIView.ScreenPointToGUI itself only does a Y-flip and no
        /// scale conversion, so we first apply the same scale conversion UIView.WorldPointToGUI uses
        /// internally (GetScreenResolution() ÷ uiCamera.pixelWidth/pixelHeight) and then call
        /// ScreenPointToGUI. If uiCamera is unavailable (abnormal cases like right after startup), fall back
        /// to the previous behavior and pass the point to ScreenPointToGUI unconverted (never throws).</summary>
        private static Vector2 ScreenToGuiPoint(UIView view, Vector2 screenPoint)
        {
            Camera cam = view.uiCamera;
            if (cam == null || cam.pixelWidth <= 0 || cam.pixelHeight <= 0)
            {
                return view.ScreenPointToGUI(screenPoint);
            }
            Vector2 screenResolution = view.GetScreenResolution();
            Vector2 scaled = new Vector2(
                screenResolution.x * (screenPoint.x / (float)cam.pixelWidth),
                screenResolution.y * (screenPoint.y / (float)cam.pixelHeight));
            return view.ScreenPointToGUI(scaled);
        }

        private static void UpdateRectVisual(Vector2 startScreen, Vector2 curScreen)
        {
            if (_rectPanel == null) return;
            UIView view = PanelChrome.GetCachedView(); // Task56: called every frame, so use the cached accessor
            if (view == null) return;

            Vector2 a = ScreenToGuiPoint(view, startScreen);
            Vector2 b = ScreenToGuiPoint(view, curScreen);

            float x = Mathf.Min(a.x, b.x);
            float y = Mathf.Min(a.y, b.y);
            float w = Mathf.Abs(a.x - b.x);
            float h = Mathf.Abs(a.y - b.y);

            _rectPanel.relativePosition = new Vector3(x, y);
            _rectPanel.width = Mathf.Max(1f, w);
            _rectPanel.height = Mathf.Max(1f, h);
            if (!_rectPanel.isVisible) _rectPanel.Show();
            _rectPanel.BringToFront();
        }

        /// <summary>Called exactly once when the drag ends. Writes the units projected inside the screen
        /// rectangle into SelectedIds, and also overwrites UnitSelection.SelectedInstanceId with the first ID.
        ///
        /// Task62: decision — an "empty drag" where nothing is found inside the rectangle does NOT silently
        /// clear the existing selection (it does nothing and leaves the previous selection as-is). Rationale:
        /// even after raising DragThresholdPixels, a drag can still be confirmed unintentionally due to hand
        /// tremor etc., and if the selection is wiped by an empty drag while a command is pending
        /// (UnitCommandInput.IsAwaitingRallyClick etc.), the command issued right afterwards misfires with 0
        /// targets — real harm. SelectedIds is replaced only when the rectangle actually captured one or
        /// more units (the normal drag gesture of re-selecting with a different area keeps working as
        /// before). A UI action to explicitly reset the selection to 0 entries is out of scope for this task
        /// (none exists today either).</summary>
        private static void FinishBoxSelect(Vector2 startScreen, Vector2 endScreen)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            float minX = Mathf.Min(startScreen.x, endScreen.x);
            float maxX = Mathf.Max(startScreen.x, endScreen.x);
            float minY = Mathf.Min(startScreen.y, endScreen.y);
            float maxY = Mathf.Max(startScreen.y, endScreen.y);

            UnitVisuals.CollectVisible(_idBuffer, _posBuffer);

            _foundBuffer.Clear();
            for (int i = 0; i < _idBuffer.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_posBuffer[i]);
                if (sp.z <= 0f || sp.z > MaxCameraDistanceCheck) continue; // Behind the camera is excluded
                if (sp.x < minX || sp.x > maxX || sp.y < minY || sp.y > maxY) continue;
                _foundBuffer.Add(_idBuffer[i]);
            }

            if (_foundBuffer.Count == 0) return; // Task62: an empty drag keeps the existing selection (see comment above).

            SelectedIds.Clear();
            SelectedIds.AddRange(_foundBuffer);
            UnitSelection.Set(SelectedIds[0]);
            ModConfig.Log("UnitBoxSelection: selected " + SelectedIds.Count + " unit(s) via drag"); // Task62: 0 entries can no longer occur, so this logs only when count>0
        }

        /// <summary>Declaratively syncs the lightweight highlight (thin cylinder, no collider) that follows
        /// each selected unit every frame (same reconcile pattern as UnitVisuals.Sync).</summary>
        private static void SyncHighlights()
        {
            _staleHighlightIds.Clear();
            foreach (var kv in _highlightMarkers)
            {
                if (!SelectedIds.Contains(kv.Key)) _staleHighlightIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleHighlightIds.Count; i++)
            {
                GameObject stale;
                if (_highlightMarkers.TryGetValue(_staleHighlightIds[i], out stale) && stale != null)
                    UnityEngine.Object.Destroy(stale);
                _highlightMarkers.Remove(_staleHighlightIds[i]);
            }

            for (int i = SelectedIds.Count - 1; i >= 0; i--)
            {
                uint id = SelectedIds[i];
                Vector3 pos;
                if (!UnitVisuals.TryGetPosition(id, out pos))
                {
                    // Task88: a selected ID whose visual has been destroyed (= the unit died) is also removed
                    // from the list, and any remaining highlight is destroyed immediately (previously it was
                    // left behind via continue, and a green rim lingered at the death location).
                    GameObject dead;
                    if (_highlightMarkers.TryGetValue(id, out dead) && dead != null)
                        UnityEngine.Object.Destroy(dead);
                    _highlightMarkers.Remove(id);
                    SelectedIds.RemoveAt(i);
                    continue;
                }

                GameObject marker;
                if (!_highlightMarkers.TryGetValue(id, out marker) || marker == null)
                {
                    marker = CreateHighlightMarker();
                    if (marker == null) continue;
                    _highlightMarkers[id] = marker;
                }
                marker.transform.position = new Vector3(pos.x, pos.y + HighlightYOffset, pos.z);
            }
        }

        private static GameObject CreateHighlightMarker()
        {
            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col); // Do not interfere with click hit-testing
                go.transform.localScale = new Vector3(HighlightRadius, HighlightThinHeight, HighlightRadius);
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.sharedMaterial = GetHighlightMaterial();
                return go;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.CreateHighlightMarker error: " + e);
                return null;
            }
        }

        private static Material GetHighlightMaterial()
        {
            if (_highlightMaterial == null)
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Diffuse");
                _highlightMaterial = new Material(shader);
                _highlightMaterial.color = new Color(0.35f, 1f, 0.4f, 1f);
            }
            return _highlightMaterial;
        }
    }
}
