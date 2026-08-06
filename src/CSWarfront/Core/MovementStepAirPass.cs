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
    ///   4. On arrival, clear the egress point; the next tick re-evaluates the anchor and re-enters.
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
                AdvanceAir(u, stepLen, egress, height);
                if (u.Position.HorizontalDistanceTo(egress) <= AirCombat.PassArrivalDistance)
                    u.AirPassEgress = null;
                return true;
            }

            WorldPos? anchor = ResolveAirCombatAnchor(state, u, type);
            if (!anchor.HasValue) return false;

            float dist = u.Position.HorizontalDistanceTo(anchor.Value);
            if (dist <= AirCombat.PassTriggerDistance)
            {
                // Point blank = "flying over". The egress point lies beyond the anchor along the current
                // heading (self → anchor).
                float dx = anchor.Value.X - u.Position.X;
                float dz = anchor.Value.Z - u.Position.Z;
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-3f) { dx = 1f; dz = 0f; len = 1f; } // degenerate directly-overhead case goes +X (deterministic)
                float ex = anchor.Value.X + dx / len * AirCombat.PassEgressDistance;
                float ez = anchor.Value.Z + dz / len * AirCombat.PassEgressDistance;
                u.AirPassEgress = new WorldPos(ex, 0f, ez); // Y is re-resolved to cruise altitude by AdvanceAir each tick
                AdvanceAir(u, stepLen, u.AirPassEgress.Value, height);
                return true;
            }

            // Approach leg: fly straight at the anchor.
            AdvanceAir(u, stepLen, anchor.Value, height);
            return true;
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
