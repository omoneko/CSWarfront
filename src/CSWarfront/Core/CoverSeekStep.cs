using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Assigns units a standing position (UnitInstance.CoverDestination) that exploits nearby cover
    /// (buildings/props, WarState.Cover) — pure logic, Task44/Task45/Task50/Task52.
    /// MilitaryManager.OnSimTick must call this BEFORE MovementStep (so a position decided this tick can be
    /// moved toward within the same tick — the same "decide first, move after" ordering as
    /// RoadGraph → InvasionOrders → MovementStep).
    ///
    /// Task45 changed the model from "seek cover once engaged" to "advance cover-to-cover once outside
    /// friendly territory". Every living unit falls into one of three modes:
    ///   1. Inside friendly territory (IsInFriendlyTerritory): no cover movement — move fast along roads.
    ///   2. Outside + engaging (State==Engaging with a living TargetId): pick a position hidden from the
    ///      threat (the target's position) and hold there with CoverHold=true while firing (not pinned
    ///      forever — capped by Task52's MaxCoverHoldHours/MaxEngageHoldHours, see below).
    ///   3. Outside + advancing (not engaging but OrderTargetPos set): treat the objective (the enemy base
    ///      being advanced on) as the threat direction and search for candidates only every
    ///      CoverUseIntervalHours (not every tick). A candidate must make real progress toward the objective
    ///      (at least MinForwardProgress) and lie within MaxCoverDetour of the current position; if one
    ///      exists it is set with CoverHold=true (Task52: pause briefly in cover on arrival, then
    ///      MovementStep's MaxCoverHoldHours automatically resumes the advance). With no qualifying
    ///      candidate, the road path (Path/OrderTargetPos) is left in charge.
    ///
    /// Task50: mode 2 (engaging) never re-evaluates cover while fighting the same opponent (TargetId — see
    /// UnitInstance.CoverTargetId). No position is re-picked until the TargetId changes (a new opponent),
    /// i.e. after arrival the unit stands completely still and trades fire (until the Task52 hold cap).
    /// A "no cover found" decision is remembered the same way — no re-searching every tick against the same
    /// opponent.
    ///
    /// Task52 (fix for "advance toward enemy bases stalls partway"): Task50's "never leave cover against the
    /// same opponent" caused permanent freezes in (a) long-range standoffs and (b) fights against opponents
    /// that cannot be killed. Mode 3's bounding advance could also mark time forever when no cover was found.
    /// The following guardrails ensure "the advance always progresses; cover is only an occasional flourish":
    ///   1. Cover is occasional: mode 3 searches only every CoverUseIntervalHours (much sparser than the old
    ///      0.5h). Candidates must satisfy both MinForwardProgress (forward gain) and MaxCoverDetour
    ///      (deviation from the current position).
    ///   2. Hold-time cap: time spent stationary in cover is capped by MovementStep.MaxCoverHoldHours
    ///      (measured via UnitInstance.CoverHoldTimer, managed on the MovementStep side). Applies to both
    ///      mode-2 and mode-3 holds.
    ///   3. Stalemate prevention: time spent fighting the same opponent is measured by EngageHoldTimer; past
    ///      MaxEngageHoldHours, MovementStep resumes movement regardless of cover (CombatStep keeps firing
    ///      while moving if the opponent stays in range).
    ///   4. Stall watchdog: if the distance to OrderTargetPos fails to shrink by StallEpsilon for
    ///      StallTimeoutHours, cover search is suspended entirely for CoverSuppressedHours and the road path
    ///      drives movement — belt and braces for stalls the above three cannot catch.
    ///
    /// Task77 (part of the "ground units can walk into the sea" fix): the standing position returned by
    /// TryFindBestCover (CoverMap.StandingPosition — StandoffMargin past the cover on the side away from the
    /// threat) can land in the water when the cover sits on a shoreline. If state.Water (IWaterSampler) is
    /// supplied and the candidate is in water, it is treated exactly like "no cover found" (the existing
    /// else branch: clear CoverDestination/CoverHold; mode 2 remembers the decision, mode 3 defers to the
    /// road path). Paired with MovementStep's off-road water checks (AdvanceStraight/MoveToward, see
    /// MovementStep.cs), this gives two layers: never choose a watery standing spot in the first place.
    /// </summary>
    public static class CoverSeekStep
    {
        /// <summary>Interval at which mode 3 (bounding advance) searches for cover (in-game hours, Task52).
        /// Searching every tick risks "hopping cover-to-cover and effectively never advancing", so the search
        /// is spaced out — cover only "occasionally". Tracked via UnitInstance.CoverReevaluateCooldown (the
        /// field reused from Task44; only its meaning changed in Task52).</summary>
        public const float CoverUseIntervalHours = 3f;

        /// <summary>Minimum forward progress toward the objective (map units) a candidate cover must provide
        /// to be adopted in mode 3 (bounding advance). Candidates below this "do not count as advancing" and
        /// are rejected — advancing along the road beats retreating/stalling in search of cover (Task45).</summary>
        public const float MinForwardProgress = 5f;

        /// <summary>Maximum detour from the current position allowed for a mode-3 cover candidate (Task52).
        /// Anything farther exceeds the "hide occasionally" budget and is rejected in favor of the road path.</summary>
        public const float MaxCoverDetour = 40f;

        /// <summary>Maximum time a unit may keep fighting the same opponent (TargetId) (in-game hours,
        /// Task52). Past this, MovementStep resumes movement toward the objective regardless of cover
        /// (engagement itself is re-evaluated by range in CombatStep every tick, so the firefight continues
        /// while moving). Measured via UnitInstance.EngageHoldTimer; reset to 0 the moment the TargetId
        /// changes or the engagement ends.</summary>
        public const float MaxEngageHoldHours = 3f;

        /// <summary>Stall watchdog (Task52): the distance to OrderTargetPos is observed over this window;
        /// failing to shrink by StallEpsilon within it counts as a stall.</summary>
        public const float StallTimeoutHours = 2f;

        /// <summary>Minimum distance the stall watchdog accepts as "made progress" (map units, Task52).</summary>
        public const float StallEpsilon = 5f;

        /// <summary>How long cover search is fully suspended, leaving only the road path, after a stall is
        /// detected (in-game hours, Task52). Remaining time tracked in UnitInstance.CoverSuppressionRemaining.</summary>
        public const float CoverSuppressedHours = 4f;

        /// <summary>Per-category cover search radius. Zero or less = that category never moves for building
        /// cover (Artillery lobs shells from the rear and has no need to hide behind cover).
        /// Task104 (user request "stop the unrealistic ducking under elevated roads; infantry should hide
        /// behind nearby friendly armor instead"): infantry-like categories (Infantry/MechInfantry/
        /// DroneInfantry) were removed from building cover (see IsInfantryLike). While engaging they instead
        /// stand "behind the nearest friendly armor (Tank/Apc)" (TryFindArmorCover); with no armor nearby
        /// they fight where they stand (no running to buildings or under overpasses). Seeking
        /// trenches/bunkers (FortSeekStep, which later overrides this step's decision) still takes top
        /// priority as before.</summary>
        private static readonly Dictionary<UnitCategory, float> SearchRadiusByCategory = new Dictionary<UnitCategory, float>
        {
            { UnitCategory.Apc, 45f },
            { UnitCategory.Tank, 45f },
            { UnitCategory.AntiAir, 45f },
            { UnitCategory.Artillery, 0f },
        };

        /// <summary>Task104: radius within which infantry look for friendly armor to hide behind.</summary>
        public const float ArmorCoverRadius = 60f;

        /// <summary>Task104: how far "behind" the armor (on the side away from the threat) infantry stand.</summary>
        public const float ArmorCoverStandoff = 6f;

        private static bool IsInfantryLike(UnitCategory category)
        {
            return category == UnitCategory.Infantry || category == UnitCategory.MechInfantry
                || category == UnitCategory.DroneInfantry;
        }

        /// <summary>Task104: finds the nearest friendly armor (Tank/Apc, alive, not being carried) within
        /// ArmorCoverRadius of u and returns the standing position on its side away from the threat.
        /// Returns false if none is found.</summary>
        private static bool TryFindArmorCover(WarState state, UnitInstance u, WorldPos threatPos, out WorldPos coverPos)
        {
            coverPos = default(WorldPos);
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried || o.InstanceId == u.InstanceId) continue;
                if (o.FactionId != u.FactionId) continue;
                UnitType t = state.Types.Get(o.TypeKey);
                if (t == null || (t.Category != UnitCategory.Tank && t.Category != UnitCategory.Apc)) continue;
                float d = u.Position.HorizontalDistanceTo(o.Position);
                if (d > ArmorCoverRadius) continue;
                if (d < bestDist) { bestDist = d; best = o; }
            }
            if (best == null) return false;

            float dx = best.Position.X - threatPos.X;
            float dz = best.Position.Z - threatPos.Z;
            float len = (float)System.Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.01f) { dx = 1f; dz = 0f; len = 1f; }
            coverPos = new WorldPos(
                best.Position.X + dx / len * ArmorCoverStandoff,
                best.Position.Y,
                best.Position.Z + dz / len * ArmorCoverStandoff);
            return true;
        }

        /// <summary>Whether u is inside the influence area (horizontal distance within InfluenceRadius) of
        /// any base owned by its own faction (u.FactionId) (Task45). Enemy bases' influence does not count.
        /// False when the faction owns no bases.</summary>
        public static bool IsInFriendlyTerritory(WarState state, UnitInstance u)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId != u.FactionId) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= b.InfluenceRadius) return true;
            }
            return false;
        }

        public static void Advance(WarState state, float dt)
        {
            IWaterSampler water = state.Water; // Task77: grab once into a null-safe local.

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                // Task48: Hold/RallyHold are the definition of "passive defense of a post", so they are
                // exempt from cover movement and all Task52 timers (no pursuit, no cover-to-cover advance).
                // FreeAdvance is treated the same as AiControlled and gets no special case here.
                if (u.Order == UnitOrder.Hold || u.Order == UnitOrder.RallyHold)
                {
                    ClearCover(u);
                    continue;
                }

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null)
                {
                    ClearCover(u);
                    continue;
                }

                // Task61: Sea/Air are exempt from cover movement (hiding behind buildings has no meaning in
                // those domains). MovementStep's Sea/Air branches never read CoverDestination, so nothing
                // breaks either way — but skipping explicitly preserves the invariant "CoverSeekStep only
                // ever touches Land unit state" (also a foundation for future Sea/Air behaviors such as
                // fleet formation keeping).
                if (type.Domain != Domain.Land)
                {
                    ClearCover(u);
                    continue;
                }

                // Mode 1: while inside friendly territory, exempt from cover movement and the Task52 timers
                // (we want fast movement along roads). The cooldown is also reset while inside, so cover
                // evaluation can start immediately on the first tick outside.
                if (IsInFriendlyTerritory(state, u))
                {
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                bool wasEngagingWithTarget = u.State == UnitState.Engaging && u.TargetId.HasValue;
                UnitInstance target = wasEngagingWithTarget ? state.FindUnit(u.TargetId.Value) : null;
                bool targetAlive = target != null && target.IsAlive;

                if (wasEngagingWithTarget && !targetAlive)
                {
                    // The engagement is over — we want an immediate re-evaluation when the next one starts,
                    // so the cooldown and the Task52 timers are reset together.
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                bool isEngaging = wasEngagingWithTarget && targetAlive;

                // Task52 rule 3: measure time spent fighting the same opponent, independent of cover
                // eligibility (so categories that never seek cover, e.g. Artillery, are also protected from
                // being pinned forever by an engagement).
                bool sameTarget = isEngaging && u.CoverTargetId.HasValue && u.CoverTargetId.Value == u.TargetId.Value;
                u.EngageHoldTimer = isEngaging ? (sameTarget ? u.EngageHoldTimer + dt : dt) : 0f;

                bool infantryLike = IsInfantryLike(type.Category); // Task104: no building cover; armor cover only
                float searchRadius = SearchRadiusByCategory.TryGetValue(type.Category, out float r) ? r : 0f;
                bool coverEligible = infantryLike || (searchRadius > 0f && state.Cover != null);

                if (!coverEligible)
                {
                    // Artillery, or no cover map supplied: never search for cover (as in Task44).
                    // CoverTargetId is still maintained while engaging because EngageHoldTimer's
                    // "same opponent" check needs it next tick.
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    u.CoverTargetId = isEngaging ? u.TargetId : null;
                    continue;
                }

                if (isEngaging)
                {
                    // Task50: while fighting the same opponent, never alter the cover decision already made
                    // (including the decision that none was found). The single most important guard against
                    // constant repositioning.
                    if (sameTarget) continue;

                    // New engagement, or the opponent changed: evaluate immediately (no cooldown wait).
                    u.CoverTargetId = u.TargetId;
                    u.CoverHoldTimer = 0f;
                    // Task104: infantry-like units take cover "behind the nearest friendly armor" (no
                    // building cover).
                    // Task77: a candidate that lands in water (state.Water.IsWater) is treated the same as
                    // "not found" (never steer land units to waterline/underwater positions).
                    bool foundCover;
                    WorldPos coverPos;
                    if (infantryLike)
                        foundCover = TryFindArmorCover(state, u, target.Position, out coverPos);
                    else
                        foundCover = state.Cover.TryFindBestCover(u.Position, target.Position, searchRadius, u.InstanceId, out coverPos);
                    if (foundCover && !(water != null && water.IsWater(coverPos.X, coverPos.Z)))
                    {
                        u.CoverDestination = coverPos;
                        u.CoverHold = true;
                    }
                    else
                    {
                        // Remember the "no cover found" decision itself (CoverTargetId is kept; only
                        // CoverDestination/CoverHold are cleared — ClearCover is NOT used here, because it
                        // would null CoverTargetId too and cause a fresh search every tick against the same
                        // opponent).
                        u.CoverDestination = null;
                        u.CoverHold = false;
                    }
                    continue;
                }

                // Only non-engaging (mode-3 advancing) units reach here. Release the lock so the next
                // engagement is evaluated immediately.
                u.CoverTargetId = null;

                // Task104: infantry-like units no longer do the bounding advance (hiding building-to-building
                // while advancing) — it was the cause of the unnatural dashes under overpasses and behind
                // buildings. They advance straight along the road, and once a fight starts, mode 2 above
                // tucks them behind friendly armor.
                if (infantryLike)
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    continue;
                }

                // Task52 rule 4: the advance stall watchdog (self-resets internally when OrderTargetPos is gone).
                UpdateStallWatchdog(u, dt);

                if (!u.OrderTargetPos.HasValue)
                {
                    // Neither engaging nor advancing (Idle etc.) → exempt from cover movement.
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    u.CoverHoldTimer = 0f;
                    continue;
                }

                // Task52 rule 4: while the stall watchdog is active, suspend cover search entirely and let
                // the road path (Path/OrderTargetPos) drive.
                if (u.CoverSuppressionRemaining > 0f)
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                    continue;
                }

                // Task52 rule 1: the re-evaluation cooldown (CoverUseIntervalHours). No per-tick search =
                // hide only "occasionally". Whether cover is currently set or not, nothing changes until the
                // interval has elapsed (MovementStep resets CoverReevaluateCooldown to 0 on bounding arrival,
                // in which case this falls straight through and the next candidate is searched right away).
                u.CoverReevaluateCooldown -= dt;
                if (u.CoverReevaluateCooldown > 0f) continue;
                u.CoverReevaluateCooldown = CoverUseIntervalHours;

                // Mode 3: advancing (not engaging). Treat the objective direction (the enemy base being
                // advanced on) as the threat; if a candidate makes real progress toward it
                // (MinForwardProgress) and is close by (MaxCoverDetour), stop over at that cover briefly
                // (CoverHold=true, Task52).
                WorldPos objective = u.OrderTargetPos.Value;
                // Task77: as in mode 2, a candidate in water counts as "not found".
                if (state.Cover.TryFindBestCover(u.Position, objective, searchRadius, u.InstanceId, out WorldPos boundCover)
                    && !(water != null && water.IsWater(boundCover.X, boundCover.Z)))
                {
                    float distNow = u.Position.HorizontalDistanceTo(objective);
                    float distAfter = boundCover.HorizontalDistanceTo(objective);
                    float detour = u.Position.HorizontalDistanceTo(boundCover);
                    if (distAfter < distNow - MinForwardProgress && detour <= MaxCoverDetour)
                    {
                        u.CoverDestination = boundCover;
                        u.CoverHold = true;
                        u.CoverHoldTimer = 0f;
                    }
                    else
                    {
                        // Only candidates that do not advance / are too far exist — better to keep the road
                        // advance (Path/OrderTargetPos) going than to mark time or detour for cover.
                        u.CoverDestination = null;
                        u.CoverHold = false;
                    }
                }
                else
                {
                    u.CoverDestination = null;
                    u.CoverHold = false;
                }
            }
        }

        /// <summary>Task52 rule 4: watches whether the distance to OrderTargetPos keeps shrinking. Shrinking
        /// by at least StallEpsilon updates the checkpoint and counts as safe (not stalled). If it fails to
        /// shrink for StallTimeoutHours, CoverSuppressionRemaining is set to CoverSuppressedHours and cover
        /// search is fully suspended for that long (read on the Advance side). With no OrderTargetPos the
        /// watchdog disables itself (checkpoint reset).</summary>
        private static void UpdateStallWatchdog(UnitInstance u, float dt)
        {
            // An active suppression window is consumed first regardless of progress (it "re-enables
            // automatically later").
            if (u.CoverSuppressionRemaining > 0f)
            {
                u.CoverSuppressionRemaining -= dt;
                if (u.CoverSuppressionRemaining < 0f) u.CoverSuppressionRemaining = 0f;
            }

            if (!u.OrderTargetPos.HasValue)
            {
                u.StallTimer = 0f;
                u.LastObjectiveDistance = null;
                return;
            }

            float dist = u.Position.HorizontalDistanceTo(u.OrderTargetPos.Value);

            if (!u.LastObjectiveDistance.HasValue || u.LastObjectiveDistance.Value - dist >= StallEpsilon)
            {
                // First measurement, or enough progress: update the checkpoint and reset the stall timer.
                u.LastObjectiveDistance = dist;
                u.StallTimer = 0f;
                return;
            }

            u.StallTimer += dt;
            if (u.StallTimer >= StallTimeoutHours)
            {
                u.CoverSuppressionRemaining = CoverSuppressedHours;
                u.StallTimer = 0f;
                u.LastObjectiveDistance = dist; // restart the next observation window from here
            }
        }

        /// <summary>Releases CoverDestination/CoverHold plus CoverTargetId (the Task50 engagement lock) and
        /// every timer added in Task52 (CoverHoldTimer/EngageHoldTimer/StallTimer/LastObjectiveDistance/
        /// CoverSuppressionRemaining). Used on every path that "wipes the cover/advance decision clean".
        /// The one exception is "no cover found while engaging", which clears only
        /// CoverDestination/CoverHold directly instead of calling this (CoverTargetId must be kept to
        /// prevent re-searching against the same opponent — see the isEngaging block above).</summary>
        private static void ClearCover(UnitInstance u)
        {
            u.CoverDestination = null;
            u.CoverHold = false;
            u.CoverTargetId = null;
            u.CoverHoldTimer = 0f;
            u.EngageHoldTimer = 0f;
            u.StallTimer = 0f;
            u.LastObjectiveDistance = null;
            u.CoverSuppressionRemaining = 0f;
        }
    }
}
