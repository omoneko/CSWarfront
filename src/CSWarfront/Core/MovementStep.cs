namespace CSWarfront.Core
{
    /// <summary>Advances units in the Moving state (kinematic, pure logic).
    /// Task37: Y is no longer held constant (old behavior). It is interpolated toward the waypoint/target Y
    /// with the same factor as X/Z, so height follows road grades (bridges, slopes) — fixes units floating
    /// above the road surface.
    /// If a Path (road route) exists its waypoints are consumed in order; once exhausted, movement falls back
    /// to a straight line toward OrderTargetPos.
    /// Task44/Task45: a unit with CoverDestination set ignores Path/OrderTargetPos regardless of its State
    /// (Engaging/Moving) and heads for the cover position instead.
    /// Behavior on entering CoverArrivalDistance branches on UnitInstance.CoverHold:
    ///   - CoverHold==true (engaging, or since Task52 also mode-3 bounding advance): stop there and keep
    ///     firing/hiding from that spot, capped by Task52's MaxCoverHoldHours (see below).
    ///   - CoverHold==false (compatibility fallback; the current CoverSeekStep never produces this, but the
    ///     behavior is kept for callers that set CoverHold=false manually): on arrival, immediately clear
    ///     CoverDestination and resume following Path/OrderTargetPos.
    /// Units without a CoverDestination keep the traditional path/straight-line movement unchanged.
    ///
    /// Task48: added branching on UnitInstance.Order.
    ///   - Hold: continue at the top of the loop; the unit never moves.
    ///   - RallyHold: CoverSeekStep never sets CoverDestination for these units, so the CoverDestination
    ///     branch above is not taken. The unit heads for RallyPoint instead of OrderTargetPos, consuming the
    ///     Path (already computed toward RallyPoint by UnitCommands.ApplyRally) first, then falling back to
    ///     straight-line movement. It stops within CoverArrivalDistance. RallyHold is designed to return fire
    ///     while moving or stopped, so it is evaluated BEFORE the Task50/52 guard below and keeps moving to
    ///     the rally point even while State==Engaging (deliberate "fight your way to your post" behavior —
    ///     do not change).
    ///   - FreeAdvance/AiControlled: move via OrderTargetPos/Path as before (no behavior change).
    ///
    /// Task50: feedback "units should stop while fighting from behind buildings". An Engaging unit without a
    /// CoverDestination (no suitable cover found, or exempt from cover movement inside friendly territory)
    /// does not move at all (no OrderTargetPos/Path progress) unless Order==RallyHold.
    ///
    /// Task52 (fix for "advance toward enemy bases stalls partway"): Task50's "never move anywhere but cover
    /// while engaging" caused permanent freezes in stalemates where no cover exists or the opponent cannot be
    /// killed. Two independent timers guarantee the advance always resumes:
    ///   - MaxCoverHoldHours: cap on how long a unit may sit still at its CoverDestination (measured by
    ///     UnitInstance.CoverHoldTimer, managed by AdvanceTowardCover). When exceeded, the CoverDestination
    ///     is released.
    ///   - CoverSeekStep.MaxEngageHoldHours: cap on how long a unit may keep fighting the same opponent
    ///     (measured by UnitInstance.EngageHoldTimer, managed by CoverSeekStep). When exceeded, movement
    ///     toward OrderTargetPos/Path is allowed even while Engaging, with or without a CoverDestination
    ///     (CombatStep keeps firing while moving if the target stays in range).
    /// Either condition alone re-enables movement while Engaging (see the body of Advance).
    ///
    /// Task53: fix for "units occasionally sink into the ground". Previously Y was only interpolated toward
    /// the waypoint/target Y (Task37 above), which could dip below the *actual* surface (road embankments,
    /// terrain modified after construction, bridges) between waypoints and during off-road straight-line,
    /// cover and rally movement. If state.Height (IHeightSampler, implemented by the Game layer via
    /// TerrainManager.SampleDetailHeight) is supplied and TrySampleHeight succeeds, Y is overwritten with the
    /// sampled surface right after computing X/Z (shared by waypoint, straight-line, cover and rally moves).
    /// If state.Height == null or TrySampleHeight fails, the traditional Y interpolation is kept (safe
    /// fallback, keeps existing test assumptions). Hardening (Task53 addendum): never adopt a failure value
    /// such as 0f into Y — on maps whose surface is far from 0 (measured ~270) that teleports the unit far
    /// below ground for one tick — hence the Try pattern with an explicit failure branch (see ResolvePosition).
    ///
    /// Task55: fix for "units start dogfighting on the ground" (fighting while floating above terrain). The
    /// Game-layer SurfaceHeightSampler called the wrong TerrainManager.SampleDetailHeight overload
    /// ((float,float) — world coordinates passed raw as detail-grid coordinates, missing the 1/64 scale), so
    /// Task53's TrySampleHeight always "succeeded" while returning nonsense heights (Game-side fix in
    /// SurfaceHeightSampler.cs). As defense in depth, ResolvePosition clamps by MaxSurfaceDeviation: even
    /// when TrySampleHeight returns true, a value that deviates too far from the interpolated Y is rejected
    /// in favor of the interpolated Y. If an IHeightSampler implementation ever misuses coordinates/APIs
    /// again, the core mechanically prevents "units launched into the sky"-scale damage (see ResolvePosition).</summary>
    public static partial class MovementStep
    {
        /// <summary>Distance at which CoverDestination counts as reached (Task44); the unit stops closer than
        /// this. Task48: reused as the arrival threshold for RallyPoint as well.</summary>
        public const float CoverArrivalDistance = 3f;

        /// <summary>Maximum time a unit may stay stationary with CoverHold==true (in-game hours, Task52).
        /// Once UnitInstance.CoverHoldTimer exceeds this, the CoverDestination is released so the unit can
        /// resume normal path/straight-line movement from the next tick (even while Engaging, subject to
        /// CoverSeekStep.MaxEngageHoldHours). This is the guardrail for the user requirement "units hold
        /// still while in cover, but never hide forever".</summary>
        public const float MaxCoverHoldHours = 1.0f;

        /// <summary>Task55 (defense in depth for the "units start dogfighting" bug): a sampled Y from
        /// state.Height (IHeightSampler, Game-layer implementation) is rejected when it deviates from the
        /// interpolated Y (the unit's own local estimate of the surface) by more than this. Even if the
        /// IHeightSampler implementation regresses to a wrong API/overload/coordinate system, this clamp
        /// mechanically prevents launching units into the sky, while still letting through the small
        /// cm-to-m surface corrections Task53 intended (embankments etc.).</summary>
        public const float MaxSurfaceDeviation = 15f;

        /// <summary>Task61: cruise height of air units above the terrain (map units). When AdvanceAir can
        /// sample the surface via state.Height (IHeightSampler), Y is always set to groundHeight + this
        /// (a vertical model entirely separate from the land logic that follows road heights).</summary>
        public const float CruiseAltitude = 120f;

        /// <summary>Task101: cruise altitude for helicopters (TargetingRules.IsHelicopter) — lower than fixed-wing.</summary>
        public const float HeliCruiseAltitude = 60f;

        /// <summary>Task79: speed multiplier applied while a suicide drone (UnitCategoryFlags.IsKamikaze) has
        /// locked a target and dives at it; the effective dive speed is type.Speed (normal cruise) times this.
        /// A documented balance constant to make the ramming attack read as "accelerating into the target".
        /// Kept in MovementStep rather than KamikazeStep because the dive movement itself is this class's
        /// responsibility.</summary>
        public const float DiveSpeedMultiplier = 1.5f;

        /// <summary>Task83 (user request "overall movement 1.25x faster"): global multiplier on all unit
        /// movement, applied only in the stepLen computation (this class's Advance — the single consumption
        /// point of movement). UnitType.Speed itself (km/h-calibrated, SpeedCalibration) is unchanged, so the
        /// relative speeds between unit classes and the km/h display stay meaningful while the overall tempo
        /// rises. UnitInfoPanel shows the effective speed with this multiplier applied.</summary>
        public const float GlobalSpeedMultiplier = 1.25f;

        // --- Task77: fixes for "ground units won't cross bridges" and "ground units can walk into the sea" ---
        //
        // [Bridges] Inspecting RoadGraphBuilder showed that all Road-service segments, bridges included, are
        // accepted into the road graph (only ItemClass.Service.Road is checked; no bridge-specific exclusion),
        // so the graph was not missing bridges. The real cause was the core's Y resolution: Task53 applied
        // "overwrite Y with TrySampleHeight right after computing X/Z" uniformly to both waypoint-following
        // (ConsumePath) and straight-line movement (AdvanceStraight). Bridges do not flatten the terrain to
        // deck height (the deck floats above the terrain), so under a bridge the Game layer's
        // SurfaceHeightSampler (TerrainManager.SampleDetailHeight) returns the water/riverbed height; adopting
        // it for a unit on the bridge made it "sink into the water while crossing" — which the user perceived
        // as "won't cross the bridge". (The 15f MaxSurfaceDeviation clamp does not always guard this: the
        // deviation can stay within 15f depending on the bridge's height.)
        //
        // Fix: split the source of Y by movement context.
        //   - On-path (Path/ConsumePath, following road-network waypoints): never touch the terrain sampler;
        //     adopt the road node's own Y (taken verbatim from NetNode.m_position.y by RoadGraphBuilder — the
        //     deck height for bridges). This is the Task37 behavior, reinstated for on-path movement only.
        //   - Off-road (straight-line fallback after the path is exhausted, cover movement AdvanceTowardCover,
        //     the straight part of rally movement AdvanceTowardRally, and the no-path case — all via
        //     AdvanceStraight/MoveToward): snap to the "visible" surface via state.Height (IHeightSampler)
        //     as before (Task53/55 defense in depth kept intact).
        //
        // [Sea] Off-road movement of Land units had no water check at all — unlike Sea/Air's AdvanceSea —
        // which was the direct cause of "ground units can walk into the sea" (state.Water (IWaterSampler) had
        // been wired up to the Game layer since Task61 but was never consulted by the Land branch's
        // ConsumePath/AdvanceStraight).
        // Fix: only in off-road movement (AdvanceStraight/MoveToward), if the landing point of the step is
        // water.IsWater, cancel the entire tick's movement (Position not updated — the unit halts at the
        // shoreline). Symmetric with AdvanceSea (which discards a step onto land, Task61). No timeout
        // transition to Idle: u.State/OrderTargetPos are untouched, so if a route appears in later ticks
        // (destination changed, road built, etc.) movement resumes naturally — simplicity and determinism
        // preferred. The check is deliberately NOT applied on-path (ConsumePath): directly under a bridge
        // HasWater is "water", but a unit following the road network must remain able to pass (extending the
        // water check to on-path movement would regress bridge crossing).
        //
        // CoverSeekStep has the matching fix (a land unit's candidate cover position in water is rejected).
        // See CoverSeekStep.cs.

        public static void Advance(WarState state, float dt)
        {
            IHeightSampler height = state.Height; // Task53: grab once into a null-safe local.
            IWaterSampler water = state.Water; // Task61: likewise.

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.IsCarried) continue; // Task101: while carried, the carrier (helicopter/train) manages position
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                // Task147/148: credit for holding a position (FortDefenseBonus.IsDugIn). Measured here
                // because this is the step that knows whether a unit is standing still by intent or by
                // circumstance - a unit halted at a river bank is State==Moving and earns nothing.
                //
                // Task148: the AI never issues a Hold order - Hold is a player command and nothing else
                // sets it - so crediting only Hold would have handed the player a defensive bonus its
                // opponents could never earn. The AI's equivalent posture is a unit that has been stood
                // down with no objective: Idle with no OrderTargetPos, which is what AssignAdvance
                // leaves behind once a faction has withdrawn and has nowhere to attack. Both count.
                bool standingDown = u.Order == UnitOrder.Hold
                    || (u.State == UnitState.Idle && !u.OrderTargetPos.HasValue);
                if (standingDown && type.Domain == Domain.Land) u.DugInHours += dt;
                else u.DugInHours = 0f;

                if (u.Order == UnitOrder.Hold) continue; // Task48: hold order = never move.

                float stepLen = type.Speed * GlobalSpeedMultiplier * dt;
                if (stepLen <= 0f) continue;

                // Task125: how this unit copes with terrain off the road network. Only consumed by the
                // land straight-line helpers below (sea/air/rail have their own movement models).
                MobilityClass mobility = TerrainMobility.ClassOf(type.Category);

                // Task132: assume the way is clear; the water checks below set this again if it is not.
                u.WaterBlocked = false;

                // Task101: military trains move on rails only (follow the Path waypoints laid by TrainStep
                // verbatim; no straight-line fallback and no water/cover/road checks — rails are always
                // considered passable. Without a Path the train does not move).
                if (type.Category == UnitCategory.MilitaryTrain)
                {
                    AdvanceTrain(u, stepLen);
                    continue;
                }

                // Task61: Sea/Air never use the land road paths (Path), cover (CoverDestination) or
                // territory-based slowdown — an entirely separate movement model (see the class comment).
                // CoverSeekStep never assigns CoverDestination to non-Land units, so u.CoverDestination is
                // normally unset here, but defensively they must never fall through into the land logic below.
                if (type.Domain != Domain.Land)
                {
                    // Task79: if a suicide drone has a locked target (KamikazeStep.Advance wrote
                    // TargetId/TargetThreatId last tick), use the dedicated dive movement straight at the
                    // target instead of normal cruise (AdvanceAir). With no lock (target destroyed/lost),
                    // fall through to ResolveDomainObjective below and behave like a regular air unit:
                    // climb back to cruise altitude and head for OrderTargetPos (advance/rally orders).
                    if (type.Category.IsKamikaze())
                    {
                        WorldPos? diveTarget = ResolveKamikazeTarget(state, u);
                        if (diveTarget.HasValue)
                        {
                            AdvanceKamikaze(u, stepLen, diveTarget.Value);
                            continue;
                        }
                    }

                    // Task86: air-unit engagement pass movement (racetrack passes, MovementStepAirPass.cs).
                    // While an engagement anchor exists it takes priority over objective movement (the pass
                    // flies even when the objective is reached, i.e. objective == null, so this is checked
                    // before ResolveDomainObjective).
                    if (type.Domain == Domain.Air && AdvanceAirPass(state, u, type, stepLen, height))
                        continue;

                    WorldPos? objective = ResolveDomainObjective(u);
                    // Task87: idle air/sea units with no orders return to the nearest home
                    // (air: own air base/carrier, sea: own navy base. MovementStepReturnHome.cs).
                    bool returningHome = false;
                    if (!objective.HasValue)
                    {
                        objective = ResolveHomeObjective(state, u, type);
                        returningHome = objective.HasValue;
                    }
                    if (!objective.HasValue)
                    {
                        // Task107 (user report "aircraft that lost their target hover forever"): no mission
                        // and no home to fly to = already inside the home area (air base/carrier), or a
                        // transport helicopter waiting at its home base. Land instead of hovering
                        // (MovementStepReturnHome.cs).
                        if (type.Domain == Domain.Air) AdvanceAirLanding(state, u, stepLen, height);
                        continue;
                    }

                    if (type.Domain == Domain.Air)
                    {
                        float cruise = TargetingRules.IsHelicopter(type.Category)
                            ? HeliCruiseAltitude : CruiseAltitude; // Task101
                        // Task107: while returning home, descend from cruise altitude based on the remaining
                        // distance (landing approach).
                        float altitude = returningHome
                            ? ApproachAltitude(u.Position.HorizontalDistanceTo(objective.Value), cruise)
                            : cruise;
                        AdvanceAir(u, stepLen, objective.Value, height, altitude);
                    }
                    else // Domain.Sea
                    {
                        AdvanceSea(u, stepLen, objective.Value, water, dt);
                    }
                    continue;
                }

                if (u.CoverDestination.HasValue)
                {
                    AdvanceTowardCover(u, stepLen, dt, height, water, mobility);
                    continue;
                }

                if (u.Order == UnitOrder.RallyHold)
                {
                    if (u.RallyPoint.HasValue) AdvanceTowardRally(u, stepLen, height, water, mobility);
                    continue;
                }

                // Task50/52: an engaging unit without a cover position holds still and returns fire as a rule
                // (RallyHold handled above). But if either MaxCoverHoldHours (just released from a held cover)
                // or CoverSeekStep.MaxEngageHoldHours (the fight against the same opponent has dragged on too
                // long) is satisfied, movement resumes even while Engaging (Task52).
                if (u.State == UnitState.Engaging)
                {
                    bool releasedFromCoverHold = u.CoverHoldTimer > MaxCoverHoldHours;
                    bool engageHoldExpired = u.EngageHoldTimer >= CoverSeekStep.MaxEngageHoldHours;
                    if (!releasedFromCoverHold && !engageHoldExpired) continue;
                }
                else if (u.State != UnitState.Moving)
                {
                    continue;
                }

                if (!u.OrderTargetPos.HasValue) continue;

                stepLen = ConsumePath(u, stepLen);
                if (stepLen > 0f)
                    AdvanceStraight(u, u.OrderTargetPos.Value, stepLen, height, water, mobility);
            }
        }

        /// <summary>Task48: kinematic movement toward the RallyPoint. First consumes the road Path (computed
        /// toward the RallyPoint by UnitCommands.ApplyRally) if present, then falls back to straight-line
        /// movement (ConsumePath/AdvanceStraight are the exact same helpers used for OrderTargetPos movement).
        /// Once within CoverArrivalDistance, does nothing further (stays put).</summary>
        private static void AdvanceTowardRally(UnitInstance u, float stepLen, IHeightSampler height, IWaterSampler water,
            MobilityClass mobility)
        {
            WorldPos rally = u.RallyPoint.Value;
            float dist = u.Position.HorizontalDistanceTo(rally);
            if (dist <= CoverArrivalDistance) return;

            stepLen = ConsumePath(u, stepLen);
            if (stepLen > 0f)
                AdvanceStraight(u, rally, stepLen, height, water, mobility);
        }

        /// <summary>Kinematic movement toward the CoverDestination. On entering CoverArrivalDistance the
        /// behavior branches on CoverHold: "keep holding still there" (true, capped by MaxCoverHoldHours since
        /// Task52) or "clear the CoverDestination and hand over to normal path/straight movement or the next
        /// cover evaluation from the next tick" (false, the compatibility fallback path). At any other
        /// distance it advances with the same interpolation as AdvanceStraight (keeping CoverHoldTimer at 0
        /// while travelling, so only time actually spent stationary is measured).</summary>
        private static void AdvanceTowardCover(UnitInstance u, float stepLen, float dt, IHeightSampler height, IWaterSampler water,
            MobilityClass mobility)
        {
            WorldPos coverPos = u.CoverDestination.Value;
            float distBefore = u.Position.HorizontalDistanceTo(coverPos);

            if (distBefore <= CoverArrivalDistance)
            {
                if (!u.CoverHold)
                {
                    // Advancing cover-to-cover (not holding): rather than keep the unit stopped here, reset
                    // the cooldown so the next CoverSeekStep evaluation runs promptly, and let go.
                    u.CoverDestination = null;
                    u.CoverReevaluateCooldown = 0f;
                    u.CoverHoldTimer = 0f;
                    return;
                }

                // Task52: measure the time spent holding still and force-release past MaxCoverHoldHours
                // (the unit resumes advancing — even if it is still engaging). On the very tick of arrival
                // the timer is still 0, so counting starts from the next tick (so a huge dt that covers the
                // whole approach cannot cap out instantly).
                u.CoverHoldTimer += dt;
                if (u.CoverHoldTimer > MaxCoverHoldHours)
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    // CoverHoldTimer is deliberately NOT reset: as long as the same engagement continues
                    // (CoverTargetId unchanged), MovementStep's releasedFromCoverHold check stays true and
                    // the unit is never pinned again against this target (a new engagement / new cover
                    // decision resets it to 0 in CoverSeekStep).
                }
                return;
            }

            // Not yet arrived = the hold has not started, so the timer stays 0.
            u.CoverHoldTimer = 0f;
            AdvanceStraight(u, coverPos, stepLen, height, water, mobility);
        }

        /// <summary>Consumes waypoints in order while the Path lasts. Returns the leftover stepLen (for the
        /// straight-line fallback).
        /// Task77: this is on-path movement (between road-network nodes) and touches neither the terrain
        /// sampler nor the water check (see MoveTowardOnPath / the waypoint snap below). Road-node Y values,
        /// bridges included, come from the road network itself via RoadGraphBuilder and are trustworthy;
        /// overwriting them with the terrain sampler sinks units to the water/terrain under bridges (see the
        /// Task77 class comment). The water check is omitted for the same reason — under a bridge HasWater
        /// says "water", but a unit following the road network must be able to pass.</summary>
        private static float ConsumePath(UnitInstance u, float stepLen)
        {
            if (u.Path == null) return stepLen;

            while (stepLen > 0f && u.PathIndex < u.Path.Count)
            {
                WorldPos waypoint = u.Path[u.PathIndex];
                float dist = u.Position.HorizontalDistanceTo(waypoint);

                if (dist <= stepLen || dist <= 0.01f)
                {
                    // Task37/Task77: on reaching a waypoint, adopt that waypoint's Y verbatim (the road
                    // node's own Y — deck height for bridges). Task53–76 also overwrote this with the terrain
                    // sampler, which was the true cause of the "won't cross bridges" bug, so on-path movement
                    // never consults the terrain sampler again (see the Task77 class comment).
                    u.Position = new WorldPos(waypoint.X, waypoint.Y, waypoint.Z);
                    stepLen -= dist;
                    u.PathIndex++;
                }
                else
                {
                    MoveTowardOnPath(u, waypoint, stepLen);
                    return 0f;
                }
            }

            return stepLen;
        }

        /// <summary>Task77: partial movement between waypoints (on-path). Only interpolates toward the
        /// waypoint's own Y with the same factor as X/Z; touches neither the terrain sampler nor the water
        /// check (same reasoning as ConsumePath's arrival branch — the waypoint Y itself expresses the bridge
        /// grade, which is both sufficient and exact).</summary>
        private static void MoveTowardOnPath(UnitInstance u, WorldPos target, float stepLen)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }

        /// <summary>Task77: off-road movement (the straight-line fallback after the path is exhausted, cover
        /// movement, the straight part of rally movement). Unlike ConsumePath, this applies both the terrain
        /// sampler (snap to the visible surface, Task53/55) and the water check (a land unit's step into
        /// water cancels the whole tick, Task77). Moving off the road network, it must track surface changes
        /// while staying out of the water.
        /// Task77 hardening: if dist &lt;= 0.01f (already at the target when called) do nothing. This absorbs
        /// the case where ConsumePath consumed the whole Path whose final waypoint equals OrderTargetPos
        /// (the road reaches the destination) and a leftover stepLen still lands here: ConsumePath has
        /// already snapped exactly to the road node's own Y (deck height on bridges), and running the
        /// terrain-sampled "arrival" handling on top at dist==0 would re-introduce the surface snap the
        /// on-path logic just avoided (recurring at bridge-end destinations). The principle made explicit
        /// here: surface/water checks apply only when actually moving a positive distance.</summary>
        /// <summary>Task125: off-road terrain handling for land units. Straight-line movement here is by
        /// definition off the road network (on-path movement is consumed by ConsumePath first), so this
        /// is where the class's off-road penalty, the slope penalty and the maximum traversable slope
        /// apply. A step onto ground too steep for the class is refused and deflected sideways; if no
        /// deflection works the unit waits this tick. Returns the (possibly reduced) step length and the
        /// direction to actually move in.
        ///
        /// Deliberately not applied to on-path movement: road-network geometry carries bridges and
        /// embankments whose height must not be re-derived from the terrain sampler (Task77).</summary>
        private static readonly float[] DeflectionDegrees = { 0f, 35f, -35f, 70f, -70f };

        /// <summary>Task129: whether the straight step from a unit's position to (nx, nz) touches water
        /// at its landing point or anywhere along the way. Sampled every WaterSampleSpacing metres (and
        /// always at both ends), which is finer than any shoreline a land unit can cross in one tick.
        /// A null sampler means "no water data" and never blocks movement.</summary>
        private const float WaterSampleSpacing = 8f;

        private static bool CrossesWater(WorldPos from, float nx, float nz, IWaterSampler water)
        {
            if (water == null) return false;
            if (water.IsWater(nx, nz)) return true;

            float dx = nx - from.X, dz = nz - from.Z;
            float dist = (float)System.Math.Sqrt(dx * dx + dz * dz);
            int steps = (int)(dist / WaterSampleSpacing);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)(steps + 1);
                if (water.IsWater(from.X + dx * t, from.Z + dz * t)) return true;
            }
            return false;
        }

        private static bool TryTerrainStep(UnitInstance u, WorldPos target, MobilityClass mobility,
            IHeightSampler height, float stepLen, out WorldPos aim, out float allowedStepLen,
            IWaterSampler water)
        {
            aim = target;
            allowedStepLen = stepLen;

            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return true;

            float dirX = (target.X - u.Position.X) / dist;
            float dirZ = (target.Z - u.Position.Z) / dist;

            float fromHeight;
            if (height == null || !height.TrySampleHeight(u.Position.X, u.Position.Z, out fromHeight))
                return true; // no terrain data: behave exactly as before (never block on missing input)

            float probe = TerrainMobility.SlopeSampleDistance;
            for (int i = 0; i < DeflectionDegrees.Length; i++)
            {
                double rad = DeflectionDegrees[i] * System.Math.PI / 180.0;
                float cos = (float)System.Math.Cos(rad), sin = (float)System.Math.Sin(rad);
                float dx = dirX * cos - dirZ * sin;
                float dz = dirX * sin + dirZ * cos;

                float toHeight;
                if (!height.TrySampleHeight(u.Position.X + dx * probe, u.Position.Z + dz * probe, out toHeight))
                    return true; // sampling failed mid-way: fall back to unrestricted movement

                float slope = TerrainMobility.SlopeDegrees(fromHeight, toHeight, probe);
                if (!TerrainMobility.CanTraverse(mobility, slope)) continue;
                // Task129: never deflect a land unit into the water to dodge a slope.
                if (water != null && water.IsWater(u.Position.X + dx * probe, u.Position.Z + dz * probe)) continue;

                allowedStepLen = stepLen * TerrainMobility.SpeedFactor(mobility, slope, false);
                // Straight ahead: keep the real target so arrival still snaps exactly onto it.
                if (i == 0) return true;
                aim = new WorldPos(u.Position.X + dx * dist, target.Y, u.Position.Z + dz * dist);
                return true;
            }

            allowedStepLen = 0f; // hemmed in by ground too steep for this class
            return false;
        }

        private static void AdvanceStraight(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height,
            IWaterSampler water, MobilityClass mobility)
        {
            WorldPos aim;
            float terrainStepLen;
            if (!TryTerrainStep(u, target, mobility, height, stepLen, out aim, out terrainStepLen, water)) return;
            target = aim;
            stepLen = terrainStepLen;
            if (stepLen <= 0f) return;

            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return; // Already there (usually ConsumePath's on-path snap): do nothing.

            if (dist <= stepLen)
            {
                // Task77: if the landing point itself is water, do not move at all this tick (halt at the shoreline).
                if (water != null && water.IsWater(target.X, target.Z)) { u.WaterBlocked = true; return; }
                // Arrival: adopt the target's Y verbatim (Task37; previously u.Position.Y was kept).
                // Task53: snap to the actual surface Y when state.Height is supplied.
                u.Position = ResolvePosition(target.X, target.Y, target.Z, height);
            }
            else
            {
                MoveToward(u, target, stepLen, height, water);
            }
        }

        /// <summary>Interpolates Y toward the target with the same factor as X/Z (t = stepLen/dist)
        /// (Task37, off-road only). The old behavior kept u.Position.Y constant, ignoring road grades
        /// (bridges/slopes) and making units appear to float above the surface. The X/Z interpolation itself
        /// is unchanged (no overshoot — guaranteed by the existing Advance_stops_at_target_without_overshoot
        /// test).
        /// Task53: when a height sampler is supplied, the interpolated Y itself is discarded in favor of the
        /// surface sampled at the computed X/Z (ResolvePosition branches).
        /// Task77: if the next step (nx,nz) is water, do not move at all this tick (a land unit's step into
        /// the sea is discarded wholesale = halted at the shoreline; symmetric with AdvanceSea's land-side
        /// design).</summary>
        private static void MoveToward(UnitInstance u, WorldPos target, float stepLen, IHeightSampler height, IWaterSampler water)
        {
            float dist = u.Position.HorizontalDistanceTo(target);
            if (dist <= 0.01f) return;
            float t = stepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            // Task129 (bug report "vehicles drive themselves into the water"): testing only the landing
            // point lets a fast unit step straight over a shoreline strip and end up in the middle of
            // the water, because nothing in between was ever sampled. Sample along the step instead —
            // any wet point on the way halts the unit at the bank, as intended.
            if (CrossesWater(u.Position, nx, nz, water)) { u.WaterBlocked = true; return; } // Task132
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            u.Position = ResolvePosition(nx, ny, nz, height);
        }

        /// <summary>Task53: assembles the WorldPos actually adopted from the computed X/Y/Z. If a height
        /// sampler is supplied and TrySampleHeight succeeds, the passed y (the traditional waypoint /
        /// interpolated Y) is replaced with the sampled value (the real post-construction surface). If height
        /// is null or TrySampleHeight fails, the passed y is used as-is (traditional interpolation/snap
        /// behavior — the safe fallback that keeps existing test assumptions).
        /// Hardening: on sampling failure, the failure value (the undefined out argument) is never adopted
        /// into Y (prevents the regression where 0f was adopted during a TerrainManager hiccup and the unit
        /// teleported far below the surface).
        /// Task55 (extra layer of the defense): even when TrySampleHeight returns true, a value deviating
        /// from the passed y (the interpolated Y — the unit's own local surface estimate) by more than
        /// MaxSurfaceDeviation is not trusted; the interpolated y is used instead. If the IHeightSampler
        /// implementation ever returns nonsense again via a wrong coordinate system/API ("units start
        /// dogfighting"-scale bug), the core stops the damage mechanically at this single point. The small
        /// surface corrections Task53 intended (embankments, bridges) deviate little and pass through.</summary>
        private static WorldPos ResolvePosition(float x, float y, float z, IHeightSampler height)
        {
            float sampled;
            if (height != null && height.TrySampleHeight(x, z, out sampled))
            {
                float deviation = sampled - y;
                if (deviation < 0f) deviation = -deviation;
                if (deviation <= MaxSurfaceDeviation) y = sampled;
            }
            return new WorldPos(x, y, z);
        }

        // --- Task61: shared Sea/Air objective resolution ---

        /// <summary>Returns the objective a Sea/Air unit should head for (null = do not move this tick).
        /// During RallyHold, head for the RallyPoint (as on land; no CoverArrivalDistance stop check is
        /// needed — AdvanceAir/AdvanceSea below snap exactly onto the objective when dist &lt;= stepLen, so
        /// from the next tick dist=0 is a no-op and the unit stops naturally). Otherwise head for
        /// OrderTargetPos only while State==Moving/Engaging (unlike land, CoverSeekStep never assigns
        /// CoverDestination to Sea/Air, so there is no "stop and fight from cover" play — the MVP
        /// simplification is to keep moving toward the objective and fight whatever comes in range).
        /// Idle / no objective returns null and the unit stays still this tick.</summary>
        private static WorldPos? ResolveDomainObjective(UnitInstance u)
        {
            if (u.Order == UnitOrder.RallyHold) return u.RallyPoint;
            if (u.State != UnitState.Moving && u.State != UnitState.Engaging) return null;
            return u.OrderTargetPos;
        }

        /// <summary>Task61: air-unit movement. Moves straight toward the objective without RoadGraph/CoverMap;
        /// Y is always "sampled ground height (at the post-move X/Z) + CruiseAltitude" when sampling
        /// succeeds, otherwise the previous Y is kept (the same safe fallback as the IHeightSampler pattern
        /// in the class comment). The altitude is re-derived relative to the surface every tick, so flying
        /// over mountains naturally moves Y up and down.</summary>
        private static void AdvanceAir(UnitInstance u, float stepLen, WorldPos objective, IHeightSampler height,
            float altitude = CruiseAltitude)
        {
            float dist = u.Position.HorizontalDistanceTo(objective);
            float nx, nz;
            if (dist <= stepLen || dist <= 0.01f) { nx = objective.X; nz = objective.Z; }
            else
            {
                float t = stepLen / dist;
                nx = u.Position.X + (objective.X - u.Position.X) * t;
                nz = u.Position.Z + (objective.Z - u.Position.Z) * t;
            }

            float ny = u.Position.Y; // On sampling failure keep the previous Y (fallback).
            float groundY;
            if (height != null && height.TrySampleHeight(nx, nz, out groundY))
            {
                float targetY = groundY + altitude;
                // Task107: only the climb (takeoff from a parked state) is limited to stepLen per tick so
                // aircraft do not teleport from the ground to cruise altitude. Descent still matches the
                // target altitude immediately (the established cruise-holding behavior is unchanged).
                if (targetY - ny > stepLen) targetY = ny + stepLen;
                ny = targetY;
            }

            u.Position = new WorldPos(nx, ny, nz);
        }

        /// <summary>Task101: rail-only movement for military trains. Simply follows the Path (the rail
        /// waypoint list laid via the RailGraph by TrainStep) — no water/cover/terrain checks; Y comes from
        /// the waypoint values, i.e. the rail height. No Path / exhausted Path = no movement.</summary>
        private static void AdvanceTrain(UnitInstance u, float stepLen)
        {
            if (u.Path == null || u.PathIndex >= u.Path.Count) return;

            while (stepLen > 0f && u.Path != null && u.PathIndex < u.Path.Count)
            {
                WorldPos wp = u.Path[u.PathIndex];
                float dist = u.Position.HorizontalDistanceTo(wp);
                if (dist <= stepLen || dist <= 0.01f)
                {
                    u.Position = wp;
                    stepLen -= dist;
                    u.PathIndex++;
                    continue;
                }
                float t = stepLen / dist;
                u.Position = new WorldPos(
                    u.Position.X + (wp.X - u.Position.X) * t,
                    u.Position.Y + (wp.Y - u.Position.Y) * t,
                    u.Position.Z + (wp.Z - u.Position.Z) * t);
                stepLen = 0f;
            }

            if (u.Path != null && u.PathIndex >= u.Path.Count) u.ClearPath(); // Route done: TrainStep decides what is next
        }

        // Task79: ResolveKamikazeTarget/AdvanceKamikaze were split into MovementStepKamikaze.cs
        // (to stay within the 500-line file limit; the base-lock feature pushed this file over, which was
        // the direct trigger for the split — the same partial-class pattern as MovementStepSea.cs).
    }
}
