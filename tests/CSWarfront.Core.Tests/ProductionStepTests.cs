using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class ProductionStepTests
{
    private static WarState OneBaseWithQueue()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void Advance_progresses_but_not_complete_before_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 5f); // 10秒中5秒
        Assert.Empty(done);
        Assert.Equal(0.5f, s.Bases[0].Queue[0].Progress, 3);
    }

    [Fact]
    public void Advance_completes_and_dequeues_at_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 10f);
        Assert.Single(done);
        Assert.Equal("Tank_T1", done[0].TypeKey);
        Assert.Equal((byte)0, done[0].FactionId);
        Assert.Empty(s.Bases[0].Queue); // 取り出し済み
    }

    [Fact]
    public void Unowned_base_does_not_produce()
    {
        var s = OneBaseWithQueue();
        s.Bases[0].OwnerFactionId = null;
        var done = ProductionStep.Advance(s, 10f);
        Assert.Empty(done);
    }
}
