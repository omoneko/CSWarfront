using CSWarfront.Core;
using Xunit;

public class OccupationTests
{
    private static WarState FallenBaseScenario()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1; b.CurrentHP = 0f; b.MaxHP = 500f; b.InfluenceRadius = 500f;
        b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f)); // Stockpile that will be seized
        s.Bases.Add(b);
        s.Factions[1].HomeBaseId = 200; // Blue's HQ
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(40, 0, 0))); // Attacker Red inside the influence radius
        return s;
    }

    [Fact]
    public void Fallen_base_transfers_to_attacker_with_queue_and_heals()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.Equal((byte)0, s.Bases[0].OwnerFactionId.Value); // Transferred to Red
        Assert.Equal(500f, s.Bases[0].CurrentHP, 3);           // Healed
        Assert.Single(s.Bases[0].Queue);                        // Production queue seized
    }

    [Fact]
    public void Losing_hq_no_longer_sets_Eliminated_directly()
    {
        // Task46: Occupation itself no longer touches Eliminated directly. The check for whether a faction
        // truly has no bases left was moved to FactionStatus.Refresh (so that a faction that was once
        // eliminated can come back by recapturing a base).
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.False(s.FindFaction(1).Eliminated);
    }

    [Fact]
    public void Losing_hq_eliminates_faction_after_status_refresh()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        FactionStatus.Refresh(s);
        Assert.True(s.FindFaction(1).Eliminated); // Blue eliminated
    }

    [Fact]
    public void No_attacker_in_radius_leaves_base_unchanged()
    {
        // When the attacker is outside the influence radius, the base transfer is deferred (waits for the next tick)
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());

        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1;
        b.CurrentHP = 0f;
        b.MaxHP = 500f;
        b.InfluenceRadius = 500f;
        b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);
        s.Factions[1].HomeBaseId = 200;

        // Attacker Red placed outside the radius (HorizontalDistance > InfluenceRadius)
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0)));

        Occupation.ResolveCaptures(s);

        // Base owner does not change
        Assert.Equal((byte)1, s.Bases[0].OwnerFactionId.Value);
        // HP is not healed
        Assert.Equal(0f, s.Bases[0].CurrentHP);
        // Blue is not eliminated
        Assert.False(s.FindFaction(1).Eliminated);
    }
}
