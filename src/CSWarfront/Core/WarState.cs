using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Aggregate of the mod's logical state. The Game layer holds exactly one and passes it to
    /// each core step.</summary>
    public class WarState
    {
        public List<Faction> Factions = new List<Faction>();
        public RelationMatrix Relations = new RelationMatrix(5);

        /// <summary>Per-faction relations toward external threats (KAIJU/Alien, Task59). Defaults to
        /// all-Hostile (matching the pre-Godzilla/Alien-mod era and loads of v4-or-earlier saves).
        /// WarStateSerializer persists this table at the tail since format v5.</summary>
        public ThreatRelations ThreatRelations = new ThreatRelations(5);
        public List<UnitInstance> Units = new List<UnitInstance>();
        public List<MilitaryBase> Bases = new List<MilitaryBase>();
        public UnitTypeRegistry Types = new UnitTypeRegistry();
        public uint NextInstanceId = 1;

        /// <summary>Road network supplied by the Game layer (runtime-only, not persisted). Null when not supplied.</summary>
        public RoadGraph Roads;

        /// <summary>Sea navigation grid supplied by the Game layer (SeaGridBuilder) (runtime-only, not
        /// persisted; Task92). Null when not supplied = sea units move with the traditional straight line
        /// plus wall-following only.</summary>
        public SeaGrid SeaNav;

        /// <summary>Task101: rail network supplied by the Game layer (RailGraphBuilder) (runtime-only, not
        /// persisted). Null when not supplied = rail transport (TrainStep) does not run at all. Reuses the
        /// RoadGraph class for rails.</summary>
        public RoadGraph Rails;

        /// <summary>Task109: per-faction cache of rail routes (station pairs) (runtime-only, not
        /// persisted). Computing them needs an A* per pair, so re-deriving every tick drowns the sim thread
        /// in pathfinding (which actually caused the "trains stop = everything stalls" bug). Discarded via
        /// TrainStep.InvalidateRoutes when the rail network is rebuilt, and rebuilt once on demand.</summary>
        public readonly System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<TrainStep.StationPair>>
            RailRoutes = new System.Collections.Generic.Dictionary<byte, System.Collections.Generic.List<TrainStep.StationPair>>();

        /// <summary>Task94: check timer for external invasion events (InvasionEvents) (runtime-only, not
        /// persisted; resetting to 0 on load merely delays the next check by up to 6 hours — harmless).</summary>
        public float InvasionCheckAccum;

        /// <summary>Task97: spatial grid for engagement checks (runtime-only, not persisted; sim-thread
        /// only). CombatStep/KamikazeStep Build it at the top of each Advance (avoids the O(N²) sweep).</summary>
        public UnitSpatialGrid UnitGrid = new UnitSpatialGrid();

        /// <summary>Cover map (buildings/props) supplied by the Game layer (runtime-only, not persisted;
        /// Task44). Null when not supplied = CoverSeekStep performs no cover movement at all (the same
        /// pattern as Roads' RoadGraph).</summary>
        public CoverMap Cover;

        /// <summary>Surface height sampler supplied by the Game layer (runtime-only, not persisted;
        /// Task53). Null when not supplied = MovementStep interpolates waypoint/target Y as before (the
        /// backward-compatible fallback for existing behavior and tests). When supplied, unit Y snaps to
        /// the "visible" surface after road/building construction (TerrainManager.SampleDetailHeight
        /// equivalent), preventing sinking into road surfaces. Same pattern as RoadGraph/CoverMap: it
        /// shares the WarState's lifecycle (discarded with the state), so Reset() need not null it
        /// individually.</summary>
        public IHeightSampler Height;

        /// <summary>Water sampler supplied by the Game layer (runtime-only, not persisted; Task61). Null
        /// when not supplied = MovementStep's Sea branch treats everywhere as water and moves freely (the
        /// same pattern as Height/RoadGraph: a safe fallback so existing straight-line movement tests can
        /// be written plainly in test environments without a Game-layer implementation).</summary>
        public IWaterSampler Water;

        /// <summary>
        /// Task42: transient buffer of the last tick's "visible shot" events (not persisted).
        /// CombatStep/BaseCombatStep push via AddShot at the moment damage is actually applied (throttled
        /// by UnitInstance.FireCooldown, so at most one entry per attacker per tick).
        /// The Game layer's MilitaryManager.OnSimTick must Clear() at the top of every tick (before the
        /// combat steps), and OnMainVisualUpdate must copy inside _stateLock before consuming. Left
        /// unconsumed it would grow without bound, hence the consumer-clears contract.
        /// Never written by WarStateSerializer (visual-only data; no need to save).
        /// </summary>
        public List<ShotEvent> RecentShots = new List<ShotEvent>();

        /// <summary>Maximum entries addable to RecentShots per tick (Task42). A defensive cap so the
        /// presentation buffer and the downstream GameObject creation cannot balloon during huge battles.</summary>
        public const int MaxRecentShotsPerTick = 200;

        /// <summary>
        /// Task51: transient buffer of the last tick's "unit killed" events (not persisted).
        /// Exactly the RecentShots contract: CombatStep pushes via AddKill at the moment it transitions a
        /// unit to UnitState.Dead. The Game layer's MilitaryManager.OnSimTick must Clear() at the top of
        /// every tick, and OnMainVisualUpdate must copy inside _stateLock before consuming.
        /// Never written by WarStateSerializer (visual/audio-only data; no need to save).
        /// </summary>
        public List<KillEvent> RecentKills = new List<KillEvent>();

        /// <summary>Maximum entries addable to RecentKills per tick (Task51). Same defensive cap as RecentShots.</summary>
        public const int MaxRecentKillsPerTick = 200;

        /// <summary>
        /// Task54: the set of "combat zones" tracked from firing/getting hit (runtime-only, not
        /// persisted). Unlike RoadGraph/Cover it is not supplied by the Game layer — the core itself
        /// maintains it from CombatStep/BaseCombatStep reports (never written by WarStateSerializer = it
        /// resets to empty on save/load; zones vanish briefly after a load, but ongoing fighting resumes
        /// reporting within a few ticks and restores them — harmless).
        /// Constructed by a field initializer, so a newly created WarState is immediately usable without
        /// null concerns.
        /// </summary>
        public CombatZoneTracker CombatZones = new CombatZoneTracker();

        /// <summary>
        /// Task58: the set of "external threats" from other mods (Godzilla Disaster / Alien Invasion)
        /// (runtime-only, not persisted). Same pattern as RoadGraph/Cover: the Game layer
        /// (ExternalThreatBridge) re-synchronizes every tick from the other mod's live state
        /// (IsActive/position) — adding new appearances, updating positions, removing the departed.
        /// HP is managed independently here by CSWarfront (the other mods expose no HP/damage API).
        /// Never written by WarStateSerializer = resets to empty on save/load, but the Game layer restores
        /// it from the other mod's current state on the next tick — harmless.
        /// </summary>
        public List<ExternalThreat> Threats = new List<ExternalThreat>();

        /// <summary>
        /// Task63: ballistic missiles in flight (runtime-only, not persisted). The same "not in the save"
        /// policy as RoadGraph/Threats, but this is not something the Game layer re-synchronizes — the core
        /// itself (BallisticMissiles.TryLaunch / MissileStep.Advance) manages the whole life cycle from
        /// launch to impact/interception. Missiles in flight are lost on save/load (they vanish without
        /// impacting or being intercepted) — a deliberate, known limitation (MVP).
        /// </summary>
        public List<MissileInFlight> MissilesInFlight = new List<MissileInFlight>();

        /// <summary>Task63: id counter for missiles in flight. A separate namespace from UnitInstance's
        /// NextInstanceId (missiles are not UnitInstances, so a collision would be harmless, but they are
        /// separated to avoid confusion). Task92: persisted since v8 together with MissilesInFlight
        /// (fixing "missiles in flight vanish on load").</summary>
        public uint NextMissileId = 1;

        /// <summary>Task63: monotonically increasing per-simtick counter used as the hash seed of the
        /// deterministic interception roll (BallisticMissiles.TryIntercept). Runtime-only, not persisted
        /// (resetting to 0 on save/load does not change the property "same input, same result", so it is
        /// harmless).</summary>
        public uint TickCounter;

        /// <summary>
        /// Task63: transient buffer of the last tick's "ballistic missile impact/interception" events (not
        /// persisted). Same design as RecentShots/RecentKills: MissileStep.Advance pushes via AddImpact at
        /// the moment an impact/interception resolves. The Game layer (MissileVisuals) must copy inside the
        /// lock every frame before consuming. Never written by WarStateSerializer (visual/audio-only data;
        /// no need to save).
        /// </summary>
        public List<MissileImpactEvent> RecentImpacts = new List<MissileImpactEvent>();

        /// <summary>Maximum entries addable to RecentImpacts per tick. Same defensive cap as
        /// RecentShots/RecentKills.</summary>
        public const int MaxRecentImpactsPerTick = 200;

        public uint AllocInstanceId() { return NextInstanceId++; }

        /// <summary>Task63: allocates one in-flight missile id.</summary>
        public uint AllocMissileId() { return NextMissileId++; }

        /// <summary>Queues one missile impact/interception event (Task63). Silently dropped once
        /// MaxRecentImpactsPerTick is reached (the same defensive policy as AddShot/AddKill).</summary>
        public void AddImpact(MissileImpactEvent e)
        {
            if (RecentImpacts.Count >= MaxRecentImpactsPerTick) return;
            RecentImpacts.Add(e);
        }

        /// <summary>Queues one shot event (Task42). Silently dropped once MaxRecentShotsPerTick is reached
        /// (no exception — huge battles must not stop the simulation).</summary>
        public void AddShot(ShotEvent e)
        {
            if (RecentShots.Count >= MaxRecentShotsPerTick) return;
            RecentShots.Add(e);
        }

        /// <summary>Queues one kill event (Task51). Silently dropped once MaxRecentKillsPerTick is reached
        /// (the same defensive policy as AddShot).</summary>
        public void AddKill(KillEvent e)
        {
            if (RecentKills.Count >= MaxRecentKillsPerTick) return;
            RecentKills.Add(e);
        }

        public UnitInstance FindUnit(uint id)
        {
            for (int i = 0; i < Units.Count; i++)
                if (Units[i].InstanceId == id) return Units[i];
            return null;
        }

        public Faction FindFaction(byte id)
        {
            for (int i = 0; i < Factions.Count; i++)
                if (Factions[i].Id == id) return Factions[i];
            return null;
        }
    }
}
