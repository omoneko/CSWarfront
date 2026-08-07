namespace CSWarfront.Core
{
    /// <summary>
    /// The shared helper consolidating muzzle-effect (ShotEvent) throttling (the
    /// UnitInstance.FireCooldown accumulator) in one place (Task58). CombatStep (vs units),
    /// BaseCombatStep (vs bases) and ThreatCombatStep (vs external threats = Godzilla/Alien) share
    /// exactly the same contract: "only on ticks where damage actually applied, subtract dt from
    /// FireCooldown; at the instant it reaches zero or below, queue exactly one ShotEvent and reset
    /// to UnitType.FireIntervalHours" (no RNG, deterministic).
    /// Never participates in the damage computation itself (see the comment atop ShotEvent.cs).
    /// </summary>
    public static class FireEffects
    {
        /// <summary>Call on the tick attacker dealt damage toward targetPos (targetId). targetId is
        /// the target's InstanceId when it is a unit, or 0 for targets with no logical unit (bases,
        /// external threats — the same existing contract as ShotEvent.TargetId).</summary>
        public static void EmitThrottled(WarState state, UnitInstance attacker, UnitType attackerType,
            WorldPos targetPos, uint targetId, float dt)
        {
            attacker.FireCooldown -= dt;
            if (attacker.FireCooldown <= 0f)
            {
                state.AddShot(new ShotEvent(attacker.Position, targetPos, attackerType.ShotKind, attacker.FactionId,
                    attacker.InstanceId, targetId, attackerType.Category));
                attacker.FireCooldown = attackerType.FireIntervalHours;
            }
        }
    }
}
