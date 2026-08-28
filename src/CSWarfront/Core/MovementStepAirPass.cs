using System;

namespace CSWarfront.Core
{
    /// <summary>Continuation of MovementStep (Task86: air units' engagement pass movement). Split out to
    /// stay within the 500-line-per-file limit (the same partial-class pattern as
    /// MovementStepSea/MovementStepKamikaze).
    ///
    /// Realizes the user request "bombers drop and hit-and-run; fighters dogfight in crossing passes
    /// without stopping" with the following racetrack pass:
    ///   1. When an engagement anchor (ResolveAirCombatAnchor below) is found, approach it.
    ///   2. On closing to AirCombat.PassTriggerDistance, arm an egress point
    ///      (UnitInstance.AirPassEgress) AirCombat.PassEgressDistance beyond the anchor along the current
    ///      heading.
    ///   3. Fly all the way to the egress point (the anchor is not re-evaluated meanwhile = the leg is
    ///      completed even if the target dies or leaves range, preventing jittery reversals at the range
    ///      boundary).
    ///   4. On arrival, clear the egress point; the next tick re-evaluates the anchor and turns back in.
    /// Task154: the turn back is flown, not snapped - the flight model (MovementStepAirFlight) limits how
    /// hard the aircraft can bank, so re-entry sweeps round on an arc and each run comes in on a fresh
    /// bearing. The egress point is taken along the aircraft's own heading for the same reason: one that
    /// has already slid past its anchor must not turn round to fly its egress backwards.
    /// Damage still applies only while in range (CombatStep etc.), so this movement alone produces
    /// "bombs/guns land during the fly-over; nothing fires during the egress". The reduced in-range time
    /// is compensated by AirCombat.DamageMultiplier.
    ///
    /// In an all-air dogfight both sides repeat approach-anchor → fly-past → egress → turn-in, which
    /// naturally becomes crossing passes (firing as they slide past each other).
    /// </summary>
    public static partial class MovementStep
    {
        /// <summary>Advances an air unit's engagement pass by one tick. Returns true when pass movement
        /// happened (the caller skips the normal AdvanceAir). False when there is no engagement anchor
        /// and no egress leg in progress (fall through to normal objective movement). Non-kamikaze
        /// Domain.Air only.</summary>
        private static bool AdvanceAirPass(WarState state, UnitInstance u, UnitType type, float stepLen,
            IHeightSampler height)
        {
            // Task101: helicopters hover (no racetrack passes — fall back to the normal AdvanceAir
            // cruise, i.e. approach and stay in range).
            if (TargetingRules.IsHelicopter(type.Category)) return false;

            // Egress leg: fly all the way to the egress point regardless of the anchor's life or range
            // (item 3 of the class comment).
            if (u.AirPassEgress.HasValue)
            {
                WorldPos egress = u.AirPassEgress.Value;
                AdvanceAir(u, type, stepLen, egress, height);
                if (u.Position.HorizontalDistanceTo(egress) <= AirCombat.PassArrivalDistance)
                    u.AirPassEgress = null;
                return true;
            }

            WorldPos? anchor = ResolveAirCombatAnchor(state, u, type);
            if (!anchor.HasValue) return false;

            float dist = u.Position.HorizontalDistanceTo(anchor.Value);
            if (dist <= AirCombat.PassTriggerDistance || HasFlownPast(u, anchor.Value, dist))
            {
                u.AirPassEgress = ResolveEgressPoint(u, anchor.Value, dist);
                AdvanceAir(u, type, stepLen, u.AirPassEgress.Value, height);
                return true;
            }

            // Approach leg: turn in toward the anchor.
            AdvanceAir(u, type, stepLen, anchor.Value, height);
            return true;
        }

        /// <summary>Task154: the run is over once the anchor is behind the wing. Before the flight model
        /// gave aircraft a turn radius they could always fly exactly at the anchor and trip
        /// PassTriggerDistance; now a hard turn-in can carry them past it by a hundred metres, and without
        /// this they would keep wheeling around trying to hit a forty-metre circle. Only counts close in
        /// (PassAbeamDistance), so the outbound half of a turn is never mistaken for a completed run.</summary>
        private static bool HasFlownPast(UnitInstance u, WorldPos anchor, float dist)
        {
            if (!u.AirHeading.HasValue) return false;
            if (dist > AirCombat.PassAbeamDistance) return false;
            float hx = (float)Math.Sin(u.AirHeading.Value);
            float hz = (float)Math.Cos(u.AirHeading.Value);
            return hx * (anchor.X - u.Position.X) + hz * (anchor.Z - u.Position.Z) < 0f;
        }

        /// <summary>Task154: where the egress leg ends - straight ahead, PassEgressDistance past the
        /// anchor. Taken along the aircraft's own heading rather than the line to the anchor, so an
        /// aircraft that has already slid past does not turn round to fly its egress backwards.</summary>
        private static WorldPos ResolveEgressPoint(UnitInstance u, WorldPos anchor, float dist)
        {
            float dx, dz;
            if (u.AirHeading.HasValue)
            {
                dx = (float)Math.Sin(u.AirHeading.Value);
                dz = (float)Math.Cos(u.AirHeading.Value);
            }
            else
            {
                dx = anchor.X - u.Position.X;
                dz = anchor.Z - u.Position.Z;
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-3f) { dx = 1f; dz = 0f; } // degenerate directly-overhead case goes +X (deterministic)
                else { dx /= len; dz /= len; }
            }

            float reach = dist + AirCombat.PassEgressDistance;
            // Y is re-resolved to cruise altitude by AdvanceAir each tick.
            return new WorldPos(u.Position.X + dx * reach, 0f, u.Position.Z + dz * reach);
        }

        /// <summary>The position this air unit should currently be flying passes against. Priority:
        ///   1. the enemy unit CombatStep has locked (TargetId, only while alive)
        ///   2. the nearest hostile threat within range (+Radius) (the same hostility test and effective
        ///      range as ThreatCombatStep)
        ///   3. the nearest hostile base in range (the same rules as BaseCombatStep; honoring Task85's
        ///      CanAttackBase = fighters never anchor on bases)
        /// Null when none exists (normal objective movement).</summary>
        private static WorldPos? ResolveAirCombatAnchor(WarState state, UnitInstance u, UnitType type)
        {
            if (u.TargetId.HasValue)
            {
                UnitInstance target = state.FindUnit(u.TargetId.Value);
                if (target != null && target.IsAlive) return target.Position;
            }

            if (TargetingRules.CanAttackThreat(type.Category))
            {
                ExternalThreat bestThreat = null;
                float bestDist = float.MaxValue;
                for (int j = 0; j < state.Threats.Count; j++)
                {
                    ExternalThreat threat = state.Threats[j];
                    if (threat.IsDefeated) continue;
                    if (!state.ThreatRelations.Get(u.FactionId, threat.Kind).IsHostile()) continue;
                    float d = u.Position.HorizontalDistanceTo(threat.Position);
                    if (d > type.Range + threat.Radius) continue; // the same effective range as ThreatCombatStep
                    if (d < bestDist) { bestDist = d; bestThreat = threat; }
                }
                if (bestThreat != null) return bestThreat.Position;
            }

            if (TargetingRules.CanAttackBase(type.Category))
            {
                MilitaryBase bestBase = null;
                float bestDist = float.MaxValue;
                for (int j = 0; j < state.Bases.Count; j++)
                {
                    MilitaryBase b = state.Bases[j];
                    if (b.CaptureGraceHours > 0f) continue; // invulnerable during grace = not a target (as in BaseCombatStep)
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) continue;
                    // Task88: bases at this attacker's HP floor (air = 1) are no longer pass anchors
                    // (paired with BaseCombatStep's stop-firing rule, preventing bombers from circling a
                    // base stuck at 1 HP).
                    if (b.CurrentHP <= TargetingRules.BaseHpFloor(type.Domain)) continue;
                    float d = u.Position.HorizontalDistanceTo(b.Position);
                    if (d > type.Range) continue;
                    if (d < bestDist) { bestDist = d; bestBase = b; }
                }
                if (bestBase != null) return bestBase.Position;
            }

            return null;
        }
    }
}
