using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for main-thread-driven work (OnMainVisualUpdate). Split into
    /// a partial class because of the 500-line limit on MilitaryManager.cs (same policy as Task34's
    /// MilitaryManagerManualProduction etc.). _stateLock / State are private static members declared in
    /// MilitaryManager.cs; being a partial class, they are directly accessible from here.
    ///
    /// OnMainVisualUpdate is called from the main thread via ThreadingExtensionBase.OnUpdate and only
    /// synchronizes unit visuals (Unity GameObjects). It never touches CS entities (Vehicle/Building
    /// etc.). _stateLock is held only while building the snapshot; Unity API calls happen after the
    /// lock is released (avoiding heavy/blocking work while holding the lock so the sim thread never
    /// has to wait).
    /// </summary>
    public static partial class MilitaryManager
    {
        // Snapshot reused across OnMainVisualUpdate calls (avoids GC). Main-thread access only.
        private static readonly List<UnitVisualState> _visualSnapshot = new List<UnitVisualState>();

        // Task60: same as above, snapshot for military bases (BaseVisuals). Main-thread access only.
        private static readonly List<BaseVisualState> _baseVisualSnapshot = new List<BaseVisualState>();

        // Task42: snapshot of shot events reused across OnMainVisualUpdate calls (avoids GC).
        // Main-thread access only. The contents of State.RecentShots are copied here inside _stateLock,
        // then passed to CombatFx.Spawn after the lock is released (same pattern as _visualSnapshot for
        // UnitVisuals).
        private static readonly List<ShotEvent> _shotSnapshot = new List<ShotEvent>();

        // Task51: same as above; the contents of State.RecentKills are copied here inside _stateLock,
        // then passed to CombatFx.SpawnKillSounds after the lock is released (exactly the same pattern
        // as _shotSnapshot).
        private static readonly List<KillEvent> _killSnapshot = new List<KillEvent>();

        // Task63: same as above, snapshot for State.MissilesInFlight (for MissileVisuals.Sync).
        private static readonly List<MissileVisualState> _missileSnapshot = new List<MissileVisualState>();

        // Task63: same as above; the contents of State.RecentImpacts are copied here inside _stateLock,
        // then passed to MissileVisuals.HandleImpacts after the lock is released (exactly the same
        // pattern as _shotSnapshot/_killSnapshot).
        private static readonly List<MissileImpactEvent> _missileImpactSnapshot = new List<MissileImpactEvent>();

        // Task62: same as above, advance/rally destinations of the selected units (for
        // UI.OrderDestinationMarkers). Referencing UI.UnitBoxSelection.SelectedIds (Game-layer,
        // main-thread-only state) inside _stateLock is fine because OnMainVisualUpdate itself is
        // main-thread-only (treated the same as other Game-layer main-thread state).
        private static readonly List<UI.OrderDestinationState> _orderMarkerSnapshot = new List<UI.OrderDestinationState>();

        /// <summary>
        /// Main thread (via ThreadingExtensionBase.OnUpdate): synchronizes unit visuals (Unity
        /// GameObjects) only. Never touches CS entities (Vehicle/Building etc.).
        /// _stateLock is held only while building the snapshot; UnitVisuals.Sync (Unity API calls)
        /// happens after the lock is released (avoiding heavy/blocking work while holding the lock so
        /// the sim thread never has to wait).
        /// </summary>
        public static void OnMainVisualUpdate()
        {
            if (State == null) return;

            // Task94: notification that an external invasion has started (flag set by the sim thread,
            // consumed on the main thread).
            if (_invasionToastPending)
            {
                _invasionToastPending = false;
                UI.CommandToast.Show("Invasion force approaching the city!");
            }

            _visualSnapshot.Clear();
            _baseVisualSnapshot.Clear();
            _shotSnapshot.Clear();
            _killSnapshot.Clear();
            _orderMarkerSnapshot.Clear();
            _missileSnapshot.Clear();
            _missileImpactSnapshot.Clear();
            lock (_stateLock)
            {
                for (int i = 0; i < State.Units.Count; i++)
                {
                    var u = State.Units[i];
                    if (u.State == UnitState.Dead) continue;
                    var type = State.Types.Get(u.TypeKey);
                    _visualSnapshot.Add(new UnitVisualState
                    {
                        InstanceId = u.InstanceId,
                        TypeKey = u.TypeKey,
                        FactionId = u.FactionId,
                        Position = new Vector3(u.Position.X, u.Position.Y, u.Position.Z),
                        AssetPrefabName = type != null ? type.AssetPrefabName : ""
                    });
                }

                // Task62: collect the advance/rally destinations of the selected units
                // (UI.UnitBoxSelection.SelectedIds) inside the same lock (M&B-style destination markers,
                // for UI.OrderDestinationMarkers). Units that are holding or have no destination set are
                // excluded (no marker shown = per spec, this means "there is no destination").
                var selectedIds = UI.UnitBoxSelection.SelectedIds;
                for (int i = 0; i < selectedIds.Count; i++)
                {
                    UnitInstance u = State.FindUnit(selectedIds[i]);
                    if (u == null || !u.IsAlive) continue;

                    if (u.Order == UnitOrder.RallyHold)
                    {
                        if (!u.RallyPoint.HasValue) continue;
                        WorldPos p = u.RallyPoint.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Rally
                        });
                    }
                    else if (u.Order != UnitOrder.Hold && u.OrderTargetPos.HasValue)
                    {
                        WorldPos p = u.OrderTargetPos.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Advance
                        });
                    }
                }

                // Task60: military bases are also snapshotted inside the same lock. The position
                // (WorldPos) comes from Core (State.Bases, an immutable value recorded once by
                // BasePlacementWatcher at base placement — bases never move after placement, so no
                // re-read is needed); the angle comes from BasePlacementWatcher._baseAngles (a cache the
                // sim thread already read from the CS building buffer; it is written under this same
                // _stateLock, so reading it here is safe).
                // The BuildingManager buffer is never accessed directly from the main thread
                // (preserving the rule that CS entities are sim-thread-only).
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.OwnerFactionId == null) continue; // unowned bases are excluded from per-faction assignment

                    float angle;
                    if (!BasePlacementWatcher.TryGetAngle(b.BaseId, out angle)) angle = 0f;

                    _baseVisualSnapshot.Add(new BaseVisualState
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        Position = new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
                        Angle = angle,
                        Type = b.Type // Task66: needed to resolve the model-assignment key per base type
                    });
                }

                // Task42: shot effects are also copied inside the same lock (State.RecentShots is a
                // transient buffer written by the sim thread, so reading it outside the lock would race).
                for (int i = 0; i < State.RecentShots.Count; i++)
                    _shotSnapshot.Add(State.RecentShots[i]);

                // Task51: kill events are copied inside the same lock, for the same reason.
                for (int i = 0; i < State.RecentKills.Count; i++)
                    _killSnapshot.Add(State.RecentKills[i]);

                // Task63: in-flight missiles and impact/interception events are also snapshotted inside
                // the same lock (State.MissilesInFlight/RecentImpacts are written by the sim thread, so
                // reading them outside the lock would race).
                for (int i = 0; i < State.MissilesInFlight.Count; i++)
                {
                    MissileInFlight m = State.MissilesInFlight[i];
                    _missileSnapshot.Add(new MissileVisualState
                    {
                        Id = m.Id,
                        FactionId = m.FactionId,
                        From = new Vector3(m.From.X, m.From.Y, m.From.Z),
                        To = new Vector3(m.To.X, m.To.Y, m.To.Z),
                        Progress = m.Progress
                    });
                }
                for (int i = 0; i < State.RecentImpacts.Count; i++)
                    _missileImpactSnapshot.Add(State.RecentImpacts[i]);
            }

            UnitVisuals.Sync(_visualSnapshot);
            BaseVisuals.Sync(_baseVisualSnapshot); // Task60: after lock release, Unity operations happen here
            UI.OrderDestinationMarkers.Sync(_orderMarkerSnapshot); // Task62: same as above
            MissileVisuals.Sync(_missileSnapshot); // Task63: same as above

            // Task42: Unity operations (GameObject create/destroy/move) happen after the lock is released
            // (same convention as UnitVisuals.Sync: calling Unity APIs while holding the lock could block
            // the sim thread for a long time).
            CombatFx.Spawn(_shotSnapshot);
            UnitVisuals.NotifyShots(_shotSnapshot); // Task83: units that fired turn to face their firing direction
            CombatFx.SpawnKillSounds(_killSnapshot); // Task51: kill sound (no visual effect)
            KillFx.Spawn(_killSnapshot); // Task65: kill explosion effect (effect-only class separate from the sound, shares the same category logic)
            CombatFx.Update(Time.deltaTime);
            KillFx.Update(Time.deltaTime);
            BombFx.Update(Time.deltaTime); // Task87: animation of falling bombs
            AaMissileFx.Update(Time.deltaTime); // Task90: in-flight anti-air missiles (homing/flares/evasion)

            // Task63: impact/interception presentation (flash/explosion + sound) and real-time update of
            // ongoing effects.
            MissileVisuals.HandleImpacts(_missileImpactSnapshot);
            MissileVisuals.UpdateFx(Time.deltaTime);
        }
    }
}
