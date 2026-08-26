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

        /// <summary>Task127: infantry holding a city building (BuildingGarrisonStep) get the same
        /// protection as a trench. Slightly less than a purpose-built fortification would be more
        /// realistic, but a single divisor keeps "infantry in cover" one predictable rule for the
        /// player rather than three grades of it.</summary>
        public const float GarrisonDamageDivisor = 1.5f;

        /// <summary>Task147 (Workshop request from siddyskylines1989: "maybe add stationary tanks too,
        /// like a bunker but in a tank format"): a vehicle left holding a position long enough to prepare
        /// it takes reduced damage. Deliberately less than a purpose-built emplacement - a berm
        /// and a hull-down position is not a concrete pillbox - but it is what turns "a tank parked in the
        /// way" into "a dug-in tank".
        ///
        /// Unlike the two divisors above this one is not limited to infantry: it exists precisely for
        /// armour. Infantry get theirs from trenches and buildings, which are better and easier to reach.</summary>
        public const float DugInDamageDivisor = 1.25f;

        /// <summary>Task147: how long a position must be held before it counts as prepared. Long enough
        /// that it cannot be claimed by tapping Hold as the shooting starts.</summary>
        public const float HoursToDigIn = 4f;

        /// <summary>The incoming-damage multiplier applied to target (1/1.5 for infantry classes
        /// inside a bonus zone or holding a building, 1.0 otherwise).</summary>
        public static float Multiplier(WarState state, UnitInstance target, UnitType targetType)
        {
            if (targetType == null) return 1f;

            bool isInfantry = targetType.Category == UnitCategory.Infantry
                || targetType.Category == UnitCategory.MechInfantry;
            if (isInfantry)
            {
                if (IsOnFortification(state, target.Position)) return 1f / DamageDivisor;
                if (IsGarrisoned(state, target)) return 1f / GarrisonDamageDivisor;
            }

            // Task147: anything that has been holding its ground long enough to prepare the position.
            if (IsDugIn(target)) return 1f / DugInDamageDivisor;
            return 1f;
        }

        /// <summary>Task127: whether this unit is currently holding a city building as cover — it was
        /// sent to one and has arrived. Reading the hold rather than a separate flag keeps the two in
        /// step automatically: the moment the garrison is released the bonus is gone.</summary>
        public static bool IsGarrisoned(WarState state, UnitInstance u)
        {
            if (state.Cover == null || !u.CoverHold || !u.CoverDestination.HasValue) return false;
            if (u.GarrisonHoldTimer <= 0f) return false; // only counts once actually in position
            return u.Position.HorizontalDistanceTo(u.CoverDestination.Value) <= MovementStep.CoverArrivalDistance;
        }

        /// <summary>Task147/148: whether this unit has held a position long enough to count as dug in.
        /// Read straight off the timer, which MovementStep only advances while the unit is deliberately
        /// standing its ground - so there is one rule here, and it applies to the AI's troops exactly as
        /// it does to the player's.</summary>
        public static bool IsDugIn(UnitInstance u)
        {
            return u != null && u.DugInHours >= HoursToDigIn;
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
