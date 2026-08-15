namespace CSWarfront.Core
{
    public static class AiTargeting
    {
        /// <summary>Task59: if the Nemesis faction owns any base, the nearest of those is returned in
        /// preference over ordinary Hostile-owned bases. With no Nemesis-owned base, the nearest
        /// Hostile-owned base is returned as before (behavior is exactly unchanged when no Nemesis exists).
        ///
        /// Task64: legacy callers that omit domain (or pass Domain.Land/Air) keep the old behavior — the
        /// nearest Hostile/Nemesis-owned base regardless of BaseType. Only domain=Domain.Sea filters down to
        /// BaseType.Navy bases, preventing naval units from steering straight at inland Army/AirForce/
        /// MissileBase targets and running aground (user request: "sea pathing can be a straight line to the
        /// enemy navy base for now"). The Nemesis-priority ordering still applies within the Navy-base
        /// subset.</summary>
        public static MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from, Domain domain = Domain.Land)
        {
            bool navyOnly = domain == Domain.Sea;
            MilitaryBase bestHostile = null; float bestHostileDist = float.MaxValue;
            MilitaryBase bestNemesis = null; float bestNemesisDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (!FortificationRules.IsTargetable(b.Type)) continue; // Task101: trenches are not advance objectives
                if (navyOnly && b.Type != BaseType.Navy) continue;
                Relation r = state.Relations.Get(factionId, b.OwnerFactionId.Value);
                if (!r.IsHostile()) continue;
                float d = from.HorizontalDistanceTo(b.Position);

                if (r == Relation.Nemesis)
                {
                    if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = b; }
                }
                else
                {
                    if (d < bestHostileDist) { bestHostileDist = d; bestHostile = b; }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }
    }

    public static class InvasionOrders
    {
        /// <summary>Maximum horizontal distance for road snapping. Units/bases farther from a road than this
        /// fall back to straight-line movement.</summary>
        public const float PathSnapRadius = 200f;

        /// <summary>Task92: snap radius for sea routes (SeaGrid). Ports and navy bases sit on the shoreline,
        /// so this is larger than the road radius to reliably reach the nearest navigable cell.</summary>
        public const float SeaPathSnapRadius = 400f;

        /// <summary>Threshold below which a destination counts as unchanged (X/Z); smaller differences reuse
        /// the existing path.</summary>
        private const float TargetChangeEpsilon = 1f;

        /// <summary>Task88: path-recompute threshold when chasing a moving threat (a Nemesis KAIJU etc.).
        /// Threats shift a little every tick; at TargetChangeEpsilon (1m) each call cleared and re-searched
        /// the path, so the route never settled and movement was almost always the off-road straight line
        /// (one cause of the report "movement toward the nemesis does not follow roads"). Recompute only
        /// when the threat has moved beyond this distance — the tail of the path grows slightly stale, but
        /// the straight-line fallback after the path is consumed always heads for the latest OrderTargetPos,
        /// so there is no practical harm.</summary>
        private const float ThreatTargetChangeEpsilon = 100f;

        /// <summary>Cooldown before the same unit retries after a FindPath failure (in-game hours). Prevents
        /// an unreachable unit from re-running a full A* every tick and monopolizing the budget (Task23
        /// review, Important).</summary>
        public const float PathRetryFailCooldownHours = 2f;

        /// <summary>Maximum fraction of jitter applied to path-search edge costs (0.35 = an edge may look up
        /// to 35% longer). Derived deterministically per unit (seed=InstanceId), so units do not all pack
        /// onto the identical shortest route — some "prefer" a parallel alternative. Larger values mean
        /// bigger detours (0.35 is tuned to "picks a parallel road"; much higher becomes irrationally
        /// roundabout).</summary>
        public const float PathJitter = 0.35f;

        /// <summary>Task58: while a living external threat (Godzilla/aliens) is within this horizontal
        /// distance of any base the faction owns, non-player units prioritize intercepting it over advancing
        /// on enemy bases (see FindDivertThreat). Once arrived, ThreatCombatStep engages normally.</summary>
        public const float ThreatDivertRadius = 600f;

        /// <summary>Gives each of the faction's non-engaged units an advance order toward the nearest enemy
        /// base from its own position. When state.Roads is supplied, road paths (A*) are computed too (up to
        /// maxPathComputations per call). A unit whose FindPath failed does not retry (and spends no budget)
        /// until its PathRetryCooldown expires.
        /// Task48: units the player ordered to Hold/RallyHold are always skipped (the AI never overwrites
        /// their objective/path). Only AiControlled/FreeAdvance are managed as before (FreeAdvance lets the
        /// AI update the target base, but the free-advance mode itself persists until the player issues a
        /// different order).
        /// Task58: while an external threat is near the faction's territory (owned bases), every managed
        /// unit diverts toward it (priority over advancing on enemy bases). Once the threat despawns or is
        /// destroyed, the next call automatically returns to normal base advances (stateless re-evaluation
        /// every call — no explicit "revert" logic needed).</summary>
        /// <summary>Task132: how many road routes may be computed per call for units halted by water.
        /// Their own budget, so a column stuck at a river never competes with routine pathfinding — and
        /// still bounded, because a bank with no bridge anywhere would otherwise retry every tick.</summary>
        public const int MaxCrossingPathComputations = 4;

        public static void AssignAdvance(WarState state, byte factionId, float dt, int maxPathComputations = 4)
        {
            int pathComputations = 0;
            int crossingComputations = 0; // Task132: separate budget for units halted at water
            WorldPos? divertTarget = FindNearbyThreatToOwnTerritory(state, factionId);

            // Task105 (aggressive rail usage): enumerate this faction's operational station pairs once
            // (empty when it has no stations). Used to reroute land units' road paths to a boarding station
            // when rail is worthwhile.
            System.Collections.Generic.List<TrainStep.StationPair> railPairs =
                TrainStep.RoutesOf(state, factionId);
            // Task96: while Invader-faction troops (external invasions) are alive, they are interception
            // targets second only to external threats (KAIJU/Alien) — prioritized over advancing on enemy
            // bases. See FindInvaderToIntercept for details.
            if (!divertTarget.HasValue) divertTarget = FindInvaderToIntercept(state, factionId);
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                if (u.State == UnitState.Engaging) continue;

                u.PathRetryCooldown -= dt;
                if (u.PathRetryCooldown < 0f) u.PathRetryCooldown = 0f;

                // Task61: Sea/Air never use road paths (MovementStep's Sea/Air branches do not read u.Path;
                // see Core/MovementStep.cs). To preserve the road pathfinding budget (maxPathComputations)
                // for land units, FindPath is never attempted for the other domains.
                UnitType type = state.Types.Get(u.TypeKey);
                Domain domain = type != null ? type.Domain : Domain.Land; // defensively treat unknown types as Land
                bool isLand = domain == Domain.Land;

                // Task99/101: logistics units (supply trucks / transport helicopters / military trains) and
                // carried units are exempt from AI advances (their dedicated steps — SupplyTruckStep /
                // TransportHeliStep / TrainStep — own all their movement; an advance objective here would
                // send them unarmed into the front line).
                if (type != null && (type.Category == UnitCategory.SupplyTruck
                    || type.Category == UnitCategory.TransportHelicopter
                    || type.Category == UnitCategory.MilitaryTrain)) continue;
                if (u.IsCarried) continue;

                // Task99: air/sea units that are out of ammo get no new advance objective; they are set to
                // Idle so the existing return-home logic (MovementStep.ResolveHomeObjective) brings them back
                // to their base/carrier. ResupplyStep restores ammo inside the home base area, and once
                // HasAmmo is true again the next call automatically resumes the normal advance = re-sortie.
                // Land units are exempt (by design they can still move and capture bases while dry, so the
                // advance continues).
                if (!isLand && type != null && type.AmmoCombatHours > 0f && !AmmoRules.HasAmmo(u, type))
                {
                    u.OrderTargetPos = null;
                    u.ClearPath();
                    u.State = UnitState.Idle;
                    continue;
                }

                WorldPos targetPos;
                if (divertTarget.HasValue)
                {
                    targetPos = divertTarget.Value;
                }
                else
                {
                    // Task64: sea units (ships) only target enemy-held BaseType.Navy bases (prevents
                    // steering straight at inland Army/AirForce/MissileBase targets and running aground).
                    var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position, domain);
                    if (target != null)
                    {
                        targetPos = target.Position;
                    }
                    else
                    {
                        // Task78: fix for "no hostile objective (threat/enemy base) exists at all".
                        // Previously this simply continued, leaving the previous call's
                        // OrderTargetPos/State/Path in place — the direct cause of the reported bugs where
                        // units kept marching to the spot of an external threat (KAIJU etc.) after it
                        // despawned, or to an enemy base after it had fallen. While no objective exists,
                        // units withdraw to the faction's nearest owned base (or go Idle where there is
                        // none). Stateless re-evaluation each call, so a new threat/enemy base automatically
                        // restores the normal advance (same design as Task58).
                        // Task107 (user request "aircraft that lost their objective should land back at a
                        // nearby air base/carrier"): air/sea units do NOT withdraw to "the nearest owned base
                        // of any type" — they defer to the dedicated return-home logic.
                        // MovementStep.ResolveHomeObjective picks only air bases/carriers for aircraft and
                        // navy bases for ships, and MovementStep.AdvanceAirLanding lands them on arrival
                        // (avoiding the half-hearted withdrawal of hovering over an army base).
                        if (!isLand)
                        {
                            u.OrderTargetPos = null;
                            u.ClearPath();
                            u.State = UnitState.Idle;
                            continue;
                        }

                        MilitaryBase home = FindNearestOwnedBase(state, factionId, u.Position);
                        if (home == null)
                        {
                            // Nowhere to withdraw to (the faction owns no bases): go Idle in place and clear
                            // the objective/path so the stale target is not pursued.
                            u.OrderTargetPos = null;
                            u.ClearPath();
                            u.State = UnitState.Idle;
                            continue;
                        }

                        targetPos = home.Position;
                        if (u.Position.HorizontalDistanceTo(targetPos) <= MovementStep.CoverArrivalDistance)
                        {
                            // Withdrawal complete: back near an own base, so go Idle (reusing MovementStep's
                            // existing arrival distance).
                            u.OrderTargetPos = null;
                            u.ClearPath();
                            u.State = UnitState.Idle;
                            continue;
                        }
                    }
                }

                u.OrderTargetPos = targetPos;
                u.State = UnitState.Moving;

                // Task88: while chasing a moving threat, rebuild the path only when the threat has moved
                // substantially (see the threshold comment; base objectives still recompute at 1m = no
                // behavior change).
                float sameTargetEps = divertTarget.HasValue ? ThreatTargetChangeEpsilon : TargetChangeEpsilon;
                if (u.PathTarget.HasValue && !IsSameTarget(u.PathTarget.Value, u.OrderTargetPos.Value, sameTargetEps))
                    u.ClearPath();

                // Task132 (playtest "vehicles drive into the river instead of using the bridge"): a unit
                // whose last step was refused by water is asking for a road route across it. Movement
                // falls back to a straight line whenever the path is missing or spent, and that line
                // runs into the river; the unit then waited out PathRetryFailCooldownHours at the bank.
                // Such a unit therefore skips the cooldown, discards a spent path, and draws on its own
                // small budget so a column stuck at a ford is never starved by the ordinary one.
                bool needsCrossing = isLand && u.WaterBlocked;
                if (needsCrossing && u.Path != null && u.PathIndex >= u.Path.Count) u.ClearPath();

                if (isLand && state.Roads != null && u.Path == null &&
                    (u.PathRetryCooldown <= 0f || needsCrossing))
                {
                    if (needsCrossing)
                    {
                        if (crossingComputations >= MaxCrossingPathComputations) continue;
                        crossingComputations++;
                    }
                    else
                    {
                        if (pathComputations >= maxPathComputations) continue; // budget exhausted; carry over to the next call
                        pathComputations++;
                    }

                    // Task105: when the rail detour is clearly worthwhile, redirect the road path's goal to
                    // the boarding station (OrderTargetPos itself remains the final objective — after
                    // reaching the station the unit is picked up within BoardRadius by a train, and after
                    // alighting resumes marching to the retained final objective. If no train ever comes,
                    // the straight-line fallback after the path is consumed heads for the final objective as
                    // usual, so nothing deadlocks).
                    WorldPos pathGoal = u.OrderTargetPos.Value;
                    WorldPos boardingStation;
                    if (!divertTarget.HasValue && railPairs.Count > 0 &&
                        TrainStep.TryFindBoardingStation(railPairs, u.Position, pathGoal, out boardingStation))
                        pathGoal = boardingStation;
                    // Seeding with InstanceId gives each unit a stable "preferred detour". InstanceId is
                    // unique and immutable for the unit's lifetime, so retries pick the same route and there
                    // is no flip-flopping (choosing a different route every time).
                    // Task88: when chasing a threat, the destination-side snap radius is unlimited — threats
                    // are often more than PathSnapRadius (200) from any road, and previously the snap failed
                    // → null path → the whole leg became an off-road straight line. With unlimited snap the
                    // unit "takes roads to the node nearest the threat, then goes straight for the rest"
                    // (user request: "on roads as much as possible").
                    float destSnap = divertTarget.HasValue ? float.MaxValue : PathSnapRadius;
                    var path = state.Roads.FindPath(u.Position, pathGoal, PathSnapRadius,
                        u.InstanceId, PathJitter, destSnap); // Task105: pathGoal = final objective or boarding station
                    u.Path = path;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                    u.PathRetryCooldown = path == null ? PathRetryFailCooldownHours : 0f;
                }
                else if (domain == Domain.Sea && state.SeaNav != null && u.Path == null && u.PathRetryCooldown <= 0f)
                {
                    // Task92: sea units route via the SeaGrid (navigable-cell A*). The budget is shared with
                    // road searches. When no route exists (a target fully enclosed by land, etc.), movement
                    // stays straight-line plus wall-following as before (MovementStepSea).
                    if (pathComputations >= maxPathComputations) continue;

                    pathComputations++;
                    var seaPath = state.SeaNav.FindPath(u.Position, u.OrderTargetPos.Value, SeaPathSnapRadius);
                    u.Path = seaPath;
                    u.PathIndex = 0;
                    u.PathTarget = u.OrderTargetPos;
                    u.PathRetryCooldown = seaPath == null ? PathRetryFailCooldownHours : 0f;
                }
            }
        }

        /// <summary>Task58: if any living (undefeated) external threat hostile to this faction (Task59:
        /// WarState.ThreatRelations is Hostile/Nemesis) is within ThreatDivertRadius (horizontal) of any base
        /// the faction owns, returns that threat's position.
        /// Task59: if any Nemesis-related threat qualifies, it takes priority over ordinary Hostile threats;
        /// the one with the smallest distance to the faction's bases is returned. Without a Nemesis, the
        /// ordinary Hostile threat with the smallest distance is returned under the same conditions (behavior
        /// is exactly unchanged when no Nemesis exists).
        /// Task62: for Nemesis threats only, the ThreatDivertRadius limit does not apply at all (a faction
        /// owning at least one land/sea/air base intercepts its nemesis anywhere on the map, prioritized over
        /// base advances). A faction owning no bases (= nothing to defend from) stays exempt even for a
        /// Nemesis (as with Hostile: do nothing). Ordinary Hostile threats keep the ThreatDivertRadius
        /// condition (no behavior change).
        /// Returns null when neither exists (= normal enemy-base advance).</summary>
        private static WorldPos? FindNearbyThreatToOwnTerritory(WarState state, byte factionId)
        {
            WorldPos? bestHostile = null; float bestHostileDist = float.MaxValue;
            WorldPos? bestNemesis = null; float bestNemesisDist = float.MaxValue;

            // Task62: "owns at least one base" is a global condition independent of distance, so it is
            // evaluated once outside the threat loop.
            bool ownsAnyBase = false;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                if (state.Bases[b].OwnerFactionId == factionId) { ownsAnyBase = true; break; }
            }

            for (int t = 0; t < state.Threats.Count; t++)
            {
                var threat = state.Threats[t];
                if (threat.IsDefeated) continue;

                Relation rel = state.ThreatRelations.Get(factionId, threat.Kind);
                if (!rel.IsHostile()) continue;

                float nearestOwnedBaseDist = float.MaxValue;
                for (int b = 0; b < state.Bases.Count; b++)
                {
                    var ownedBase = state.Bases[b];
                    if (ownedBase.OwnerFactionId != factionId) continue;
                    float d = threat.Position.HorizontalDistanceTo(ownedBase.Position);
                    if (d < nearestOwnedBaseDist) nearestOwnedBaseDist = d;
                }

                if (rel == Relation.Nemesis)
                {
                    // Task62: no distance limit at all. Only a faction with no bases is exempt
                    // (nearestOwnedBaseDist stays float.MaxValue with no bases to measure — rejected
                    // explicitly via ownsAnyBase; with at least one base the distance is always finite).
                    if (!ownsAnyBase) continue;
                    if (nearestOwnedBaseDist < bestNemesisDist) { bestNemesisDist = nearestOwnedBaseDist; bestNemesis = threat.Position; }
                }
                else
                {
                    if (nearestOwnedBaseDist > ThreatDivertRadius) continue;
                    if (nearestOwnedBaseDist < bestHostileDist) { bestHostileDist = nearestOwnedBaseDist; bestHostile = threat.Position; }
                }
            }
            return bestNemesis ?? bestHostile;
        }

        /// <summary>Task96 (playtest feedback "deployed units do not move to intercept the invasion force"):
        /// the AI's advance objectives used to be only "bases owned by hostile factions" and "external
        /// threats (state.Threats)"; there was no behavior chasing enemy units themselves. Invader-faction
        /// troops (external invasions, Faction.InvaderFactionId) own no bases and are not on the threat
        /// list, so no defender ever moved to intercept and the invasion force was ignored until it walked
        /// into a base's firing range.
        ///
        /// This method returns the position of the living Invader unit closest to the faction's nearest
        /// owned base (= crush the most imminent threat first). Rules match Nemesis-threat interception
        /// (Task62):
        ///  - Unlimited distance (a faction owning any base sorties even to a landing site at the map edge).
        ///  - A faction owning no bases is exempt (nothing to sortie from).
        ///  - Stateless per-tick re-evaluation, so once the invasion force is wiped out the next call
        ///    automatically returns to normal enemy-base advances (same design as Task58/Task62).
        /// The Invader faction's own troops are on the other side of this interception (an Invader caller
        /// gets null = advances on bases as before). Like nemesis threats, the target moves every tick, and
        /// the caller's ThreatTargetChangeEpsilon (recompute only after 100m of movement) and unlimited
        /// destination snap apply as-is.</summary>
        private static WorldPos? FindInvaderToIntercept(WarState state, byte factionId)
        {
            if (factionId == Faction.InvaderFactionId) return null;

            WorldPos? best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.FactionId != Faction.InvaderFactionId || !u.IsAlive) continue;

                // Distance from this Invader unit to the faction's nearest owned base (stays MaxValue with
                // no bases, so the < comparison below never succeeds — which is exactly how "factions with
                // no bases are exempt" falls out).
                float nearestOwnedBaseDist = float.MaxValue;
                for (int b = 0; b < state.Bases.Count; b++)
                {
                    MilitaryBase ownedBase = state.Bases[b];
                    if (ownedBase.OwnerFactionId != factionId) continue;
                    float d = u.Position.HorizontalDistanceTo(ownedBase.Position);
                    if (d < nearestOwnedBaseDist) nearestOwnedBaseDist = d;
                }

                if (nearestOwnedBaseDist < bestDist) { bestDist = nearestOwnedBaseDist; best = u.Position; }
            }
            return best;
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b)
        {
            return IsSameTarget(a, b, TargetChangeEpsilon);
        }

        private static bool IsSameTarget(WorldPos a, WorldPos b, float epsilon)
        {
            return System.Math.Abs(a.X - b.X) < epsilon && System.Math.Abs(a.Z - b.Z) < epsilon;
        }

        /// <summary>Task78: returns the base owned by factionId nearest to from (null when it owns none).
        /// On a near-tie (within TargetChangeEpsilon) the headquarters (IsHeadquarters) wins — a
        /// deterministic tie-break so the same base is chosen on every call even in the rare case of several
        /// own bases at equal distance (no randomness, per the core-wide policy).
        ///
        /// Task136 (playtest report "with no objective left, crowds of units pile onto our own bunkers and
        /// artillery positions and then disappear"): fortifications are not somewhere an army withdraws to.
        /// They are 16×16 emplacements holding three men (FortSeekStep.GarrisonCapacity), and one of them
        /// is almost always the nearest owned thing on the map — so every unit of the faction converged on
        /// a single pillbox, could not all reach the arrival distance that ends the withdrawal, and kept
        /// milling about as State==Moving until the stall watchdog tidied them away. A withdrawal now
        /// targets a real base only; a faction that holds nothing but emplacements has nowhere to fall back
        /// to, and the caller stands its units down where they are instead.</summary>
        private static MilitaryBase FindNearestOwnedBase(WarState state, byte factionId, WorldPos from)
        {
            MilitaryBase best = null; float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                var mb = state.Bases[b];
                if (mb.OwnerFactionId != factionId) continue;
                if (FortificationRules.IsFortification(mb.Type)) continue; // Task136
                float d = from.HorizontalDistanceTo(mb.Position);
                if (best == null || d < bestDist - TargetChangeEpsilon)
                {
                    best = mb; bestDist = d;
                }
                else if (System.Math.Abs(d - bestDist) <= TargetChangeEpsilon && mb.IsHeadquarters && !best.IsHeadquarters)
                {
                    best = mb; bestDist = d;
                }
            }
            return best;
        }
    }
}
