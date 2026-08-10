using CSWarfront.Core;
using Xunit;

/// <summary>Task101: trench/bunker defense bonus (+50% = incoming damage / 1.5) and infantry fort-seeking AI.</summary>
public class FortDefenseBonusTests
{
    [Fact]
    public void Infantry_on_a_trench_takes_reduced_damage_regardless_of_ownership()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trench = new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0));
        trench.OwnerFactionId = 1; // Even an enemy-owned trench
        s.Bases.Add(trench);

        var infantry = new UnitInstance(1, "Infantry_T1", 0, 10000f, new WorldPos(5, 0, 0)); // On the trench
        var infantryOpen = new UnitInstance(2, "Infantry_T1", 0, 10000f, new WorldPos(200, 0, 0)); // In the open
        var enemyA = new UnitInstance(3, "Tank_T1", 1, 10000f, new WorldPos(30, 0, 0));
        var enemyB = new UnitInstance(4, "Tank_T1", 1, 10000f, new WorldPos(230, 0, 0));
        s.Units.Add(infantry); s.Units.Add(infantryOpen); s.Units.Add(enemyA); s.Units.Add(enemyB);

        CombatStep.Advance(s, 1f);

        float dmgOnTrench = 10000f - infantry.CurrentHP;
        float dmgOpen = 10000f - infantryOpen.CurrentHP;
        Assert.True(dmgOnTrench > 0f && dmgOpen > 0f);
        Assert.Equal(dmgOpen / FortDefenseBonus.DamageDivisor, dmgOnTrench, 2); // / 1.5
    }

    [Fact]
    public void Tanks_get_no_fortification_bonus()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        s.Bases.Add(new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0)));
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        UnitType tankType = s.Types.Get("Tank_T1");

        Assert.Equal(1f, FortDefenseBonus.Multiplier(s, tank, tankType), 3);
    }

    [Fact]
    public void Engaging_infantry_hides_behind_friendly_armor_not_buildings()
    {
        // Task104: infantry no longer takes cover behind buildings; while engaging it hides behind friendly armor (Tank/Apc).
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var cover = new CoverMap();
        cover.Add(new WorldPos(0, 0, 50), 10f); // Building (infantry no longer uses these)
        s.Cover = cover;

        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        var tank = new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(20, 0, 0)); // Friendly armor
        var enemy = new UnitInstance(3, "Tank_T1", 1, 100f, new WorldPos(35, 0, 0));
        infantry.State = UnitState.Engaging;
        infantry.TargetId = enemy.InstanceId;
        s.Units.Add(infantry); s.Units.Add(tank); s.Units.Add(enemy);

        CoverSeekStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.True(infantry.CoverDestination.HasValue);
        // The standing position is "on the far side of the armor from the enemy" = farther from the enemy (35) than the armor (20).
        Assert.True(infantry.CoverDestination.Value.X < 20f,
            "expected a position behind the friendly tank (away from the enemy)");

        // Without friendly armor there is no cover (it does not run to buildings or under overpasses).
        var loneInf = new UnitInstance(4, "Infantry_T1", 0, 100f, new WorldPos(200, 0, 0));
        var loneEnemy = new UnitInstance(5, "Tank_T1", 1, 100f, new WorldPos(230, 0, 0));
        loneInf.State = UnitState.Engaging;
        loneInf.TargetId = loneEnemy.InstanceId;
        s.Units.Add(loneInf); s.Units.Add(loneEnemy);
        CoverSeekStep.Advance(s, 0.1f);
        Assert.False(loneInf.CoverHold);
    }

    [Fact]
    public void Infantry_near_enemies_moves_to_the_fort_closest_to_the_enemy()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trenchNearEnemy = new MilitaryBase(1, BaseType.Trench, new WorldPos(200, 0, 0));
        var trenchFar = new MilitaryBase(2, BaseType.Trench, new WorldPos(-200, 0, 0));
        s.Bases.Add(trenchFar); s.Bases.Add(trenchNearEnemy);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(500, 0, 0));
        s.Units.Add(infantry); s.Units.Add(enemy);

        FortSeekStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.True(infantry.CoverDestination.HasValue);
        Assert.Equal(200f, infantry.CoverDestination.Value.X, 1); // The trench on the side closer to the enemy
    }

    [Fact]
    public void Infantry_idles_without_enemies_and_assaults_enemy_held_trenches()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trench = new MilitaryBase(1, BaseType.Trench, new WorldPos(200, 0, 0));
        s.Bases.Add(trench);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(infantry);

        // No enemies: does nothing.
        FortSeekStep.Advance(s, 0.1f);
        Assert.False(infantry.CoverHold);

        // Task122: an enemy infantry occupies the trench -> it becomes an assault target.
        var enemyInf = new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(205, 0, 0));
        s.Units.Add(enemyInf);
        FortSeekStep.Advance(s, 0.1f);
        Assert.True(infantry.CoverHold);
        Assert.Equal(200f, infantry.CoverDestination.Value.X, 1);

        // Confirming tanks are outside FortSeek's scope: tanks do not seek fortified positions.
        var tank = new UnitInstance(3, "Tank_T1", 0, 100f, new WorldPos(0, 0, 50));
        s.Units.Add(tank);
        FortSeekStep.Advance(s, 0.1f);
        Assert.False(tank.CoverHold);
    }

    /// <summary>Task122: contested trenches outrank free ones, capture completes as soon as the
    /// defenders are gone, and the squad then rolls on to the next contested trench.</summary>
    [Fact]
    public void Squad_takes_the_contested_trench_then_moves_on_to_the_next_one()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var free = new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 60));     // empty, closer to the enemy
        var near = new MilitaryBase(2, BaseType.Trench, new WorldPos(80, 0, 0));     // contested, nearest to us
        var next = new MilitaryBase(3, BaseType.Trench, new WorldPos(160, 0, 0));    // contested, further along
        s.Bases.Add(free); s.Bases.Add(near); s.Bases.Add(next);

        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        var defenderNear = new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(80, 0, 0));
        var defenderNext = new UnitInstance(3, "Infantry_T1", 1, 100f, new WorldPos(160, 0, 0));
        s.Units.Add(infantry); s.Units.Add(defenderNear); s.Units.Add(defenderNext);

        // The contested trench nearest to us wins over the empty one closer to the enemy.
        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(80f, infantry.CoverDestination.Value.X, 1);

        // Its defender is wiped out: the trench is ours immediately, and the squad rolls on to the
        // next contested trench (this one is now free, so it no longer outranks an assault target).
        defenderNear.State = UnitState.Dead;
        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(160f, infantry.CoverDestination.Value.X, 1);

        // With every defender gone it simply garrisons the free trench closest to the enemy.
        defenderNext.State = UnitState.Dead;
        var enemyTank = new UnitInstance(4, "Tank_T1", 1, 100f, new WorldPos(0, 0, 400));
        s.Units.Add(enemyTank);
        FortSeekStep.Advance(s, 0.1f);
        Assert.Equal(60f, infantry.CoverDestination.Value.Z, 1);
    }

    /// <summary>Task120: entrenching is time-boxed, so an assault can never stall in a trench forever.</summary>
    [Fact]
    public void Entrenched_infantry_lets_go_after_the_hold_cap_and_will_not_immediately_re_entrench()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        s.Bases.Add(new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0)));
        // The infantry already stands on the trench; its objective is far away (no objective lock).
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        infantry.OrderTargetPos = new WorldPos(3000, 0, 0);
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(400, 0, 0));
        s.Units.Add(infantry); s.Units.Add(enemy);

        FortSeekStep.Advance(s, 1f);
        Assert.True(infantry.CoverHold); // digs in first

        // Hold past the cap: the unit releases and starts advancing again.
        for (int i = 0; i < 6; i++) FortSeekStep.Advance(s, 1f);
        Assert.False(infantry.CoverHold);
        Assert.Null(infantry.CoverDestination);
        Assert.True(infantry.FortSeekCooldown > 0f);

        // While the cooldown lasts it does not re-entrench on the very next tick.
        FortSeekStep.Advance(s, 1f);
        Assert.False(infantry.CoverHold);
    }

    /// <summary>Task121: a fortification holds at most 3 friendly units; the overflow goes to the next
    /// fortification in range.</summary>
    [Fact]
    public void Fourth_unit_overflows_to_the_adjacent_fortification()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var near = new MilitaryBase(1, BaseType.Trench, new WorldPos(100, 0, 0));   // closest to the enemy
        var spare = new MilitaryBase(2, BaseType.Trench, new WorldPos(0, 0, 100));  // the adjacent one
        s.Bases.Add(near); s.Bases.Add(spare);
        s.Units.Add(new UnitInstance(99, "Tank_T1", 1, 100f, new WorldPos(400, 0, 0))); // the enemy

        var squad = new UnitInstance[4];
        for (uint i = 0; i < 4; i++)
        {
            squad[i] = new UnitInstance(i + 1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
            s.Units.Add(squad[i]);
        }

        FortSeekStep.Advance(s, 0.1f);

        // Three take the trench nearest the enemy, the fourth is pushed to the other one.
        for (int i = 0; i < 3; i++)
            Assert.Equal(near.Position.X, squad[i].CoverDestination.Value.X, 1);
        Assert.Equal(spare.Position.Z, squad[3].CoverDestination.Value.Z, 1);

        Assert.Equal(FortSeekStep.GarrisonCapacity, FortSeekStep.CountGarrison(s, near, 0));
        Assert.Equal(1, FortSeekStep.CountGarrison(s, spare, 0));
    }

    /// <summary>Task121: with every nearby fortification full, the unit just keeps advancing.</summary>
    [Fact]
    public void Units_beyond_capacity_advance_normally_when_no_fort_has_room()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        s.Bases.Add(new MilitaryBase(1, BaseType.Trench, new WorldPos(100, 0, 0)));
        s.Units.Add(new UnitInstance(99, "Tank_T1", 1, 100f, new WorldPos(400, 0, 0)));

        var squad = new UnitInstance[4];
        for (uint i = 0; i < 4; i++)
        {
            squad[i] = new UnitInstance(i + 1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
            s.Units.Add(squad[i]);
        }

        FortSeekStep.Advance(s, 0.1f);

        for (int i = 0; i < 3; i++) Assert.True(squad[i].CoverHold);
        Assert.False(squad[3].CoverHold);
        Assert.Null(squad[3].CoverDestination);
    }

    /// <summary>Task121: the firing emplacements are garrisonable too, and enemy-owned ones are not.</summary>
    [Fact]
    public void Firing_emplacements_are_garrisonable_when_friendly()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var mine = new MilitaryBase(1, BaseType.AaPosition, new WorldPos(100, 0, 0));
        mine.OwnerFactionId = 0;
        var theirs = new MilitaryBase(2, BaseType.AtPillbox, new WorldPos(150, 0, 0)); // closer to the enemy
        theirs.OwnerFactionId = 1;
        s.Bases.Add(mine); s.Bases.Add(theirs);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(infantry);
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(400, 0, 0)));

        FortSeekStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.Equal(100f, infantry.CoverDestination.Value.X, 1); // the friendly AA position, not the enemy pillbox
    }

    /// <summary>Task120: near its objective the unit presses the attack instead of digging in.</summary>
    [Fact]
    public void Infantry_close_to_its_objective_does_not_divert_to_a_fort()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        s.Bases.Add(new MilitaryBase(1, BaseType.Trench, new WorldPos(100, 0, 0)));
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        infantry.OrderTargetPos = new WorldPos(50, 0, 0); // the base being assaulted, well inside the lock radius
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(300, 0, 0));
        s.Units.Add(infantry); s.Units.Add(enemy);

        FortSeekStep.Advance(s, 1f);

        Assert.False(infantry.CoverHold);
        Assert.Null(infantry.CoverDestination);
    }
}
