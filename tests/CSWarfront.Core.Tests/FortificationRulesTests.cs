using CSWarfront.Core;
using Xunit;

/// <summary>Task101: 野戦築城の基本規則（種別・占領/機能停止・標的除外・HP既定値）。</summary>
public class FortificationRulesTests
{
    [Fact]
    public void Classification_and_defaults_match_the_design_table()
    {
        Assert.True(FortificationRules.IsFortification(BaseType.Bunker));
        Assert.True(FortificationRules.IsFortification(BaseType.Trench));
        Assert.True(FortificationRules.IsFortification(BaseType.CargoStation));
        Assert.False(FortificationRules.IsFortification(BaseType.Army));

        Assert.False(FortificationRules.IsTargetable(BaseType.Trench));
        Assert.True(FortificationRules.IsTargetable(BaseType.Bunker));

        Assert.False(FortificationRules.IsCapturable(BaseType.Bunker));
        Assert.False(FortificationRules.IsCapturable(BaseType.ArtilleryPost));
        Assert.True(FortificationRules.IsCapturable(BaseType.SupplyDepot));
        Assert.True(FortificationRules.IsCapturable(BaseType.Army));

        Assert.Equal(300f, FortificationRules.DefaultMaxHP(BaseType.Bunker), 3);
        Assert.Equal(300f, FortificationRules.StoredSupplyCap(BaseType.SupplyDepot), 3);
        Assert.Equal(500f, FortificationRules.StoredSupplyCap(BaseType.CargoStation), 3);
        Assert.Equal(0f, FortificationRules.StoredSupplyCap(BaseType.Bunker), 3);
    }

    [Fact]
    public void Fortifications_spawn_no_units()
    {
        Assert.Equal(DomainMask.None, new MilitaryBase(1, BaseType.Bunker, new WorldPos(0, 0, 0)).SpawnableDomains);
        Assert.Equal(DomainMask.None, new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0)).SpawnableDomains);
        Assert.Equal(DomainMask.Land, new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)).SpawnableDomains);
    }

    [Fact]
    public void Bunker_at_zero_hp_neutralizes_instead_of_being_captured()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var bunker = new MilitaryBase(1, BaseType.Bunker, new WorldPos(0, 0, 0));
        bunker.OwnerFactionId = 0;
        bunker.CurrentHP = 0f;
        s.Bases.Add(bunker);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 1, 100f, new WorldPos(10, 0, 0))); // 敵が圏内にいても

        Occupation.ResolveCaptures(s);

        Assert.Null(bunker.OwnerFactionId); // 占領ではなく機能停止（中立化）
    }

    [Fact]
    public void Depot_capture_keeps_the_stored_supplies()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var depot = new MilitaryBase(1, BaseType.SupplyDepot, new WorldPos(0, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = 250f;
        depot.CurrentHP = 0f;
        s.Bases.Add(depot);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 1, 100f, new WorldPos(10, 0, 0)));

        Occupation.ResolveCaptures(s);

        Assert.Equal((byte)1, depot.OwnerFactionId); // 通常の占領
        Assert.Equal(250f, depot.StoredSupplies, 3); // 備蓄ごと奪取
    }

    [Fact]
    public void Trenches_are_never_damaged_or_targeted()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trench = new MilitaryBase(1, BaseType.Trench, new WorldPos(30, 0, 0));
        trench.OwnerFactionId = 1;
        trench.MaxHP = trench.CurrentHP = FortificationRules.DefaultMaxHP(BaseType.Trench);
        s.Bases.Add(trench);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(tank);

        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(trench.MaxHP, trench.CurrentHP, 0); // 無傷

        // AIの進軍目標にもならない（他に敵対基地が無ければ目標なし）。
        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, tank.Position));
    }
}
