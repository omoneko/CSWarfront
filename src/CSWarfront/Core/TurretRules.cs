namespace CSWarfront.Core
{
    /// <summary>
    /// Task126: which unit categories may have their model split into a rotating turret.
    ///
    /// The geometric detector (<see cref="TurretDetection"/>) is deliberately conservative, but it is
    /// still only a heuristic over an arbitrary Workshop mesh. Gating it by category means an unlucky
    /// silhouette on a helicopter or a supply truck can never take a model apart: those are never
    /// offered to the detector in the first place.
    ///
    /// The list is the vehicles that traverse a gun on a ring in reality — main guns and the SPAAG's
    /// mount. Everything else (APCs with a fixed cupola, trucks, infantry, aircraft, ships, trains)
    /// renders rigid, exactly as before.
    /// </summary>
    public static class TurretRules
    {
        public static bool CanHaveTurret(UnitCategory category)
        {
            return category == UnitCategory.Tank
                || category == UnitCategory.Artillery
                || category == UnitCategory.AntiAir;
        }

        /// <summary>Convenience for the Game layer, which holds a TypeKey rather than a category.
        /// Unparsable keys are treated as "no turret" (the safe answer).</summary>
        public static bool CanHaveTurret(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            return CanHaveTurret(category);
        }
    }
}
