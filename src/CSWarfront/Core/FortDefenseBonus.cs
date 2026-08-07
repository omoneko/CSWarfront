namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: the +50% defense bonus for infantry classes (Infantry/MechInfantry) standing on a
    /// trench or bunker (= incoming damage ÷ 1.5, design §1.2). **Owner and operational state are
    /// irrelevant** — a captured trench serves enemy infantry, and a defunct bunker keeps its
    /// defensive value as terrain.
    /// Applied to: CombatStep's unit-vs-unit damage, KamikazeStep's detonation, FortCombatStep's
    /// fire. External threats (the Godzilla beam etc.) and disaster missiles are excluded (not
    /// regular weapons, so they punch through).
    /// </summary>
    public static class FortDefenseBonus
    {
        /// <summary>The trench's (16×32m) effect radius (a simple circle a bit over the diagonal
        /// radius).</summary>
        public const float TrenchRadius = 18f;

        /// <summary>The bunker's (16×16m) effect radius.</summary>
        public const float BunkerRadius = 12f;

        /// <summary>The incoming-damage divisor (1.5 = +50% defense).</summary>
        public const float DamageDivisor = 1.5f;

        /// <summary>The incoming-damage multiplier applied to target (1/1.5 for infantry classes
        /// inside a bonus zone, 1.0 otherwise).</summary>
        public static float Multiplier(WarState state, UnitInstance target, UnitType targetType)
        {
            if (targetType == null) return 1f;
            if (targetType.Category != UnitCategory.Infantry &&
                targetType.Category != UnitCategory.MechInfantry) return 1f;
            return IsOnFortification(state, target.Position) ? 1f / DamageDivisor : 1f;
        }

        /// <summary>Whether pos lies inside a trench/bunker bonus zone (also used by FortSeekStep's
        /// "already entrenched" check).</summary>
        public static bool IsOnFortification(WarState state, WorldPos pos)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                float radius;
                if (b.Type == BaseType.Trench) radius = TrenchRadius;
                else if (b.Type == BaseType.Bunker) radius = BunkerRadius;
                else continue;
                if (pos.HorizontalDistanceTo(b.Position) <= radius) return true;
            }
            return false;
        }
    }
}
