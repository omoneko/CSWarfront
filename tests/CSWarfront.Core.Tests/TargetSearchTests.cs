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

    // Task85（ユーザー要望「戦闘機は戦闘機・爆撃機・KAIJUのみ攻撃可能」）: 旧仕様
    // （Air_attacker_can_target_land_sea_and_air_hostiles、CanTargetDomains=All）を廃し、
    // 戦闘機は航空ユニットしか標的にしない（地上・海上は最寄りでも素通しする）。
    [Fact]
    public void Fighter_targets_only_air_hostiles_ignoring_closer_land_and_sea()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "AirSuperiority_T1"); // CanTargetDomains=Air（Task85）
        var tank = UOf(2, 1, 5f, "Tank_T1");
        var destroyer = UOf(3, 1, 30f, "Destroyer_T1");
        var bomber = UOf(4, 1, 55f, "TacticalBomber_T1");
        var all = new List<UnitInstance> { self, tank, destroyer, bomber };

        UnitType fighterType = types.Get("AirSuperiority_T1");
        var nearest = TargetSearch.FindNearestHostile(self, all, rel, 60f, fighterType.CanTargetDomains, types);
        Assert.Equal((uint)4, nearest.InstanceId); // 最寄りの戦車・駆逐艦は無視し、航空(爆撃機)だけを狙う
    }

    // Task85→Task88改訂: 爆撃機は地上・海上目標（航空だけは標的にしない）。
    // 当初のTask85は地上のみだったが、実機で「爆撃機が敵海上ユニットを無視する」とユーザーが
    // 指摘したため、対艦爆撃を許可する形へ変更した。
    [Fact]
    public void Bomber_targets_land_and_sea_hostiles_ignoring_closer_air()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "TacticalBomber_T1"); // CanTargetDomains=Land|Sea（Task88）
        var fighter = UOf(2, 1, 5f, "AirSuperiority_T1");
        var destroyer = UOf(3, 1, 30f, "Destroyer_T1");
        var tank = UOf(4, 1, 55f, "Tank_T1");
        var all = new List<UnitInstance> { self, fighter, destroyer, tank };

        UnitType bomberType = types.Get("TacticalBomber_T1");
        var nearest = TargetSearch.FindNearestHostile(self, all, rel, 60f, bomberType.CanTargetDomains, types);
        Assert.Equal((uint)3, nearest.InstanceId); // 最寄りの戦闘機は無視し、次に近い海上(駆逐艦)を狙う
    }

    // Task91（ユーザー確認依頼「海上戦力から地上戦力に攻撃ができるようになってるか」）:
    // 駆逐艦は地上ユニットを標的にでき、CombatStepで実際にダメージが入る。
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
        var tank = new UnitInstance(2, "Tank_T1", 1, 100000f, new WorldPos(150, 0, 0)); // 射程220内の沿岸目標
        s.Units.Add(tank);

        float hpBefore = tank.CurrentHP;
        CombatStep.Advance(s, 1f);

        Assert.Equal(UnitState.Engaging, destroyer.State);
        Assert.Equal(2u, destroyer.TargetId.Value);
        Assert.True(tank.CurrentHP < hpBefore, "expected the destroyer to damage the land unit");
    }

    // Task85: 空母は何も攻撃しない（発着艦プラットフォーム専任、CanTargetDomains=None）。
    [Fact]
    public void Carrier_targets_nothing()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        NavalUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = UOf(1, 0, 0f, "Carrier_T1"); // CanTargetDomains=None（Task85）
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
