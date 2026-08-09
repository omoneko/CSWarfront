using CSWarfront.Core;
using Xunit;

/// <summary>Task101: automatic fire from fortifications (bunker = single-target fire with line-of-sight check, artillery post = area fire, ammo).</summary>
public class FortCombatStepTests
{
    private static WarState StateWithFort(BaseType fortType, out MilitaryBase fort)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        fort = new MilitaryBase(1, fortType, new WorldPos(0, 0, 0));
        fort.OwnerFactionId = 0;
        fort.MaxHP = fort.CurrentHP = FortificationRules.DefaultMaxHP(fortType);
        s.Bases.Add(fort);
        return s;
    }

    private static UnitInstance AddEnemyTank(WarState s, float x, float z, uint id = 100)
    {
        var u = new UnitInstance(id, "Tank_T1", 1, 1000f, new WorldPos(x, 0, z));
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Bunker_fires_at_hostiles_in_range_and_consumes_ammo()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);
        UnitInstance farEnemy = AddEnemyTank(s, 500, 0, 101); // Out of range

        FortCombatStep.Advance(s, 1f);

        Assert.True(enemy.CurrentHP < 1000f, "expected bunker damage");
        Assert.Equal(1000f, farEnemy.CurrentHP, 3);
        Assert.Equal(1f - 1f / FortCombatStep.BunkerAmmoHours, bunker.FortAmmo, 3);
    }

    [Fact]
    public void Bunker_does_not_shoot_through_buildings()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);
        var cover = new CoverMap();
        cover.Add(new WorldPos(50, 0, 0), 10f); // A building right in the middle of the line of fire
        s.Cover = cover;

        FortCombatStep.Advance(s, 1f);

        Assert.Equal(1000f, enemy.CurrentHP, 3); // Does not penetrate buildings
        Assert.Equal(1f, bunker.FortAmmo, 3);    // Did not fire, so no ammo is consumed either
    }

    [Fact]
    public void Artillery_post_splashes_all_hostiles_near_the_target()
    {
        var s = StateWithFort(BaseType.ArtilleryPost, out MilitaryBase post);
        UnitInstance a = AddEnemyTank(s, 100, 0, 100);
        UnitInstance b = AddEnemyTank(s, 100, 20, 101); // 20 m from the target = within Splash (30)
        UnitInstance c = AddEnemyTank(s, 100, 90, 102); // Outside Splash (not caught even though in range)

        FortCombatStep.Advance(s, 1f);

        Assert.True(a.CurrentHP < 1000f);
        Assert.True(b.CurrentHP < 1000f);
        Assert.Equal(1000f, c.CurrentHP, 3);
    }

    [Fact]
    public void Dry_or_neutralized_forts_do_not_fire()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);

        bunker.FortAmmo = 0f; // Out of ammo
        FortCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, enemy.CurrentHP, 3);

        bunker.FortAmmo = 1f;
        bunker.OwnerFactionId = null; // Out of action (neutralized)
        FortCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, enemy.CurrentHP, 3);
    }

    [Fact]
    public void At_pillbox_hits_armor_in_range_and_respects_line_of_sight()
    {
        var s = StateWithFort(BaseType.AtPillbox, out MilitaryBase pillbox);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);

        FortCombatStep.Advance(s, 1f);
        Assert.True(enemy.CurrentHP < 1000f, "expected AT pillbox damage");
        Assert.Equal(1f - 1f / FortCombatStep.AtAmmoHours, pillbox.FortAmmo, 3);

        // Direct fire: a building on the sight line stops it (same rule as the bunker).
        var s2 = StateWithFort(BaseType.AtPillbox, out MilitaryBase pillbox2);
        UnitInstance enemy2 = AddEnemyTank(s2, 100, 0);
        var cover = new CoverMap();
        cover.Add(new WorldPos(50, 0, 0), 10f);
        s2.Cover = cover;
        FortCombatStep.Advance(s2, 1f);
        Assert.Equal(1000f, enemy2.CurrentHP, 3);
        Assert.Equal(1f, pillbox2.FortAmmo, 3);
    }

    [Fact]
    public void Aa_position_shoots_aircraft_only_with_discrete_shots()
    {
        var s = StateWithFort(BaseType.AaPosition, out MilitaryBase aa);
        AirUnitRoster.RegisterAll(s.Types);
        var fighter = new UnitInstance(200, "AirSuperiority_T1", 1, 1000f, new WorldPos(100, 120, 0));
        s.Units.Add(fighter);
        UnitInstance tank = AddEnemyTank(s, 80, 0); // ground unit: never an AA target

        FortCombatStep.Advance(s, 1f);

        Assert.Single(s.RecentShots); // exactly one discrete shot (hit or miss)
        Assert.Equal(ShotKind.SamMissile, s.RecentShots[0].Kind);
        Assert.Equal(UnitCategory.AntiAir, s.RecentShots[0].Category);
        Assert.Equal(1000f, tank.CurrentHP, 3); // the ground tank was not engaged
        Assert.Equal(1f - FortCombatStep.AaFireIntervalHours / FortCombatStep.AaAmmoHours, aa.FortAmmo, 3);
    }

    [Fact]
    public void Aa_position_holds_fire_with_no_aircraft_in_range()
    {
        var s = StateWithFort(BaseType.AaPosition, out MilitaryBase aa);
        AddEnemyTank(s, 80, 0); // ground only

        FortCombatStep.Advance(s, 1f);

        Assert.Empty(s.RecentShots);
        Assert.Equal(1f, aa.FortAmmo, 3);
    }

    [Fact]
    public void Forts_refill_ammo_next_to_a_stocked_depot()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        bunker.FortAmmo = 0f;
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(100, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = 100f;
        s.Bases.Add(depot);

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(ResupplyStep.RefillPerHour, bunker.FortAmmo, 3);
        Assert.True(depot.StoredSupplies < 100f);
    }

    [Fact]
    public void New_emplacements_refill_ammo_like_the_bunker()
    {
        // Task118: AT pillbox and AA position share the bunker's ammo/resupply treatment.
        foreach (BaseType type in new[] { BaseType.AtPillbox, BaseType.AaPosition })
        {
            var s = StateWithFort(type, out MilitaryBase fort);
            fort.FortAmmo = 0f;
            var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(100, 0, 0));
            depot.OwnerFactionId = 0;
            depot.StoredSupplies = 100f;
            s.Bases.Add(depot);

            ResupplyStep.Advance(s, 1f);

            Assert.Equal(ResupplyStep.RefillPerHour, fort.FortAmmo, 3);
            Assert.True(depot.StoredSupplies < 100f);
        }
    }
}
