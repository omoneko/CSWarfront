using CSWarfront.Core;
using Xunit;

// Task63: MissileDoctrine — AI auto-launch target selection and per-base cooldown.
public class MissileDoctrineTests
{
    private static WarState Base(out MilitaryBase missileBase)
    {
        var s = new WarState();
        var red = new Faction(0, "Red"); s.Factions.Add(red);
        var blue = new Faction(1, "Blue"); s.Factions.Add(blue);
        s.Relations.Set(0, 1, Relation.Hostile);

        missileBase = new MilitaryBase(100, BaseType.MissileBase, new WorldPos(0, 0, 0));
        missileBase.OwnerFactionId = 0;
        missileBase.StockpiledMissiles = 1;
        s.Bases.Add(missileBase);
        return s;
    }

    [Fact]
    public void Advance_launches_at_nearest_hostile_base_beyond_MinLaunchDistance()
    {
        var s = Base(out var mb);
        var near = new MilitaryBase(200, BaseType.Army, new WorldPos(500, 0, 0)); // within MinLaunchDistance
        near.OwnerFactionId = 1;
        s.Bases.Add(near);
        var far = new MilitaryBase(201, BaseType.Army, new WorldPos(1500, 0, 0)); // beyond MinLaunchDistance
        far.OwnerFactionId = 1;
        s.Bases.Add(far);

        MissileDoctrine.Advance(s, 0f);

        Assert.Equal(0, mb.StockpiledMissiles); // consumed
        Assert.Single(s.MissilesInFlight);
        Assert.Equal(far.Position.X, s.MissilesInFlight[0].To.X, 3);
    }

    // Task90（ユーザー要望「生産と発射を手動に切り替えられるように」）: 自動発射OFFの基地はAIが撃たない。
    [Fact]
    public void Advance_does_not_launch_from_a_base_with_AutoLaunchMissiles_off()
    {
        var s = Base(out var mb);
        mb.AutoLaunchMissiles = false;
        var far = new MilitaryBase(201, BaseType.Army, new WorldPos(1500, 0, 0));
        far.OwnerFactionId = 1;
        s.Bases.Add(far);

        MissileDoctrine.Advance(s, 0f);

        Assert.Equal(1, mb.StockpiledMissiles); // 消費されない
        Assert.Empty(s.MissilesInFlight);

        // 手動発射（プレイヤーの照準ボタン→TryLaunch）は引き続き可能。
        Assert.Equal(LaunchResult.Ok, MissileStep.TryLaunch(s, mb.BaseId, far.Position));
    }

    [Fact]
    public void Advance_does_not_launch_when_only_target_is_within_MinLaunchDistance()
    {
        var s = Base(out var mb);
        var near = new MilitaryBase(200, BaseType.Army, new WorldPos(500, 0, 0));
        near.OwnerFactionId = 1;
        s.Bases.Add(near);

        MissileDoctrine.Advance(s, 0f);

        Assert.Equal(1, mb.StockpiledMissiles); // untouched
        Assert.Empty(s.MissilesInFlight);
    }

    [Fact]
    public void Advance_prefers_nemesis_base_over_a_farther_hostile_ignoring_MinLaunchDistance()
    {
        var s = Base(out var mb);
        var farHostile = new MilitaryBase(200, BaseType.Army, new WorldPos(2000, 0, 0));
        farHostile.OwnerFactionId = 1;
        s.Bases.Add(farHostile);

        var nearNemesisFaction = new Faction(2, "Green");
        s.Factions.Add(nearNemesisFaction);
        s.Relations.Set(0, 2, Relation.Nemesis);
        var nemesisBase = new MilitaryBase(201, BaseType.Army, new WorldPos(50, 0, 0)); // very close, no distance floor
        nemesisBase.OwnerFactionId = 2;
        s.Bases.Add(nemesisBase);

        MissileDoctrine.Advance(s, 0f);

        Assert.Single(s.MissilesInFlight);
        Assert.Equal(nemesisBase.Position.X, s.MissilesInFlight[0].To.X, 3);
    }

    [Fact]
    public void Advance_prefers_nemesis_external_threat_over_hostile_base()
    {
        var s = Base(out var mb);
        var farHostile = new MilitaryBase(200, BaseType.Army, new WorldPos(2000, 0, 0));
        farHostile.OwnerFactionId = 1;
        s.Bases.Add(farHostile);

        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Nemesis);
        s.Threats.Add(new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(30, 0, 0), MaxHP = 100f, CurrentHP = 100f });

        MissileDoctrine.Advance(s, 0f);

        Assert.Single(s.MissilesInFlight);
        Assert.Equal(30f, s.MissilesInFlight[0].To.X, 3);
    }

    [Fact]
    public void Advance_does_not_launch_without_stockpile()
    {
        var s = Base(out var mb);
        mb.StockpiledMissiles = 0;
        var far = new MilitaryBase(201, BaseType.Army, new WorldPos(1500, 0, 0));
        far.OwnerFactionId = 1;
        s.Bases.Add(far);

        MissileDoctrine.Advance(s, 0f);
        Assert.Empty(s.MissilesInFlight);
    }

    [Fact]
    public void Advance_skips_player_factions()
    {
        var s = Base(out var mb);
        s.Factions[0].IsPlayer = true;
        var far = new MilitaryBase(201, BaseType.Army, new WorldPos(1500, 0, 0));
        far.OwnerFactionId = 1;
        s.Bases.Add(far);

        MissileDoctrine.Advance(s, 0f);
        Assert.Empty(s.MissilesInFlight);
        Assert.Equal(1, mb.StockpiledMissiles);
    }

    [Fact]
    public void Advance_respects_LaunchCooldownHours_per_base()
    {
        var s = Base(out var mb);
        mb.StockpiledMissiles = 2;
        var far = new MilitaryBase(201, BaseType.Army, new WorldPos(1500, 0, 0));
        far.OwnerFactionId = 1;
        s.Bases.Add(far);

        MissileDoctrine.Advance(s, 0f); // launches #1, sets cooldown
        Assert.Equal(1, mb.StockpiledMissiles);
        Assert.Equal(MissileDoctrine.LaunchCooldownHours, mb.MissileLaunchCooldownRemaining, 3);

        MissileDoctrine.Advance(s, 1f); // cooldown still remaining -> no second launch
        Assert.Equal(1, mb.StockpiledMissiles);
        Assert.Single(s.MissilesInFlight);

        MissileDoctrine.Advance(s, MissileDoctrine.LaunchCooldownHours); // cooldown fully drains and re-triggers
        Assert.Equal(0, mb.StockpiledMissiles);
        Assert.Equal(2, s.MissilesInFlight.Count);
    }
}
