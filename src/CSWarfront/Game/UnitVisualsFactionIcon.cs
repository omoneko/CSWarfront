using System;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task49: Additional UnitVisuals members for the faction icon above units (a small sphere that
    /// behaves like CS's crime/fire icons). Split into a partial class because of the 500-line limit
    /// on UnitVisuals.cs (same policy as Task34's MilitaryManagerManualProduction / Task48's
    /// MilitaryManagerUnitCommands). The private nested type VisualEntry and private consts
    /// (IconGapAboveMesh etc.) declared on the UnitVisuals.cs side are directly accessible from here
    /// as part of the same partial class.
    ///
    /// All methods are main thread only (because of Unity GameObject/Material APIs). Expected to be
    /// called every frame from UnitVisuals.Sync, once per snapshot entry (on both the create and move
    /// paths).
    /// </summary>
    public static partial class UnitVisuals
    {
        // Task49: Parameters for keeping the apparent size roughly constant regardless of camera
        // distance, like CS's crime/fire icons. Every frame we set worldSize = distance *
        // IconApparentSizeFactor as the world-unit scale (under perspective projection,
        // screenSize ∝ worldSize / distance, so making worldSize proportional to distance keeps
        // screenSize near-constant). MinIconWorldSize/MaxIconWorldSize are its safety clamps: a lower
        // bound so the icon does not collapse to 0 and vanish at close range, and an upper bound so
        // it does not grow without limit at extreme zoom-out.
        // Task50: Per user feedback "halve the size of the sphere icon", changed to exactly half of
        // the Task49 values (0.02f / 2f / 20f).
        private const float IconApparentSizeFactor = 0.01f;
        private const float MinIconWorldSize = 1f;
        private const float MaxIconWorldSize = 10f;

        // Task50: While the vanilla free-camera mode (the cinematic free-view camera toggled by the C
        // key etc.) is active, hide all faction icons (user feedback "please hide them in FreeCamera
        // mode"). We directly reference the m_freeCamera field (public bool) exposed by
        // ToolsModifierControl.cameraController (a static property of type CameraController). Both
        // members were confirmed public by reflecting over Assembly-CSharp.dll with PowerShell (same
        // verification approach as Game/UI/PanelChrome.IsGameMenuOpen). If the state cannot be
        // retrieved or an exception occurs, treat it as "not free camera" and log only once
        // (UpdateFactionIcon is called every frame, so avoid log spam).
        private static bool _loggedFreeCameraCheckFailure;

        /// <summary>
        /// Synchronizes the faction icon (small sphere) every frame. If
        /// WarfrontSettings.ShowFactionIcons is OFF, destroys any existing icon and resets it to
        /// null. If ON and not yet created, lazily creates it; if already created, updates its scale
        /// according to camera distance (no need to always face the camera: a sphere looks the same
        /// from any angle, so billboard rotation is unnecessary). fromAssignedProp (assigned-asset)
        /// units are handled through the same path with no distinction (requirement: works for both).
        /// </summary>
        private static void UpdateFactionIcon(VisualEntry entry, byte factionId, Camera mainCamera)
        {
            if (entry == null || entry.GameObject == null) return;

            // Task50: While the ShowFactionIcons setting is OFF, or free-camera mode is active,
            // destroy the existing icon and hide it (exactly the same path as the existing ON/OFF
            // toggle. Once free camera ends, this branch is no longer taken, and the "lazily create
            // if entry.Icon == null" below restores it automatically on the next frame = the same
            // "hide/restore" idea as the existing pattern of hiding panels for the Esc menu).
            if (!WarfrontSettings.ShowFactionIcons || IsFreeCameraActive())
            {
                if (entry.Icon != null)
                {
                    UnityEngine.Object.Destroy(entry.Icon);
                    entry.Icon = null;
                }
                return;
            }

            if (entry.Icon == null)
            {
                entry.Icon = CreateFactionIcon(entry.GameObject, factionId, entry.IconLocalHeightY);
                if (entry.Icon == null) return; // Material could not be resolved, etc. Already logged inside CreateFactionIcon. Try again next frame.
            }

            if (mainCamera == null) return; // Scale cannot be computed. Keep the existing scale (the look stays as at creation time).

            Vector3 iconWorldPos = entry.Icon.transform.position;

            // Task49: If off-screen, skip the CPU-side distance computation and scale update (the
            // rendering itself is already skipped by Unity's frustum culling, so GPU load does not
            // change with or without this check).
            Vector3 viewportPoint = mainCamera.WorldToViewportPoint(iconWorldPos);
            bool onScreen = viewportPoint.z > 0f
                && viewportPoint.x > -0.1f && viewportPoint.x < 1.1f
                && viewportPoint.y > -0.1f && viewportPoint.y < 1.1f;
            if (!onScreen) return;

            float distance = Vector3.Distance(iconWorldPos, mainCamera.transform.position);
            float worldSize = Mathf.Clamp(distance * IconApparentSizeFactor, MinIconWorldSize, MaxIconWorldSize);
            entry.Icon.transform.localScale = new Vector3(worldSize, worldSize, worldSize);
        }

        /// <summary>
        /// Task50: Whether the vanilla free-camera mode is active. References m_freeCamera (public
        /// bool field) of ToolsModifierControl.cameraController (static property). If it cannot be
        /// retrieved or an exception occurs, returns false (= disables the icon-hiding feature,
        /// falling back to the safe side) and logs only once.
        /// </summary>
        private static bool IsFreeCameraActive()
        {
            try
            {
                CameraController cc = ToolsModifierControl.cameraController;
                return cc != null && cc.m_freeCamera;
            }
            catch (Exception e)
            {
                if (!_loggedFreeCameraCheckFailure)
                {
                    _loggedFreeCameraCheckFailure = true;
                    ModConfig.LogError("UnitVisuals.IsFreeCameraActive: failed to retrieve state, disabling free-camera hide feature: " + e);
                }
                return false;
            }
        }

        /// <summary>
        /// Creates the sphere for the faction icon (main thread only). Same technique as
        /// CombatFx.CreateSmallSphere: the primitive sphere's Collider is only disabled, not
        /// destroyed, so it does not interfere with raycast/click selection (the existing click hit
        /// testing is separately provided by the marker's/root's Collider). The material reuses
        /// UnitMaterialFactory.TryGetFactionMaterial to match the existing faction colors (the same
        /// palette as the body material and the marker cube). Returns null on failure (the caller
        /// retries next frame).
        /// </summary>
        private static GameObject CreateFactionIcon(GameObject parent, byte factionId, float localHeightY)
        {
            try
            {
                Material material;
                if (!UnitMaterialFactory.TryGetFactionMaterial(factionId, out material)) return null;

                GameObject icon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Collider col = icon.GetComponent<Collider>();
                if (col != null) col.enabled = false;

                icon.name = "CSWarfrontFactionIcon";
                icon.transform.SetParent(parent.transform, false);
                icon.transform.localPosition = new Vector3(0f, localHeightY, 0f);
                icon.transform.localScale = new Vector3(MinIconWorldSize, MinIconWorldSize, MinIconWorldSize); // corrected immediately by the next UpdateFactionIcon according to distance

                Renderer renderer = icon.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;

                return icon;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateFactionIcon error: " + e);
                return null;
            }
        }
    }
}
