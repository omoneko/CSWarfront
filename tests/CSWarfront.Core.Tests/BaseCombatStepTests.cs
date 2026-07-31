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
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        // Task38: Tank_T1.Accuracy=0.70 < BaseCombatStep.SiegeAccuracyFloor(0.8), so the floor applies.
        // Task91: Tank Attack 40->42。dmg = DamagePerHit(42,0)=42 * dt(1) * siegeAccuracy(0.8) = 33.6 -> 100-33.6=66.4
        Assert.Equal(66.4f, s.Bases[0].CurrentHP, 3);
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
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 0.5f);
        // Task91: dmg = 42 * 0.5 * siegeAccuracy(0.8) = 16.8 -> 100-16.8=83.2
        Assert.Equal(83.2f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Does_not_damage_own_or_neutral_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 Neutral 既定
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var neutralBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        neutralBase.OwnerFactionId = 1; neutralBase.CurrentHP = 100f; neutralBase.MaxHP = 100f; // Task89: 回復を無効化
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
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
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
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        enemyBase.CaptureGraceHours = 2f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f); // grace 2 -> 1（このtickは減算後もまだ>0なので保護）

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
        Assert.Equal(1f, s.Bases[0].CaptureGraceHours, 3);

        BaseCombatStep.Advance(s, 1f); // grace 1 -> 0（減算後0なので、このtickから通常通りダメージ）

        // Task91: dmg = 42 * 1 * siegeAccuracy(0.8) = 33.6 -> 100-33.6=66.4
        Assert.Equal(66.4f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task38: 命中率(Accuracy)の基地攻めへの適用（0.8下限） ---

    [Fact]
    public void Siege_accuracy_floor_boosts_artillerys_low_accuracy_against_a_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1)); // Attack=55 (Task91), Accuracy=0.35, Range=120
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        // Artillery.Accuracy(0.35) < SiegeAccuracyFloor(0.8), so the floor of 0.8 is used instead of 0.35.
        // Task91: dmg = DamagePerHit(55,0)=55 * dt(1) * 0.8 = 44 -> 100-44=56
        // (without the floor, dmg would only have been 55*0.35=19.25: the floor keeps artillery
        // a strong siege weapon despite its low anti-unit accuracy)
        Assert.Equal(56f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task42: 発砲エフェクト(ShotEvent)は基地位置(To)を狙う ---

    [Fact]
    public void Attack_on_base_emits_shot_event_aimed_at_the_base_position()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 0.01f); // FireCooldown既定0なので、ダメージを与えた瞬間に必ず1発出る

        Assert.Single(s.RecentShots);
        var shot = s.RecentShots[0];
        Assert.Equal(ShotKind.DirectFire, shot.Kind); // Tank
        Assert.Equal(0f, shot.From.X, 3); // 攻撃側ユニットの位置
        Assert.Equal(40f, shot.To.X, 3);  // 基地の位置（ユニットの位置ではない）
        Assert.Equal((byte)0, shot.FactionId);
        // Task43: 基地は論理ユニットではないため TargetId=0（Game層はこれを「ユニットでない対象」
        // として扱い、基地用の既定の着弾高さを使う）。AttackerIdは攻撃側ユニットのInstanceId。
        Assert.Equal(1u, shot.AttackerId);
        Assert.Equal(0u, shot.TargetId);
        // Task51: 兵科別射撃音をGame層が選べるよう、発砲したユニットのUnitCategoryをShotEventに載せる。
        Assert.Equal(UnitCategory.Tank, shot.Category);
    }

    [Fact]
    public void Base_under_grace_emits_no_shot_events()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        enemyBase.CaptureGraceHours = 5f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Empty(s.RecentShots);
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

    // Task79: 自爆ドローンは対拠点の継続射程内砲撃を行わない（体当たり特攻は対ユニット/対外部脅威の
    // KamikazeStepの範囲のみで、対拠点の体当たりは本タスクのスコープ外として明示的に見送った）。
    [Fact]
    public void SuicideDrone_deals_no_damage_to_bases_and_emits_no_ShotEvent()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(10, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: 回復を無効化（このテストは回復対象外の検証）
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
        Assert.Equal(40f, s.FindUnit(1).CurrentHP, 3);
        Assert.Empty(s.RecentShots);
    }
}
