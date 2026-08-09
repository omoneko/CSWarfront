namespace CSWarfront.Core
{
    /// <summary>Base/facility kinds. Army/Navy/AirForce/MissileBase = the traditional military bases
    /// (with production and income). Task101 (Update 3) appended field fortifications and the cargo
    /// station at the tail (preserving int-value compatibility): these have
    /// FortificationRules.IsFortification==true and no production, income or missile capability
    /// (SpawnableDomains=None). Task117 (Workshop request) appended two more fortifications at the
    /// tail: AtPillbox (anti-armor direct fire) and AaPosition (static anti-air).
    /// See FortificationRules for the individual rules.</summary>
    public enum BaseType
    {
        Army, Navy, AirForce, MissileBase,
        Bunker, ArtilleryPost, SupplyDepot, Trench, CargoStation,
        AtPillbox, AaPosition
    }
}
