using CSWarfront.Core;
using Xunit;

public class BaseDefenseStepTests
{
    private static WarState BaseWithHostileUnit(float unitDistanceX, float graceHours = 0f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1)); // Armor=1

        var myBase = new MilitaryBase(100, BaseType.Army, new WorldPos(0f, 0f, 0f));
        myBase.OwnerFactionId = 0;
        myBase.CaptureGraceHours = graceHours;
        s.Bases.Add(myBase);

        s.Units.Add(new UnitInstance(1, "Infantry_T1", 1, 100f, new WorldPos(unitDistanceX, 0f, 0f)));
        return s;
    }

    [Fact]
    public void Base_damages_hostile_unit_in_range()
    {
        var s = BaseWithHostileUnit(50f); // range既定120内
        BaseDefenseStep.Advance(s, 1f);
        // DamagePerHit(DefaultDefenseAttack=35, Infantry.Armor=1) = 34, dt=1 -> 34ダメージ
        Assert.Equal(66f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Base_ignores_own_and_neutral_faction_units()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 は既定でNeutral（Hostile未設定）
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1));

        var myBase = new MilitaryBase(100, BaseType.Army, new WorldPos(0f, 0f, 0f));
        myBase.OwnerFactionId = 0;
        s.Bases.Add(myBase);

        // 自軍ユニット
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(10f, 0f, 0f)));
        // 中立ユニット
        s.Units.Add(new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(20f, 0f, 0f)));

        BaseDefenseStep.Advance(s, 1f);

        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(100f, s.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Base_ignores_units_beyond_defense_range()
    {
        var s = BaseWithHostileUnit(150f); // DefaultDefenseRange=120を超える
        BaseDefenseStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Base_under_capture_grace_does_nothing()
    {
        var s = BaseWithHostileUnit(50f, graceHours: 5f);
        BaseDefenseStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Unit_reduced_to_zero_hp_by_base_defense_ends_up_dead()
    {
        var s = BaseWithHostileUnit(50f);
        s.FindUnit(1).CurrentHP = 34f; // DamagePerHit(35,1)=34 でちょうど0に到達
        BaseDefenseStep.Advance(s, 1f);
        Assert.Equal(0f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(UnitState.Dead, s.FindUnit(1).State);
    }

    // --- Task35: 撃破報酬（Research.KillReward）の付与 ---

    [Fact]
    public void Base_kill_awards_research_points_to_the_bases_owner()
    {
        var s = BaseWithHostileUnit(50f);
        s.FindUnit(1).CurrentHP = 34f; // dies this tick (see test above)
        BaseDefenseStep.Advance(s, 1f);

        // Infantry_T1.Cost = 20, KillRewardRate = 0.5 -> 10
        Assert.Equal(10f, s.FindFaction(0).ResearchPoints, 3); // base owner (Red)
        Assert.Equal(0f, s.FindFaction(1).ResearchPoints, 3);  // victim's own faction
    }

    [Fact]
    public void Base_defense_does_not_award_research_points_when_target_survives()
    {
        var s = BaseWithHostileUnit(50f); // 100 HP, survives this tick's 34 damage
        BaseDefenseStep.Advance(s, 1f);

        Assert.Equal(0f, s.FindFaction(0).ResearchPoints, 3);
    }

    [Fact]
    public void Damage_scales_with_dt()
    {
        var s = BaseWithHostileUnit(50f);
        BaseDefenseStep.Advance(s, 0.5f);
        // DamagePerHit(35,1)=34 × dt(0.5) = 17
        Assert.Equal(83f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Deterministic_nearest_target_selection_breaks_ties_by_lower_instance_id()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1));

        var myBase = new MilitaryBase(100, BaseType.Army, new WorldPos(0f, 0f, 0f));
        myBase.OwnerFactionId = 0;
        s.Bases.Add(myBase);

        // 両方とも基地から水平距離10で同着。InstanceId=1(先) と 2(後)。
        s.Units.Add(new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(0f, 0f, 10f)));
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 1, 100f, new WorldPos(10f, 0f, 0f)));

        BaseDefenseStep.Advance(s, 1f);

        Assert.Equal(66f, s.FindUnit(1).CurrentHP, 3); // 小さいInstanceIdが優先して狙われる
        Assert.Equal(100f, s.FindUnit(2).CurrentHP, 3);
    }
}
