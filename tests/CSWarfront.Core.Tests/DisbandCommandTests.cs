using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task134 (Workshop request from siddyskylines1989: "could you add a button to remove all
/// troops?"). Disbanding is a quiet removal: no explosion, no kill credit, no losses — the troops are
/// simply gone, and the sim tick's dead sweep drops them from the list.</summary>
public class DisbandCommandTests
{
    private static WarState StateWithTroops()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(50, 0, 0)));
        s.Units.Add(new UnitInstance(3, "Infantry_T1", 1, 100f, new WorldPos(500, 0, 0)));
        return s;
    }

    [Fact]
    public void Disbanding_removes_every_unit_and_reports_the_count()
    {
        WarState s = StateWithTroops();

        Assert.Equal(3, DisbandCommand.DisbandAll(s));
        Assert.All(s.Units, u => Assert.False(u.IsAlive));
        Assert.All(s.Units, u => Assert.Equal(UnitState.Dead, u.State));
    }

    [Fact]
    public void Disbanding_credits_nobody_with_a_kill()
    {
        WarState s = StateWithTroops();

        DisbandCommand.DisbandAll(s);

        Assert.Empty(s.RecentKills); // no explosions, no combat report
    }

    [Fact]
    public void Already_dead_units_are_not_counted_twice()
    {
        WarState s = StateWithTroops();
        s.Units[0].State = UnitState.Dead;
        s.Units[0].CurrentHP = 0f;

        Assert.Equal(2, DisbandCommand.DisbandAll(s));
        Assert.Equal(0, DisbandCommand.DisbandAll(s)); // nothing left to remove
    }

    [Fact]
    public void One_faction_can_be_disbanded_on_its_own()
    {
        WarState s = StateWithTroops();

        Assert.Equal(2, DisbandCommand.DisbandFaction(s, 0));
        Assert.True(s.FindUnit(3).IsAlive); // the other faction is untouched
    }

    /// <summary>A squad riding a transport that is disbanded must not stay flagged as carried: every
    /// step skips carried units, so it would be a permanently frozen unit on the map.</summary>
    [Fact]
    public void A_passenger_whose_carrier_is_disbanded_is_put_back_on_its_feet()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "F0"));
        s.Factions.Add(new Faction(1, "F1"));
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        var heli = new UnitInstance(10, "TransportHelicopter_T1", 0, 100f, new WorldPos(0, 60, 0));
        var squad = new UnitInstance(11, "Infantry_T1", 1, 100f, new WorldPos(0, 60, 0));
        squad.CarriedByUnitId = heli.InstanceId;
        s.Units.Add(heli);
        s.Units.Add(squad);

        DisbandCommand.DisbandFaction(s, 0); // the carrier only

        Assert.False(heli.IsAlive);
        Assert.True(squad.IsAlive);
        Assert.False(squad.IsCarried);
    }

    [Fact]
    public void Disbanding_an_empty_map_is_a_no_op()
    {
        var s = new WarState();
        Assert.Equal(0, DisbandCommand.DisbandAll(s));
        Assert.Equal(0, DisbandCommand.DisbandAll(null));
    }
}
}
