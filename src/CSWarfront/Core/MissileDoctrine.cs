namespace CSWarfront.Core
{
    /// <summary>
    /// The AI factions' automatic ballistic-missile launch doctrine (Task63). Kept as a small helper
    /// separate from ProductionPlanning/AiProductionPolicy (its decisions differ in timing and subject
    /// from production planning).
    ///
    /// Doctrine: any friendly missile base with at least one missile stockpiled and its cooldown
    /// elapsed will
    ///  1. If a base owned by a Nemesis-relation faction, or a Nemesis-relation external threat
    ///     (KAIJU/Alien), exists, fire at the nearest of them with no distance limit (the same
    ///     philosophy as the nemesis-first logic in AiTargeting.ChooseTargetBase/InvasionOrders:
    ///     nemeses come first at any distance).
    ///  2. With no nemesis, target the nearest regular Hostile-owned base farther than
    ///     MinLaunchDistance (close range is conventional forces' job; missiles are long-range only,
    ///     by design).
    ///  3. With neither, hold fire.
    /// Deterministic (no RNG). No UnityEngine dependency.
    /// </summary>
    public static class MissileDoctrine
    {
        /// <summary>Regular Hostile bases closer than this are never missile targets (nemeses are the
        /// exception — no distance limit).</summary>
        public const float MinLaunchDistance = 800f;

        /// <summary>The launch cooldown per base (in-game hours).</summary>
        public const float LaunchCooldownHours = 12f;

        /// <summary>
        /// Runs down every missile base's cooldown and auto-launches from bases meeting the conditions
        /// (sim thread; either side of MissileStep.Advance works, but MilitaryManager.OnSimTick is
        /// expected to call it at production-planning-like timing = near the AI advance orders). Bases
        /// of player factions (Faction.IsPlayer) are excluded (players launch manually via the UI,
        /// the Part 1 spec).
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.MissileBase) continue;

                if (b.MissileLaunchCooldownRemaining > 0f)
                {
                    b.MissileLaunchCooldownRemaining -= dt;
                    if (b.MissileLaunchCooldownRemaining < 0f) b.MissileLaunchCooldownRemaining = 0f;
                }

                if (b.OwnerFactionId == null) continue;
                if (!b.AutoLaunchMissiles) continue; // Task90: the AI never fires from bases switched to manual launch
                Faction f = state.FindFaction(b.OwnerFactionId.Value);
                if (f == null || f.IsPlayer || f.Eliminated) continue;
                if (b.StockpiledMissiles <= 0) continue;
                if (b.MissileLaunchCooldownRemaining > 0f) continue;

                WorldPos? target = ChooseTarget(state, f.Id, b.Position);
                if (!target.HasValue) continue;

                LaunchResult result = MissileStep.TryLaunch(state, b.BaseId, target.Value);
                if (result == LaunchResult.Ok) b.MissileLaunchCooldownRemaining = LaunchCooldownHours;
            }
        }

        /// <summary>Nemeses first (bases/external threats, no distance limit); otherwise the nearest
        /// regular Hostile base beyond MinLaunchDistance. Null if neither exists.</summary>
        private static WorldPos? ChooseTarget(WarState state, byte factionId, WorldPos from)
        {
            WorldPos? bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase ob = state.Bases[j];
                if (ob.OwnerFactionId == null) continue;
                if (state.Relations.Get(factionId, ob.OwnerFactionId.Value) != Relation.Nemesis) continue;
                float d = from.HorizontalDistanceTo(ob.Position);
                if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = ob.Position; }
            }

            for (int t = 0; t < state.Threats.Count; t++)
            {
                ExternalThreat threat = state.Threats[t];
                if (threat.IsDefeated) continue;
                if (state.ThreatRelations.Get(factionId, threat.Kind) != Relation.Nemesis) continue;
                float d = from.HorizontalDistanceTo(threat.Position);
                if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = threat.Position; }
            }

            if (bestNemesis.HasValue) return bestNemesis;

            WorldPos? bestHostile = null;
            float bestHostileDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase ob = state.Bases[j];
                if (ob.OwnerFactionId == null) continue;
                if (!state.Relations.Get(factionId, ob.OwnerFactionId.Value).IsHostile()) continue;
                float d = from.HorizontalDistanceTo(ob.Position);
                if (d <= MinLaunchDistance) continue; // close range is conventional forces' job (off-limits to missiles)
                if (d < bestHostileDist) { bestHostileDist = d; bestHostile = ob.Position; }
            }
            return bestHostile;
        }
    }
}
