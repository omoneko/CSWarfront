using CSWarfront.Core;
using Xunit;

/// <summary>Task101: basic field fortification rules (classification, capture/neutralization, target exclusion, default HP).</summary>
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
        // Task103: the cargo station is Land (because trains = Domain.Land are produced there; category filtering is done by CanProduceUnit).
        Assert.Equal(DomainMask.Land, new MilitaryBase(1, BaseType.CargoStation, new WorldPos(0, 0, 0)).SpawnableDomains);
    }

    [Fact]
    public void Trains_are_produced_only_at_cargo_stations()
    {
        // Task103: military trains and cargo stations are mutually exclusive to each other.
        Assert.True(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.MilitaryTrain));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.Army, UnitCategory.MilitaryTrain));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.Tank));
        Assert.False(FortificationRules.CanProduceUnit(BaseType.CargoStation, UnitCategory.SupplyTruck));
        Assert.True(FortificationRules.CanProduceUnit(BaseType.Army, UnitCategory.Tank)); // Unchanged from before

        // Actual manual-production behavior: trains rejected at an army base, trains OK at a cargo station, tanks rejected at a cargo station.
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

        // AI auto-production does not target fortifications or cargo stations (and produces nothing at supply depots either).
        var depot = new MilitaryBase(3, BaseType.SupplyDepot, new WorldPos(200, 0, 0));
        depot.OwnerFactionId = 0;
        s.Bases.Add(depot);
        station.Queue.Clear();
        ProductionPlanning.Advance(s);
        Assert.Empty(station.Queue);
        Assert.Empty(depot.Queue);
        Assert.NotEmpty(army.Queue); // Regular bases keep being produced at by the AI as before
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
        s.Units.Add(new UnitInstance(1, "Tank_T1", 1, 100f, new WorldPos(10, 0, 0))); // Even with an enemy inside the radius

        Occupation.ResolveCaptures(s);

        Assert.Null(bunker.OwnerFactionId); // Neutralized (put out of action) instead of captured
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

        Assert.Equal((byte)1, depot.OwnerFactionId); // Regular capture
        Assert.Equal(250f, depot.StoredSupplies, 3); // Seized together with its stockpile
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
        Assert.Equal(trench.MaxHP, trench.CurrentHP, 0); // Undamaged

        // Also never becomes an AI advance target (no target when there is no other hostile base).
        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, tank.Position));
    }
}
