using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// External invasion events (Task94; Workshop comment request: "an option where enemy units spawn from
    /// outside the city at random times and attack — instead of hand-building enemy bases, you build your
    /// own bases and defend the city").
    ///
    /// While the Options toggle (Game-layer WarfrontSettings.InvasionEventsEnabled) is on, an invasion roll
    /// (deterministic hash) runs every CheckIntervalHours; on a win, a raiding force spawns at a random
    /// point (on land) at the map edge.
    ///
    /// Task95 (playtest feedback): the force belongs to the dedicated "Invader" faction
    /// (Faction.InvaderFactionId, moss green). The original design reused "the existing faction owning the
    /// fewest bases" as the invader, which had two real problems: (1) the player's faction (Blue etc.)
    /// could suddenly become the invader, which was confusing, and (2) a faction with zero bases gets
    /// Eliminated by FactionStatus.Refresh every tick → excluded from AI advances → frozen at the spawn
    /// point. The Invader is hard-coded permanently Hostile in the relation tables
    /// (RelationMatrix/ThreatRelations) and exempt from the Eliminated check (FactionStatus), so after
    /// spawning, the ordinary AI (InvasionOrders.AssignAdvance) marches it against the nearest base (the
    /// military bases built in the city) as-is. No dedicated attack logic is needed.
    ///
    /// Wave size and tier track the defenders' highest unlocked tier (raids grow stronger as the game
    /// progresses). No RNG (deterministic hashes of TickCounter and a serial number — the same fmix32 as
    /// AntiAirCombat.RollHit).
    /// </summary>
    public static class InvasionEvents
    {
        /// <summary>Interval between invasion rolls (in-game hours).</summary>
        public const float CheckIntervalHours = 6f;

        /// <summary>Win probability per roll for each frequency setting (Options: Low/Medium/High).
        /// Expectation: Low ≈ once per 5 days, Medium ≈ once per 2.5 days, High ≈ once per 1.2 days.</summary>
        public static readonly float[] ChancePerCheck = { 0.05f, 0.10f, 0.21f };

        /// <summary>Distance of the spawn point from the map center (along an edge). Inside the
        /// SeaGrid/playable bounds.</summary>
        public const float SpawnEdgeDistance = 4300f;

        /// <summary>Retry count and per-step distance for nudging a spawn candidate inland when it lands
        /// in water.</summary>
        private const int LandSearchSteps = 8;
        private const float LandSearchStepDistance = 300f;

        /// <summary>Advances the invasion roll by one tick. When a spawn happens, returns the number of
        /// spawned units (used by the Game layer for the notification toast). 0 = nothing happened.</summary>
        public static int Advance(WarState state, float dt, bool enabled, int frequencyIndex)
        {
            if (!enabled) return 0;

            state.InvasionCheckAccum += dt;
            if (state.InvasionCheckAccum < CheckIntervalHours) return 0;
            state.InvasionCheckAccum -= CheckIntervalHours;

            if (frequencyIndex < 0) frequencyIndex = 0;
            if (frequencyIndex >= ChancePerCheck.Length) frequencyIndex = ChancePerCheck.Length - 1;

            float roll = Hash01(state.TickCounter, 0xA11ACEu);
            if (roll >= ChancePerCheck[frequencyIndex]) return 0;

            return SpawnWave(state);
        }

        /// <summary>Adds the Invader faction (Faction.InvaderFactionId) to State.Factions if absent. An
        /// idempotent helper called both when a new state is created (Game-layer MilitaryManager) and on
        /// save load (WarStateSerializer.Deserialize — saves predating the Invader hold only five
        /// factions).</summary>
        public static Faction EnsureInvaderFaction(WarState state)
        {
            Faction invader = state.FindFaction(Faction.InvaderFactionId);
            if (invader == null)
            {
                invader = new Faction(Faction.InvaderFactionId, "Invader");
                state.Factions.Add(invader);
            }
            return invader;
        }

        /// <summary>Spawns one raiding force (also callable directly from tests). Returns the number of
        /// spawned units. 0 when no defender (base-owning faction) exists or no land spawn point can be
        /// found.</summary>
        public static int SpawnWave(WarState state)
        {
            // Defenders = factions owning at least one base. With none, there is nothing to attack.
            var defenders = new List<byte>();
            int[] baseCounts = new int[Faction.InvaderFactionId];
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId == null) continue;
                byte owner = b.OwnerFactionId.Value;
                if (owner < baseCounts.Length) baseCounts[owner]++;
            }
            for (int f = 0; f < baseCounts.Length; f++)
            {
                Faction df = state.FindFaction((byte)f);
                if (df != null && baseCounts[f] > 0) defenders.Add(df.Id);
            }
            if (defenders.Count == 0) return 0;

            // The attacker = the dedicated Invader faction (Task95). Its relations need no setup here (and
            // cannot be set) — RelationMatrix/ThreatRelations return hard-coded permanent hostility.
            Faction attacker = EnsureInvaderFaction(state);

            // Spawn point: a deterministic random point on one map edge; nudged inland to find land when in
            // water.
            WorldPos? spawn = FindLandSpawnPoint(state);
            if (!spawn.HasValue) return 0;

            // Wave tier = the defenders' highest unlocked tier (tracks game progression).
            byte tier = 1;
            for (int d = 0; d < defenders.Count; d++)
            {
                Faction df = state.FindFaction(defenders[d]);
                if (df != null && df.UnlockedTier > tier) tier = df.UnlockedTier;
            }
            // Keep the Invader faction's unlocked tier in step with the wave tier (so anything reading it,
            // e.g. AiThreatAssessment, stays consistent with the troops actually spawning).
            if (attacker.UnlockedTier < tier) attacker.UnlockedTier = tier;

            // Composition: a combined-arms raiding force (more tanks at higher tiers).
            var composition = new List<UnitCategory>
            {
                UnitCategory.Tank, UnitCategory.Tank,
                UnitCategory.MechInfantry, UnitCategory.MechInfantry,
                UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.AntiAir
            };
            for (int extra = 1; extra < tier; extra++) composition.Add(UnitCategory.Tank);

            int spawned = 0;
            spawned += SpawnAirEscort(state, attacker, tier, spawn.Value);
            for (int i = 0; i < composition.Count; i++)
            {
                string key = LandUnitRoster.TypeKey(composition[i], tier);
                UnitType type = state.Types.Get(key);
                if (type == null) continue;

                // Scatter with small deterministic offsets so they do not stack.
                float ox = (Hash01(state.TickCounter, (uint)(i * 2 + 1)) - 0.5f) * 60f;
                float oz = (Hash01(state.TickCounter, (uint)(i * 2 + 2)) - 0.5f) * 60f;
                var u = new UnitInstance(state.AllocInstanceId(), key, attacker.Id, type.MaxHP,
                    new WorldPos(spawn.Value.X + ox, spawn.Value.Y, spawn.Value.Z + oz));
                u.State = UnitState.Moving; // the next AssignAdvance assigns a target base and a route
                state.Units.Add(u);
                spawned++;
            }
            return spawned;
        }

        /// <summary>Task145 (Workshop request from siddyskylines1989: "the invasion forces should also
        /// spawn with a couple of aerial units"): the air component of a raiding force.
        ///
        /// A gunship and a bomber, plus a fighter escort once the defenders have the tier to field
        /// interceptors of their own - by then an unescorted bomber is just a delivery of free kills. They
        /// spawn at cruise altitude rather than on the deck, because a wave that begins by climbing from
        /// the map edge spends its first minutes as target practice for the AA the wave also brought.
        ///
        /// Separate from the ground composition because air units come from a different roster and need a
        /// height; the wave tier and spawn point are shared, so they arrive together.</summary>
        private static int SpawnAirEscort(WarState state, Faction attacker, byte tier, WorldPos spawn)
        {
            var air = new List<UnitCategory>
            {
                UnitCategory.AttackHelicopter,
                UnitCategory.TacticalBomber
            };
            if (tier >= 3) air.Add(UnitCategory.AirSuperiority);

            int spawned = 0;
            for (int i = 0; i < air.Count; i++)
            {
                string key = AirUnitRoster.TypeKey(air[i], tier);
                UnitType type = state.Types.Get(key);
                if (type == null) continue; // air roster not registered (some tests): the wave is ground-only

                float ox = (Hash01(state.TickCounter, (uint)(100 + i * 2)) - 0.5f) * 120f;
                float oz = (Hash01(state.TickCounter, (uint)(101 + i * 2)) - 0.5f) * 120f;
                float altitude = TargetingRules.IsHelicopter(air[i])
                    ? MovementStep.HeliCruiseAltitude : MovementStep.CruiseAltitude;

                var u = new UnitInstance(state.AllocInstanceId(), key, attacker.Id, type.MaxHP,
                    new WorldPos(spawn.X + ox, spawn.Y + altitude, spawn.Z + oz));
                u.State = UnitState.Moving; // the next AssignAdvance gives it an objective
                state.Units.Add(u);
                spawned++;
            }
            return spawned;
        }

        /// <summary>From a deterministic random point on a map edge, searches inland for land as needed.
        /// water==null (tests etc.) counts everything as land. Null when nothing is found (this tick's
        /// invasion fails to materialize).</summary>
        private static WorldPos? FindLandSpawnPoint(WarState state)
        {
            int side = (int)(Hash01(state.TickCounter, 0xED6Eu) * 4f) & 3;
            float t = (Hash01(state.TickCounter, 0x5EEDu) * 2f - 1f) * SpawnEdgeDistance;

            float x, z, ix, iz; // position on the edge, and the unit direction pointing into the map
            switch (side)
            {
                case 0: x = t; z = SpawnEdgeDistance; ix = 0f; iz = -1f; break;   // north edge
                case 1: x = t; z = -SpawnEdgeDistance; ix = 0f; iz = 1f; break;   // south edge
                case 2: x = SpawnEdgeDistance; z = t; ix = -1f; iz = 0f; break;   // east edge
                default: x = -SpawnEdgeDistance; z = t; ix = 1f; iz = 0f; break;  // west edge
            }

            IWaterSampler water = state.Water;
            IHeightSampler height = state.Height;
            for (int step = 0; step <= LandSearchSteps; step++)
            {
                float cx = x + ix * step * LandSearchStepDistance;
                float cz = z + iz * step * LandSearchStepDistance;
                if (water != null && water.IsWater(cx, cz)) continue; // never drop land forces on the sea

                float y = 0f;
                float h;
                if (height != null && height.TrySampleHeight(cx, cz, out h)) y = h;
                return new WorldPos(cx, y, cz);
            }
            return null;
        }

        /// <summary>Deterministic [0,1) hash based on fmix32 (the same technique as
        /// AntiAirCombat.RollHit).</summary>
        private static float Hash01(uint a, uint b)
        {
            unchecked
            {
                uint h = a * 2654435761u + b;
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
