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
        var rel = new RelationMatrix(5); // 0-1 is Neutral
        var self = U(1, 0, 0f);
        var neutral = U(2, 1, 10f);
        rel.Set(0, 2, Relation.Hostile);
        var hostileFar = U(3, 2, 100f); // hostile but out of range
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

    // --- Task59: Nemesis (arch-enemy) ---

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

    // --- Task61: Domain filter ---

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

    // Task85 (user request: "fighters may only attack fighters, bombers, and KAIJU"): drops the old
    // spec (Air_attacker_can_target_land_sea_and_air_hostiles, CanTargetDomains=All); fighters now
    // target only air units (land and sea units are passed over even when they are the nearest).
    [Fact]
    public void Fighter_targets_only_air_hostiles_ignoring_closer_land_and_sea()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "AirSuperiority_T1"); // CanTargetDomains=Air (Task85)
        var tank = UOf(2, 1, 5f, "Tank_T1");
        var destroyer = UOf(3, 1, 30f, "Destroyer_T1");
        var bomber = UOf(4, 1, 55f, "TacticalBomber_T1");
        var all = new List<UnitInstance> { self, tank, destroyer, bomber };

        UnitType fighterType = types.Get("AirSuperiority_T1");
        var nearest = TargetSearch.FindNearestHostile(self, all, rel, 60f, fighterType.CanTargetDomains, types);
        Assert.Equal((uint)4, nearest.InstanceId); // ignores the nearest tank and destroyer, targets only the air unit (bomber)
    }

    // Task85 -> revised in Task88: bombers hit land and sea targets (air is the only domain they do
    // not target). Task85 originally allowed land only, but the user pointed out in-game that
    // "bombers ignore enemy naval units", so this was changed to permit anti-ship bombing.
    [Fact]
    public void Bomber_targets_land_and_sea_hostiles_ignoring_closer_air()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "TacticalBomber_T1"); // CanTargetDomains=Land|Sea (Task88)
        var fighter = UOf(2, 1, 5f, "AirSuperiority_T1");
        var destroyer = UOf(3, 1, 30f, "Destroyer_T1");
        var tank = UOf(4, 1, 55f, "Tank_T1");
        var all = new List<UnitInstance> { self, fighter, destroyer, tank };

        UnitType bomberType = types.Get("TacticalBomber_T1");
        var nearest = TargetSearch.FindNearestHostile(self, all, rel, 60f, bomberType.CanTargetDomains, types);
        Assert.Equal((uint)3, nearest.InstanceId); // ignores the nearest fighter, targets the next-closest sea unit (destroyer)
    }

    // Task91 (user verification request: "can naval forces attack land forces now?"):
    // destroyers can target land units, and CombatStep actually applies the damage.
    [Fact]
    public void Destroyer_targets_and_damages_a_land_unit_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);

        var destroyer = new UnitInstance(1, "Destroyer_T1", 0, 260f, new WorldPos(0, 0, 0));
        s.Units.Add(destroyer);
        var tank = new UnitInstance(2, "Tank_T1", 1, 100000f, new WorldPos(150, 0, 0)); // coastal target within the 220 range
        s.Units.Add(tank);

        float hpBefore = tank.CurrentHP;
        CombatStep.Advance(s, 1f);

        Assert.Equal(UnitState.Engaging, destroyer.State);
        Assert.Equal(2u, destroyer.TargetId.Value);
        Assert.True(tank.CurrentHP < hpBefore, "expected the destroyer to damage the land unit");
    }

    // Task85: carriers attack nothing (dedicated launch/recovery platform, CanTargetDomains=None).
    [Fact]
    public void Carrier_targets_nothing()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "Carrier_T1"); // CanTargetDomains=None (Task85)
        var tank = UOf(2, 1, 5f, "Tank_T1");
        var destroyer = UOf(3, 1, 10f, "Destroyer_T1");
        var fighter = UOf(4, 1, 15f, "AirSuperiority_T1");
        var all = new List<UnitInstance> { self, tank, destroyer, fighter };

        UnitType carrierType = types.Get("Carrier_T1");
        var t = TargetSearch.FindNearestHostile(self, all, rel, 200f, carrierType.CanTargetDomains, types);
        Assert.Null(t);
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
