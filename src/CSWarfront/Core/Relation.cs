namespace CSWarfront.Core
{
    // Task59: Nemesis was appended at the tail. WarStateSerializer writes relations as raw ints (the
    // enum's numeric value) — no v4-or-earlier format change needed — so the existing values
    // (Hostile=0/Neutral=1/Allied=2) keep their meaning. Nemesis=3 is the new addition.
    public enum Relation { Hostile, Neutral, Allied, Nemesis }

    /// <summary>
    /// Task59: a "nemesis" is a special hostile relation that merely adds "targeted with priority
    /// over other hostile factions" on top of regular Hostile; everywhere that asks "is it hostile" —
    /// damage application, base capture, being captured, AI advance-target selection — it must be
    /// treated exactly like Hostile. Leaving raw `== Relation.Hostile` comparisons in Core would make
    /// Nemesis non-hostile at just those spots, so the check must always go through this helper
    /// (see TargetSearch/BaseCombatStep/Occupation/AiTargeting/ThreatCombatStep/InvasionOrders).
    /// </summary>
    public static class RelationExtensions
    {
        public static bool IsHostile(this Relation r)
        {
            return r == Relation.Hostile || r == Relation.Nemesis;
        }
    }
}
