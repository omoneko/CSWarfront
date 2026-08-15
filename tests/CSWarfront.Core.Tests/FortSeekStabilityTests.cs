using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task136 (playtest report "troops shuttle back and forth between the enemy and an enemy trench
/// and get stuck"). Both of FortSeekStep's rankings move every tick — the assault list depends on whether
/// enemy infantry happens to be standing in a trench right now, the free list on distance to an enemy who
/// is himself moving — so a squad between two candidates was re-sent to the other one over and over and
/// arrived nowhere, until the stall watchdog removed it.</summary>
public class FortSeekStabilityTests
{
    private static WarState TwoTrenchesAndAnEnemy(out UnitInstance squad, out UnitInstance enemy)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);

        // Two trenches either side of the squad, both well inside SeekRadius.
        var west = new MilitaryBase(1, BaseType.Trench, new WorldPos(-100, 0, 0));
        var east = new MilitaryBase(2, BaseType.Trench, new WorldPos(100, 0, 0));
        s.Bases.Add(west);
        s.Bases.Add(east);

        squad = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(squad);

        // Enemy infantry sitting in the west trench: that makes it the assault target.
        enemy = new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(-100, 0, 0));
        s.Units.Add(enemy);
        return s;
    }

    [Fact]
    public void A_committed_squad_is_not_re_sent_when_the_defenders_step_out_of_the_trench()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);

        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(-100f, squad.CoverDestination.Value.X, 3); // assaulting the west trench

        // The defenders step out for a moment — the trench momentarily stops counting as held. Before
        // Task136 the squad instantly switched to the east trench (the free position nearest the enemy).
        enemy.Position = new WorldPos(-60, 0, 0);
        FortSeekStep.Advance(s, 0.1f);

        Assert.Equal(-100f, squad.CoverDestination.Value.X, 3);
    }

    [Fact]
    public void It_stays_committed_across_many_ticks_of_the_enemy_moving_about()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);

        for (int i = 0; i < 20; i++)
        {
            // The enemy hops in and out of the trench, flipping the assault classification every tick.
            enemy.Position = (i % 2 == 0) ? new WorldPos(-100, 0, 0) : new WorldPos(-55, 0, 0);
            FortSeekStep.Advance(s, 0.1f);
            Assert.Equal(-100f, squad.CoverDestination.Value.X, 3);
        }
    }

    /// <summary>The commitment must not become a new way to get stuck: a squad that never reaches the
    /// position it was sent to eventually gives up and gets on with its advance.</summary>
    [Fact]
    public void A_squad_that_can_never_arrive_gives_up_and_resumes_advancing()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);

        FortSeekStep.Advance(s, 0.1f);
        Assert.True(squad.CoverHold);

        // MovementStep is never run, so the squad stays put — exactly the "cannot reach it" case.
        float elapsed = 0.1f;
        while (elapsed <= FortSeekStep.MaxFortApproachHours + 1f)
        {
            FortSeekStep.Advance(s, 0.5f);
            elapsed += 0.5f;
        }

        Assert.False(squad.CoverHold);
        Assert.Null(squad.CoverDestination);
        Assert.True(squad.FortSeekCooldown > 0f, "and it should not be re-assigned on the very next tick");
    }

    /// <summary>Arriving stops the approach clock, so an entrenched squad is still governed by
    /// MaxFortHoldHours and nothing else.</summary>
    [Fact]
    public void Arriving_clears_the_approach_clock()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);

        FortSeekStep.Advance(s, 1f);
        Assert.True(squad.FortApproachTimer > 0f);

        squad.Position = new WorldPos(-100, 0, 0); // reached the trench
        FortSeekStep.Advance(s, 1f);

        Assert.Equal(0f, squad.FortApproachTimer, 3);
        Assert.True(squad.FortHoldTimer > 0f);
    }

    /// <summary>The damping must not survive the defenders actually dying — that is Task122's whole
    /// point: a cleared trench is released at once and the squad rolls on.</summary>
    [Fact]
    public void A_trench_whose_defenders_are_dead_is_released_immediately()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);
        // Someone else keeps the squad interested in the area once the trench defender dies.
        s.Units.Add(new UnitInstance(3, "Tank_T1", 1, 100f, new WorldPos(300, 0, 0)));

        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(-100f, squad.CoverDestination.Value.X, 3);

        enemy.State = UnitState.Dead;
        FortSeekStep.Advance(s, 0.1f);

        Assert.Equal(100f, squad.CoverDestination.Value.X, 3); // on to the position nearest the enemy
    }

    /// <summary>Commitment yields to capacity: a position that fills up while a fourth unit is marching to
    /// it still pushes that unit elsewhere (Task121's overflow rule must keep working).</summary>
    [Fact]
    public void Commitment_does_not_override_the_garrison_capacity()
    {
        UnitInstance squad, enemy;
        WarState s = TwoTrenchesAndAnEnemy(out squad, out enemy);

        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(-100f, squad.CoverDestination.Value.X, 3);

        // Three more of ours claim the west trench.
        for (uint i = 0; i < FortSeekStep.GarrisonCapacity; i++)
        {
            var other = new UnitInstance(100 + i, "Infantry_T1", 0, 100f, new WorldPos(-100, 0, 0));
            other.CoverDestination = new WorldPos(-100, 0, 0);
            other.CoverHold = true;
            s.Units.Add(other);
        }

        FortSeekStep.Advance(s, 0.1f);

        Assert.Equal(100f, squad.CoverDestination.Value.X, 3); // pushed to the other trench
    }
}
}
