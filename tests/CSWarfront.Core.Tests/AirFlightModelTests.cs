using System;
using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task154 (playtest: "no sharp U-turns - draw a wide arc when turning back" and "never stand
/// still, not even for an instant; only helicopters hover").
///
/// Air movement used to be "step straight toward the objective", which knows nothing about which way the
/// aircraft is facing. Reversing cost one tick, and arriving snapped the aircraft onto its objective and
/// left it there. These fix both ends of that: a fixed-wing aircraft always moves a full step, and its
/// heading turns no harder than its own turn radius allows.</summary>
public class AirFlightModelTests
{
    private const float Dt = 0.05f;

    private class FlatGround : IHeightSampler
    {
        public bool TrySampleHeight(float x, float z, out float y) { y = 0f; return true; }
    }

    private static WarState Scenario(out MilitaryBase target)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Defender"));
        s.Factions.Add(new Faction(1, "Attacker"));
        InvasionEvents.EnsureInvaderFaction(s);
        for (byte i = 0; i < 6; i++)
            for (byte j = 0; j < 6; j++)
                if (i != j) s.Relations.Set(i, j, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        s.Height = new FlatGround();

        target = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        target.OwnerFactionId = 0;
        target.MaxHP = target.CurrentHP = 1000000f; // never falls: this is about flying, not damage
        s.Bases.Add(target);
        return s;
    }

    private static UnitInstance Spawn(WarState s, UnitCategory category, WorldPos at)
    {
        string key = AirUnitRoster.TypeKey(category, 1);
        var u = new UnitInstance(100, key, 1, s.Types.Get(key).MaxHP, at);
        u.State = UnitState.Moving;
        s.Units.Add(u);
        return u;
    }

    private static float StepLength(WarState s, UnitInstance u)
    {
        return s.Types.Get(u.TypeKey).Speed * MovementStep.GlobalSpeedMultiplier * Dt;
    }

    /// <summary>The complaint, directly: a bomber or a fighter is never in the same place twice. Measured
    /// over a whole engagement, including the moment it reaches its egress point and turns back, which is
    /// where it used to snap to a halt.</summary>
    [Theory]
    [InlineData(UnitCategory.TacticalBomber)]
    [InlineData(UnitCategory.AirSuperiority)]
    public void A_fixed_wing_aircraft_never_stands_still(UnitCategory category)
    {
        MilitaryBase target;
        WarState s = Scenario(out target);
        UnitInstance u = Spawn(s, category, new WorldPos(0, 120f, -1200f));

        float shortest = float.MaxValue;
        WorldPos previous = u.Position;
        for (int i = 0; i < 1500; i++)
        {
            InvasionOrders.AssignAdvance(s, 1, Dt);
            CombatStep.Advance(s, Dt);
            MovementStep.Advance(s, Dt);
            float moved = u.Position.HorizontalDistanceTo(previous);
            if (moved < shortest) shortest = moved;
            previous = u.Position;
        }

        // A step is speed x dt; anything much under that is the aircraft slowing to a stop.
        Assert.True(shortest > StepLength(s, u) * 0.9f,
            category + " stopped in the air: shortest step was " + shortest);
    }

    /// <summary>The other half: turning back is an arc, not a pivot. The heading may not swing further in
    /// one tick than a circle of the turn radius allows, so a reversal takes half a circle of flying - and
    /// the flight path is measurably that wide.</summary>
    [Theory]
    [InlineData(UnitCategory.TacticalBomber)]
    [InlineData(UnitCategory.AirSuperiority)]
    public void Turning_back_draws_an_arc_rather_than_a_hairpin(UnitCategory category)
    {
        MilitaryBase target;
        WarState s = Scenario(out target);
        UnitInstance u = Spawn(s, category, new WorldPos(0, 120f, -1200f));
        float radius = MovementStep.TurnRadius(category);

        float sharpest = 0f, widest = 0f;
        float? previousHeading = null;
        for (int i = 0; i < 1500; i++)
        {
            InvasionOrders.AssignAdvance(s, 1, Dt);
            CombatStep.Advance(s, Dt);
            MovementStep.Advance(s, Dt);

            if (u.AirHeading.HasValue && previousHeading.HasValue)
            {
                float d = u.AirHeading.Value - previousHeading.Value;
                float turned = Math.Abs((float)Math.Atan2(Math.Sin(d), Math.Cos(d)));
                if (turned > sharpest) sharpest = turned;
            }
            previousHeading = u.AirHeading;

            // It runs in along Z, so how far it swings out in X is the width of the turn.
            float offset = Math.Abs(u.Position.X);
            if (offset > widest) widest = offset;
        }

        float allowed = StepLength(s, u) / radius;
        Assert.True(sharpest <= allowed * 1.01f,
            category + " turned " + (sharpest * 180f / Math.PI) + " degrees in one tick, more than the "
            + (allowed * 180f / Math.PI) + " its turn radius allows");
        Assert.True(widest >= radius,
            category + " turned back within " + widest + " of the run-in line - a hairpin, not an arc "
            + "(a circle of radius " + radius + " is that wide on its own)");
    }

    /// <summary>With a turn radius the aircraft can no longer thread a forty-metre circle every time, and
    /// a target that sits inside its turn circle is the awkward case. It must still get its runs in rather
    /// than wheeling around the target forever - which is what the abeam rule is for.</summary>
    [Theory]
    [InlineData(0f, -60f)]
    [InlineData(120f, 0f)]
    [InlineData(30f, 30f)]
    public void A_target_inside_the_turn_circle_is_still_attacked(float x, float z)
    {
        MilitaryBase target;
        WarState s = Scenario(out target);
        UnitInstance u = Spawn(s, UnitCategory.TacticalBomber, new WorldPos(x, 120f, z));

        int passes = 0;
        float previous = float.MaxValue;
        bool closing = true;
        for (int i = 0; i < 1500; i++)
        {
            InvasionOrders.AssignAdvance(s, 1, Dt);
            CombatStep.Advance(s, Dt);
            MovementStep.Advance(s, Dt);
            float d = u.Position.HorizontalDistanceTo(target.Position);
            if (closing && d > previous) { passes++; closing = false; }
            else if (!closing && d < previous) closing = true;
            previous = d;
        }

        Assert.True(passes >= 3, "starting at " + x + "," + z + " it only made " + passes + " passes");
    }

    /// <summary>An idle aircraft with no air base anywhere used to settle vertically onto whatever field
    /// was underneath it. It holds a circle instead: still at height, still moving, still where it was.
    /// </summary>
    [Fact]
    public void An_idle_fixed_wing_with_no_home_holds_a_circle()
    {
        MilitaryBase target;
        WarState s = Scenario(out target);
        s.Bases.Clear(); // nowhere to go home to
        var start = new WorldPos(500f, 120f, 500f);
        UnitInstance u = Spawn(s, UnitCategory.TacticalBomber, start);
        u.State = UnitState.Idle;

        float lowest = float.MaxValue, shortest = float.MaxValue, drift = 0f;
        WorldPos previous = u.Position;
        for (int i = 0; i < 600; i++)
        {
            MovementStep.Advance(s, Dt);
            float moved = u.Position.HorizontalDistanceTo(previous);
            if (moved < shortest) shortest = moved;
            if (u.Position.Y < lowest) lowest = u.Position.Y;
            float away = u.Position.HorizontalDistanceTo(start);
            if (away > drift) drift = away;
            previous = u.Position;
        }

        Assert.Equal(MovementStep.CruiseAltitude, lowest, 1); // never sank
        Assert.True(shortest > StepLength(s, u) * 0.9f, "it stopped: shortest step " + shortest);
        // A holding circle is two radii across; anything much wider means it flew off instead.
        Assert.True(drift <= MovementStep.TurnRadius(UnitCategory.TacticalBomber) * 2.5f,
            "it wandered " + drift + " from where it was told to wait");
    }

    /// <summary>Helicopters are the exception the report allowed for, and they keep the old direct
    /// movement: they arrive on their objective and hold there.</summary>
    [Fact]
    public void A_helicopter_may_still_hover()
    {
        MilitaryBase target;
        WarState s = Scenario(out target);
        var destination = new WorldPos(0f, 0f, -100f);
        UnitInstance u = Spawn(s, UnitCategory.AttackHelicopter, new WorldPos(0f, 60f, -300f));
        u.OrderTargetPos = destination;

        for (int i = 0; i < 400; i++) MovementStep.Advance(s, Dt);

        Assert.True(u.Position.HorizontalDistanceTo(destination) < 1f,
            "the helicopter did not settle on its objective");
        Assert.False(u.AirHeading.HasValue, "the fixed-wing flight model was applied to a helicopter");
    }
}
}
