using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for sim-thread-driven execution (OnSimTick). Split into a
    /// partial class because of the 500-line limit on MilitaryManager.cs (same policy as Task34's
    /// MilitaryManagerManualProduction etc.). _stateLock / State and the accumulator counters that
    /// Reset() zeroes out (_economyAccum etc.) are private static members declared in
    /// MilitaryManager.cs; since this is a partial class they are directly accessible from here.
    ///
    /// OnSimTick is invoked from the sim thread via ThreadingExtensionBase.OnAfterSimulationTick and
    /// is dedicated to Core decision logic plus read-only access to CS buffers (it never touches
    /// Unity GameObjects).
    /// </summary>
    public static partial class MilitaryManager
    {
        // Throttling for diagnostic logging (touched only by the sim thread).
        private static int _diagTicks;
        private const int DiagIntervalTicks = 300;
        private const float EconomyIntervalHours = 6f;      // Economy tick interval (in-game time, 4 times per day)
        // Task35: with 0.01f the income amounts were so small they looked like essentially zero in the
        // UI, which caused the misunderstanding that "income is not implemented". 0.04f is a game-balance
        // tuning value (a balance knob) and may be adjusted further based on playtest results.
        private const float IncomeRate = 0.04f;

        private const float RoadRebuildIntervalHours = 12f;

        // Task92: rebuild interval for the sea navigation grid (State.SeaNav). Water areas change more
        // rarely than roads, so this is longer.
        private const float SeaGridRebuildIntervalHours = 24f;
        private static float _seaGridRebuildAccum;
        private static bool _hasAttemptedSeaGridBuild;

        // Task94: flag that signals an invasion occurrence to the main thread's toast display
        // (one-way sim->main; benign because it is a bool).
        private static bool _invasionToastPending;

        // Task101: build timers for the rail network (State.Rails) (same pattern as the road network).
        private static float _railBuildRetryAccum;
        private static float _railRebuildAccum;
        private static bool _hasAttemptedRailBuild;
        private const float RoadBuildRetryIntervalHours = 0.25f;

        private const float CoverRebuildIntervalHours = 12f;
        private const float CoverBuildRetryIntervalHours = 0.25f;

        private const float BaseReconcileIntervalHours = 6f;

        private const float MaxHoursPerTick = 1f; // Clamp ceiling against large clock jumps, e.g. right after a save load

        /// <summary>
        /// Sim thread (via ThreadingExtensionBase.OnAfterSimulationTick): concentrates decision logic
        /// (Core) and CS entity operations (reading the building buffer etc.) in this single place.
        /// Since Task19 units themselves carry no CS entity (vehicle), so no vehicle creation/release
        /// happens here.
        /// Note: OnAfterSimulationTick does not fire while the game is paused, so base placement and
        /// production wait until the game is unpaused (acceptable for the MVP).
        /// </summary>
        public static void OnSimTick()
        {
            EnsureInitialized();
            if (State == null) return;

            // dt = in-game time elapsed since the previous tick (in hours). Automatically reflects
            // game speed (1x/2x/3x) and pausing (Task21). SimulationManager.instance.m_currentGameTime
            // is a DateTime field verified via reflection against Assembly-CSharp.dll.
            DateTime now = SimulationManager.instance.m_currentGameTime;
            float dt;
            if (!_hasLastGameTime)
            {
                // First tick (or right after Reset()): just establish the time baseline without advancing.
                _lastGameTime = now;
                _hasLastGameTime = true;
                dt = 0f;
            }
            else
            {
                dt = (float)(now - _lastGameTime).TotalHours;
                _lastGameTime = now;
            }

            if (dt <= 0f) return; // Paused / in-game clock not advancing: only update the timestamp and do nothing this tick
            if (dt > MaxHoursPerTick) dt = MaxHoursPerTick; // Protect against huge jumps, e.g. right after a save load

            SpeedCalibrationDiagnostics.AccumulateGameHours(dt);

            lock (_stateLock)
            {
                // Task42: muzzle-flash events (ShotEvent) are a transient buffer holding only "the most
                // recent single tick", so they must be cleared before the combat step (otherwise past
                // ticks' entries keep getting double-consumed by the Game layer and the buffer grows
                // without bound).
                State.RecentShots.Clear();
                // Task51: kill events (KillEvent) are cleared for exactly the same reason and at exactly
                // the same timing as RecentShots.
                State.RecentKills.Clear();
                // Task63: missile impact/interception events (RecentImpacts) are cleared for exactly the
                // same reason and at the same timing as RecentShots/RecentKills (the contract is that
                // MissileStep.Advance itself does not clear them).
                State.RecentImpacts.Clear();

                // Reflect military base buildings that the player placed/demolished as Options-designated
                // buildings into the logical bases (WarState.Bases) (Task18; Task82 removed the duplicated
                // electricity-tab prefab route and unified everything onto this route alone).
                // Sim-thread only because it involves reading the CS building buffer. A newly registered
                // base becomes a production target in the same tick via the ProductionPlanning call
                // immediately below.
                BasePlacementWatcher.ProcessPending(State);

                // Task106: lay trench lines (continuous placement via CreateBuilding between the two points
                // queued by the UI. The generated buildings are registered as logical trenches by the next
                // tick's BasePlacementWatcher.ProcessPending).
                ProcessPendingTrenchLines();

                // Task114: register/rebuild the saved defense layout (requests queued by the
                // construction panel's Save Defense Layout / Rebuild Defenses buttons; rebuilt
                // buildings are registered as logical facilities by BasePlacementWatcher as usual).
                ProcessDefenseLayoutRequests();

                // Task134: clear every unit off the map on request (construction panel's Disband All
                // Units button). Done on the sim thread like every other unit mutation.
                ProcessDisbandRequest();

                // Task106: clear problem icons (no road connection, electricity, water, etc.) on
                // fortification-type buildings (field fortifications are treated as not needing city
                // infrastructure).
                SuppressFortificationProblems();

                // Task71: apply to the CS building buffer the pending "should this base's vanilla look be
                // hidden" entries recorded by faction-asset overlay creation/destruction (BaseVisuals, main
                // thread) (requirement 2, anti-stacking). Placed right after
                // BasePlacementWatcher.ProcessPending (after base registration/demolition has been settled).
                BaseHiddenSync.ApplyPending();

                // Cleanup of ghost bases (logical bases whose building entity no longer exists) (Task24).
                // Sim-thread only because it involves reading the CS building buffer. A full scan every
                // tick is wasteful, so run only at a fixed interval.
                _baseReconcileAccum += dt;
                if (_baseReconcileAccum >= BaseReconcileIntervalHours)
                {
                    _baseReconcileAccum -= BaseReconcileIntervalHours;
                    BasePlacementWatcher.ReconcileBases(State);
                }

                // Production planning (refill queues by spending treasury) -> production -> only add
                // completed items as UnitInstance (Task19: no CS vehicle CreateVehicle. The visuals are
                // rebuilt declaratively from State.Units by OnMainVisualUpdate).
                ProductionPlanning.Advance(State);
                var completed = ProductionStep.Advance(State, dt);
                foreach (var c in completed)
                {
                    uint id = State.AllocInstanceId();
                    var type = State.Types.Get(c.TypeKey);
                    State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                }
                // Task63: progress of ballistic-missile stockpile construction (only for bases that
                // ProductionPlanning.Advance has already started on). Aligned with the same
                // "production planning -> progress consumption" ordering as unit production.
                MissileStockpile.Advance(State, dt);

                // Task64: carrier air wing operations. CarrierAirWing adds UnitInstances directly without
                // using the base (MilitaryBase) queue mechanism (same ordering position as ProductionStep,
                // immediately after ProductionStep itself). The design is that CarrierAirWing.Advance does
                // not raise exceptions, but it is wrapped in try/catch as a last line of defense so the sim
                // loop can never be stopped (an extra guard not present on the other Core step calls, but
                // warranted for a task that hooks new logic into the game loop for the first time).
                try
                {
                    CarrierAirWing.Advance(State, dt);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("MilitaryManager: exception in CarrierAirWing.Advance: " + e);
                }

                // Build/rebuild the road network (State.Roads). Done before the advance orders so that
                // InvasionOrders can compute paths in the same tick (Task23). If not yet supplied, build it
                // immediately here; if already supplied, rebuild at a fixed interval to reflect the
                // player's road construction/demolition. On build failure (null), keep the existing graph
                // as-is (so a transient failure does not lose pathfinding capability).
                if (State.Roads == null)
                {
                    // While failures persist, space out attempts so we don't retry a full build (plus a
                    // failure log) every tick (Task23 review Important). Only the very first attempt of the
                    // session runs immediately without waiting for the interval.
                    _roadBuildRetryAccum += dt;
                    if (!_hasAttemptedRoadBuild || _roadBuildRetryAccum >= RoadBuildRetryIntervalHours)
                    {
                        _hasAttemptedRoadBuild = true;
                        _roadBuildRetryAccum -= RoadBuildRetryIntervalHours;
                        if (_roadBuildRetryAccum < 0f) _roadBuildRetryAccum = 0f;
                        State.Roads = RoadGraphBuilder.Build();
                    }
                }
                else
                {
                    _roadRebuildAccum += dt;
                    if (_roadRebuildAccum >= RoadRebuildIntervalHours)
                    {
                        _roadRebuildAccum -= RoadRebuildIntervalHours;
                        var rebuilt = RoadGraphBuilder.Build();
                        if (rebuilt != null) State.Roads = rebuilt;
                    }
                }

                // Task92: build/rebuild the sea navigation grid (State.SeaNav). Same supply pattern as the
                // road network. Build immediately the first time, then rebuild every
                // SeaGridRebuildIntervalHours (keep the existing grid on failure).
                if (State.SeaNav == null)
                {
                    if (!_hasAttemptedSeaGridBuild)
                    {
                        _hasAttemptedSeaGridBuild = true;
                        State.SeaNav = SeaGridBuilder.Build();
                    }
                    else
                    {
                        // After the first failure (including maps without water), retry only at the rebuild interval.
                        _seaGridRebuildAccum += dt;
                        if (_seaGridRebuildAccum >= SeaGridRebuildIntervalHours)
                        {
                            _seaGridRebuildAccum = 0f;
                            State.SeaNav = SeaGridBuilder.Build();
                        }
                    }
                }
                else
                {
                    _seaGridRebuildAccum += dt;
                    if (_seaGridRebuildAccum >= SeaGridRebuildIntervalHours)
                    {
                        _seaGridRebuildAccum = 0f;
                        var rebuiltSea = SeaGridBuilder.Build();
                        if (rebuiltSea != null) State.SeaNav = rebuiltSea;
                    }
                }

                // Task101: build/rebuild the rail network (State.Rails). Same supply pattern as the road
                // network (every 12h). On every rebuild, re-derive the cargo stations' rail-connection
                // determination (RailConnected) as well.
                if (State.Rails == null)
                {
                    _railBuildRetryAccum += dt;
                    if (!_hasAttemptedRailBuild || _railBuildRetryAccum >= RoadBuildRetryIntervalHours)
                    {
                        _hasAttemptedRailBuild = true;
                        _railBuildRetryAccum = 0f;
                        State.Rails = RailGraphBuilder.Build();
                        if (State.Rails != null)
                        {
                            CargoStationRules.RefreshConnectivity(State);
                            TrainStep.InvalidateRoutes(State); // Task109: force the route cache to be rebuilt
                        }
                    }
                }
                else
                {
                    _railRebuildAccum += dt;
                    if (_railRebuildAccum >= RoadRebuildIntervalHours)
                    {
                        _railRebuildAccum = 0f;
                        var rebuiltRail = RailGraphBuilder.Build();
                        if (rebuiltRail != null) State.Rails = rebuiltRail;
                        CargoStationRules.RefreshConnectivity(State);
                        // Task109: routes (station pairs) are a heavy computation requiring an A* per pair,
                        // so discard and rebuild them only at this timing, when the rail network or stations
                        // may have changed (recomputing every tick would swamp the sim thread with
                        // pathfinding, stalling not just the trains but everything).
                        TrainStep.InvalidateRoutes(State);
                        LogRailRoutes(); // Task107: for isolating the cause when trains don't move
                    }
                }

                // Supply the surface height sampler (State.Height) (Task53). Unlike RoadGraph/Cover it is
                // not a "snapshot" that needs rebuilding every tick, but a thin adapter that queries
                // TerrainManager on the spot, so create it once and keep reusing it (while unsupplied it
                // stays null = MovementStep automatically falls back to the legacy Y interpolation, so no
                // retry-on-failure logic is needed). When State itself is discarded, Height dies with it
                // (same lifecycle as Roads).
                if (State.Height == null)
                {
                    State.Height = new SurfaceHeightSampler();
                }

                // Supply the water surface sampler (State.Water) (Task61). Exactly the same pattern as
                // State.Height: a thin adapter created once and reused thereafter (while unsupplied it
                // stays null = MovementStep's Sea branch automatically switches to the "always on water"
                // fallback).
                if (State.Water == null)
                {
                    State.Water = new WaterSampler();
                }

                // Build/rebuild the cover map (State.Cover) (Task44). Same pattern as RoadGraph: "attempt a
                // build immediately when unsupplied / rebuild at a fixed interval when supplied / keep the
                // existing map on failure". Done before the advance orders so that CoverSeekStep can use
                // this map in the same tick.
                if (State.Cover == null)
                {
                    _coverBuildRetryAccum += dt;
                    if (!_hasAttemptedCoverBuild || _coverBuildRetryAccum >= CoverBuildRetryIntervalHours)
                    {
                        _hasAttemptedCoverBuild = true;
                        _coverBuildRetryAccum -= CoverBuildRetryIntervalHours;
                        if (_coverBuildRetryAccum < 0f) _coverBuildRetryAccum = 0f;
                        State.Cover = CoverMapBuilder.Build();
                    }
                }
                else
                {
                    _coverRebuildAccum += dt;
                    if (_coverRebuildAccum >= CoverRebuildIntervalHours)
                    {
                        _coverRebuildAccum -= CoverRebuildIntervalHours;
                        var rebuiltCover = CoverMapBuilder.Build();
                        if (rebuiltCover != null) State.Cover = rebuiltCover;
                    }
                }

                // Sync external threats (Godzilla disaster / alien invasion, Task58). Does nothing if the
                // other mods are not installed (ExternalThreatBridge internally throttles and caches its
                // reflection resolution results). Done before the AI advance orders (detour decision) and
                // ThreatCombatStep so the latest positions are used during this tick.
                ExternalThreatBridge.Advance(State, dt);

                // Task94: reflect MissileDisaster (disaster missile) impacts as unit damage
                // (Workshop comment response. Auto-disables internally if not installed / old version).
                DisasterImpactBridge.Advance(State);

                // Task94: external invasion events (Options toggle, Workshop comment request). Spawned
                // squads are marched toward the nearest enemy base as regular AI by the next
                // InvasionOrders.AssignAdvance.
                int invaders = InvasionEvents.Advance(State, dt,
                    WarfrontSettings.InvasionEventsEnabled, WarfrontSettings.InvasionFrequencyIndex);
                if (invaders > 0)
                {
                    _invasionToastPending = true; // Toast displayed on the main thread (OnMainVisualUpdate)
                    ModConfig.Log("InvasionEvents: spawned an invasion wave of " + invaders + " unit(s).");
                }

                // AI advance orders (non-player factions). Task58: if an external threat is near a
                // faction's own territory, it detours toward it in preference to enemy bases (decided
                // inside InvasionOrders.AssignAdvance).
                foreach (var f in State.Factions)
                    if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id, dt);

                // Task63: automatic ballistic-missile launches by AI factions (archenemy priority /
                // long-range Hostile, per-base cooldown). Decided right after the regular AI advance orders
                // (grouped into the same "AI decision-making" phase).
                MissileDoctrine.Advance(State, dt);

                // Cover-movement decision-making (assigns engaged units a position that exploits cover,
                // Task44). Called before MovementStep so that units can start moving toward the position
                // decided this tick within the same tick.
                CoverSeekStep.Advance(State, dt);

                // Task127: infantry take cover against a nearby city building while in contact, instead
                // of fighting on the open road. Between the two so that a fortification still wins:
                // this overrides the plain cover decision, and FortSeekStep below overrides this.
                BuildingGarrisonStep.Advance(State, dt);

                // Task101: infantry position-seeking (head for trenches/bunkers when enemies approach).
                // Runs right after CoverSeekStep and overrides the cover decision when a fortified position
                // is available (fortification > shadow of a building, see the FortSeekStep comment).
                FortSeekStep.Advance(State, dt);

                // Movement (kinematic advance of Moving-state units toward OrderTargetPos; CoverDestination priority is Task44)
                MovementStep.Advance(State, dt);

                // Task99: automatic resupply within base/carrier range (ammo recovery, SupplyStock
                // consumption) and supply-truck dispatch/transfer. "In range or not" is judged right after
                // movement = at this tick's final positions.
                ResupplyStep.Advance(State, dt);
                SupplyTruckStep.Advance(State, dt);
                TransportHeliStep.Advance(State, dt); // Task101: transport helicopter logistics + position tracking of boarded units
                TrainStep.Advance(State, dt);         // Task101: military train operation (loading/boarding/running/unloading)

                // Task98: automatic despawn of units stuck at water edges etc. (judged right after
                // movement = after seeing this tick's actual displacement. Units near their own bases and
                // non-Moving states are excluded; no sound, no explosion, just marked Dead).
                int stuckDespawned = StuckCleanupStep.Advance(State, dt);
                if (stuckDespawned > 0)
                    ModConfig.Log("StuckCleanupStep: despawned " + stuckDespawned + " stuck unit(s).");

                // Task79: suicide-drone target locking and ram detonation. Placed right after MovementStep
                // and before CombatStep (a lock decided this tick being referenced by the next tick's
                // MovementStep dive movement is the usual 1-tick-delay pattern of AI decision steps,
                // symmetric with CoverSeekStep -> MovementStep).
                // Placing it before CombatStep lets the immediately following CombatStep second pass
                // (death determination / KillEvent issuance) pick up, within the same tick, both the
                // suicide drone itself whose CurrentHP KamikazeStep set to 0 and the opposing unit killed
                // by the detonation.
                KamikazeStep.Advance(State, dt);

                // Combat (unit vs unit + base attacks + external threats, Task58) -> occupation ->
                // re-derivation of faction status (Task46: base self-defense fire was removed.
                // Eliminated/HomeBaseId are not touched directly by Occupation; FactionStatus.Refresh
                // re-derives them every tick from owned-base presence = even a faction once Eliminated
                // revives if it retakes a base). ThreatCombatStep runs "in addition to" regular combat and
                // does not compete for target selection (units fire at both simultaneously when in range,
                // see Core/ThreatCombatStep).
                // Task101: automatic fire from fortifications (bunkers / artillery positions). Placed
                // before CombatStep so units destroyed here are picked up in the same tick by the
                // CombatStep second pass (death determination / KillEvent) (same pattern as KamikazeStep).
                FortCombatStep.Advance(State, dt);
                CombatStep.Advance(State, dt);
                BaseCombatStep.Advance(State, dt);
                ThreatCombatStep.Advance(State, dt);

                // Task65: proximity aura damage from threats (Godzilla/aliens). Executed right after
                // ThreatCombatStep (right after unit->threat attacks have been settled). It is
                // reverse-direction damage (threat->unit) that does not consult ThreatRelations, so it has
                // no effect whatsoever on regular combat target selection.
                ThreatAuraStep.Advance(State, dt);

                // Task63: ballistic missile flight progress, interception, and impact resolution. Per the
                // spec, executed right after ThreatCombatStep and before the economy tick (impact damage is
                // reflected in the same tick's Occupation/FactionStatus re-derivation).
                MissileStep.Advance(State, dt);

                Occupation.ResolveCaptures(State);
                FactionStatus.Refresh(State);

                // Expiry management of combat zones (Task54). Decrement after CombatStep/BaseCombatStep
                // above have finished stacking this tick's reports (so that entries reported this same tick
                // don't immediately drop below 0 and vanish).
                State.CombatZones.Advance(dt);

                // Road blocking according to combat zones (Task54). Ideally applied after CombatZones is
                // settled and before civilian path computation, but MovementStep (including path
                // consumption and new computations) has already finished above. It is reflected starting
                // from the next tick's path computations, so a 1-tick delay is acceptable.
                CombatRoadBlocker.Advance(State, dt);

                // Task65: rare fires / building collapses near combat zones (State.CombatZones).
                // DisasterHelpers is sim-thread only, so placed right after CombatRoadBlocker, which also
                // deals with the CS building buffer (running after the road-block determination is fine =
                // the two are independent operations with no dependency on each other).
                CombatCollateral.Advance(State, dt);

                // Economy (low frequency, based on in-game time). Subtract by the interval so no time is
                // lost (zero-clearing would discard the fractional part of dt every time, effectively
                // lowering the frequency).
                _economyAccum += dt;
                if (_economyAccum >= EconomyIntervalHours)
                {
                    _economyAccum -= EconomyIntervalHours;
                    var samples = DevelopmentSampler.Sample(); // Task 12
                    foreach (var b in State.Bases)
                    {
                        if (b.OwnerFactionId == null) continue;
                        // Task101: fortifications and cargo stations generate no income (1km-radius income
                        // comes from the 4 military base types only. Prevents income from doubling over and
                        // over just by lining up trenches).
                        if (FortificationRules.IsFortification(b.Type)) { b.LastIncome = 0f; continue; }
                        // Task99: 3-resource economy. From per-zone development levels within a 1km radius,
                        // residential yields manpower, commercial/office yields funds, and industrial yields
                        // production (previously: all buildings -> funds only).
                        ZonedIncome inc = TerritoryIncome.ZonedForBase(b, samples, IncomeRate);
                        Faction owner = State.FindFaction(b.OwnerFactionId.Value);
                        if (owner != null)
                        {
                            owner.AddTreasury(inc.Funds);
                            owner.AddManpower(inc.Manpower);
                            owner.AddProduction(inc.Production);
                        }
                        b.LastIncome = inc.Funds; // Task35: cache for the UI to display on the base panel (not persisted)
                    }

                    // Task99: automatic supply production (production -> SupplyStock, with funds as a
                    // substitute when short) and automatic supply-truck upkeep (per army base, 30-truck cap
                    // per faction). Economy-tick frequency is sufficient for both.
                    foreach (var f in State.Factions)
                        ResupplyStep.ProduceSupplies(f);
                    SupplyTruckStep.MaintainTrucks(State);
                    TransportHeliStep.MaintainHelis(State); // Task101: automatic upkeep of transport helicopters
                    TrainStep.MaintainTrains(State);        // Task101: automatic upkeep of military trains (per station pair)
                }

                // Cleanup of dead units. The visuals (GameObjects) hold no representation here, so no
                // coupling is needed at this point (UnitVisuals.Sync automatically destroys them on the
                // next OnMainVisualUpdate from the diff against State.Units = declarative reconcile).
                State.Units.RemoveAll(u => u.State == UnitState.Dead);

                LogDiagnostics(dt);
            }
        }

        /// <summary>
        /// Diagnostic log that records the runtime state as a single line every fixed number of ticks
        /// (for investigating issues that only reproduce on the real game). Records as facts whether
        /// units are actually moving, whether they are engaging, and whether base HP is being chipped.
        /// The caller must hold _stateLock.
        /// </summary>
        private static void LogDiagnostics(float dt)
        {
            _diagTicks++;
            if (_diagTicks < DiagIntervalTicks) return;
            _diagTicks = 0;

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("DIAG dt=").Append(dt.ToString("0.000")).Append("h");
                sb.Append(" units=").Append(State.Units.Count);

                // Per-faction unit counts (Task24): makes it obvious at a glance when no faction has any
                // units (a bug). The +1 accounts for the Invader faction (Task95,
                // Faction.InvaderFactionId=5).
                var unitsPerFaction = new int[WarfrontSettings.MaxFactions + 1];
                for (int u = 0; u < State.Units.Count; u++)
                {
                    byte fid = State.Units[u].FactionId;
                    if (fid < unitsPerFaction.Length) unitsPerFaction[fid]++;
                }
                sb.Append(" |");
                for (int f2 = 0; f2 < unitsPerFaction.Length; f2++)
                    sb.Append(" uf").Append(f2).Append("=").Append(unitsPerFaction[f2]);

                sb.Append(" | roads=").Append(State.Roads != null ? State.Roads.NodeCount : 0);
                sb.Append(" cover=").Append(State.Cover != null ? State.Cover.Count : 0);
                // Task58: makes the remaining HP% of currently active external threats (Godzilla/aliens)
                // visible at a glance.
                for (int ti = 0; ti < State.Threats.Count; ti++)
                {
                    var t = State.Threats[ti];
                    float pct = t.MaxHP > 0f ? (t.CurrentHP / t.MaxHP) * 100f : 0f;
                    sb.Append(" threat=").Append(t.Kind).Append(" ").Append(pct.ToString("0")).Append("%");
                }
                for (int i = 0; i < State.Units.Count && i < 2; i++)
                {
                    UnitInstance u = State.Units[i];
                    UnitType ut = State.Types.Get(u.TypeKey);
                    sb.Append(" | u").Append(u.InstanceId)
                      .Append(" type=").Append(u.TypeKey)
                      .Append(" f=").Append(u.FactionId)
                      .Append(" st=").Append(u.State)
                      .Append(" hp=").Append(u.CurrentHP.ToString("0"))
                      .Append(" pos=").Append(u.Position.X.ToString("0")).Append(",").Append(u.Position.Z.ToString("0"))
                      .Append(" tgt=").Append(u.OrderTargetPos.HasValue
                          ? u.OrderTargetPos.Value.X.ToString("0") + "," + u.OrderTargetPos.Value.Z.ToString("0")
                          : "none");
                    // Cover-movement mode (Task45): territory=inside own faction territory, no cover
                    // movement; hold=engaged and staying in cover; bound=advancing, currently bounding from
                    // cover to cover; none=not subject to cover movement / no candidate.
                    string coverMode = CoverSeekStep.IsInFriendlyTerritory(State, u) ? "territory"
                        : u.CoverDestination.HasValue ? (u.CoverHold ? "hold" : "bound")
                        : "none";
                    sb.Append(" cov=").Append(coverMode);
                    // Display Speed (map distance / in-game time) converted back to km/h using the
                    // calibration constant (assumed value) (Task26).
                    if (ut != null)
                        sb.Append(" spd=").Append((ut.Speed * SpeedCalibration.InGameHoursPerRealSecond * 3.6f).ToString("0")).Append("km/h");
                    if (i == 0)
                    {
                        // Record road-path consumption progress for the first sampled unit only (Task23).
                        sb.Append(" path=").Append(u.Path != null ? u.PathIndex + "/" + u.Path.Count : "none");
                    }
                }
                for (int j = 0; j < State.Bases.Count; j++)
                {
                    MilitaryBase b = State.Bases[j];
                    sb.Append(" | base").Append(b.BaseId)
                      .Append(" own=").Append(b.OwnerFactionId.HasValue ? b.OwnerFactionId.Value.ToString() : "-")
                      .Append(" hp=").Append(b.CurrentHP.ToString("0"))
                      .Append(" g=").Append(b.CaptureGraceHours.ToString("0"))
                      .Append(" pos=").Append(b.Position.X.ToString("0")).Append(",").Append(b.Position.Z.ToString("0"));
                }
                for (int k = 0; k < State.Factions.Count; k++)
                {
                    Faction f = State.Factions[k];
                    if (f.Treasury > 0f || f.HomeBaseId.HasValue)
                        sb.Append(" | f").Append(f.Id).Append(" $").Append(f.Treasury.ToString("0"));
                }
                sb.Append(" | visuals=").Append(UnitVisuals.Count);
                ModConfig.Log(sb.ToString());
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LogDiagnostics error: " + e);
            }
        }
    }
}
