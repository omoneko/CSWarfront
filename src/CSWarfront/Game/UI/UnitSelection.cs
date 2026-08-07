using System;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Click-selection of units (Task31). Main thread only. A raycast is performed only on the
    /// frame where the left mouse button goes down (Input.GetMouseButtonDown(0)); no per-frame
    /// raycasting (cost minimization).
    ///
    /// Clicks on UI are ignored: when ColossalFramework.UI.UIInput.hoveredComponent
    /// (verified via reflection over ColossalManaged.dll; a public static property with return type
    /// ColossalFramework.UI.UIComponent, backed by private static UIComponent m_HoveredComponent)
    /// is non-null — i.e. the cursor is over some UI component — the raycast itself is skipped.
    /// This ensures clicks over our own panels as well as over all vanilla UI (electricity tab,
    /// building panels, etc.) never pass through into the 3D world.
    ///
    /// Coexistence with vanilla input: the selection state is updated only when the hit GameObject
    /// can be identified as one of this mod's unit representations (via
    /// UnitVisuals.TryGetInstanceId). Otherwise (clicks on buildings, terrain, roads, empty ground,
    /// or when the raycast hits nothing at all) nothing whatsoever is done (no deselection and no
    /// Input consumption). Physics.Raycast is used purely as a test, and no event-consuming
    /// operations (Input disabling etc.) are performed, so vanilla building selection and tool
    /// operations continue to work completely unchanged.
    /// </summary>
    public static class UnitSelection
    {
        // Distance sufficient to cover the entire map (CS maps are roughly on the order of a few km).
        private const float MaxRaycastDistance = 10000f;

        public static uint SelectedInstanceId { get; private set; }

        public static void Clear()
        {
            SelectedInstanceId = 0;
        }

        /// <summary>Task48: Public setter so Game/UI/UnitBoxSelection can overwrite with the box
        /// selection result (the first entry, or 0 if none). Ordinary single clicks (Update below)
        /// do not use this and assign directly themselves. Passing 0 means the same as Clear().</summary>
        public static void Set(uint instanceId)
        {
            SelectedInstanceId = instanceId;
        }

        /// <summary>Called every main-thread frame. Processes only the frame where the left button goes down.</summary>
        public static void Update()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (!Input.GetMouseButtonDown(0)) return;

                // Clicks while the cursor is over UI are not passed to the 3D-world raycast.
                if (UIInput.hoveredComponent != null) return;

                Camera cam = Camera.main;
                if (cam == null) return; // camera not ready (level loading etc.). Retry on the next click.

                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                RaycastHit hit;
                if (!Physics.Raycast(ray, out hit, MaxRaycastDistance)) return;

                GameObject hitGo = hit.collider != null ? hit.collider.gameObject : null;
                uint instanceId;
                if (UnitVisuals.TryGetInstanceId(hitGo, out instanceId))
                {
                    SelectedInstanceId = instanceId;
                }
                // If the hit is not one of this mod's units, do nothing = keep the current
                // selection and defer to vanilla click handling (building selection etc.) as-is.
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitSelection.Update error: " + e);
            }
        }
    }
}
