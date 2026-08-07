using System;
using ColossalFramework;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for save/load (SerializeLocked/LoadAndRebuild).
    /// Split into a partial class because of the 500-line limit on MilitaryManager.cs (same policy as
    /// Task34's MilitaryManagerManualProduction etc.). _stateLock / State are private static members
    /// declared in MilitaryManager.cs; being a partial class, they are directly accessible from here.
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// For saving: serializes WarState while holding _stateLock.
        /// Prevents the "Collection was modified" exception (= a silent save failure = data loss)
        /// that would occur while OnSimTick is mutating State.Units etc.
        /// The caller (OnSaveData) must not hold _stateLock (the lock is non-reentrant).
        ///
        /// Task72: to avoid baking this mod's "visual-only" flags (CombatRoadBlocker's
        /// NetSegment.PathFailed, BaseHiddenSync's Building.Hidden) into the save data, they are
        /// temporarily cleared before serialization and restored afterwards.
        ///
        /// Important (actual save order confirmed by decompiling SimulationManager/LoadingManager/
        /// AsyncTask with ilspycmd; details in task-72-report.md): this method (and hence
        /// WarStateDataExtension.OnSaveData) is called inside step (1) of SimulationManager.Data.Serialize's
        /// order: "(1) call every mod's OnSaveData() -> (2) only afterwards call the vanilla managers'
        /// Serialize (BuildingManager.Data/NetManager.Data etc., which is where Building.m_flags/
        /// NetSegment.m_flags are actually written to the stream)". Moreover (1) and (2) happen back to
        /// back inside the same AsyncTask.Execute() (which drives the LoadingManager.SaveSimulationData
        /// coroutine synchronously to completion via while(m_Action.MoveNext()); the only yield is a
        /// single one at the very end). In other words, the old implementation that cleared the flags
        /// here and immediately restored them in finally was useless, because it restored them before
        /// (2) read Building.m_flags/NetSegment.m_flags — the save file kept getting Hidden/PathFailed
        /// baked in after all (i.e. the "leak" suspected in the requirements really did exist on the
        /// CombatRoadBlocker side too).
        ///
        /// Fix: instead of restoring synchronously here, enqueue the restore via
        /// Singleton&lt;SimulationManager&gt;.instance.AddAction as "the next action after the current
        /// Saving AsyncTask has fully completed". The `while(m_hasActions){...}` loop at the top of
        /// SimulationManager.SimulationStep keeps looping — because calling AddAction from within an
        /// action that was just dequeued and is executing sets m_hasActions back to true — so that
        /// action also runs within the same frame, and before the normal OnSimTick. This reliably
        /// captures the moment "right after vanilla has finished reading the Building/NetSegment flags
        /// into the stream" without any external IL patching (see also the comments in
        /// CombatRoadBlocker.ReblockAfterSave / BaseHiddenSync.ReapplyAfterSave).
        /// </summary>
        public static byte[] SerializeLocked()
        {
            lock (_stateLock)
            {
                if (State == null) EnsureInitialized();

                CombatRoadBlocker.UnblockAllForSave();
                BaseHiddenSync.UnhideAllForSave();
                try
                {
                    return CSWarfront.Core.WarStateSerializer.Serialize(State);
                }
                finally
                {
                    ScheduleReapplySaveFlags();
                }
            }
        }

        /// <summary>
        /// Called from SerializeLocked's finally. Schedules the restore as the sim thread's next action
        /// so it runs right after vanilla's Building/NetSegment flag write-out (inside
        /// SimulationManager.Data.Serialize, which happens even later than this method's return point)
        /// has finished (Task72; see SerializeLocked for the full commentary). As a safeguard against
        /// SimulationManager not existing (which should never happen in theory — since the save process
        /// itself presupposes SimulationManager, its absence would be the anomaly), fall back to
        /// restoring synchronously on the spot (the timing may be wrong, but that is safer than leaving
        /// the flags cleared forever).
        /// </summary>
        private static void ScheduleReapplySaveFlags()
        {
            if (Singleton<SimulationManager>.exists)
            {
                Singleton<SimulationManager>.instance.AddAction("CSWarfront.ReapplySaveFlags", delegate
                {
                    try { CombatRoadBlocker.ReblockAfterSave(); }
                    catch (Exception e) { ModConfig.LogError("CSWarfront.ReapplySaveFlags (road): " + e); }
                    try { BaseHiddenSync.ReapplyAfterSave(); }
                    catch (Exception e) { ModConfig.LogError("CSWarfront.ReapplySaveFlags (base hidden): " + e); }
                });
            }
            else
            {
                CombatRoadBlocker.ReblockAfterSave();
                BaseHiddenSync.ReapplyAfterSave();
            }
        }

        /// <summary>
        /// Dedicated entry point for restoring from save data (called from the save/load thread).
        /// Only swaps in the State. The visuals (GameObjects) of surviving units are regenerated
        /// automatically because subsequent OnMainVisualUpdate calls snapshot State.Units and pass them
        /// to UnitVisuals.Sync (declarative reconcile, so no special respawn flag/handling is needed —
        /// removed in Task19).
        /// </summary>
        public static void LoadAndRebuild(WarState restored)
        {
            lock (_stateLock)
            {
                State = restored;
                // Restored from a save = base buildings were already restored by CS (BaseId is a stable
                // buildingId). BasePlacementWatcher.ProcessPending skips already-restored BaseIds via its
                // idempotency check, so even if EventBuildingCreated fires again there is no double
                // registration.
            }
        }
    }
}
