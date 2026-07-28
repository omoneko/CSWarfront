using CSWarfront.Core;
using Xunit;

public class FactionStatusTests
{
    [Fact]
    public void Faction_with_no_bases_becomes_Eliminated()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));

        FactionStatus.Refresh(s);

        Assert.True(s.FindFaction(0).Eliminated);
    }

    [Fact]
    public void Faction_that_owns_a_base_is_never_Eliminated()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        FactionStatus.Refresh(s);

        Assert.False(s.FindFaction(0).Eliminated);
    }

    [Fact]
    public void Giving_an_eliminated_faction_a_base_clears_Eliminated_on_next_refresh()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.Eliminated = true; // 過去にHQを失って脱落済み（旧バグでは永久にtrueのまま）
        s.Factions.Add(f);

        FactionStatus.Refresh(s); // まだ基地なし -> Eliminatedのまま
        Assert.True(s.FindFaction(0).Eliminated);

        // プレイヤーが新しい基地を与える
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        FactionStatus.Refresh(s);

        Assert.False(s.FindFaction(0).Eliminated); // 復活
    }

    [Fact]
    public void Faction_with_bases_but_null_HomeBaseId_gets_promoted_new_hq()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        // HomeBaseId is left null (e.g. base was captured/created outside the usual placement flow)

        FactionStatus.Refresh(s);

        Assert.Equal((ushort)100, f.HomeBaseId);
        Assert.True(b.IsHeadquarters);
    }

    [Fact]
    public void Faction_with_bases_but_stale_HomeBaseId_gets_promoted_new_hq()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.HomeBaseId = 999; // points at a base this faction no longer owns (or that no longer exists)
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        FactionStatus.Refresh(s);

        Assert.Equal((ushort)100, f.HomeBaseId);
        Assert.True(b.IsHeadquarters);
        Assert.False(f.Eliminated);
    }

    [Fact]
    public void Faction_with_valid_HomeBaseId_is_left_untouched()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        s.Factions.Add(f);
        var home = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        home.OwnerFactionId = 0;
        home.IsHeadquarters = true;
        s.Bases.Add(home);
        var other = new MilitaryBase(101, BaseType.Army, new WorldPos(10, 0, 0));
        other.OwnerFactionId = 0;
        s.Bases.Add(other);
        f.HomeBaseId = 100;

        FactionStatus.Refresh(s);

        Assert.Equal((ushort)100, f.HomeBaseId); // 変わらない
        Assert.False(other.IsHeadquarters);       // 昇格されない
    }

    [Fact]
    public void PromoteFirstOwnedBaseToHq_picks_first_matching_base_in_list_order()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        s.Factions.Add(f);
        var b1 = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b1.OwnerFactionId = 0;
        s.Bases.Add(b1);
        var b2 = new MilitaryBase(101, BaseType.Army, new WorldPos(10, 0, 0));
        b2.OwnerFactionId = 0;
        s.Bases.Add(b2);

        FactionStatus.PromoteFirstOwnedBaseToHq(s, 0);

        Assert.Equal((ushort)100, f.HomeBaseId);
        Assert.True(b1.IsHeadquarters);
        Assert.False(b2.IsHeadquarters);
    }

    [Fact]
    public void PromoteFirstOwnedBaseToHq_does_nothing_when_faction_owns_no_bases()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.HomeBaseId = null;
        s.Factions.Add(f);

        FactionStatus.PromoteFirstOwnedBaseToHq(s, 0);

        Assert.Null(f.HomeBaseId);
    }
}
