using CSWarfront.Core;
using Xunit;

// Task63: MissileStockpile — manual/AI missile build start (TryBuildMissile) and progress advance.
public class MissileStockpileTests
{
    private static WarState WithMissileBase(float treasury, out MilitaryBase b)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(treasury);
        b = new MilitaryBase(100, BaseType.MissileBase, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void TryBuildMissile_spends_cost_and_starts_progress()
    {
        var s = WithMissileBase(300f, out var b);
        var result = MissileStockpile.TryBuildMissile(s, b.BaseId);

        Assert.Equal(MissileBuildResult.Ok, result);
        Assert.Equal(300f - MissileStockpile.MissileCost, s.Factions[0].Treasury, 3);
        Assert.True(MissileStockpile.IsBuilding(b));
    }

    [Fact]
    public void TryBuildMissile_fails_when_not_affordable()
    {
        var s = WithMissileBase(10f, out var b);
        var result = MissileStockpile.TryBuildMissile(s, b.BaseId);

        Assert.Equal(MissileBuildResult.NotAffordable, result);
        Assert.Equal(10f, s.Factions[0].Treasury, 3);
        Assert.False(MissileStockpile.IsBuilding(b));
    }

    [Fact]
    public void TryBuildMissile_fails_when_already_building()
    {
        var s = WithMissileBase(1000f, out var b);
        Assert.Equal(MissileBuildResult.Ok, MissileStockpile.TryBuildMissile(s, b.BaseId));

        var second = MissileStockpile.TryBuildMissile(s, b.BaseId);
        Assert.Equal(MissileBuildResult.AlreadyBuilding, second);
        // Only the first attempt spent money.
        Assert.Equal(1000f - MissileStockpile.MissileCost, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryBuildMissile_fails_when_stockpile_full()
    {
        var s = WithMissileBase(10000f, out var b);
        b.StockpiledMissiles = MissileStockpile.MaxStockpile;

        var result = MissileStockpile.TryBuildMissile(s, b.BaseId);
        Assert.Equal(MissileBuildResult.StockpileFull, result);
    }

    [Fact]
    public void TryBuildMissile_fails_for_non_missile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(1000f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        var result = MissileStockpile.TryBuildMissile(s, 100);
        Assert.Equal(MissileBuildResult.NotMissileBase, result);
    }

    [Fact]
    public void TryBuildMissile_fails_for_unknown_base()
    {
        var s = new WarState();
        var result = MissileStockpile.TryBuildMissile(s, 999);
        Assert.Equal(MissileBuildResult.BaseNotFound, result);
    }

    [Fact]
    public void TryBuildMissile_fails_for_unowned_base()
    {
        var s = new WarState();
        var b = new MilitaryBase(100, BaseType.MissileBase, new WorldPos(0, 0, 0));
        s.Bases.Add(b);

        var result = MissileStockpile.TryBuildMissile(s, 100);
        Assert.Equal(MissileBuildResult.NoOwner, result);
    }

    [Fact]
    public void Advance_completes_build_after_MissileBuildHours_and_increments_stockpile()
    {
        var s = WithMissileBase(1000f, out var b);
        MissileStockpile.TryBuildMissile(s, b.BaseId);

        MissileStockpile.Advance(s, MissileStockpile.MissileBuildHours - 0.01f);
        Assert.Equal(0, b.StockpiledMissiles);
        Assert.True(MissileStockpile.IsBuilding(b));

        MissileStockpile.Advance(s, 0.02f); // crosses the 1.0 threshold
        Assert.Equal(1, b.StockpiledMissiles);
        Assert.False(MissileStockpile.IsBuilding(b));
    }

    [Fact]
    public void Advance_does_not_exceed_MaxStockpile()
    {
        var s = WithMissileBase(1000f, out var b);
        b.StockpiledMissiles = MissileStockpile.MaxStockpile;
        b.MissileBuildProgress = 0.999f; // simulate a build somehow in progress despite a full stockpile

        MissileStockpile.Advance(s, 1f);
        Assert.Equal(MissileStockpile.MaxStockpile, b.StockpiledMissiles);
    }

    [Fact]
    public void Advance_ignores_non_missile_bases()
    {
        var s = new WarState();
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        s.Bases.Add(b);
        MissileStockpile.Advance(s, 100f); // should not throw or touch anything
        Assert.Equal(0, b.StockpiledMissiles);
    }

    [Fact]
    public void Advance_ignores_idle_missile_bases()
    {
        var s = WithMissileBase(1000f, out var b);
        MissileStockpile.Advance(s, 1000f);
        Assert.Equal(0, b.StockpiledMissiles);
        Assert.Equal(0f, b.MissileBuildProgress, 3);
    }
}
