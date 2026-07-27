using CSWarfront.Core;
using Xunit;

public class BaseCombatStepTests
{
    [Fact]
    public void Attacker_in_range_damages_hostile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // attack 40 (per in-game hour), range 60
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        // Task38: Tank_T1.Accuracy=0.70 < BaseCombatStep.SiegeAccuracyFloor(0.8), so the floor applies.
        // dmg = DamagePerHit(40,0)=40 * dt(1) * siegeAccuracy(0.8) = 32 -> 100-32=68 (old: 100-40=60)
        Assert.Equal(68f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Base_damage_scales_with_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 0.5f);
        // dmg = 40 * 0.5 * siegeAccuracy(0.8) = 16 -> 100-16=84 (old: 100-40*0.5=80)
        Assert.Equal(84f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Does_not_damage_own_or_neutral_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 Neutral 既定
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var neutralBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        neutralBase.OwnerFactionId = 1; neutralBase.CurrentHP = 100f;
        s.Bases.Add(neutralBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Base_under_grace_takes_no_damage_and_grace_decreases_by_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        enemyBase.CaptureGraceHours = 5f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3); // 保護中はダメージなし
        Assert.Equal(4f, s.Bases[0].CaptureGraceHours, 3); // dt分だけ減少
    }

    [Fact]
    public void Base_takes_damage_normally_once_grace_hits_zero()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        enemyBase.CaptureGraceHours = 2f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f); // grace 2 -> 1（このtickは減算後もまだ>0なので保護）

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
        Assert.Equal(1f, s.Bases[0].CaptureGraceHours, 3);

        BaseCombatStep.Advance(s, 1f); // grace 1 -> 0（減算後0なので、このtickから通常通りダメージ）

        // dmg = 40 * 1 * siegeAccuracy(0.8) = 32 -> 100-32=68 (old: 100-40=60)
        Assert.Equal(68f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task38: 命中率(Accuracy)の基地攻めへの適用（0.8下限） ---

    [Fact]
    public void Siege_accuracy_floor_boosts_artillerys_low_accuracy_against_a_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1)); // Attack=50, Accuracy=0.35, Range=120
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        // Artillery.Accuracy(0.35) < SiegeAccuracyFloor(0.8), so the floor of 0.8 is used instead of 0.35.
        // dmg = DamagePerHit(50,0)=50 * dt(1) * 0.8 = 40 -> 100-40=60
        // (without the floor, dmg would only have been 50*0.35=17.5 -> 82.5: the floor keeps artillery
        // a strong siege weapon despite its low anti-unit accuracy)
        Assert.Equal(60f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Grace_never_goes_negative()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1; b.CaptureGraceHours = 0.5f;
        s.Bases.Add(b);

        BaseCombatStep.Advance(s, 2f); // dtがgraceを大きく超える

        Assert.Equal(0f, s.Bases[0].CaptureGraceHours, 3);
    }
}
