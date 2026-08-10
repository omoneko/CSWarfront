using System.Collections.Generic;

namespace CSWarfront.Core
{
    public enum UnitState { Idle, Moving, Engaging, Dead }

    /// <summary>Command the player gives to an individual unit / box selection (Task48; runtime-only, not persisted).
    ///   AiControlled - default. The AI (InvasionOrders) assigns target bases; the traditional behavior.
    ///   FreeAdvance  - free advance. Move at full speed to the nearest enemy base and engage normally. The
    ///                  AI may update the target base, but the mode persists until the player issues another order.
    ///   Hold         - halt. Never move from this spot (movement fields are cleared by UnitCommands on
    ///                  assignment) but keep returning fire at enemies in range (passive defense).
    ///   RallyHold    - rally and wait. Move to RallyPoint and stop on arrival; whether moving or stopped,
    ///                  only return fire at enemies in range (no pursuit, no cover movement, no base advance).
    /// </summary>
    public enum UnitOrder { AiControlled, FreeAdvance, Hold, RallyHold }

    /// <summary>One live unit. Its presentation (vehicle id) is held separately by the Game layer; only
    /// logical state lives here.</summary>
    public class UnitInstance
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public float CurrentHP;
        public WorldPos Position;
        public UnitState State;
        public uint? TargetId;

        /// <summary>ExternalThreat.Id of the external threat (Godzilla/alien) locked by KamikazeStep
        /// (runtime-only, not persisted; Task79). TargetId is for UnitInstances; this is for ExternalThreats
        /// — one field per target kind, because the two id spaces are unrelated and mixing them in a single
        /// uint? field would invite collisions and misreads. Always null and ignored for categories other
        /// than suicide drones. Read both by MovementStep (dive destination resolution) and KamikazeStep
        /// (detonation check).</summary>
        public uint? TargetThreatId;

        /// <summary>MilitaryBase.BaseId of the hostile base locked by KamikazeStep (runtime-only, not
        /// persisted; Task79). Same "one field per target kind" policy as TargetId/TargetThreatId. Set only
        /// when neither a unit target nor an external threat was found (priority: unit → external threat →
        /// base). Always null and ignored for categories other than suicide drones. Read both by
        /// MovementStep (dive destination resolution) and KamikazeStep (detonation check).</summary>
        public ushort? TargetBaseId;

        public WorldPos? OrderTargetPos;

        /// <summary>Task86: egress point of an air unit's engagement pass (racetrack) movement
        /// (runtime-only, not persisted). Set/consumed by MovementStepAirPass.AdvanceAirPass. While set, the
        /// unit flies all the way to this point regardless of whether an engagement anchor exists (prevents
        /// dithering at the boundary). Always null and ignored for non-air categories.</summary>
        public WorldPos? AirPassEgress;

        /// <summary>The player's command (Task48). Defaults to AiControlled.
        /// Task92: persisted since v8 together with RallyPoint (fixes "orders revert to AI control on load").</summary>
        public UnitOrder Order;

        /// <summary>Destination while Order==RallyHold (runtime-only, not persisted; Task48). Set by
        /// UnitCommands.ApplyRally.</summary>
        public WorldPos? RallyPoint;

        /// <summary>Remaining road path (runtime-only, not persisted). Null/empty falls back to
        /// straight-line movement.</summary>
        public List<WorldPos> Path;
        /// <summary>Index of the next Path element to head for.</summary>
        public int PathIndex;
        /// <summary>Final destination this path leads to. Recorded so the path can be discarded when
        /// OrderTargetPos changes.</summary>
        public WorldPos? PathTarget;

        /// <summary>Time remaining until the next pathfinding (A*) attempt (in-game hours; runtime-only, not
        /// persisted). Set on FindPath failure so a persistently failing unit does not consume the budget
        /// every tick (Task23 review).</summary>
        public float PathRetryCooldown;

        /// <summary>Task99: ammo gauge (0..1, persisted in v9). Drains only on ticks the unit fires
        /// (AmmoRules); restored by in-base auto-resupply (ResupplyStep) and supply trucks (SupplyTruckStep).
        /// At 0 the unit stops firing (moving and capturing still work). Full when freshly produced.</summary>
        public float Ammo = 1f;

        /// <summary>Task99: cargo load of a supply truck (UnitCategory.SupplyTruck) (0..1, persisted in v9).
        /// Loaded from the faction's SupplyStock at a base; drained by transferring ammo to friendly land
        /// units at the front (SupplyTruckStep). Always 0 and unused for non-truck units.</summary>
        public float SupplyLoad;

        /// <summary>Task101: InstanceId of the unit carrying this one (transport helicopter / military
        /// train). Non-null = being carried (persisted in v10). While carried the unit is excluded from
        /// every step (movement/combat/supply/stuck/AI advance) and cannot be targeted
        /// (TargetSearch/UnitSpatialGrid drop it from candidates). Its position is synchronized every tick
        /// by the carrier, and the carrier's death takes it along (silent removal) —
        /// TransportHeliStep/TrainStep.</summary>
        public uint? CarriedByUnitId;

        /// <summary>Whether being carried (readable helper for CarriedByUnitId != null).</summary>
        public bool IsCarried => CarriedByUnitId.HasValue;

        /// <summary>Task101: whether a supply truck's current load came from a depot/cargo station stock
        /// (runtime-only, not persisted). Prevents the infinite shuffle of re-loading depot stock into the
        /// same or another depot: only trucks loaded from the faction pool (army bases) perform
        /// "stock delivery to depots" (SupplyTruckStep). Reverting to the default false on load costs at
        /// most one extra depot-to-depot transfer — harmless.</summary>
        public bool SupplyLoadFromDepot;

        /// <summary>Task110 (user request "trains need time to unload, so pause at stations"):
        /// remaining dwell time of a military train (in-game hours; runtime-only, not persisted). While
        /// positive, no station processing runs and the train stays put. Resetting to 0 on load merely means
        /// the next stop takes the normal time again.</summary>
        public float StationDwell;

        /// <summary>Task110: whether the cargo work (unload, disembark, load, board) at the currently
        /// occupied station is done (runtime-only, not persisted). Sequences service → dwell → departure
        /// exactly once per station call.</summary>
        public bool StationServiced;

        /// <summary>Task98: anchor position for stuck detection (runtime-only, not persisted). Updated to
        /// the current position each time the unit moves at least MinProgressDistance away from it, which
        /// also resets StuckHours (see StuckCleanupStep).</summary>
        public WorldPos? StuckAnchor;

        /// <summary>Task98: in-game hours the unit has remained essentially unable to move from StuckAnchor
        /// (runtime-only, not persisted; resetting to 0 on load merely delays the verdict — harmless).</summary>
        public float StuckHours;

        /// <summary>In-game time remaining until the next muzzle-effect (ShotEvent) may be emitted
        /// (runtime-only, not persisted; Task42). Decremented by dt only on ticks in which this unit
        /// actually dealt damage; when it reaches 0 one ShotEvent is queued and it resets to
        /// UnitType.FireIntervalHours (see CombatStep/BaseCombatStep). Never decremented on ticks without
        /// damage (so idle units cannot bank "firing rights" and burst-fire unnaturally the moment they next
        /// engage). Default 0, so the first damaging tick emits the first shot immediately.</summary>
        public float FireCooldown;

        /// <summary>Standing position for firing from cover (building/prop) while engaging (runtime-only,
        /// not persisted; Task44). Set/cleared by CoverSeekStep. While set, MovementStep moves here instead
        /// of Path/OrderTargetPos. Null restores the traditional movement (advance path / straight line).</summary>
        public WorldPos? CoverDestination;

        /// <summary>In-game time remaining until CoverSeekStep re-evaluates cover (runtime-only, not
        /// persisted; Task44). Searching every tick is expensive and causes jitter (the standing spot flips
        /// on tiny score differences), so re-evaluation happens only every CoverSeekStep interval. Default 0,
        /// so the first tick of an engagement evaluates immediately.</summary>
        public float CoverReevaluateCooldown;

        /// <summary>Whether to stay put and keep firing on reaching CoverDestination (true), or keep
        /// advancing cover-to-cover (false) (runtime-only, not persisted; Task45). CoverSeekStep sets true
        /// for engaging units (threat = TargetId) and false for advancing units (threat = OrderTargetPos,
        /// not yet engaged). On entering CoverArrivalDistance, MovementStep keeps the unit stopped when
        /// true, or clears CoverDestination when false so the unit can move on to the next cover
        /// (cover-to-cover bounding advance).</summary>
        public bool CoverHold;

        /// <summary>Remembers which TargetId the current CoverDestination/CoverHold was decided against
        /// while engaging (State==Engaging) (runtime-only, not persisted; Task50). As long as the TargetId
        /// keeps matching this value during the engagement, CoverSeekStep never re-evaluates cover (the
        /// decided outcome is kept as-is, including "none found"). Prevents the bug where, against the same
        /// opponent, the cover position flipped constantly on tiny score differences and units fidgeted
        /// restlessly behind buildings. Reset to null the moment the TargetId changes (new opponent) or the
        /// engagement ends, so the next engagement re-evaluates afresh.</summary>
        public uint? CoverTargetId;

        /// <summary>Elapsed in-game time actually spent stationary at CoverDestination with CoverHold==true
        /// (runtime-only, not persisted; Task52). Measured by MovementStep.AdvanceTowardCover; past
        /// MovementStep.MaxCoverHoldHours the CoverDestination is force-released (Task52's guarantee: "hold
        /// still while in cover, but never hide forever"). Stays 0 while travelling (before reaching the
        /// CoverDestination) so only post-arrival stillness is measured. Reset to 0 the moment a new cover
        /// is decided (CoverSeekStep).</summary>
        public float CoverHoldTimer;

        /// <summary>Task120: elapsed in-game time this unit has been entrenched at a fortification chosen by
        /// FortSeekStep (runtime-only, not persisted). Past FortSeekStep.MaxFortHoldHours the unit lets go
        /// and resumes its advance (so entrenching can never block an assault/capture forever), and
        /// FortSeekCooldown then keeps it from immediately re-entrenching in the same spot.</summary>
        public float FortHoldTimer;

        /// <summary>Task120: remaining in-game time during which FortSeekStep must not pick a fortification
        /// for this unit (runtime-only, not persisted). Set when a fort hold is released by
        /// MaxFortHoldHours.</summary>
        public float FortSeekCooldown;

        /// <summary>Elapsed in-game time during which State==Engaging and TargetId has kept pointing at the
        /// same opponent (runtime-only, not persisted; Task52). Incremented/reset by CoverSeekStep each
        /// tick; past CoverSeekStep.MaxEngageHoldHours, MovementStep allows movement toward
        /// OrderTargetPos/Path even while Engaging (CombatStep keeps up the firefight while moving if the
        /// opponent stays in range). Reset to 0 the moment the TargetId changes or the engagement ends.</summary>
        public float EngageHoldTimer;

        /// <summary>Stall watchdog (runtime-only, not persisted; Task52): horizontal distance to
        /// OrderTargetPos at the last checkpoint. Null = not yet measured (initialized next tick). Updated —
        /// with StallTimer reset to 0 — every time the distance shrinks by at least
        /// CoverSeekStep.StallEpsilon.</summary>
        public float? LastObjectiveDistance;

        /// <summary>Stall watchdog (runtime-only, not persisted; Task52): elapsed in-game time without
        /// advancing at least CoverSeekStep.StallEpsilon from LastObjectiveDistance. Past
        /// CoverSeekStep.StallTimeoutHours, CoverSuppressionRemaining is set.</summary>
        public float StallTimer;

        /// <summary>After the stall watchdog fires: remaining in-game time during which cover search is
        /// ignored entirely and only the road path is followed (runtime-only, not persisted; Task52).
        /// CoverSeekStep.Advance decrements it by dt every tick; at 0 the normal cover logic automatically
        /// resumes (the implementation of "suppresses cover after a stall and re-enables it later").</summary>
        public float CoverSuppressionRemaining;

        /// <summary>Used only by UnitCategory.Carrier: build progress of the aircraft currently under
        /// construction (0..1, Task64). 0f means "not building" (next tick decides whether to start a new
        /// airframe); CarrierAirWing.Advance sets a tiny positive value at the moment construction starts to
        /// distinguish "building" (the same runtime-only, non-persisted pattern as
        /// MilitaryBase.MissileBuildProgress). Always 0 and ignored for non-carrier units.</summary>
        public float CarrierBuildProgress;

        /// <summary>Used only by UnitCategory.Carrier: cumulative count of aircraft this carrier has started
        /// building (runtime-only, not persisted; Task64). Input to CarrierAirWing.NextBuildCategory, which
        /// hashes "carrier id + this counter" to pick the next airframe class deterministically (seeded
        /// variety without System.Random). Increments by one each time a build starts.</summary>
        public uint CarrierBuildCounter;

        /// <summary>Task78: continuous in-game time a sea unit (Domain.Sea) has been fully blocked — neither
        /// the straight step nor the ±30/60/90-degree detours could land on water (runtime-only, not
        /// persisted). MovementStep.AdvanceSea resets it to 0 on every successful move and the moment the
        /// objective (OrderTargetPos) changes (= new orders). Past MovementStep.SeaBlockedIdleHours the unit
        /// transitions to State=Idle and stops attempting to move until the objective changes (prevents the
        /// endless per-tick detour searching that looked like spinning in place — the counterpart of
        /// Task52's StallTimer).</summary>
        public float SeaBlockedHours;

        /// <summary>The last objective AdvanceSea processed, used as the reference for SeaBlockedHours
        /// (runtime-only, not persisted; Task78). When OrderTargetPos/RallyPoint differs from this,
        /// MovementStep treats it as "new orders" and resets SeaBlockedHours to 0.</summary>
        public WorldPos? SeaLastObjective;

        public UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)
        {
            InstanceId = id; TypeKey = typeKey; FactionId = factionId;
            CurrentHP = hp; Position = pos; State = UnitState.Idle;
        }

        public bool IsAlive => State != UnitState.Dead && CurrentHP > 0f;

        public void ClearPath()
        {
            Path = null;
            PathIndex = 0;
            PathTarget = null;
            PathRetryCooldown = 0f;
        }
    }
}
