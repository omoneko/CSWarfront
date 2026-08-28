using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task87 (user request: "aircraft freeze in place when they lose their target -> return them to a
/// nearby air base or carrier; naval forces return to a nearby navy base"): tests for the
/// return-home movement of Idle air/naval units.
/// </summary>
public class ReturnHomeTests
{
    /// <summary>Sampler that returns the ground at a constant height (0) (for the Task107 landing tests).</summary>
    private class FlatGroundSampler : IHeightSampler
    {
        private readonly float _ground;
        public FlatGroundSampler(float ground = 0f) { _ground = ground; }
        public bool TrySampleHeight(float x, float z, out float height) { height = _ground; return true; }
    }

    /// <summary>Sampler where everything is water (open sea) (Task107: verifies no ditching anywhere except onto a carrier).</summary>
    private class AllWater : IWaterSampler
    {
        public bool IsWater(float x, float z) { return true; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return true; }
    }

    private static WarState BaseState()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static MilitaryBase AddBase(WarState s, ushort id, BaseType type, byte owner, float x)
    {
        var b = new MilitaryBase(id, type, new WorldPos(x, 0, 0));
        b.OwnerFactionId = owner;
        b.CurrentHP = 500f;
        s.Bases.Add(b);
        return b;
    }

    [Fact]
    public void Idle_fighter_flies_back_toward_its_own_air_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, 1000f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle; // state after losing its target
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X > 0f, "expected the idle fighter to head home");
    }

    [Fact]
    public void Idle_fighter_stops_within_home_arrival_distance()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, MovementStep.HomeArrivalDistance - 10f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, fighter.Position.X, 3); // already within arrival range, so it does not move
    }

    [Fact]
    public void Idle_fighter_prefers_a_closer_friendly_carrier_over_a_far_air_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, 2000f);
        var carrier = new UnitInstance(2, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 500f));
        s.Units.Add(carrier);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Z > 0f, "expected the fighter to head to the closer carrier");
        Assert.True(fighter.Position.X < 1f, "expected the fighter not to head to the far air base");
    }

    [Fact]
    public void Idle_fighter_ignores_enemy_air_bases()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 1, 100f);  // enemy air base (near)
        AddBase(s, 201, BaseType.AirForce, 0, -800f); // own air base (far)
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X < 0f, "expected the fighter to head to its OWN base, not the enemy's");
    }

    [Fact]
    public void Idle_destroyer_sails_back_toward_its_own_navy_base_not_an_army_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.Army, 0, 100f);   // own army base (near, but not a valid destination)
        AddBase(s, 201, BaseType.Navy, 0, -800f);  // own navy base
        var destroyer = new UnitInstance(1, "Destroyer_T1", 0, 100f, new WorldPos(0, 0, 0));
        destroyer.State = UnitState.Idle;
        s.Units.Add(destroyer);

        MovementStep.Advance(s, 1f);

        Assert.True(destroyer.Position.X < 0f, "expected the destroyer to head to the navy base");
    }

    [Fact]
    public void Idle_land_unit_does_not_return_home()
    {
        // Return-home applies only to air/naval units (land units keep their previous behavior:
        // standing still while Idle).
        var s = BaseState();
        AddBase(s, 200, BaseType.Army, 0, 1000f);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        tank.State = UnitState.Idle;
        s.Units.Add(tank);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, tank.Position.X, 3);
    }

    // --- Task107 (user report: "air units that lost their objective end up hovering in mid-air") ---

    [Fact]
    public void Idle_fighter_over_its_home_base_lands_instead_of_hovering()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 0f); // directly below (= already arrived at the return destination)
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected the idle fighter to start descending onto its base");
        Assert.Equal(0f, fighter.Position.X, 3); // horizontal position must not change
    }

    [Fact]
    public void Landing_fighter_settles_at_parked_altitude_and_stays_there()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 0f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        for (int i = 0; i < 20; i++) MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.ParkedAltitude, fighter.Position.Y, 2); // touched down and at rest (no ground penetration)
    }

    [Fact]
    public void Idle_transport_helicopter_lands_at_its_base()
    {
        // Transport helicopters are moved by TransportHeliStep (they are excluded from the
        // return-destination resolution), but while waiting they must land rather than remain
        // frozen in mid-air.
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.Army, 0, 0f);
        var heli = new UnitInstance(1, "TransportHelicopter_T1", 0, 100f,
            new WorldPos(0, MovementStep.HeliCruiseAltitude, 0));
        heli.State = UnitState.Idle;
        s.Units.Add(heli);

        MovementStep.Advance(s, 1f);

        Assert.True(heli.Position.Y < MovementStep.HeliCruiseAltitude,
            "expected the waiting transport helicopter to land rather than hover");
    }

    [Fact]
    public void Idle_fighter_over_a_carrier_lands_on_the_deck_not_in_the_sea()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler(-30f); // sea floor
        s.Water = new AllWater();
        var carrier = new UnitInstance(2, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(carrier);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        for (int i = 0; i < 20; i++) MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.CarrierDeckAltitude, fighter.Position.Y, 2);
    }

    [Fact]
    /// <summary>Task154: it circles above the water rather than hovering over it, but the point of the
    /// test is unchanged - with no deck to land on it must not put itself in the sea.</summary>
    public void Idle_fighter_over_open_water_stays_up_rather_than_ditching()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler(-30f);
        s.Water = new AllWater();
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter); // no return destination and no carrier = nowhere to land

        WorldPos previous = fighter.Position;
        float lowest = float.MaxValue, shortest = float.MaxValue;
        for (int i = 0; i < 20; i++)
        {
            MovementStep.Advance(s, 1f);
            float moved = fighter.Position.HorizontalDistanceTo(previous);
            if (moved < shortest) shortest = moved;
            if (fighter.Position.Y < lowest) lowest = fighter.Position.Y;
            previous = fighter.Position;
        }

        Assert.True(lowest > 0f, "the fighter went into the sea (lowest Y " + lowest + ")");
        Assert.True(shortest > 0f, "the fighter stopped in mid-air over the water");
    }

    [Fact]
    public void Returning_fighter_descends_below_cruise_altitude_on_final_approach()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 120f); // inside DescentStartDistance (500)
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected a gliding approach, not a level fly-over at cruise altitude");
    }

    [Fact]
    public void Parked_fighter_climbs_gradually_when_ordered_out_again()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.ParkedAltitude, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(100000f, 0, 0); // sortie to a distant point (unreachable in 1 tick)
        s.Units.Add(fighter);

        MovementStep.Advance(s, 0.1f); // small tick close to the real game (climb per tick is capped by stepLen)

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected a climb, not a teleport to cruise altitude");
        Assert.True(fighter.Position.Y > MovementStep.ParkedAltitude, "expected the fighter to be climbing");
    }

    [Fact]
    public void Moving_fighter_with_objective_is_unaffected()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, -1000f); // own base in the opposite direction
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X > 0f, "expected the ordered objective to take priority over home");
    }
}
