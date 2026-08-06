using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// One-shot damage to units from threat beam attacks (Godzilla's ray / the tripods' laser) (Task83,
    /// user request "give the Godzilla ray and the tripod laser damage against units").
    ///
    /// The other mods (GodzillaDisaster/AlienInvasion) expose each beam firing as a one-off event via
    /// their firing-record API (CurrentId+Snapshot; see Game/ExternalThreatBridge), and CSWarfront calls
    /// ApplyStrike exactly once per new firing record. It is a single hit rather than sustained DPS
    /// because both mods' beams really are momentary discharges (Godzilla: one purple heat ray while
    /// Rampaging; tripods: single lasers at intervals).
    ///
    /// The damage policy is the same asymmetric design as ThreatAuraStep:
    ///  - ThreatRelations is never consulted (anyone in the beam's path is hit equally, even factions set
    ///    to allied/neutral).
    ///  - Flat damage ignoring armor (UnitType.Armor).
    ///  - Death resolution / KillEvent emission follows the same pattern as the
    ///    ThreatAuraStep/CombatStep death passes.
    ///
    /// The test is 2D (X/Z plane): "within width W of the segment" (round caps at the endpoints).
    /// Godzilla's ray flies horizontally at mouth height, but GodzillaRampage applies its building
    /// destruction to the ground along the path too, so the ground-unit test stays consistently 2D.
    /// </summary>
    public static class ThreatBeamStep
    {
        /// <summary>Effective half-width of Godzilla's ray (the purple heat ray). A beam as thick as
        /// Godzilla's own hit radius (45).</summary>
        public const float KaijuBeamWidth = 40f;

        /// <summary>Godzilla-ray damage (armor-ignoring, one shot). A heat ray equivalent to a 5kt nuke:
        /// designed as an instant-death attack to be avoided — units in the path die outright even at
        /// tier 5 (HP 300+) (on par with the ballistic missiles' ImpactDamageThreat=2000).</summary>
        public const float KaijuBeamDamage = 2000f;

        /// <summary>Effective half-width of the tripod laser. A thin single-shot laser, narrower than
        /// Godzilla's.</summary>
        public const float AlienBeamWidth = 15f;

        /// <summary>Tripod-laser damage (armor-ignoring, one shot). One-shots tier 1–3 (HP ~100–250);
        /// tier 5 survives crippled — "painful but not instant death" (the Alien is the smaller threat,
        /// consistent with ExternalThreatBridge's HP ratios).</summary>
        public const float AlienBeamDamage = 400f;

        /// <summary>Deals the one-shot damage to every unit within the width of the beam segment
        /// (x1,z1)-(x2,z2). Returns the number of units damaged (for the Game layer's log).</summary>
        public static int ApplyStrike(WarState state, ThreatKind kind, float x1, float z1, float x2, float z2)
        {
            float width, damage;
            switch (kind)
            {
                case ThreatKind.Kaiju: width = KaijuBeamWidth; damage = KaijuBeamDamage; break;
                case ThreatKind.Alien: width = AlienBeamWidth; damage = AlienBeamDamage; break;
                default: return 0;
            }

            int hitCount = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (DistanceToSegment(u.Position.X, u.Position.Z, x1, z1, x2, z2) > width) continue;

                u.CurrentHP -= damage;
                hitCount++;
            }

            // Death resolution (the same pattern as the ThreatAuraStep/CombatStep death passes). Units
            // already transitioned to Dead by other steps are skipped naturally by the State != Dead
            // condition, so no KillEvent is double-queued.
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

            return hitCount;
        }

        /// <summary>2D (X/Z plane) distance from the point (px,pz) to the segment (x1,z1)-(x2,z2).
        /// The degenerate start==end case is the distance to the point.</summary>
        private static float DistanceToSegment(float px, float pz, float x1, float z1, float x2, float z2)
        {
            float dx = x2 - x1, dz = z2 - z1;
            float lenSq = dx * dx + dz * dz;
            float t;
            if (lenSq < 1e-6f)
            {
                t = 0f; // degenerate: distance to the start point
            }
            else
            {
                t = ((px - x1) * dx + (pz - z1) * dz) / lenSq;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;
            }
            float cx = x1 + dx * t, cz = z1 + dz * t;
            float ex = px - cx, ez = pz - cz;
            return (float)Math.Sqrt(ex * ex + ez * ez);
        }
    }
}
