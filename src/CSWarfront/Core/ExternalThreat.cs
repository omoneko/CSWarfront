namespace CSWarfront.Core
{
    /// <summary>
    /// CSWarfront's combat-side state for the monsters/invaders other MODs (Godzilla Disaster /
    /// Alien Invasion) spawn (Task58).
    ///
    /// Those MODs expose no HP, hit or defeat API at all (Godzilla.Game.GodzillaManager /
    /// AlienInvasion.Game.InvasionManager only tell us position and alive/dead), so CSWarfront keeps
    /// its own HP. When it hits 0 the Game layer (Game/ExternalThreatBridge) calls the other MOD's
    /// despawn via reflection (Defeat/ForceDespawn, or ResetForNewLevel if absent) and removes this
    /// threat.
    ///
    /// WarState.Threats is runtime-only and never persisted: the Game layer resyncs it every tick
    /// from the live state of the other MODs (the same pattern as RoadGraph/CoverMap).
    /// </summary>
    public class ExternalThreat
    {
        public uint Id;

        /// <summary>Kaiju (Godzilla) / Alien (set by the Game layer's ExternalThreatBridge). Task59:
        /// changed from a string to the ThreatKind enum so it can key WarState.ThreatRelations
        /// lookups.</summary>
        public ThreatKind Kind;

        public WorldPos Position;

        /// <summary>The hit radius (horizontal). These are huge, so it is wider than regular
        /// unit-vs-unit engagements (ThreatCombatStep treats unitType.Range + Radius as the effective
        /// range).</summary>
        public float Radius;

        public float MaxHP;
        public float CurrentHP;

        public bool IsDefeated { get { return CurrentHP <= 0f; } }
    }
}
