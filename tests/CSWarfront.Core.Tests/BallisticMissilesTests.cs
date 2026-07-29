using CSWarfront.Core;
using Xunit;

// Task63: BallisticMissiles (MissileStep) — launch gating, flight progress, interception, impact.
public class BallisticMissilesTests
{
    private static WarState WithLaunchedMissileBase(int stockpile, out MilitaryBase b)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        b = new MilitaryBase(100, BaseType.MissileBase, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.StockpiledMissiles = stockpile;
        s.Bases.Add(b);
        return s;
    }

    // --- TryLaunch gating ---

    [Fact]
    public void TryLaunch_ok_decrements_stockpile_and_adds_flight()
    {
        var s = WithLaunchedMissileBase(2, out var b);
        var result = MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        Assert.Equal(LaunchResult.Ok, result);
        Assert.Equal(1, b.StockpiledMissiles);
        Assert.Single(s.MissilesInFlight);
        Assert.Equal(0f, s.MissilesInFlight[0].Progress, 3);
        Assert.Equal((byte)0, s.MissilesInFlight[0].FactionId);
    }

    [Fact]
    public void TryLaunch_fails_when_no_stockpile()
    {
        var s = WithLaunchedMissileBase(0, out var b);
        var result = MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        Assert.Equal(LaunchResult.NoStockpile, result);
        Assert.Empty(s.MissilesInFlight);
    }

    [Fact]
    public void TryLaunch_fails_when_target_beyond_MaxRangeMeters()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        var result = MissileStep.TryLaunch(s, b.BaseId, new WorldPos(MissileStep.MaxRangeMeters + 1f, 0, 0));

        Assert.Equal(LaunchResult.OutOfRange, result);
        Assert.Equal(1, b.StockpiledMissiles); // not spent
        Assert.Empty(s.MissilesInFlight);
    }

    [Fact]
    public void TryLaunch_fails_for_non_missile_base()
    {
        var s = new WarState();
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        var result = MissileStep.TryLaunch(s, 100, new WorldPos(10, 0, 0));
        Assert.Equal(LaunchResult.NotMissileBase, result);
    }

    [Fact]
    public void TryLaunch_fails_for_unknown_base()
    {
        var s = new WarState();
        Assert.Equal(LaunchResult.BaseNotFound, MissileStep.TryLaunch(s, 999, new WorldPos(0, 0, 0)));
    }

    [Fact]
    public void TryLaunch_fails_for_unowned_base()
    {
        var s = new WarState();
        var b = new MilitaryBase(100, BaseType.MissileBase, new WorldPos(0, 0, 0));
        b.StockpiledMissiles = 1;
        s.Bases.Add(b);

        Assert.Equal(LaunchResult.NoOwner, MissileStep.TryLaunch(s, 100, new WorldPos(10, 0, 0)));
    }

    // --- Flight progress ---

    [Fact]
    public void Advance_increases_progress_by_dt_over_FlightHours()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        MissileStep.Advance(s, 0.3f);
        Assert.Single(s.MissilesInFlight);
        Assert.Equal(0.3f / MissileStep.FlightHours, s.MissilesInFlight[0].Progress, 3);
    }

    // --- Impact ---

    [Fact]
    public void Impact_at_progress_1_damages_unit_within_radius_and_removes_the_missile()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var victim = new UnitInstance(1, "x", 1, 500f, new WorldPos(1000 + MissileStep.ImpactRadius - 1f, 0, 0));
        s.Units.Add(victim);

        MissileStep.Advance(s, MissileStep.FlightHours); // progress reaches exactly 1

        Assert.Empty(s.MissilesInFlight);
        Assert.Equal(500f - MissileStep.ImpactDamageUnit, victim.CurrentHP, 3);
        Assert.Single(s.RecentImpacts);
        Assert.False(s.RecentImpacts[0].Intercepted);
    }

    [Fact]
    public void Impact_kills_a_unit_whose_HP_drops_to_zero_or_below_and_emits_a_kill_event()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var victim = new UnitInstance(1, "x", 1, 100f, target);
        s.Units.Add(victim);

        MissileStep.Advance(s, MissileStep.FlightHours);

        Assert.Equal(0f, victim.CurrentHP, 3);
        Assert.Equal(UnitState.Dead, victim.State);
        Assert.Single(s.RecentKills);
    }

    [Fact]
    public void Impact_ignores_units_beyond_ImpactRadius()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var farAway = new UnitInstance(1, "x", 1, 500f, new WorldPos(1000 + MissileStep.ImpactRadius + 50f, 0, 0));
        s.Units.Add(farAway);

        MissileStep.Advance(s, MissileStep.FlightHours);
        Assert.Equal(500f, farAway.CurrentHP, 3);
    }

    [Fact]
    public void Impact_damages_bases_within_radius()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var enemyBase = new MilitaryBase(200, BaseType.Army, target);
        enemyBase.OwnerFactionId = 1;
        enemyBase.MaxHP = 500f; enemyBase.CurrentHP = 500f;
        s.Bases.Add(enemyBase);

        MissileStep.Advance(s, MissileStep.FlightHours);
        Assert.Equal(500f - MissileStep.ImpactDamageBase, enemyBase.CurrentHP, 3);
    }

    [Fact]
    public void Impact_spares_bases_under_capture_grace()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var graced = new MilitaryBase(200, BaseType.Army, target);
        graced.OwnerFactionId = 1;
        graced.MaxHP = 500f; graced.CurrentHP = 500f;
        graced.CaptureGraceHours = 10f;
        s.Bases.Add(graced);

        MissileStep.Advance(s, MissileStep.FlightHours);
        Assert.Equal(500f, graced.CurrentHP, 3); // untouched
    }

    [Fact]
    public void Impact_damages_external_threats_within_radius()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        WorldPos target = new WorldPos(1000, 0, 0);
        MissileStep.TryLaunch(s, b.BaseId, target);

        var threat = new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = target, MaxHP = 2000f, CurrentHP = 2000f };
        s.Threats.Add(threat);

        MissileStep.Advance(s, MissileStep.FlightHours);
        Assert.Equal(2000f - MissileStep.ImpactDamageThreat, threat.CurrentHP, 3);
    }

    // --- Interception ---

    [Fact]
    public void Interception_never_happens_before_the_descent_half_even_with_a_guaranteed_roll()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        var aa = new UnitInstance(1, "AntiAir_T1", 1, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(aa);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.AntiAir, 1));

        // dt chosen so progress lands well below 0.5 (0.7/1.5 ~= 0.467) — the interception check must be
        // skipped entirely regardless of how large InterceptRatePerHour*dt would be.
        MissileStep.Advance(s, 0.7f);

        Assert.Single(s.MissilesInFlight);
        Assert.True(s.MissilesInFlight[0].Progress <= 0.5f);
        Assert.Empty(s.RecentImpacts);
    }

    [Fact]
    public void Interception_succeeds_in_descent_half_when_chance_is_effectively_certain()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        // The missile flies along X from (0,0,0) to (1000,0,0). Place the AntiAir near the ground-track
        // position it will occupy once dt pushes progress past 0.5 (dt=1.2 -> progress=0.8 -> x=800).
        var aa = new UnitInstance(1, "AntiAir_T1", 1, 100f, new WorldPos(800, 0, 0));
        s.Units.Add(aa);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.AntiAir, 1));

        // chance = InterceptRatePerHour(0.9) * dt(1.2) = 1.08 >= 1.0 -> deterministically certain.
        MissileStep.Advance(s, 1.2f);

        Assert.Empty(s.MissilesInFlight);
        Assert.Single(s.RecentImpacts);
        Assert.True(s.RecentImpacts[0].Intercepted);
    }

    [Fact]
    public void Interception_ignores_friendly_AntiAir()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        // Same faction as the launcher (0) -> not hostile, must not intercept even with a certain roll.
        var friendlyAa = new UnitInstance(1, "AntiAir_T1", 0, 100f, new WorldPos(800, 0, 0));
        s.Units.Add(friendlyAa);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.AntiAir, 1));

        MissileStep.Advance(s, 1.2f);

        Assert.Single(s.MissilesInFlight); // still flying, not intercepted
        Assert.Empty(s.RecentImpacts);
    }

    [Fact]
    public void Interception_ignores_non_AntiAir_units()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        var tank = new UnitInstance(1, "Tank_T1", 1, 100f, new WorldPos(800, 0, 0));
        s.Units.Add(tank);
        s.Types.Register(MvpUnitTypes.Tank_T1());

        MissileStep.Advance(s, 1.2f);

        Assert.Single(s.MissilesInFlight);
        Assert.Empty(s.RecentImpacts);
    }

    [Fact]
    public void Interception_ignores_AntiAir_beyond_InterceptRange()
    {
        var s = WithLaunchedMissileBase(1, out var b);
        MissileStep.TryLaunch(s, b.BaseId, new WorldPos(1000, 0, 0));

        var farAa = new UnitInstance(1, "AntiAir_T1", 1, 100f,
            new WorldPos(800 + MissileStep.InterceptRange + 50f, 0, 0));
        s.Units.Add(farAa);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.AntiAir, 1));

        MissileStep.Advance(s, 1.2f);

        Assert.Single(s.MissilesInFlight);
        Assert.Empty(s.RecentImpacts);
    }
}
