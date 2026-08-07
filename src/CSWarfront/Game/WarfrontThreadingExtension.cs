using ICities;
using CSWarfront.Game.UI;
namespace CSWarfront.Game
{
    /// <summary>
    /// Thread split (changed in Task19):
    ///  - sim thread (OnAfterSimulationTick): Core decision logic plus read-only access to CS entity
    ///    (building etc.) buffers. The free-slot allocation of CS building buffers and the spatial
    ///    grid are owned by the sim thread, so touching them concurrently from another thread corrupts
    ///    the buffers and CS's own simulation code throws IndexOutOfRangeException (an uncaught
    ///    popup).
    ///  - main thread (OnUpdate): dedicated to syncing unit visuals (Unity GameObjects).
    ///    It never touches CS entities (Unity object APIs such as new GameObject/AddComponent/
    ///    Destroy/transform writes may only be called on the main thread, hence they live here).
    /// </summary>
    public class WarfrontThreadingExtension : ThreadingExtensionBase
    {
        public override void OnAfterSimulationTick()
        {
            try { MilitaryManager.OnSimTick(); }
            catch (System.Exception e) { ModConfig.LogError("OnSimTick: " + e); }
        }

        /// <summary>
        /// Main thread. In addition to syncing unit visuals (Task25), this also drives creation and
        /// display updates of the base info panel (Game/UI/BaseInfoPanel). Each BaseInfoPanel method
        /// always swallows exceptions internally, so the try/catch here is defense in depth like the
        /// other main-thread work. Runs while the game is paused too.
        /// </summary>
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try { MilitaryManager.OnMainVisualUpdate(); }
            catch (System.Exception e) { ModConfig.LogError("OnMainVisualUpdate: " + e); }

            // On-device calibration diagnostics for SpeedCalibration.InGameHoursPerRealSecond (Task26).
            try { SpeedCalibrationDiagnostics.AccumulateRealSeconds(realTimeDelta); }
            catch (System.Exception e) { ModConfig.LogError("SpeedCalibrationDiagnostics: " + e); }

            try
            {
                BaseInfoPanel.EnsureCreated();
                BaseInfoPanel.UpdateVisibility();
            }
            catch (System.Exception e) { ModConfig.LogError("BaseInfoPanel update: " + e); }

            // Task36: the model-assignment panel is toggle-open rather than permanent, so here we only
            // call EnsureCreated (idempotent) to lay the groundwork. Showing/hiding itself is driven by
            // the "model settings" button on BaseInfoPanel.
            // Task47: UpdateGameMenuState temporarily hides a visible panel while the Esc menu is open
            // and restores it when closed (the toggle state itself is not changed).
            try
            {
                AssetAssignPanel.EnsureCreated();
                AssetAssignPanel.UpdateGameMenuState();
            }
            catch (System.Exception e) { ModConfig.LogError("AssetAssignPanel update: " + e); }

            // Unit click selection and status panel (Task31). Doing this after position sync
            // (OnMainVisualUpdate, above) makes the raycast agree with colliders already placed at this
            // frame's latest positions.
            try
            {
                UnitSelection.Update();
                UnitInfoPanel.EnsureCreated();
                UnitInfoPanel.UpdateVisibility();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitInfoPanel update: " + e); }

            // Box selection (Task48). Calling this right after UnitSelection.Update (single click,
            // above) lets us correctly detect, within the same frame, the case where a single click
            // develops into a drag.
            try
            {
                UnitBoxSelection.EnsureCreated();
                UnitBoxSelection.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitBoxSelection update: " + e); }

            // Unit-command hotkey input (Task48). Calling it after UnitBoxSelection.Update (above)
            // allows commands to target the selection (SelectedIds) finalized in the same frame.
            try
            {
                UnitCommandInput.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitCommandInput update: " + e); }

            // Task63: ballistic missile launch-point designation (right-click targeting). Grouped into
            // the same "selection/command hotkey input" phase as UnitCommandInput.
            try
            {
                MissileLaunchTargeting.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("MissileLaunchTargeting update: " + e); }

            // Task102: military build panel (one-click placement of the 9 military building kinds;
            // opened/closed via hotkey or the persistent button).
            try
            {
                MilitaryBuildPanel.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("MilitaryBuildPanel update: " + e); }

            // Task106: targeting for trench-line placement (two right-clicked points).
            try
            {
                TrenchLineTargeting.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("TrenchLineTargeting update: " + e); }

            // Task62: screen-center toast on command issue. UnitCommandInput is the caller of Show(),
            // so here we only do idempotent creation and per-frame fade/hide timekeeping.
            try
            {
                CommandToast.EnsureCreated();
                CommandToast.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("CommandToast update: " + e); }
        }
    }
}
