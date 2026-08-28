using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task153 (playtest: "bombers still do not repeat hit and run, the invasion ones especially").
///
/// The racetrack itself was working - traced over 2000 steps it approached, overflew, egressed 350 out
/// and turned back in, for both an ordinary faction and the invasion force. What ended it was ammunition:
/// a bomber carries about three hours of firing, and the design is that it then flies home and rearms.
/// An invasion wave has no home - the Invader faction owns nothing and Task100 deliberately bars it from
/// the supply network - so its bombers went dry on the second pass and then simply stopped, hanging over
/// the city with the distance to their target frozen to the metre for the rest of the game.</summary>
public class BomberPassTests
{
    private static WarState Scenario(byte attacker, out UnitInstance bomber, out MilitaryBase target)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Defender"));
        s.Factions.Add(new Faction(1, "Attacker"));
        InvasionEvents.EnsureInvaderFaction(s);
        for (byte i = 0; i < 6; i++)
            for (byte j = 0; j < 6; j++)
                if (i != j) s.Relations.Set(i, j, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        target = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        target.OwnerFactionId = 0;
        target.MaxHP = target.CurrentHP = 100000f; // never falls: this is about movement, not damage
        s.Bases.Add(target);

        string key = AirUnitRoster.TypeKey(UnitCategory.TacticalBomber, 1);
        UnitType type = s.Types.Get(key);
        bomber = new UnitInstance(100, key, attacker, type.MaxHP, new WorldPos(0, 120f, -1200f));
        bomber.State = UnitState.Moving;
        s.Units.Add(bomber);
        return s;
    }

    private static int Fly(WarState s, byte attacker, UnitInstance bomber, MilitaryBase target, int steps,
        bool fire)
    {
        int passes = 0;
        float previous = float.MaxValue;
        bool closing = true;
        for (int i = 0; i < steps; i++)
        {
            InvasionOrders.AssignAdvance(s, attacker, 0.05f);
            CombatStep.Advance(s, 0.05f);
            if (fire) BaseCombatStep.Advance(s, 0.05f);
            MovementStep.Advance(s, 0.05f);

            float d = bomber.Position.HorizontalDistanceTo(target.Position);
            if (closing && d > previous) { passes++; closing = false; }
            else if (!closing && d < previous) closing = true;
            previous = d;
        }
        return passes;
    }

    /// <summary>While it has bombs, the run repeats: in, over, out, turn, in again.</summary>
    [Theory]
    [InlineData((byte)1)]
    [InlineData(Faction.InvaderFactionId)]
    public void An_armed_bomber_keeps_making_passes(byte attacker)
    {
        UnitInstance bomber;
        MilitaryBase target;
        WarState s = Scenario(attacker, out bomber, out target);

        int passes = Fly(s, attacker, bomber, target, 2000, fire: false);

        Assert.True(passes >= 5, "faction " + attacker + " made only " + passes + " passes in 2000 steps");
    }

    /// <summary>Out of bombs with no base to rearm at, it leaves - rather than parking in mid-air over
    /// the city, which is what it used to do and what the report was about.</summary>
    [Theory]
    [InlineData((byte)1)]
    [InlineData(Faction.InvaderFactionId)]
    public void A_dry_bomber_with_nowhere_to_rearm_leaves_the_map(byte attacker)
    {
        UnitInstance bomber;
        MilitaryBase target;
        WarState s = Scenario(attacker, out bomber, out target);

        Fly(s, attacker, bomber, target, 2000, fire: true);

        Assert.Equal(0f, bomber.Ammo, 3);
        Assert.False(bomber.IsAlive, "the dry bomber is still on the map");
    }

    /// <summary>A faction that does have an air base keeps its bomber: it goes home to rearm instead of
    /// being written off, which is the loop the ammunition budget was designed around.</summary>
    [Fact]
    public void A_dry_bomber_with_an_air_base_goes_home_instead()
    {
        UnitInstance bomber;
        MilitaryBase target;
        WarState s = Scenario(1, out bomber, out target);
        var home = new MilitaryBase(2, BaseType.AirForce, new WorldPos(0, 0, -1500f));
        home.OwnerFactionId = 1;
        s.Bases.Add(home);
        s.FindFaction(1).AddSupply(10000f);

        Fly(s, 1, bomber, target, 2000, fire: true);

        Assert.True(bomber.IsAlive, "a bomber with a base to return to was written off");
    }
}
}
