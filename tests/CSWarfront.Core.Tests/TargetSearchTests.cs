using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class TargetSearchTests
{
    private static UnitInstance U(uint id, byte fac, float x)
        => new UnitInstance(id, "Tank_T1", fac, 100f, new WorldPos(x, 0f, 0f));

    [Fact]
    public void Finds_nearest_hostile_in_range()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var near = U(2, 1, 30f);
        var far = U(3, 1, 55f);
        var all = new List<UnitInstance> { self, near, far };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)2, t.InstanceId);
    }

    [Fact]
    public void Ignores_non_hostile_and_out_of_range()
    {
        var rel = new RelationMatrix(5); // 0-1 は Neutral
        var self = U(1, 0, 0f);
        var neutral = U(2, 1, 10f);
        rel.Set(0, 2, Relation.Hostile);
        var hostileFar = U(3, 2, 100f); // 敵対だが射程外
        var all = new List<UnitInstance> { self, neutral, hostileFar };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }

    [Fact]
    public void Ignores_dead_units()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var dead = U(2, 1, 10f); dead.State = UnitState.Dead;
        var all = new List<UnitInstance> { self, dead };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }

    // --- Task59: Nemesis (宿敵) ---

    [Fact]
    public void Nemesis_counts_as_hostile_for_targeting()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Nemesis);
        var self = U(1, 0, 0f);
        var nemesis = U(2, 1, 30f);
        var all = new List<UnitInstance> { self, nemesis };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)2, t.InstanceId);
    }

    [Fact]
    public void Prefers_a_farther_nemesis_over_a_closer_ordinary_hostile()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile); // faction 1: ordinary hostile
        rel.Set(0, 2, Relation.Nemesis); // faction 2: nemesis
        var self = U(1, 0, 0f);
        var closeHostile = U(2, 1, 10f);
        var fartherNemesis = U(3, 2, 40f);
        var all = new List<UnitInstance> { self, closeHostile, fartherNemesis };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)3, t.InstanceId);
    }

    [Fact]
    public void Among_multiple_nemesis_candidates_picks_the_nearest_one()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Nemesis);
        var self = U(1, 0, 0f);
        var farNemesis = U(2, 1, 40f);
        var nearNemesis = U(3, 1, 15f);
        var all = new List<UnitInstance> { self, farNemesis, nearNemesis };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)3, t.InstanceId);
    }

    [Fact]
    public void Falls_back_to_nearest_ordinary_hostile_when_no_nemesis_in_range()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        rel.Set(0, 2, Relation.Nemesis);
        var self = U(1, 0, 0f);
        var hostile = U(2, 1, 20f);
        var nemesisOutOfRange = U(3, 2, 500f); // beyond range, must not affect the fallback
        var all = new List<UnitInstance> { self, hostile, nemesisOutOfRange };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)2, t.InstanceId);
    }

    // --- Task61: 領域(Domain)フィルタ ---

    private static UnitInstance UOf(uint id, byte fac, float x, string typeKey)
        => new UnitInstance(id, typeKey, fac, 100f, new WorldPos(x, 0f, 0f));

    [Fact]
    public void Land_only_attacker_ignores_hostile_air_unit_in_range()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "Tank_T1"); // CanTargetDomains=Land
        var fighter = UOf(2, 1, 10f, "AirSuperiority_T1"); // Domain=Air
        var all = new List<UnitInstance> { self, fighter };

        UnitType tankType = types.Get("Tank_T1");
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f, tankType.CanTargetDomains, types);
        Assert.Null(t);
    }

    [Fact]
    public void AntiAir_can_target_hostile_air_unit_in_range()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "AntiAir_T1"); // CanTargetDomains=Land|Air
        var fighter = UOf(2, 1, 10f, "AirSuperiority_T1");
        var all = new List<UnitInstance> { self, fighter };

        UnitType antiAirType = types.Get("AntiAir_T1");
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f, antiAirType.CanTargetDomains, types);
        Assert.Equal((uint)2, t.InstanceId);
    }

    [Fact]
    public void Air_attacker_can_target_land_sea_and_air_hostiles()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "AirSuperiority_T1"); // CanTargetDomains=All
        var tank = UOf(2, 1, 5f, "Tank_T1");
        var destroyer = UOf(3, 1, 55f, "Destroyer_T1");
        var all = new List<UnitInstance> { self, tank, destroyer };

        UnitType fighterType = types.Get("AirSuperiority_T1");
        var nearest = TargetSearch.FindNearestHostile(self, all, rel, 60f, fighterType.CanTargetDomains, types);
        Assert.Equal((uint)2, nearest.InstanceId); // nearest overall (tank), domain filter doesn't exclude it
    }

    [Fact]
    public void Domain_filter_is_skipped_when_types_registry_is_null_for_backward_compat()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "Tank_T1");
        var other = UOf(2, 1, 10f, "AirSuperiority_T1");
        var all = new List<UnitInstance> { self, other };

        // types=null -> no domain filtering applied, matches the legacy 4-arg overload's behaviour.
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f, DomainMask.Land, null);
        Assert.Equal((uint)2, t.InstanceId);
    }
}
