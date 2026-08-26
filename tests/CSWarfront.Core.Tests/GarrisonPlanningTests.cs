using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task149 (asked during playtest: "does the AI make use of Hold?"): it had no notion of defence
/// at all - every unit went at the nearest enemy base, leaving its own bases empty behind the offensive.
/// Part of each faction now stays home.</summary>
public class GarrisonPlanningTests
{
    private static WarState Force(int units, int ownBases, out MilitaryBase home)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);

        home = null;
        for (ushort b = 0; b < ownBases; b++)
        {
            var mb = new MilitaryBase((ushort)(1 + b), BaseType.Army, new WorldPos(b * 1000, 0, 0));
            mb.OwnerFactionId = 0;
            s.Bases.Add(mb);
            if (home == null) home = mb;
        }
        var enemy = new MilitaryBase(90, BaseType.Army, new WorldPos(5000, 0, 0));
        enemy.OwnerFactionId = 1;
        s.Bases.Add(enemy);

        for (uint i = 0; i < units; i++)
            s.Units.Add(new UnitInstance(i + 1, "Tank_T1", 0, 100f, new WorldPos(i * 10f, 0, 0)));
        return s;
    }

    [Fact]
    public void Part_of_the_force_is_posted_to_defend_its_own_bases()
    {
        MilitaryBase home;
        WarState s = Force(12, 1, out home);

        var posts = GarrisonPlanning.Assign(s, 0);

        Assert.NotEmpty(posts);
        Assert.True(posts.Count <= GarrisonPlanning.PerBase);
        foreach (var kv in posts) Assert.Equal(home.BaseId, kv.Value);
    }

    /// <summary>Two per base would swallow a small army whole. The share cap is what keeps a faction in
    /// the war rather than turning it into a garrison force.</summary>
    [Fact]
    public void A_small_force_is_not_turned_entirely_into_sentries()
    {
        MilitaryBase home;
        WarState s = Force(4, 4, out home); // four bases would want eight sentries

        var posts = GarrisonPlanning.Assign(s, 0);

        Assert.True(posts.Count <= (int)(4 * GarrisonPlanning.MaxShareOfForce) + 1,
            "posted " + posts.Count + " of 4 units");
        Assert.True(posts.Count < 4, "the whole army was posted as sentries");
    }

    [Fact]
    public void A_faction_with_only_emplacements_posts_nobody()
    {
        MilitaryBase home;
        WarState s = Force(10, 0, out home);
        var pillbox = new MilitaryBase(50, BaseType.AtPillbox, new WorldPos(0, 0, 0));
        pillbox.OwnerFactionId = 0;
        s.Bases.Add(pillbox);

        Assert.Empty(GarrisonPlanning.Assign(s, 0)); // emplacements defend themselves
    }

    [Fact]
    public void Player_orders_and_logistics_are_never_posted()
    {
        MilitaryBase home;
        WarState s = Force(10, 1, out home);
        foreach (UnitInstance u in s.Units) u.Order = UnitOrder.Hold; // all under player command
        Assert.Empty(GarrisonPlanning.Assign(s, 0));

        WarState s2 = Force(0, 1, out home);
        for (uint i = 0; i < 10; i++)
            s2.Units.Add(new UnitInstance(i + 1, LandUnitRoster.TypeKey(UnitCategory.SupplyTruck, 1),
                0, 40f, new WorldPos(i * 10f, 0, 0)));
        Assert.Empty(GarrisonPlanning.Assign(s2, 0));
    }

    /// <summary>The posted units actually go home and stand down - which is the posture Task148 credits
    /// as dug in, so the AI's defenders earn the same armour the player's Hold units do.</summary>
    [Fact]
    public void A_posted_unit_stands_down_at_its_base_and_digs_in()
    {
        MilitaryBase home;
        WarState s = Force(12, 1, out home);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        UnitInstance sentry = null;
        foreach (UnitInstance u in s.Units)
            if (u.State == UnitState.Idle && !u.OrderTargetPos.HasValue) { sentry = u; break; }
        Assert.NotNull(sentry);

        for (float h = 0f; h <= FortDefenseBonus.HoursToDigIn + 1f; h += 1f)
            MovementStep.Advance(s, 1f);

        Assert.True(FortDefenseBonus.IsDugIn(sentry));
    }

    /// <summary>A defence that ignores the thing attacking the city is not a defence: while a threat is
    /// loose in friendly territory the garrison joins in.</summary>
    [Fact]
    public void The_garrison_turns_out_when_a_threat_reaches_the_city()
    {
        MilitaryBase home;
        WarState s = Force(12, 1, out home);
        InvasionOrders.AssignAdvance(s, 0, 0f);
        int idleBefore = CountIdle(s);
        Assert.True(idleBefore > 0);

        var threat = new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = home.Position,
            MaxHP = 1000f, CurrentHP = 1000f, Radius = 20f };
        s.Threats.Add(threat);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(0, CountIdle(s));
    }

    private static int CountIdle(WarState s)
    {
        int n = 0;
        foreach (UnitInstance u in s.Units)
            if (u.State == UnitState.Idle && !u.OrderTargetPos.HasValue) n++;
        return n;
    }
}
}
