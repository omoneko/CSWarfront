using System;
using System.Reflection;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task58: Bridges monsters/invaders spawned by other mods (Godzilla disaster = GodzillaDisaster,
    /// alien invasion = AlienInvasion; separate mods by the same author that may not be installed)
    /// into CSWarfront's combat (Core/ThreatCombatStep) as "external threats" (ExternalThreat).
    /// Sim-thread only (must be called from inside MilitaryManager.OnSimTick's _stateLock).
    ///
    /// Design principles:
    ///  - The other mods' assemblies are not referenced at build time (no references added to the
    ///    csproj). They are resolved by name via reflection from the loaded assemblies
    ///    (AppDomain.CurrentDomain.GetAssemblies()). In environments where they are not installed the
    ///    assembly simply is not found; this is not treated as an error.
    ///  - Type/member resolution is attempted only once per process and the result (whether success
    ///    or failure) is cached (the fact that resolution failed is cached too, so environments
    ///    without the mods never pay per-tick reflection costs).
    ///  - The other mods expose no HP/damage/defeat API, so HP is owned independently by CSWarfront
    ///    (ExternalThreat). When it is whittled down to 0, we look for the other mod's
    ///    "defeat/force-despawn" method and call it: Defeat or ForceDespawn (which may be added in the
    ///    future; neither exists as of Task58) is preferred, falling back to ResetForNewLevel
    ///    (ForceCleanup equivalent) otherwise. Which one was actually found is logged once at
    ///    resolution time.
    ///  - No matter what happens, no exception is thrown into the game loop (swallowed via try/catch,
    ///    logged once, and the bridge to that mod is disabled for the rest of the session).
    /// </summary>
    internal static class ExternalThreatBridge
    {
        /// <summary>Interval (in-game hours) at which the other mods' current state (alive/position)
        /// is reflected into State.Threats. There is no need to query every tick = a giant monster's
        /// position being stale for the duration of this interval does no real harm (same reasoning
        /// as the RoadGraph/CoverMap rebuild throttling).</summary>
        public const float ThreatSyncIntervalHours = 0.1f;

        // HP/armor table (values specified in the design, Task58). The other mods carry no HP, so
        // this is CSWarfront's single source of truth. Radius is a value tuned together with
        // ThreatCombatStep.ThreatArmor to give large targets a usable hitbox (it is added to a
        // unit's Range, see Core/ThreatCombatStep).
        //
        // Task64 re-tuning (old Godzilla20000/Alien8000 -> 65000/26000): after raising
        // Core/ThreatCombatStep.ThreatArmor from 20 to 45, HP was re-set so that a formation of 50
        // Tier5 tanks (DamagePerHit(104,45)=59 * accuracy0.868 ≈ 51.2/h, ~2560/h total) kills the
        // threat in roughly one in-game day:
        //   Godzilla 65000 / 2560 ≈ 25.4h (a bit over 1 day)
        //   Alien    26000 / 2560 ≈ 10.2h (about 40% of Godzilla; positioned as a "smaller threat"
        //   dispatched in less time)
        // Bombers and observer-supported artillery whittle these down far faster (see the task-64
        // report for details). Ballistic missiles (BallisticMissiles.ImpactDamageThreat=2000) were
        // raised at the same time; a full stockpile of 5 (10000) amounts to about 15% of Godzilla /
        // 38% of Alien — a "force-supplementing strike" that does not solve the fight by itself.
        private const float GodzillaMaxHP = 65000f;
        private const float GodzillaRadius = 45f;
        private const float AlienMaxHP = 26000f;
        private const float AlienRadius = 25f;

        // Initializing to ThreatSyncIntervalHours makes the very first OnSimTick call after session
        // start sync immediately (same "don't wait on the first pass" policy as RoadGraphBuildRetry).
        private static float _accum = ThreatSyncIntervalHours;

        private static readonly MonsterModAdapter _godzilla = new MonsterModAdapter(
            "Godzilla", "GodzillaDisaster", "GodzillaDisaster.Game.GodzillaManager", "TryGetPosition");
        private static readonly MonsterModAdapter _alien = new MonsterModAdapter(
            "Alien", "AlienInvasion", "AlienInvasion.Game.InvasionManager", "TryGetAnyTripodPosition");

        // Task83: reads each mod's beam-firing log (Godzilla ray = RayStrikeLog, tripod laser =
        // BeamStrikeLog; both are public APIs of the form "monotonically increasing ID + float[]
        // snapshot") and, for each newly arrived firing, applies one-shot damage to units via
        // Core/ThreatBeamStep.ApplyStrike.
        private static readonly BeamLogAdapter _godzillaBeams = new BeamLogAdapter(
            "Godzilla ray", "GodzillaDisaster", "GodzillaDisaster.Game.RayStrikeLog", ThreatKind.Kaiju);
        private static readonly BeamLogAdapter _alienBeams = new BeamLogAdapter(
            "Alien laser", "AlienInvasion", "AlienInvasion.Game.BeamStrikeLog", ThreatKind.Alien);

        // Ids of the entries this bridge manages inside State.Threats (at most one each for
        // Godzilla/Alien. The other mods themselves only expose single-instance APIs
        // (IsActive/TryGetPosition), so CSWarfront likewise tracks at most one of each).
        // 0 means "no entry at present".
        private static uint _godzillaThreatId;
        private static uint _alienThreatId;
        private static uint _nextThreatId = 1;

        /// <summary>Call every tick from the sim thread inside _stateLock. Throttling is internal, so
        /// the caller does not need to care about the interval.</summary>
        /// <summary>Task59: whether GodzillaDisaster is installed (assembly + expected members
        /// resolved). Used by OptionsRelationsPage to decide whether to show the KAIJU relations rows.
        /// Reflection resolution is performed exactly once on first access (only the failure is cached
        /// if not installed) and the result is returned thereafter.</summary>
        public static bool IsGodzillaModPresent { get { return _godzilla.IsAvailable(); } }

        /// <summary>Task59: same as above but for AlienInvasion (used to show the Alien rows on the
        /// Options screen).</summary>
        public static bool IsAlienModPresent { get { return _alien.IsAvailable(); } }

        public static void Advance(WarState state, float dt)
        {
            _accum += dt;
            if (_accum < ThreatSyncIntervalHours) return;
            _accum = 0f;

            try
            {
                SyncOne(state, _godzilla, "Godzilla", ThreatKind.Kaiju, GodzillaMaxHP, GodzillaRadius, ref _godzillaThreatId);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: exception while syncing Godzilla: " + e);
            }

            try
            {
                SyncOne(state, _alien, "Alien", ThreatKind.Alien, AlienMaxHP, AlienRadius, ref _alienThreatId);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: exception while syncing Alien: " + e);
            }

            // Task83: consume the beam-firing logs (apply damage to units if there are new entries).
            // The same 0.1h interval as the sync is sufficient (in real time roughly every 0.05s =
            // the damage lands practically simultaneously with the firing visual).
            try
            {
                _godzillaBeams.Consume(state);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: exception while consuming Godzilla ray strikes: " + e);
            }

            try
            {
                _alienBeams.Consume(state);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: exception while consuming Alien laser strikes: " + e);
            }
        }

        private static void SyncOne(WarState state, MonsterModAdapter adapter, string label, ThreatKind kind, float maxHp,
            float radius, ref uint threatId)
        {
            bool isActive;
            Vector3 pos;
            bool resolved = adapter.TryGetState(out isActive, out pos);

            if (!resolved || !isActive)
            {
                // Not installed / permanent error / currently inactive: if an entry exists, treat it
                // as "gone" and clean it up.
                RemoveThreat(state, ref threatId);
                return;
            }

            WorldPos worldPos = new WorldPos(pos.x, pos.y, pos.z);
            ExternalThreat threat = threatId != 0 ? FindThreat(state, threatId) : null;

            if (threat == null)
            {
                threat = new ExternalThreat
                {
                    Id = _nextThreatId++,
                    Kind = kind,
                    Position = worldPos,
                    Radius = radius,
                    MaxHP = maxHp,
                    CurrentHP = maxHp
                };
                state.Threats.Add(threat);
                threatId = threat.Id;
                ModConfig.Log("ExternalThreatBridge: " + label + " appeared (HP=" + maxHp.ToString("0") + ").");
                return;
            }

            threat.Position = worldPos;

            if (threat.IsDefeated)
            {
                float totalDamage = threat.MaxHP; // reduced from MaxHP to 0 = total damage dealt
                adapter.Despawn();
                ModConfig.Log("ExternalThreatBridge: " + label + " repelled (total damage " + totalDamage.ToString("0") + ").");
                RemoveThreat(state, ref threatId);
            }
        }

        private static ExternalThreat FindThreat(WarState state, uint id)
        {
            for (int i = 0; i < state.Threats.Count; i++)
                if (state.Threats[i].Id == id) return state.Threats[i];
            return null;
        }

        private static void RemoveThreat(WarState state, ref uint threatId)
        {
            if (threatId == 0) return;
            uint id = threatId; // ref parameters can't be captured by the RemoveAll lambda below
            state.Threats.RemoveAll(t => t.Id == id);
            threatId = 0;
        }

        /// <summary>Reflection bridge to one other mod (Godzilla or Alien). Type/member resolution is
        /// attempted only once; the cached result is reused thereafter.</summary>
        private sealed class MonsterModAdapter
        {
            private readonly string _label;          // for logging ("Godzilla" / "Alien")
            private readonly string _assemblyName;    // e.g. "GodzillaDisaster"
            private readonly string _typeName;        // e.g. "GodzillaDisaster.Game.GodzillaManager"
            private readonly string _positionMethodName; // "TryGetPosition" / "TryGetAnyTripodPosition"

            private bool _resolveAttempted;
            private bool _available;
            private PropertyInfo _isActiveProp;
            private MethodInfo _positionMethod;
            private MethodInfo _despawnMethod;
            private bool _stateErrorLogged;

            public MonsterModAdapter(string label, string assemblyName, string typeName, string positionMethodName)
            {
                _label = label;
                _assemblyName = assemblyName;
                _typeName = typeName;
                _positionMethodName = positionMethodName;
            }

            /// <summary>Task59: whether this mod is installed (type/member resolution succeeded).
            /// EnsureResolved already implements "attempt once, then return the cache", so it is
            /// exposed as-is.</summary>
            public bool IsAvailable()
            {
                return EnsureResolved();
            }

            /// <summary>Gets IsActive/position. A return value of false means "the bridge to this mod
            /// is unusable" (not installed, or resolution/invocation failed permanently); in that case
            /// isActive/position may be ignored. A return value of true with isActive=false means the
            /// mod is simply inactive at the moment.</summary>
            public bool TryGetState(out bool isActive, out Vector3 position)
            {
                isActive = false;
                position = default(Vector3);

                if (!EnsureResolved()) return false;

                try
                {
                    isActive = (bool)_isActiveProp.GetValue(null, null);
                    if (!isActive) return true;

                    object[] args = { null };
                    bool found = (bool)_positionMethod.Invoke(null, args);
                    if (found)
                    {
                        position = (Vector3)args[0];
                    }
                    else
                    {
                        // IsActive but no position available: treat this instant as "no target"
                        // (retry next time).
                        isActive = false;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    if (!_stateErrorLogged)
                    {
                        _stateErrorLogged = true;
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " state retrieval error, disabling the bridge for the rest of this session: " + e);
                    }
                    _available = false; // from now on EnsureResolved returns false without retrying
                    return false;
                }
            }

            /// <summary>Despawn call on defeat (Defeat/ForceDespawn if present, ResetForNewLevel
            /// otherwise). Does nothing if the bridge is disabled. Exceptions are swallowed with only
            /// a log entry (the defeat log line has already been emitted, so there is no reason to
            /// halt the game over a failed despawn call).</summary>
            public void Despawn()
            {
                if (!_available || _despawnMethod == null) return;
                try
                {
                    _despawnMethod.Invoke(null, null);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("ExternalThreatBridge: " + _label + " despawn call (" +
                        _despawnMethod.Name + ") failed: " + e);
                }
            }

            private bool EnsureResolved()
            {
                if (_resolveAttempted) return _available;
                _resolveAttempted = true;

                try
                {
                    Assembly asm = FindAssembly(_assemblyName);
                    if (asm == null)
                    {
                        // Not installed: not an error. As with DIAG, do not log routinely (convention).
                        _available = false;
                        return false;
                    }

                    Type type = asm.GetType(_typeName);
                    if (type == null)
                    {
                        ModConfig.LogError("ExternalThreatBridge: " + _label + " type not found (" +
                            _typeName + "). Disabling the bridge.");
                        _available = false;
                        return false;
                    }

                    PropertyInfo isActiveProp = type.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo positionMethod = type.GetMethod(_positionMethodName, BindingFlags.Public | BindingFlags.Static);
                    MethodInfo resetMethod = type.GetMethod("ResetForNewLevel", BindingFlags.Public | BindingFlags.Static);

                    if (isActiveProp == null || positionMethod == null || resetMethod == null)
                    {
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " expected member(s) (IsActive/" + _positionMethodName + "/ResetForNewLevel) not found. Disabling the bridge.");
                        _available = false;
                        return false;
                    }

                    // Prefer Defeat/ForceDespawn (dedicated APIs that may be added in the future) and
                    // fall back to ResetForNewLevel otherwise. Since there is no build-time reference,
                    // the only way to know what was actually found is this one-time reflection
                    // resolution = report it here.
                    MethodInfo despawn = type.GetMethod("Defeat", BindingFlags.Public | BindingFlags.Static)
                        ?? type.GetMethod("ForceDespawn", BindingFlags.Public | BindingFlags.Static)
                        ?? resetMethod;

                    _isActiveProp = isActiveProp;
                    _positionMethod = positionMethod;
                    _despawnMethod = despawn;
                    _available = true;

                    ModConfig.Log("ExternalThreatBridge: detected " + _label + " (despawn=" + despawn.Name + ").");
                    return true;
                }
                catch (Exception e)
                {
                    ModConfig.LogError("ExternalThreatBridge: failed to resolve " + _label + ": " + e);
                    _available = false;
                    return false;
                }
            }

            private static Assembly FindAssembly(string name)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == name) return assemblies[i];
                }
                return null;
            }
        }

        /// <summary>Task83: reflection bridge to one other mod's beam-firing log (CurrentId(): long /
        /// Snapshot(): float[], newest first, {id, startX, startZ, endX, endZ} ×N).
        /// Same policy as MonsterModAdapter: "resolve only once, disable for the rest of the session
        /// on a permanent error, never throw into the game loop". Must be called from the sim thread
        /// inside _stateLock.</summary>
        private sealed class BeamLogAdapter
        {
            private readonly string _label;
            private readonly string _assemblyName;
            private readonly string _typeName;
            private readonly ThreatKind _kind;

            private bool _resolveAttempted;
            private bool _available;
            private MethodInfo _currentIdMethod;
            private MethodInfo _snapshotMethod;
            private bool _errorLogged;

            /// <summary>Last consumed firing ID. -1 means "not yet baselined" = the first Consume sets
            /// the current ID as consumed, so firings before it (leftovers from a previous session,
            /// etc.) never deal damage. The other mod's IDs never roll back even across level reloads
            /// (contract of RayStrikeLog/BeamStrikeLog), so this consumed ID also needs no
            /// reset.</summary>
            private long _lastConsumedId = -1;

            public BeamLogAdapter(string label, string assemblyName, string typeName, ThreatKind kind)
            {
                _label = label;
                _assemblyName = assemblyName;
                _typeName = typeName;
                _kind = kind;
            }

            public void Consume(WarState state)
            {
                if (!EnsureResolved()) return;

                try
                {
                    long current = (long)_currentIdMethod.Invoke(null, null);
                    if (_lastConsumedId < 0)
                    {
                        _lastConsumedId = current; // baseline: past firings are not applied
                        return;
                    }
                    if (current <= _lastConsumedId) return;

                    float[] snap = (float[])_snapshotMethod.Invoke(null, null);
                    for (int s = 0; s + 4 < snap.Length; s += 5)
                    {
                        long id = (long)snap[s];
                        if (id <= _lastConsumedId) break; // newest first, so stop once we hit a consumed ID

                        int hits = ThreatBeamStep.ApplyStrike(state, _kind,
                            snap[s + 1], snap[s + 2], snap[s + 3], snap[s + 4]);
                        if (hits > 0)
                        {
                            ModConfig.Log("ExternalThreatBridge: " + _label + " strike hit " + hits + " unit(s).");
                        }
                    }
                    _lastConsumedId = current;
                }
                catch (Exception e)
                {
                    if (!_errorLogged)
                    {
                        _errorLogged = true;
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " strike log read error, disabling for the rest of this session: " + e);
                    }
                    _available = false;
                }
            }

            private bool EnsureResolved()
            {
                if (_resolveAttempted) return _available;
                _resolveAttempted = true;

                try
                {
                    Assembly asm = FindBeamAssembly(_assemblyName);
                    if (asm == null)
                    {
                        _available = false; // not installed: not an error
                        return false;
                    }

                    Type type = asm.GetType(_typeName);
                    if (type == null)
                    {
                        // Older version of the other mod (strike-log API not present yet): not an
                        // error, the feature is simply disabled.
                        ModConfig.Log("ExternalThreatBridge: " + _label + " strike log not found (" + _typeName +
                            "); beam damage to units is disabled (older mod version?).");
                        _available = false;
                        return false;
                    }

                    MethodInfo currentId = type.GetMethod("CurrentId", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo snapshot = type.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static);
                    if (currentId == null || snapshot == null)
                    {
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " strike log members (CurrentId/Snapshot) not found. Disabling.");
                        _available = false;
                        return false;
                    }

                    _currentIdMethod = currentId;
                    _snapshotMethod = snapshot;
                    _available = true;
                    ModConfig.Log("ExternalThreatBridge: detected " + _label + " strike log.");
                    return true;
                }
                catch (Exception e)
                {
                    ModConfig.LogError("ExternalThreatBridge: failed to resolve " + _label + " strike log: " + e);
                    _available = false;
                    return false;
                }
            }

            private static Assembly FindBeamAssembly(string name)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == name) return assemblies[i];
                }
                return null;
            }
        }
    }
}
