namespace CSWarfront.Core
{
    /// <summary>
    /// The kind of external threat (ExternalThreat) other MODs (Godzilla Disaster / Alien Invasion)
    /// spawn (Task59). Used as ExternalThreat.Kind and the key into WarState.ThreatRelations.
    /// When adding kinds, always append at the tail: ThreatRelations.ThreatKindCount and
    /// WarStateSerializer's persistence block read/write "0..ThreatKindCount-1" in fixed order, so
    /// reordering existing values would misread another threat's relations (the same caveat as
    /// Relation's Nemesis addition).
    /// </summary>
    public enum ThreatKind { Kaiju, Alien }
}
