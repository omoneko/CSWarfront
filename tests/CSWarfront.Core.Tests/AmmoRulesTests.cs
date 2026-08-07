using CSWarfront.Core;
using Xunit;

/// <summary>Task99: ammo gauge (consumption, ceasing fire when dry, unlimited ammo for Invaders, target release for return-and-rearm).</summary>
public class AmmoRulesTests
{
    private static WarState TwoHostileTanks(out UnitInstance red, out UnitInstance blue)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        red = new UnitInstance(1, "Tank_T1", 0, 1000f, new WorldPos(0, 0, 0));
        blue = new UnitInstance(2, "Tank_T1", 1, 1000f, new WorldPos(30, 0, 0));
        s.Units.Add(red); s.Units.Add(blue);
        return s;
    }

    [Fact]
    public void Firing_consumes_ammo_at_dt_over_combat_hours()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        UnitType tank = s.Types.Get("Tank_T1");
        Assert.Equal(8f, tank.AmmoCombatHours, 3); // tank default = 8h

        CombatStep.Advance(s, 2f); // both sides fire for 2 hours

        Assert.Equal(1f - 2f / 8f, red.Ammo, 3);
        Assert.Equal(1f - 2f / 8f, blue.Ammo, 3);
    }

    [Fact]
    public void Out_of_ammo_units_stop_dealing_damage_and_disengage()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        red.Ammo = 0f;
        blue.Ammo = 0f;
        float hpBefore = blue.CurrentHP;

        CombatStep.Advance(s, 1f);

        Assert.Equal(hpBefore, blue.CurrentHP, 3); // out of ammo deals no damage
        Assert.Equal(UnitState.Idle, red.State);
        Assert.Null(red.TargetId);
    }

    [Fact]
    public void Idle_units_do_not_consume_ammo()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        blue.Position = new WorldPos(5000, 0, 5000); // out of range = no firing

        CombatStep.Advance(s, 5f);

        Assert.Equal(1f, red.Ammo, 3);
    }

    [Fact]
    public void Invader_units_consume_ammo_and_stop_when_dry()
    {
        // Task100 (playtest feedback "the invading force is too strong"): Invaders are ammo-limited too.
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        InvasionEvents.EnsureInvaderFaction(s);
        var invader = new UnitInstance(3, "Tank_T1", Faction.InvaderFactionId, 1000f, new WorldPos(-30, 0, 0));
        s.Units.Add(invader);

        CombatStep.Advance(s, 2f);
        Assert.Equal(1f - 2f / 8f, invader.Ammo, 3); // consumes ammo just like regular factions

        // With no ammo, firing must stop completely (red takes damage from both invader and blue,
        // so drain blue's ammo too and observe "the invader cannot fire" via red's unchanged HP).
        invader.Ammo = 0f;
        blue.Ammo = 0f;
        float redHp = red.CurrentHP;
        CombatStep.Advance(s, 1f);
        Assert.Equal(redHp, red.CurrentHP, 3);
    }

    [Fact]
    public void Invader_kills_scavenge_ammo()
    {
        // Task100: the Invaders' only resupply source is "destroying defending units"
        // (living off the land, +25% per kill capped at 1).
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        InvasionEvents.EnsureInvaderFaction(s);
        var invader = new UnitInstance(3, "Tank_T1", Faction.InvaderFactionId, 1000f, new WorldPos(60, 0, 0));
        invader.Ammo = 0.1f;
        s.Units.Add(invader);
        blue.Position = new WorldPos(90, 0, 0); // within the invader's range, outside red's range
        blue.CurrentHP = 1f;                    // destroyed by the next hit
        red.Position = new WorldPos(5000, 0, 5000); // moved away so it is not caught in the fight

        CombatStep.Advance(s, 0.05f);

        Assert.False(blue.IsAlive);
        // The kill reward (+0.25) exceeds the consumption (0.05/8) = a net increase.
        Assert.True(invader.Ammo > 0.1f, "expected the invader to scavenge ammo from the kill");
        Assert.True(invader.Ammo <= 1f);
    }

    [Fact]
    public void Normal_faction_kills_do_not_scavenge_ammo()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        blue.CurrentHP = 1f;
        blue.Ammo = 0f; // so return fire does not change red's ammo
        red.Ammo = 0.5f;

        CombatStep.Advance(s, 0.05f);

        Assert.False(blue.IsAlive);
        Assert.True(red.Ammo < 0.5f, "expected only consumption, no scavenging, for normal factions");
    }

    [Fact]
    public void Invaders_never_refill_from_base_zones()
    {
        // Task100: Invaders do not replenish even inside a captured base's zone (kills are the only supply source).
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        InvasionEvents.EnsureInvaderFaction(s);
        LandUnitRoster.RegisterAll(s.Types);
        Faction invaderFaction = s.FindFaction(Faction.InvaderFactionId);
        invaderFaction.AddSupply(100f); // even if it hypothetically has stock
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = Faction.InvaderFactionId; // captured base
        s.Bases.Add(b);
        var invader = new UnitInstance(1, "Tank_T1", Faction.InvaderFactionId, 100f, new WorldPos(50, 0, 0));
        invader.Ammo = 0f;
        s.Units.Add(invader);

        ResupplyStep.Advance(s, 10f);

        Assert.Equal(0f, invader.Ammo, 3);
    }

    [Fact]
    public void Base_attack_stops_and_consumes_with_ammo()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var tank = new UnitInstance(1, "Tank_T1", 0, 1000f, new WorldPos(0, 0, 0));
        s.Units.Add(tank);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(30, 0, 0));
        b.OwnerFactionId = 1;
        s.Bases.Add(b);

        BaseCombatStep.Advance(s, 1f);
        Assert.True(b.CurrentHP < b.MaxHP, "expected base damage");
        Assert.True(tank.Ammo < 1f, "expected ammo consumption for the siege");

        tank.Ammo = 0f;
        float hp = b.CurrentHP;
        BaseCombatStep.Advance(s, 0.001f); // regen exists, so use a tiny dt to check only that the attack stopped
        Assert.True(b.CurrentHP >= hp, "expected no siege damage when out of ammo (only regen applies)");
    }

    [Fact]
    public void Empty_air_units_are_recalled_instead_of_advancing()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        AirUnitRoster.RegisterAll(s.Types);
        var fighter = new UnitInstance(1, "TacticalBomber_T1", 0, 1000f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        s.Units.Add(fighter);
        var enemyBase = new MilitaryBase(1, BaseType.Army, new WorldPos(2000, 0, 0));
        enemyBase.OwnerFactionId = 1;
        s.Bases.Add(enemyBase);

        fighter.Ammo = 0f;
        InvasionOrders.AssignAdvance(s, 0, 0.1f);

        Assert.Equal(UnitState.Idle, fighter.State); // given no advance objective, so Idle -> return-home logic
        Assert.False(fighter.OrderTargetPos.HasValue);

        fighter.Ammo = 1f; // rearming complete -> sorties again on the next call
        InvasionOrders.AssignAdvance(s, 0, 0.1f);
        Assert.Equal(UnitState.Moving, fighter.State);
        Assert.True(fighter.OrderTargetPos.HasValue);
    }
}
