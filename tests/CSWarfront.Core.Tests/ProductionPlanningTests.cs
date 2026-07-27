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
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Task28: Tank_T1.Cost is now 60 (was 50)
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        // 200 - 2*60 = 80 (old arithmetic was 200 - 2*50 = 100, before Tank_T1's cost changed)
        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal(80f, s.Factions[0].Treasury, 3);
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

    // --- ChooseUnitKey (Task28: 陸上ロスター全体からの決定的な選択) ---

    private static WarState WithFullRoster(float treasury)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(treasury);
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void ChooseUnitKey_with_small_treasury_picks_cheapest_affordable_unit()
    {
        // Infantry_T1 (Cost 20) is the globally cheapest Tier1 unit in the table; every other
        // category's Tier1 costs more, and every higher tier costs more than its own Tier1.
        var s = WithFullRoster(25f);
        string key = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
        Assert.Equal("Infantry_T1", key);
    }

    [Fact]
    public void ChooseUnitKey_with_large_treasury_picks_most_expensive_affordable_unit()
    {
        // Artillery_T5 (Cost 70 * 3.4 = 238) is the most expensive non-AntiAir land unit.
        var s = WithFullRoster(10000f);
        string key = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
        Assert.Equal("Artillery_T5", key);
    }

    [Fact]
    public void ChooseUnitKey_never_picks_AntiAir()
    {
        var s = WithFullRoster(10000f);
        string key = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
        UnitType chosen = s.Types.Get(key);
        Assert.NotEqual(UnitCategory.AntiAir, chosen.Category);
    }

    [Fact]
    public void ChooseUnitKey_is_deterministic_across_repeated_calls()
    {
        var s = WithFullRoster(150f);
        string first = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
        for (int i = 0; i < 10; i++)
        {
            string again = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void ChooseUnitKey_returns_null_when_nothing_affordable()
    {
        var s = WithFullRoster(5f); // less than Infantry_T1's cost of 20
        string key = ProductionPlanning.ChooseUnitKey(s, s.Factions[0], s.Bases[0]);
        Assert.Null(key);
    }

    [Fact]
    public void Advance_with_full_roster_and_tiny_treasury_queues_nothing()
    {
        var s = WithFullRoster(5f);
        ProductionPlanning.Advance(s);
        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(5f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_with_full_roster_picks_strongest_affordable_units_each_slot()
    {
        // 300: first slot buys Artillery_T5 (238, the globally strongest affordable pick),
        // leaving 62 -> second slot re-evaluates and the strongest thing left affordable is
        // Tank_T1 (60; the next tier/category up all cost more than 62). 300-238-60=2 left.
        var s = WithFullRoster(300f);
        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal("Artillery_T5", s.Bases[0].Queue[0].TypeKey);
        Assert.Equal("Tank_T1", s.Bases[0].Queue[1].TypeKey);
        Assert.Equal(2f, s.Factions[0].Treasury, 3);
    }
}
