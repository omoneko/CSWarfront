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
        // Task103: 貨物駅はLand（列車=Domain.Landを生産するため。兵科の絞り込みはCanProduceUnit）。
        Assert.Equal(DomainMask.Land, new MilitaryBase(1, BaseType.CargoStation, new WorldPos(0, 0, 0)).SpawnableDomains);
    }

    [Fact]
    public void Trains_are_produced_only_at_cargo_stations()
    {
        // Task103: 軍用列車⇔貨物駅の相互専属。
        Assert.True(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.MilitaryTrain));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.Army, UnitCategory.MilitaryTrain));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.Tank));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.SupplyTruck));
        Assert.True(FortificationRules.CanProduceUnit(BaseType.Army, UnitCategory.Tank)); // 従来どおり

        // 手動生産の実挙動: 陸軍基地で列車は拒否、貨物駅で列車はOK、貨物駅で戦車は拒否。
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.AddTreasury(10000f); f.AddManpower(10000f); f.AddProduction(10000f);
        f.UnlockedTier = 5;
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        var army = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        army.OwnerFactionId = 0;
        var station = new MilitaryBase(2, BaseType.CargoStation, new WorldPos(100, 0, 0));
        station.OwnerFactionId = 0;
        s.Bases.Add(army); s.Bases.Add(station);

        Assert.Equal(QueueResult.WrongDomain, ManualProduction.TryEnqueue(s, 1, "MilitaryTrain_T1"));
        Assert.Equal(QueueResult.Ok, ManualProduction.TryEnqueue(s, 2, "MilitaryTrain_T1"));
        Assert.Equal(QueueResult.WrongDomain, ManualProduction.TryEnqueue(s, 2, "Tank_T1"));

        // AI自動生産は築城・貨物駅を対象にしない（補給拠点も一切生産しない）。
        var depot = new MilitaryBase(3, BaseType.SupplyDepot, new WorldPos(200, 0, 0));
        depot.OwnerFactionId = 0;
        s.Bases.Add(depot);
        station.Queue.Clear();
        ProductionPlanning.Advance(s);
        Assert.Empty(station.Queue);
        Assert.Empty(depot.Queue);
        Assert.NotEmpty(army.Queue); // 通常基地は従来どおりAIが生産する
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
