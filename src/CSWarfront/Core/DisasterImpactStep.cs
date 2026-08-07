namespace CSWarfront.Core
{
    /// <summary>
    /// One-shot unit damage from impacts of the MissileDisaster MOD (the disaster missiles the player
    /// fires) — Task94, the Workshop-comment fix for "units don't die to the missile disaster".
    ///
    /// MissileDisaster publishes impacts as loosely-coupled beacons (ImpactBeacon: coordinates +
    /// destruction radius + burn radius + nuclear flag); the Game layer's DisasterImpactBridge calls
    /// this ApplyImpact once per new beacon.
    ///
    /// The damage policy matches ThreatBeamStep (the Godzilla beam):
    ///  - Never consults factions or ThreatRelations (a disaster missile is nobody's ally).
    ///  - A fixed armor-ignoring one-shot. Death resolution and KillEvent emission follow the same
    ///    pattern.
    ///  - Two concentric rings: heavy damage inside the destruction radius (the building-collapse
    ///    zone), medium damage inside the burn radius (outside it). For nukes the destruction zone is
    ///    instant-death grade (no survival allowed) and the burn zone (thermal flash) is
    ///    critical-injury grade.
    /// </summary>
    public static class DisasterImpactStep
    {
        /// <summary>Conventional warhead: damage inside the destruction radius (enough to one-shot a
        /// tier-5-grade 336 HP).</summary>
        public const float ConventionalDestructionDamage = 500f;

        /// <summary>Conventional warhead: damage inside the burn radius (outside the destruction
        /// zone).</summary>
        public const float ConventionalBurnDamage = 120f;

        /// <summary>Nuclear warhead: damage inside the destruction radius (the 5psi zone). A value
        /// that reliably kills any unit outright.</summary>
        public const float NuclearDestructionDamage = 1000000f;

        /// <summary>Nuclear warhead: damage inside the thermal-flash radius (outside the destruction
        /// zone).</summary>
        public const float NuclearBurnDamage = 500f;

        /// <summary>Applies one impact's damage. Returns the number of units damaged (for logging).</summary>
        public static int ApplyImpact(WarState state, float x, float z, float destructionRadius, float burnRadius,
            bool isNuclear)
        {
            float destructionDamage = isNuclear ? NuclearDestructionDamage : ConventionalDestructionDamage;
            float burnDamage = isNuclear ? NuclearBurnDamage : ConventionalBurnDamage;
            float outerRadius = burnRadius > destructionRadius ? burnRadius : destructionRadius;

            int hit = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                float dx = u.Position.X - x, dz = u.Position.Z - z;
                float distSq = dx * dx + dz * dz;
                if (distSq > outerRadius * outerRadius) continue;

                u.CurrentHP -= distSq <= destructionRadius * destructionRadius ? destructionDamage : burnDamage;
                hit++;
            }

            // Death resolution (the same pattern as ThreatBeamStep/ThreatAuraStep; units already Dead
            // from another step are never queued twice).
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.State = UnitState.Dead;
                    UnitType uType = state.Types.Get(u.TypeKey);
                    UnitCategory category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }

            return hit;
        }
    }
}
