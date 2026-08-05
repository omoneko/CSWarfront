namespace CSWarfront.Core
{
    /// <summary>Result of BallisticMissiles.TryLaunch (Task63; the same enum-style API as
    /// ManualProduction.QueueResult).</summary>
    public enum LaunchResult
    {
        Ok,
        BaseNotFound,
        NotMissileBase,
        NoOwner,
        /// <summary>StockpiledMissiles is 0 (nothing to launch).</summary>
        NoStockpile,
        /// <summary>The target exceeds MissileStep.MaxRangeMeters (horizontal distance from the base).</summary>
        OutOfRange
    }

    /// <summary>One ballistic missile in flight (Task63). No UnityEngine dependency. The Game layer
    /// (MissileVisuals) draws the visual ballistic arc from From/To/Progress; the core deals only with the
    /// abstraction "trace the From→To line by Progress (0..1)" and leaves the visual trajectory
    /// (high-altitude arch etc.) to Game-layer presentation.</summary>
    public class MissileInFlight
    {
        public uint Id;
        public byte FactionId;
        public WorldPos From;
        public WorldPos To;
        /// <summary>Flight progress (0..1). Impact resolves the moment it reaches 1.</summary>
        public float Progress;
        /// <summary>Whether interception succeeded. A missile set to true is removed from the list
        /// immediately inside MissileStep.Advance, so externally this is only observable via RecentImpacts
        /// (MissileImpactEvent.Intercepted=true); the field itself remains for future debugging/tests.</summary>
        public bool Intercepted;
    }

    /// <summary>Lightweight event describing a missile impact/interception (Task63). Same design as
    /// ShotEvent/KillEvent: a throttled, presentation-only transient event with zero influence on damage
    /// computation. Intercepted=false is an impact (big explosion display); Intercepted=true is an
    /// interception (flash only, no damage).</summary>
    public struct MissileImpactEvent
    {
        public WorldPos Position;
        public bool Intercepted;
        public byte FactionId;

        public MissileImpactEvent(WorldPos position, bool intercepted, byte factionId)
        {
            Position = position; Intercepted = intercepted; FactionId = factionId;
        }
    }

    /// <summary>
    /// Ballistic missile launch, flight, interception and impact (Task63, block 6 MVP). The
    /// "altitude band + horizontal range + Pk" interception model of the Missile Disaster mod
    /// (MissileDisaster.Core.InterceptDecision/InterceptorTier) translated into CSWarfront's deterministic
    /// simulation policy (no RNG; seed hashes only). Difference from MissileDisaster: a simplified version
    /// judged only by the single AntiAir category × horizontal range instead of multiple interception
    /// layers with altitude bands (MVP scope; conventional warheads only — no nuclear/cluster).
    /// No UnityEngine dependency. Deterministic (no RNG, hashes only).
    /// </summary>
    public static class MissileStep
    {
        /// <summary>Flight time from launch to impact (in-game hours, fixed).</summary>
        public const float FlightHours = 1.5f;

        /// <summary>Maximum horizontal distance from the launching base to the target; TryLaunch fails
        /// beyond it.</summary>
        public const float MaxRangeMeters = 4000f;

        /// <summary>Horizontal distance within which interception is possible (between an AntiAir unit and
        /// the missile's ground-track position).</summary>
        public const float InterceptRange = 300f;

        /// <summary>Interception probability per in-game hour. The per-tick probability is this times dt
        /// (at most one roll per AntiAir unit per tick).</summary>
        public const float InterceptRatePerHour = 0.9f;

        /// <summary>Radius (horizontal) of impact damage.</summary>
        public const float ImpactRadius = 90f;

        /// <summary>Impact damage to units (armor-ignoring, fixed).</summary>
        public const float ImpactDamageUnit = 400f;

        /// <summary>Base HP reduction on impact (fixed). Bases in capture grace are unharmed (the same
        /// policy as BaseCombatStep).</summary>
        public const float ImpactDamageBase = 300f;

        /// <summary>Impact damage to external threats (KAIJU/Alien) (fixed).
        /// Task64: raised 600→2000 to match the tier-5 rebalance of threat HP (Godzilla 20000→65000,
        /// Alien 8000→26000, Core/ThreatCombatStep.ThreatArmor 20→45). Even a full stockpile of five is
        /// 2000*5=10000 (≈15% of Godzilla, ≈38% of Alien), preserving missiles as "a blow that supplements
        /// your forces" — neither single shots nor volleys can kill a threat alone (derivation details in
        /// the ThreatCombatStep.ThreatArmor comment and the task-64 report).</summary>
        public const float ImpactDamageThreat = 2000f;

        /// <summary>
        /// Launches one missile from the base with baseId at target. Check order (the first failure wins):
        ///  1. does the base exist -&gt; BaseNotFound
        ///  2. is b.Type BaseType.MissileBase -&gt; NotMissileBase
        ///  3. does it have an owner -&gt; NoOwner
        ///  4. StockpiledMissiles &gt; 0 -&gt; NoStockpile
        ///  5. horizontal distance from base to target within MaxRangeMeters -&gt; OutOfRange
        /// If all pass, one StockpiledMissiles is consumed, a new flight is added to
        /// WarState.MissilesInFlight, and Ok is returned.
        /// </summary>
        public static LaunchResult TryLaunch(WarState state, ushort baseId, WorldPos target)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return LaunchResult.BaseNotFound;
            if (b.Type != BaseType.MissileBase) return LaunchResult.NotMissileBase;
            if (b.OwnerFactionId == null) return LaunchResult.NoOwner;
            if (b.StockpiledMissiles <= 0) return LaunchResult.NoStockpile;

            float dist = b.Position.HorizontalDistanceTo(target);
            if (dist > MaxRangeMeters) return LaunchResult.OutOfRange;

            b.StockpiledMissiles--;
            var missile = new MissileInFlight
            {
                Id = state.AllocMissileId(),
                FactionId = b.OwnerFactionId.Value,
                From = b.Position,
                To = target,
                Progress = 0f,
                Intercepted = false
            };
            state.MissilesInFlight.Add(missile);
            return LaunchResult.Ok;
        }

        /// <summary>
        /// Advances every missile in flight by one tick (sim thread, via MilitaryManager.OnSimTick; call
        /// after ThreatCombatStep and before the economy tick). Missiles that impacted or were intercepted
        /// are removed from WarState.MissilesInFlight within this call, and the corresponding
        /// MissileImpactEvents are queued to WarState.RecentImpacts. The caller must have cleared
        /// RecentImpacts before this Advance (the same "caller clears at the top of every tick" contract
        /// as RecentShots/RecentKills).
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            state.TickCounter++;

            for (int i = state.MissilesInFlight.Count - 1; i >= 0; i--)
            {
                MissileInFlight m = state.MissilesInFlight[i];

                m.Progress += FlightHours > 0f ? dt / FlightHours : 1f;
                if (m.Progress >= 1f)
                {
                    m.Progress = 1f;
                    ResolveImpact(state, m);
                    state.MissilesInFlight.RemoveAt(i);
                    continue;
                }

                // Interception applies only to the descending half (Progress > 0.5). Boost/midcourse are
                // exempt — the simplification that translates MissileDisaster's altitude-band model into
                // AntiAir horizontal range only (Task63 spec).
                if (m.Progress > 0.5f && TryIntercept(state, m, dt))
                {
                    state.AddImpact(new MissileImpactEvent(GroundTrackPos(m), true, m.FactionId));
                    state.MissilesInFlight.RemoveAt(i);
                }
            }
        }

        /// <summary>The missile's current position linearly interpolated from From by m.Progress (ground
        /// track; the visual arc is Game-layer presentation — the core deals in straight interpolation
        /// only).</summary>
        private static WorldPos GroundTrackPos(MissileInFlight m)
        {
            float t = m.Progress;
            return new WorldPos(
                m.From.X + (m.To.X - m.From.X) * t,
                m.From.Y + (m.To.Y - m.From.Y) * t,
                m.From.Z + (m.To.Z - m.From.Z) * t);
        }

        /// <summary>Each defending AntiAir unit (belonging to a faction hostile to the launcher —
        /// Hostile/Nemesis) within InterceptRange of the missile's current ground-track position attempts
        /// an interception with a roll derived from a deterministic hash. Returns true as soon as any one
        /// succeeds (even with several AA units simultaneously in effective range, one successful
        /// interception suffices = no further rolls).</summary>
        private static bool TryIntercept(WarState state, MissileInFlight m, float dt)
        {
            WorldPos pos = GroundTrackPos(m);
            float chance = InterceptRatePerHour * dt;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.AntiAir) continue;

                if (!state.Relations.Get(m.FactionId, u.FactionId).IsHostile()) continue;

                float d = pos.HorizontalDistanceTo(u.Position);
                if (d > InterceptRange) continue;

                uint seed = HashSeed(m.Id, u.InstanceId, state.TickCounter);
                float roll = (seed % 1000000u) / 1000000f; // deterministic pseudo-random 0..1
                if (roll < chance) return true;
            }
            return false;
        }

        /// <summary>Impact resolution: damages units/bases/external threats within ImpactRadius.
        /// Task64 (friendly-fire exclusion): the launching faction itself, and factions Relation.Allied to
        /// it, take no damage to units/bases (RelationMatrix always holds self-relations as Allied, so
        /// "the launcher itself" is automatically covered as a special case of the Allied test — no
        /// separate branch needed). Neutral factions still take damage (collateral damage is deliberate:
        /// the missile just sweeps the aimed area and extends no courtesy to neutrals). External threats
        /// (KAIJU/Alien) are nobody's ally and always take damage regardless of faction (the Threats loop
        /// below has no Allied test). Bases in capture grace (CaptureGraceHours&gt;0) are unharmed (the
        /// same grace protection as BaseCombatStep).</summary>
        private static void ResolveImpact(WarState state, MissileInFlight m)
        {
            WorldPos pos = m.To;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (pos.HorizontalDistanceTo(u.Position) > ImpactRadius) continue;
                if (state.Relations.Get(m.FactionId, u.FactionId) == Relation.Allied) continue; // own/allied unharmed (Task64)

                u.CurrentHP -= ImpactDamageUnit;
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.CurrentHP = 0f;
                    u.State = UnitState.Dead;
                    UnitType uType = state.Types.Get(u.TypeKey);
                    UnitCategory category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (!FortificationRules.IsTargetable(b.Type)) continue; // Task101: trenches cannot be attacked
                if (b.OwnerFactionId == null) continue;
                if (b.CaptureGraceHours > 0f) continue; // invulnerable during grace (same policy as BaseCombatStep)
                if (pos.HorizontalDistanceTo(b.Position) > ImpactRadius) continue;
                if (state.Relations.Get(m.FactionId, b.OwnerFactionId.Value) == Relation.Allied) continue; // own/allied bases unharmed (Task64)

                b.CurrentHP -= ImpactDamageBase;
                // Task89 (user request "ballistic missiles should also only grind bases down to HP 1, like
                // air strikes"): missiles cannot reach capture (HP 0). Same idea as TargetingRules.
                // BaseHpFloor's non-land floor — the final point must be taken by ground forces.
                if (b.CurrentHP < 1f) b.CurrentHP = 1f;
            }

            for (int k = 0; k < state.Threats.Count; k++)
            {
                ExternalThreat t = state.Threats[k];
                if (t.IsDefeated) continue;
                if (pos.HorizontalDistanceTo(t.Position) > ImpactRadius) continue;

                t.CurrentHP -= ImpactDamageThreat;
                if (t.CurrentHP < 0f) t.CurrentHP = 0f;
            }

            state.AddImpact(new MissileImpactEvent(pos, false, m.FactionId));
        }

        /// <summary>Deterministic combination of the integer hash inputs (the same technique as
        /// AiProductionPolicy.MakeSeed: mix missileId/unitId/tick with a near-prime constant, then run
        /// through the finalizer).</summary>
        private static uint HashSeed(uint missileId, uint unitId, uint tick)
        {
            unchecked
            {
                uint h = missileId;
                h = h * 2654435761u + unitId;
                h = h * 2654435761u + tick;
                return Hash(h);
            }
        }

        /// <summary>Deterministic integer hash (MurmurHash3 finalizer equivalent; the same technique as
        /// AiProductionPolicy.Hash).</summary>
        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
