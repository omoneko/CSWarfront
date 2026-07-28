using CSWarfront.Core;
using Xunit;

public class CombatSynergyTests
{
    private static WarState BaseScenario()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1));     // Accuracy=0.35
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));         // Accuracy=0.70 (non-Artillery control)
        return s;
    }

    [Fact]
    public void Artillery_with_friendly_drone_in_range_gets_the_accuracy_bonus()
    {
        var s = BaseScenario();
        var artillery = new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        var drone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f, new WorldPos(100f, 0f, 0f)); // within 150
        s.Units.Add(artillery);
        s.Units.Add(drone);

        UnitType artilleryType = s.Types.Get("Artillery_T1");
        float result = CombatSynergy.AccuracyFor(s, artillery, artilleryType);

        // 0.35 + 0.5 = 0.85, below the 0.95 cap.
        Assert.Equal(0.85f, result, 3);
    }

    [Fact]
    public void Bonus_clamps_at_0_95_even_if_sum_would_exceed_it()
    {
        var s = BaseScenario();
        // A hypothetical Artillery type with a base accuracy high enough that base+bonus (0.9+0.5=1.4)
        // would exceed 1.0 without the clamp. Real roster Artillery never reaches this high a base
        // accuracy (Tier5 is only ~0.434), so this uses a hand-built UnitType to exercise the clamp path.
        var hypotheticalArtillery = new UnitType("TestArtillery_HighAcc", Domain.Land, UnitCategory.Artillery,
            1, 100f, 50f, 100f, 0f, 10f, 0f, 10f, 5f, "", 0.9f, 0.60f, ShotKind.IndirectFire);
        var artillery = new UnitInstance(1, hypotheticalArtillery.TypeKey, 0, 100f, new WorldPos(0f, 0f, 0f));
        var drone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f, new WorldPos(10f, 0f, 0f));
        s.Units.Add(artillery);
        s.Units.Add(drone);

        float result = CombatSynergy.AccuracyFor(s, artillery, hypotheticalArtillery);
        Assert.Equal(0.95f, result, 3);
    }

    [Fact]
    public void Enemy_drone_in_range_gives_no_bonus()
    {
        var s = BaseScenario();
        var artillery = new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        var enemyDrone = new UnitInstance(2, "DroneInfantry_T1", 1, 100f, new WorldPos(50f, 0f, 0f)); // faction 1, hostile-ish
        s.Units.Add(artillery);
        s.Units.Add(enemyDrone);

        UnitType artilleryType = s.Types.Get("Artillery_T1");
        float result = CombatSynergy.AccuracyFor(s, artillery, artilleryType);

        Assert.Equal(0.35f, result, 3);
    }

    [Fact]
    public void Drone_just_outside_the_radius_gives_no_bonus()
    {
        var s = BaseScenario();
        var artillery = new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        // Just beyond DroneSpotterRadius (150).
        var farDrone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f,
            new WorldPos(CombatSynergy.DroneSpotterRadius + 1f, 0f, 0f));
        s.Units.Add(artillery);
        s.Units.Add(farDrone);

        UnitType artilleryType = s.Types.Get("Artillery_T1");
        float result = CombatSynergy.AccuracyFor(s, artillery, artilleryType);

        Assert.Equal(0.35f, result, 3);
    }

    [Fact]
    public void Drone_exactly_at_the_radius_boundary_gives_the_bonus()
    {
        var s = BaseScenario();
        var artillery = new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        var edgeDrone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f,
            new WorldPos(CombatSynergy.DroneSpotterRadius, 0f, 0f));
        s.Units.Add(artillery);
        s.Units.Add(edgeDrone);

        UnitType artilleryType = s.Types.Get("Artillery_T1");
        float result = CombatSynergy.AccuracyFor(s, artillery, artilleryType);

        Assert.Equal(0.85f, result, 3);
    }

    [Fact]
    public void Dead_drone_gives_no_bonus()
    {
        var s = BaseScenario();
        var artillery = new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        var deadDrone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f, new WorldPos(10f, 0f, 0f));
        deadDrone.State = UnitState.Dead;
        s.Units.Add(artillery);
        s.Units.Add(deadDrone);

        UnitType artilleryType = s.Types.Get("Artillery_T1");
        float result = CombatSynergy.AccuracyFor(s, artillery, artilleryType);

        Assert.Equal(0.35f, result, 3);
    }

    [Fact]
    public void Non_artillery_attackers_are_unaffected_by_nearby_friendly_drones()
    {
        var s = BaseScenario();
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        var drone = new UnitInstance(2, "DroneInfantry_T1", 0, 100f, new WorldPos(10f, 0f, 0f));
        s.Units.Add(tank);
        s.Units.Add(drone);

        UnitType tankType = s.Types.Get("Tank_T1");
        float result = CombatSynergy.AccuracyFor(s, tank, tankType);

        // Tank's own base accuracy (0.70), unchanged regardless of the nearby drone.
        Assert.Equal(0.70f, result, 3);
    }
}
