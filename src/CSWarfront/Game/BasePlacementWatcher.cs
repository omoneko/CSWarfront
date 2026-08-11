using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Detects military base buildings that the player placed/demolished as Options-designated buildings
    /// (BaseBuildingDesignation), and creates/deletes the corresponding logical MilitaryBase (Task18;
    /// Task82 removed the power-tab cloned-prefab path and consolidated onto this path only).
    /// Threading notes:
    ///  - CS events (EventBuildingCreated/EventBuildingReleased) give no guarantee about which thread
    ///    they fire on, so the handlers are kept minimal (only recording ids) and never make CS API
    ///    calls or touch WarState (an exception escaping here would make the game show an unhandled
    ///    popup, so everything is always wrapped in try/catch).
    ///  - The actual application (reading the BuildingManager buffer + updating WarState) happens only
    ///    via ProcessPending, called from MilitaryManager.OnSimTick (sim thread, _stateLock already held).
    /// </summary>
    public static class BasePlacementWatcher
    {
        private static bool _subscribed;
        private static readonly object _pendingLock = new object();
        private static readonly List<ushort> _pendingCreated = new List<ushort>();
        private static readonly List<ushort> _pendingReleased = new List<ushort>();
        private static readonly List<ushort> _pendingRelocated = new List<ushort>(); // Task123

        /// <summary>
        /// Task60: cache of base building orientations (radians, CS Building.m_angle). Used by
        /// Game/BaseVisuals.cs to display the per-faction model overlays with the correct orientation.
        /// Writes always happen from this class's sim-thread-only method (ProcessCreated, whose caller
        /// MilitaryManager.OnSimTick already holds _stateLock) — we are already reading the CS building
        /// buffer (Building struct) there, so we piggyback on that single read and avoid adding another
        /// path that touches BuildingManager from the main thread (the safe default choice: keep the
        /// rule that CS entities are sim-thread-only).
        /// Reads (<see cref="TryGetAngle"/>) are expected to be made by MilitaryManager.OnMainVisualUpdate
        /// while it still holds the same _stateLock during snapshot construction (the Dictionary itself is
        /// not thread-safe, so thread safety is guaranteed by the callers' convention).
        /// </summary>
        private static readonly Dictionary<ushort, float> _baseAngles = new Dictionary<ushort, float>();

        /// <summary>
        /// Task74: cache of each building's <see cref="BuildingInfo"/>.name captured at base registration
        /// time (ProcessCreated). Used to narrow ReconcileBases' "ghost base" detection down to only
        /// detecting building-entity disappearance / id reuse (previously it compared Info.name against
        /// the current Options designation (BaseBuildingDesignation), so merely changing/clearing the
        /// designation after registration caused live bases to be treated as ghosts and deleted — a bug).
        /// Writes always happen from this class's sim-thread-only method (ProcessCreated, whose caller
        /// MilitaryManager.OnSimTick already holds _stateLock) — written at the same place and time as
        /// _baseAngles (Task82 removed the power-tab cloned-prefab match path, leaving the single match
        /// path via Options-designated buildings (BaseBuildingDesignation)).
        /// Reads (ReconcileBases) are expected to be made while holding the same _stateLock (the
        /// Dictionary itself is not thread-safe, so thread safety is guaranteed by the callers' convention).
        /// Note this is a session-only cache and is not part of the save file (right after a save load,
        /// the affected bases have no entry = "ProcessCreated has never run for them yet"; see the
        /// comments in ReconcileBases for how that case is handled).
        /// </summary>
        private static readonly Dictionary<ushort, string> _baseInfoNames = new Dictionary<ushort, string>();

        /// <summary>Idempotent. Expected to be called from OnLevelLoaded.</summary>
        public static void Subscribe()
        {
            if (_subscribed) return;
            try
            {
                if (!Singleton<BuildingManager>.exists)
                {
                    ModConfig.LogError("BasePlacementWatcher.Subscribe: BuildingManager not ready; skip");
                    return;
                }
                BuildingManager bm = Singleton<BuildingManager>.instance;
                bm.EventBuildingCreated += OnBuildingCreated;
                bm.EventBuildingReleased += OnBuildingReleased;
                // Task123 (bug report "units keep spawning where the base was originally built, even
                // after moving it"): relocation (Move It!, vanilla relocate) goes through
                // BuildingManager.RelocateBuilding, which keeps the same building id and raises
                // neither Created nor Released — so the logical base kept its registration-time
                // Position forever and every position-derived behavior (unit spawn point, overlay
                // model, capture radius, income sampling, rail/supply distances) stayed at the old
                // spot. EventBuildingRelocated is the event CS raises for exactly this.
                bm.EventBuildingRelocated += OnBuildingRelocated;
                _subscribed = true;
                ModConfig.Log("BasePlacementWatcher: subscribed to EventBuildingCreated/Released/Relocated");
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Subscribe exception: " + e); }
        }

        /// <summary>Idempotent. Expected to be called from OnLevelUnloading.</summary>
        public static void Unsubscribe()
        {
            if (!_subscribed) return;
            try
            {
                if (Singleton<BuildingManager>.exists)
                {
                    BuildingManager bm = Singleton<BuildingManager>.instance;
                    bm.EventBuildingCreated -= OnBuildingCreated;
                    bm.EventBuildingReleased -= OnBuildingReleased;
                    bm.EventBuildingRelocated -= OnBuildingRelocated; // Task123
                }
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Unsubscribe exception: " + e); }
            finally { _subscribed = false; }
        }

        /// <summary>Prevents carry-over at session end (via MilitaryManager.Reset).</summary>
        public static void ClearPending()
        {
            lock (_pendingLock)
            {
                _pendingCreated.Clear();
                _pendingReleased.Clear();
                _pendingRelocated.Clear(); // Task123
            }
            // Task60: the caller (MilitaryManager.Reset) already holds _stateLock, so no additional
            // lock is needed here (_baseAngles writes always happen inside that lock, via ProcessCreated).
            _baseAngles.Clear();
            // Task74: _baseInfoNames follows the same convention (_stateLock held, written only via ProcessCreated).
            _baseInfoNames.Clear();
        }

        /// <summary>Task60: returns the orientation (radians) of the given base. The caller must hold
        /// _stateLock (see the <see cref="_baseAngles"/> comment at the top of the class). For a base
        /// that has never been observed by ProcessCreated (theoretically impossible, but defensively)
        /// this returns false, and the caller should fall back to the default angle (0).</summary>
        internal static bool TryGetAngle(ushort baseId, out float angleRadians)
        {
            return _baseAngles.TryGetValue(baseId, out angleRadians);
        }

        // The actual CS event handlers. Calling thread unknown, so only record the id (always swallow exceptions).
        private static void OnBuildingCreated(ushort id)
        {
            try { lock (_pendingLock) { _pendingCreated.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingCreated exception: " + e); }
        }

        private static void OnBuildingReleased(ushort id)
        {
            try { lock (_pendingLock) { _pendingReleased.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingReleased exception: " + e); }
        }

        // Task123: the building moved but kept its id — re-read its transform on the sim thread.
        private static void OnBuildingRelocated(ushort id)
        {
            try { lock (_pendingLock) { _pendingRelocated.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingRelocated exception: " + e); }
        }

        /// <summary>
        /// Called from the sim thread (MilitaryManager.OnSimTick, whose caller already holds _stateLock).
        /// Drains the pending lists, reads the CS building buffer, and updates WarState.Bases.
        /// </summary>
        public static void ProcessPending(WarState state)
        {
            if (state == null) return;

            List<ushort> created = null;
            List<ushort> released = null;
            List<ushort> relocated = null;
            lock (_pendingLock)
            {
                if (_pendingCreated.Count > 0) { created = new List<ushort>(_pendingCreated); _pendingCreated.Clear(); }
                if (_pendingReleased.Count > 0) { released = new List<ushort>(_pendingReleased); _pendingReleased.Clear(); }
                if (_pendingRelocated.Count > 0) { relocated = new List<ushort>(_pendingRelocated); _pendingRelocated.Clear(); }
            }

            // Debug instrumentation (temporary): to isolate the cause of a bug where base registration
            // never ran, log the counts whenever the drain produced anything. Not intended as a permanent log.
            if (created != null || released != null)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessPending: drained created=" +
                    (created != null ? created.Count : 0) + " released=" + (released != null ? released.Count : 0));
            }

            // Process releases first (important): CS reuses building IDs, so if the player demolishes
            // and then rebuilds with the same ID, both events can land in the same batch. Processing
            // creations first would skip registering the new base because the duplicate check still
            // sees the lingering old base, and the old base would be removed right afterwards, leaving
            // a "building exists but no logical base" state (which no subsequent event ever repairs).
            if (released != null) ProcessReleased(state, released);
            if (created != null) ProcessCreated(state, created);
            // Task123: relocation keeps the id, so order against created/released does not matter;
            // run it last so a base registered this very tick also picks up its current transform.
            if (relocated != null) ProcessRelocated(state, relocated);
        }

        /// <summary>Task123: re-reads the transform of bases whose building was moved (Move It!,
        /// vanilla relocate) and writes it back into the logical base. Sim thread, _stateLock held by
        /// the caller — the same convention as ProcessCreated, and the same single read of the CS
        /// building buffer.</summary>
        private static void ProcessRelocated(WarState state, List<ushort> ids)
        {
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 0; i < ids.Count; i++)
            {
                ushort id = ids[i];
                MilitaryBase mb = FindBase(state, id);
                if (mb == null) continue; // not one of ours
                if (id >= buffer.Length) continue;
                if ((buffer[id].m_flags & Building.Flags.Created) == 0) continue;

                Vector3 pos = buffer[id].m_position;
                mb.Position = new WorldPos(pos.x, pos.y, pos.z);
                _baseAngles[id] = buffer[id].m_angle; // keep the faction-model overlay aligned too

                ModConfig.Log("BasePlacementWatcher: base relocated id=" + id +
                    " -> pos=(" + pos.x.ToString("0") + "," + pos.z.ToString("0") + ")");
            }
        }

        private static void ProcessCreated(WarState state, List<ushort> ids)
        {
            // Debug instrumentation (temporary): make it traceable why a base was not registered,
            // including the early returns.
            // Task82: the power-tab cloned-prefab mechanism (WarfrontBasePrefab) was fully removed, so
            // the only match targets are the building assets designated in Options
            // (BaseBuildingDesignation). If there is not a single designation, no building can ever
            // become a base, so return early (the only recovery is to designate a building per base
            // type in Options).
            if (!BaseBuildingDesignation.HasAny)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: no designated buildings; " +
                    "designate a building per base type in Options to place bases; skipping " + ids.Count + " id(s)");
                return;
            }

            if (!Singleton<BuildingManager>.exists)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: BuildingManager does not exist; skipping " + ids.Count + " id(s)");
                return;
            }
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            foreach (ushort id in ids)
            {
                if (id >= buf.Length)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " out of building buffer range (len=" + buf.Length + ") -> skip");
                    continue;
                }

                Building b = buf[id];
                bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;
                string infoName = b.Info != null ? b.Info.name : null;

                // Task82: which base type (Army/Navy/AirForce/MissileBase) a building matches is decided
                // solely by comparing Info.name against the building asset names designated in Options
                // (BaseBuildingDesignation) (the reference/name matching against the power-tab cloned
                // prefab = WarfrontBasePrefab.TryMatch has been removed).
                BaseType matchedType = default(BaseType);
                bool match = infoName != null && BaseBuildingDesignation.TryMatch(infoName, out matchedType);

                if (!flagsCreated)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=False info='" + (infoName ?? "null") +
                        "' match=" + match + " -> skip (not created)");
                    continue;
                }
                if (!match)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + (infoName ?? "null") +
                        "' match=False -> skip (not our prefab)");
                    continue;
                }

                // Task60: at this point we can take the base's orientation from the CS building buffer
                // (the Building struct, already read into b). Always update it, for both fresh
                // registration and already-restored (existing, checked below) bases — for the restore
                // case (EventBuildingCreated re-firing after a save load), this must happen before the
                // existing check, or BaseVisuals would know nothing about orientations right after
                // process startup.
                _baseAngles[id] = b.m_angle;
                // Task74: cache Info.name at the moment registration is confirmed (match==true, whether
                // via the cloned prefab or via a designated building). From then on ReconcileBases
                // compares only against this name and is unaffected by however the current Options
                // designation changes (as long as the id refers to the same building entity, the base
                // is kept).
                _baseInfoNames[id] = infoName;

                bool existing = FindBase(state, id) != null; // Idempotency: guards against post-save-load and duplicate events
                if (existing)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                        "' match=True(" + matchedType + ") existing=True -> skip (already registered)");
                    continue;
                }

                ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                    "' match=True(" + matchedType + ") existing=False -> REGISTERING");

                Vector3 pos = b.m_position;
                // Task61: create the MilitaryBase with the type identified by TryMatch — Army/Navy/AirForce
                // (previously it was always hard-coded to Army).
                var mb = new MilitaryBase(id, matchedType, new WorldPos(pos.x, pos.y, pos.z));
                mb.OwnerFactionId = WarfrontSettings.BuildFactionId;
                // Task101 (user request "trenches should be usable regardless of faction"): register
                // trenches as fully unowned terrain (the build-faction dropdown selection is ignored too.
                // The defensive bonus and the infantry's preference for positions were owner-agnostic
                // all along; only the ownership record remained, and that is now abolished).
                if (matchedType == BaseType.Trench) mb.OwnerFactionId = null;
                // Task101: default HP per type (regular bases 500 / fortifications use their per-type
                // values, see FortificationRules).
                mb.MaxHP = FortificationRules.DefaultMaxHP(matchedType);
                mb.CurrentHP = mb.MaxHP;
                // Newly built bases cannot be captured for a while (Task24): fixes a bug where a base
                // was captured one-sidedly before the player finished placing bases for both sides.
                mb.CaptureGraceHours = MilitaryBase.NewBaseGraceHours;
                state.Bases.Add(mb);

                Faction f = state.FindFaction(WarfrontSettings.BuildFactionId);
                bool isHq = false;
                if (f != null)
                {
                    // Task101: fortifications (trenches, bunkers, etc.) never become the headquarters (HQ)
                    // (preserving the meaning of HQ = the military base the faction's survival hinges on).
                    if (f.HomeBaseId == null && !FortificationRules.IsFortification(matchedType))
                    {
                        f.HomeBaseId = id;
                        mb.IsHeadquarters = true;
                        isHq = true;
                    }
                }
                else
                {
                    ModConfig.LogError("BasePlacementWatcher: no Faction found for id=" + WarfrontSettings.BuildFactionId +
                        " while registering base id=" + id + "; base added without a valid owning faction record");
                }

                ModConfig.Log("BasePlacementWatcher: base registered id=" + id +
                    " faction=" + WarfrontSettings.BuildFactionId +
                    (isHq ? " (HQ)" : "") +
                    " grace=" + mb.CaptureGraceHours.ToString("0") + "h" +
                    " pos=(" + pos.x + "," + pos.y + "," + pos.z + ")");
            }
        }

        private static void ProcessReleased(WarState state, List<ushort> ids)
        {
            // Task119 (mass-disappearance investigation): when one drain contains many releases,
            // record how many of them were our military facilities and what they were, so the next
            // occurrence of the "whole defense line vanished at once" report can be attributed. The
            // per-base "base removed" log below stays, but with dozens of entries the summary line
            // plus the cached Info names give the full picture even if the log gets truncated.
            if (ids.Count >= 10)
            {
                int military = 0;
                var sample = new System.Text.StringBuilder(256);
                foreach (ushort id in ids)
                {
                    if (FindBase(state, id) == null) continue;
                    military++;
                    if (military <= 8)
                    {
                        string infoName;
                        _baseInfoNames.TryGetValue(id, out infoName);
                        sample.Append(' ').Append(id).Append('=').Append(infoName ?? "?");
                    }
                }
                if (military > 0)
                {
                    ModConfig.Log("BasePlacementWatcher: WARNING mass release drain: total=" + ids.Count +
                        " military=" + military + " first:" + sample);
                }
            }

            foreach (ushort id in ids)
            {
                MilitaryBase mb = FindBase(state, id);
                if (mb == null) continue;

                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(id); // Task60: do not carry over the angle cache for demolished ids
                _baseInfoNames.Remove(id); // Task74: do not carry over the Info-name cache for demolished ids either

                ModConfig.Log("BasePlacementWatcher: base removed id=" + id +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// Cleans up "ghost bases" whose logical base's building (the CS building entity managed by
        /// BuildingManager) no longer exists (Task24). Fixes a bug where, if a building was demolished
        /// without ever being detected, or in cases originating from old saves, the logical base kept
        /// lingering in WarState.Bases and remained a production/attack target.
        /// Expected to be called with throttling from the sim thread (MilitaryManager.OnSimTick, whose
        /// caller already holds _stateLock).
        /// </summary>
        public static void ReconcileBases(WarState state)
        {
            if (state == null) return;
            // Task82: for the same reason as ProcessCreated, if the Options-designated buildings
            // (BaseBuildingDesignation) contain nothing to compare against, there is no point in
            // continuing (the check via the power-tab cloned prefab = WarfrontBasePrefab.
            // IsAnyRegistered has been removed).
            if (!BaseBuildingDesignation.HasAny) return;
            if (!Singleton<BuildingManager>.exists) return;

            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            // Collect deletion candidates first, then delete (to avoid modifying state.Bases while enumerating).
            List<MilitaryBase> ghosts = null;
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase mb = state.Bases[i];
                bool isGhost;
                if (mb.BaseId >= buf.Length)
                {
                    isGhost = true;
                }
                else
                {
                    Building b = buf[mb.BaseId];
                    bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;

                    if (!flagsCreated || b.Info == null)
                    {
                        // The building entity no longer exists (demolished / never created) -> delete
                        // as a ghost base (unchanged behavior).
                        isGhost = true;
                    }
                    else
                    {
                        // Task74: ReconcileBases' real job is to catch exactly two things: "the building
                        // no longer exists" and "the id was reused for an unrelated building". Comparing
                        // here against the current Options designation (BaseBuildingDesignation) is
                        // wrong — merely changing/clearing the designation after registration would get
                        // live bases treated as ghosts and deleted (the bug reported in Task74). So the
                        // current designation is never consulted.
                        string cachedName;
                        if (_baseInfoNames.TryGetValue(mb.BaseId, out cachedName))
                        {
                            // By comparing the Info.name recorded at registration (ProcessCreated)
                            // against the current Info.name, we detect only genuine id reuse (this id
                            // was reassigned to a different building type after demolition). Whatever
                            // the current designation is, the base is not treated as a ghost as long
                            // as it is the same building as at registration.
                            isGhost = b.Info.name != cachedName;
                        }
                        else
                        {
                            // Not in the cache = a base ProcessCreated has never run for in this session
                            // (typically right after restoring from a save: _baseInfoNames is a
                            // session-only cache and not part of the save data). Treat this case
                            // leniently: trust the mere fact that the building entity is alive
                            // (flagsCreated && Info!=null was verified above) and keep the base — the
                            // base must have been valid at the moment it was saved, and id reuse cannot
                            // happen until CS itself releases the id once and then reassigns it (as long
                            // as flagsCreated has stayed true, this id has never been demolished).
                            // Backfill the current Info.name into the cache here, so it joins the normal
                            // id-reuse detection (the branch above) from then on.
                            isGhost = false;
                            _baseInfoNames[mb.BaseId] = b.Info.name;
                        }

                        // Task123 safety net: re-sync the transform even when no relocation event was
                        // seen (a save made before this fix, or a mod that writes m_position directly).
                        // The building is alive here and we already hold its struct, so this is free.
                        if (!isGhost)
                        {
                            Vector3 livePos = b.m_position;
                            if (mb.Position.HorizontalDistanceTo(new WorldPos(livePos.x, livePos.y, livePos.z)) > 1f)
                            {
                                mb.Position = new WorldPos(livePos.x, livePos.y, livePos.z);
                                _baseAngles[mb.BaseId] = b.m_angle;
                                ModConfig.Log("BasePlacementWatcher: ReconcileBases: re-synced moved base id=" +
                                    mb.BaseId + " -> pos=(" + livePos.x.ToString("0") + "," + livePos.z.ToString("0") + ")");
                            }
                        }
                    }
                }
                if (isGhost)
                {
                    if (ghosts == null) ghosts = new List<MilitaryBase>();
                    ghosts.Add(mb);
                }
            }

            if (ghosts == null) return;

            foreach (MilitaryBase mb in ghosts)
            {
                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(mb.BaseId); // Task60: do not carry over the angle cache for ghost bases
                _baseInfoNames.Remove(mb.BaseId); // Task74: do not carry over the Info-name cache for ghost bases either
                ModConfig.Log("BasePlacementWatcher: ReconcileBases: removed ghost base id=" + mb.BaseId +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// Removes a base from the logical state, and if it was its owning faction's HQ, clears
        /// HomeBaseId and promotes the first remaining owned base, if any (logic shared by the
        /// demolition path and the ghost-base cleanup path).
        /// Eliminated is not set here (faction elimination is a combat outcome decided by Core's
        /// Occupation).
        /// </summary>
        private static void RemoveBaseAndReassignHq(WarState state, MilitaryBase mb)
        {
            state.Bases.Remove(mb);

            if (mb.OwnerFactionId == null) return;
            ReassignHqIfCleared(state, mb.OwnerFactionId.Value, mb.BaseId);
        }

        /// <summary>
        /// If factionId's HomeBaseId points at baseId, clears it and, if the faction still owns other
        /// bases, promotes the first one to the new HQ. Does nothing if it does not point there (no-op).
        /// This is the single implementation of HQ-consistency maintenance, shared by both base removal
        /// (RemoveBaseAndReassignHq) and ownership changes (MilitaryManager.TrySetBaseOwner) (Task25:
        /// exposed as internal to avoid duplicating the logic).
        /// The promotion itself calls Core.FactionStatus.PromoteFirstOwnedBaseToHq (Task46: to avoid
        /// duplicating the "promote the first owned base to the new HQ" rule with FactionStatus.Refresh).
        /// The caller must hold _stateLock.
        /// </summary>
        internal static void ReassignHqIfCleared(WarState state, byte factionId, ushort baseId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null || !f.HomeBaseId.HasValue || f.HomeBaseId.Value != baseId) return;

            f.HomeBaseId = null;
            // At this point the base for baseId has either already been removed (via
            // RemoveBaseAndReassignHq) or has already switched to an owner other than factionId (via
            // TrySetBaseOwner), so PromoteFirstOwnedBaseToHq's ownership check naturally excludes baseId.
            FactionStatus.PromoteFirstOwnedBaseToHq(state, factionId);
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
