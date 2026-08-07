namespace CSWarfront.Core
{
    /// <summary>Transfers bases at 0 HP to the nearest hostile attacker (pure logic). Task46: faction
    /// elimination (Eliminated) is no longer touched directly here. FactionStatus.Refresh became the
    /// only place deriving it every tick from "does the faction own any base" (so a once-eliminated
    /// faction can revive by regaining a base, the path that left Eliminated=true set forever was
    /// removed from here).</summary>
    public static class Occupation
    {
        public static void ResolveCaptures(WarState state)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.CurrentHP > 0f || b.OwnerFactionId == null) continue;
                byte oldOwner = b.OwnerFactionId.Value;

                // Task101: non-capturable fortifications (Bunker/ArtilleryPost) go defunct at 0 HP
                // (neutralized, never reactivated). Only the terrain defense bonus (FortDefenseBonus)
                // remains.
                if (!FortificationRules.IsCapturable(b.Type))
                {
                    b.OwnerFactionId = null;
                    continue;
                }

                // The nearest in-zone hostile attacker becomes the new owner
                UnitInstance nearest = null; float best = float.MaxValue;
                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (!u.IsAlive) continue;
                    if (!state.Relations.Get(oldOwner, u.FactionId).IsHostile()) continue; // Task59: Nemesis counts as hostile too
                    float d = b.Position.HorizontalDistanceTo(u.Position);
                    if (d > b.InfluenceRadius) continue;
                    if (d < best) { best = d; nearest = u; }
                }
                if (nearest == null) continue; // no attacker present: hold off (until the next tick)

                b.OwnerFactionId = nearest.FactionId;
                b.CurrentHP = b.MaxHP;         // reactivated (the queue rides along = seized)
            }
        }
    }
}
