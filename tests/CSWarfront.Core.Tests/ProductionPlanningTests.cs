using CSWarfront.Core;
using Xunit;

public class ProductionPlanningTests
{
    [Fact]
    public void Advance_spends_treasury_and_fills_queue_to_cap()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(200f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal(100f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_does_not_queue_when_faction_broke()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(0f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_skips_eliminated_faction()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.Eliminated = true;
        f.AddTreasury(200f);
        s.Factions.Add(f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(200f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_respects_queue_cap()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal(500f, s.Factions[0].Treasury, 3);
    }
}
