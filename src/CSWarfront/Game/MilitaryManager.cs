using System;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Bridge (singleton-like static) that drives the Core steps from CS ticks and reflects the
    /// results into the presentation.
    /// Thread boundary (important; changed in Task19):
    ///  - Units no longer borrow CS vehicles (VehicleManager). CS vehicle AIs (e.g. FireTruckAI) are
    ///    embedded in the dispatch of service systems like TransferManager, and when invoked in an
    ///    invalid context such as TransferReason.None, the offer bookkeeping crashes with an
    ///    array-out-of-range access (confirmed in real-game logs,
    ///    TransferManager.RemoveIncomingOffer). Ever since Task15 made Core (MovementStep) logically
    ///    own unit positions, the CS vehicle AI (pathfinding etc.) is never used at all and offers no
    ///    benefit. Therefore unit visuals are plain Unity GameObjects (UnitVisuals), borrowing only
    ///    the mesh + material from vehicle prefabs and never touching the AI.
    ///  - Sim thread (OnSimTick, via ThreadingExtensionBase.OnAfterSimulationTick):
    ///    dedicated to Core decision logic plus CS buffer reads (base buildings, terrain sampling,
    ///    etc.). Executes serially while holding _stateLock.
    ///  - Main thread (OnMainVisualUpdate, via ThreadingExtensionBase.OnUpdate):
    ///    dedicated to Unity object operations (GameObject creation/movement/destruction). Never
    ///    touches CS entities (Vehicle/Building etc.). _stateLock is held only while building the
    ///    snapshot, and Unity operations run after the lock is released (calling Unity APIs while
    ///    holding the lock could, in the worst case, block the sim thread for a long time).
    ///  - Note: OnAfterSimulationTick does not fire while the game is paused, so base placement and
    ///    spawning wait until the game is unpaused (acceptable for the MVP). OnUpdate, on the other
    ///    hand, keeps running while paused, so visual sync continues during pause (intentional: keep
    ///    displaying the current deployment even while paused).
    ///  - _stateLock is still required to prevent concurrent access to State between the save/load
    ///    thread (SerializeLocked/LoadAndRebuild) and the sim thread (OnSimTick)
    ///    (System.Collections.Concurrent is unavailable because of net35).
    ///
    /// Task67: this file holds only the core state (State/_stateLock) and the lifecycle
    /// (EnsureInitialized/ReplaceState/Reset). Already split by responsibility into the following
    /// partial files (MilitaryManagerRelations/MilitaryManagerUnitCommands etc., following the
    /// existing Task34/48 split policy):
    ///  - MilitaryManagerSimTick.cs: OnSimTick with its accumulator counters/constants, LogDiagnostics.
    ///  - MilitaryManagerVisuals.cs: OnMainVisualUpdate with its snapshot lists.
    ///  - MilitaryManagerPersistence.cs: SerializeLocked/LoadAndRebuild (save/load).
    ///  - MilitaryManagerUiApi.cs: thin wrappers for UI-facing base/unit snapshot retrieval, ownership changes, etc.
    /// The accumulator counters touched by Reset() (_economyAccum etc.) remain here because they are
    /// shared with the OnSimTick side.
    /// </summary>
    public static partial class MilitaryManager
    {
        public static WarState State { get; private set; }

        // Coarse-grained lock preventing concurrent access to State between the save/load thread and
        // the sim thread. At MVP scale (dozens of units) a single lock is sufficient.
        private static readonly object _stateLock = new object();

        // Throttling for the economy tick (OnSimTick, MilitaryManagerSimTick.cs). Kept here because Reset() zeroes it.
        private static float _economyAccum;

        // Rebuild interval for the road graph (in-game time). Rebuilt periodically because the player
        // keeps laying/demolishing roads (Task23). Touched only by the sim thread. Kept here because
        // Reset() zeroes it.
        private static float _roadRebuildAccum;

        // Retry interval (in-game time) while the road graph is unbuilt (State.Roads == null). Prevents
        // attempting a full build every tick and flooding the log while failures persist, e.g. when
        // NetManager is not ready yet (Task23 review Important). Touched only by the sim thread. Kept
        // here because Reset() zeroes it.
        private static float _roadBuildRetryAccum;
        // If no build has been attempted yet in this session, only the first attempt runs immediately (skipping the interval wait above).
        private static bool _hasAttemptedRoadBuild;

        // Rebuild interval for the cover map (State.Cover) (in-game time). Same reason as RoadGraph
        // (rebuilt periodically because the player keeps constructing/demolishing buildings, Task44).
        // Touched only by the sim thread.
        // Kept here because Reset() zeroes it.
        private static float _coverRebuildAccum;

        // Retry interval (in-game time) while the cover map is unbuilt (State.Cover == null). Same
        // throttling pattern as RoadGraph's _roadBuildRetryAccum (Task44). Touched only by the sim thread.
        // Kept here because Reset() zeroes it.
        private static float _coverBuildRetryAccum;
        private static bool _hasAttemptedCoverBuild;

        // Throttling (in-game time) for cleanup of ghost bases (logical bases whose building no longer
        // exists). A full scan every tick is wasteful, so run only at a fixed interval (Task24). Touched
        // only by the sim thread. Kept here because Reset() zeroes it.
        private static float _baseReconcileAccum;

        // For in-game-time-based dt computation (Task21). Touched only by the sim thread. Kept here because Reset() zeroes it.
        private static DateTime _lastGameTime;
        private static bool _hasLastGameTime;

        public static void EnsureInitialized()
        {
            if (State != null) return;
            // Initialize fully in a local variable and only assign to State at the very end.
            // This way OnSimTick's `if (State == null) return;` can never observe a
            // half-initialized state.
            var state = new WarState();
            UnitStatsFile.EnsureLoaded(); // Task92: apply unit-stats.xml overrides before building the rosters
            LandUnitRoster.RegisterAll(state.Types); // 7 land unit classes x Tier1-5 (Task28)
            NavalUnitRoster.RegisterAll(state.Types); // 2 naval classes (Destroyer/Carrier) x Tier1-5 (Task61)
            AirUnitRoster.RegisterAll(state.Types);   // 3 air classes (AirSuperiority/TacticalBomber/SuicideDrone) x Tier1-5 (Task61)

            // Create all 5 factions (so that every choice in the Options "construction faction"
            // dropdown points at a valid value).
            // Bases are no longer seeded here (Task18): the moment the player places an
            // Options-designated building (which Task74/Task82 made the sole placement route),
            // BasePlacementWatcher creates the logical base.
            // Only the starting treasury is granted here so production can
            // begin immediately after placement.
            string[] names = WarfrontSettings.FactionNames;
            for (byte i = 0; i < WarfrontSettings.MaxFactions; i++)
            {
                var f = new Faction(i, names[i]);
                f.AddTreasury(200f);
                // Task99: initial grant for the 3-resource economy (so the first units can be formed even
                // in an undeveloped city. Same rationale as the starting funds of 200. The values also
                // match the defaults used by WarStateSerializer when loading format v8 or earlier).
                f.AddManpower(200f);
                f.AddProduction(200f);
                state.Factions.Add(f);
            }

            // Task95: dedicated Invader faction for external invasion events (6th faction, moss green,
            // always hostile). Does not appear in the construction-faction dropdown (MaxFactions=5) or
            // the relations UI. Needs no treasury either (it owns no bases or production at all; its
            // squads are spawned directly by InvasionEvents).
            InvasionEvents.EnsureInvaderFaction(state);

            // The MVP default relation is Hostile for every faction pair. The implementation is delegated
            // to Core.RelationPresets.ApplyAllHostile (Task49: so the same implementation is reused by the
            // "reset all to hostile" button on the Options screen).
            // This default applies only when creating a new State; when loading an existing save, the
            // serialized relations (WarState.Relations, all 25 pairs persisted since format v4) are
            // restored as-is.
            RelationPresets.ApplyAllHostile(state.Relations, WarfrontSettings.MaxFactions);

            State = state;
        }

        public static void ReplaceState(WarState s) { lock (_stateLock) { State = s; } }

        /// <summary>
        /// Resets all session state on level unload (returning to the main menu, etc.) (Task16 review
        /// Important). Without this, session state originating from a save-restored session would carry
        /// over into the next game start within the same process. The caller
        /// (WarfrontLoadingExtension.OnLevelUnloading) runs outside OnSimTick (in CS's load lifecycle),
        /// so no _stateLock reentrancy occurs. BasePlacementWatcher.Unsubscribe is expected to be called
        /// by the caller before this Reset() (event unsubscription is tied to the level lifecycle, so
        /// only the pending list is cleared here).
        /// </summary>
        public static void Reset()
        {
            // UnitVisuals.DestroyAll destroys Unity GameObjects and is therefore a main-thread-only API.
            // Reset() itself is called from CS's load lifecycle (main thread, via OnLevelUnloading), so
            // calling it directly here is fine (_stateLock only protects the State swap, which holds no
            // CS entities).
            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: also destroy the bases' per-faction overlays on level unload.
            CombatFx.DestroyAll(); // Task42: also destroy muzzle-flash effects on level unload.
            KillFx.DestroyAll(); // Task65: also destroy kill-explosion effects on level unload.
            BombFx.DestroyAll(); // Task87: also destroy falling bombs on level unload.
            AaMissileFx.DestroyAll(); // Task90: also destroy in-flight anti-air missiles.
            UnitStatsFile.Reset(); // Task92: force unit-stats.xml to be reloaded on the next load.
            MissileVisuals.DestroyAll(); // Task63: also destroy in-flight missile visuals on level unload.
            MissileVisuals.DestroyAllFx(); // Task63: likewise for impact/interception effects.
            UI.OrderDestinationMarkers.DestroyAll(); // Task62: also destroy destination markers on level unload.
            UI.CommandToast.Destroy(); // Task62: also destroy the toast label on level unload.
            // Task54: release the roads this mod blocked (PathFailed bit). Reset() itself is called from
            // OnLevelUnloading (during level transition, when the sim thread is assumed to have already
            // stopped), so no contention with other threads is expected. CombatRoadBlocker.Reset is
            // internally guarded against propagating exceptions outward (NetManager may already be
            // deactivated during level teardown; failure is tolerated as harmless = per the requirement).
            // Task56 review: this comment existed originally but the actual call was missing — an
            // omission where the PathFailed bits could carry over into the next session without being
            // released even on level unload — so the call was added.
            CombatRoadBlocker.Reset();
            // Task72: also release the base buildings this mod hid (Hidden bit) on level unload.
            // Previously BaseVisuals.DestroyAll() only queued BaseHiddenSync.SetDesired(false), and
            // ApplyPending, which actually writes to the CS building buffer, is only called via the sim
            // thread's OnSimTick — so if OnSimTick never runs again after unload, the change was never
            // applied (Hidden could leak onto whatever entirely different building next lands on this
            // buildingId) — an omission. Restore it reliably in the same "write directly to the CS
            // buffer from the main thread" style as CombatRoadBlocker.Reset.
            BaseHiddenSync.Reset();
            GarrisonAbandonSync.Reset(); // Task139: hand back every building held by troops
            CombatEvacuation.Reset();   // Task150
            CombatCollateral.Reset(); // Task65: also clear the internal state used for lottery throttling on level unload.

            lock (_stateLock)
            {
                State = null;
                _economyAccum = 0f;
                _roadRebuildAccum = 0f;
                _roadBuildRetryAccum = 0f;
                _hasAttemptedRoadBuild = false;
                _seaGridRebuildAccum = 0f; // Task92: don't let the sea navigation grid's build state cross sessions either
                _hasAttemptedSeaGridBuild = false;
                _coverRebuildAccum = 0f;
                _coverBuildRetryAccum = 0f;
                _hasAttemptedCoverBuild = false;
                _baseReconcileAccum = 0f;
                _hasLastGameTime = false;
                _lastGameTime = default(DateTime);
                BasePlacementWatcher.ClearPending();
            }

            // Calibration diagnostics accumulation (Task26): protected by its own dedicated lock separate
            // from MilitaryManager, so cleared individually.
            SpeedCalibrationDiagnostics.Reset();

            // *Panel.Destroy destroys Unity GameObjects and is therefore a main-thread-only API. Reset()
            // itself being called from CS's load lifecycle is the same premise as UnitVisuals.DestroyAll
            // above, so calling it directly here is fine. UnitSelection.Clear is so selection IDs don't
            // carry over into the next session (Task31).
            UI.BaseInfoPanel.Destroy();
            UI.UnitInfoPanel.Destroy();
            UI.AssetAssignPanel.Destroy(); // Task36
            UI.UnitSelection.Clear();
            UI.UnitBoxSelection.Destroy(); // Task48: the box-selection rectangle/highlight GameObjects and selection state
            UI.UnitCommandInput.Reset(); // Task48: don't carry over the rally-point targeting state
            UI.MissileLaunchTargeting.Reset(); // Task63: don't carry over the missile launch-point targeting state
            UI.PanelChrome.ResetCache(); // Task56: don't carry cached PauseMenu/UIView references into the next session
        }
    }
}
