using CSWarfront.Core;
using Xunit;

// Task58: AI diversion — non-player units retarget a nearby external threat (Godzilla/Alien) that is
// close to their own faction's territory, instead of marching on an enemy base. Player-ordered units
// (Hold/RallyHold) must remain untouched, exactly as before this feature (Task48 contract).
public class InvasionOrdersThreatDivertTests
{
    private static ExternalThreat Threat(float x, float z = 0f)
    {
        return new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(x, 0f, z), Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f };
    }

    [Fact]
    public void Unit_diverts_to_a_threat_near_its_own_territory_even_with_no_enemy_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        s.Threats.Add(Threat(100f)); // 100 <= ThreatDivertRadius(600) from the owned base
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Equal(100f, u.OrderTargetPos.Value.X, 3);
    }

    // --- Task61: Sea/Air units never attempt road pathfinding (they ignore RoadGraph entirely) ---

    [Fact]
    public void Sea_unit_gets_OrderTargetPos_toward_enemy_base_but_never_attempts_a_road_path()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        NavalUnitRoster.RegisterAll(s.Types);
        var ownBase = new MilitaryBase(1, BaseType.Navy, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Navy, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Roads = new RoadGraph(); // supplied but empty; a Land unit would still attempt FindPath against it
        s.Units.Add(new UnitInstance(1, NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1), 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.Equal(300f, u.OrderTargetPos.Value.X, 3); // still gets an objective toward the enemy base
        Assert.Null(u.Path);
        // A Land unit in the same situation would have attempted FindPath and, on failure (empty
        // RoadGraph), set PathRetryCooldown to PathRetryFailCooldownHours; a Sea unit must never
        // enter that branch at all, so the cooldown must remain untouched (0, its initial value).
        Assert.Equal(0f, u.PathRetryCooldown, 3);
    }

    [Fact]
    public void Land_unit_in_the_same_situation_does_attempt_a_road_path_and_sets_the_retry_cooldown()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Roads = new RoadGraph(); // empty -> FindPath fails
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Null(u.Path);
        Assert.Equal(InvasionOrders.PathRetryFailCooldownHours, u.PathRetryCooldown, 3);
    }

    // Task62 verdict (b): the threat-diversion branch must never flip a FreeAdvance unit's Order back
    // to AiControlled (AssignAdvance only ever writes OrderTargetPos/State, never Order itself).
    [Fact]
    public void Diversion_branch_does_not_change_a_FreeAdvance_unit_s_Order()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        s.Threats.Add(Threat(50f)); // within ThreatDivertRadius, triggers the diversion branch
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)) { Order = UnitOrder.FreeAdvance };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(50f, u.OrderTargetPos.Value.X, 3); // diverted, confirming the branch actually ran
        Assert.Equal(UnitOrder.FreeAdvance, u.Order); // ...without touching Order
    }

    [Fact]
    public void Unit_prefers_the_nearby_threat_over_an_enemy_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Threats.Add(Threat(50f)); // near the own base, well within ThreatDivertRadius(600)
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(50f, u.OrderTargetPos.Value.X, 3); // threat, not the enemy base at 300
    }

    [Fact]
    public void Unit_ignores_a_threat_beyond_ThreatDivertRadius_of_all_owned_bases()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(200, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Threats.Add(Threat(700f)); // 700 > ThreatDivertRadius(600) from the only owned base
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(200f, u.OrderTargetPos.Value.X, 3); // normal enemy-base targeting, not the far threat
    }

    [Fact]
    public void Unit_ignores_a_threat_near_an_enemy_owned_base_it_does_not_own()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Threats.Add(Threat(1050f)); // near the ENEMY base (50 away), but 1050 from the own base (>600)
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(1000f, u.OrderTargetPos.Value.X, 3); // marches on the enemy base as usual
    }

    [Fact]
    public void Diversion_reverts_once_the_threat_is_gone()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        var threat = Threat(50f);
        s.Threats.Add(threat);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(50f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // diverted while the threat lives

        threat.CurrentHP = 0f; // defeated: Game layer would remove it next tick, but even before removal...

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(300f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // ...defeated threats no longer divert
    }

    // --- Task59: WarState.ThreatRelations gating + Nemesis priority for diversion ---

    [Fact]
    public void Unit_does_not_divert_to_a_threat_its_faction_is_not_hostile_to()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Threats.Add(Threat(50f)); // near own base, but faction 0 is set to Neutral toward Kaiju below
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Neutral);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(300f, u.OrderTargetPos.Value.X, 3); // ignores the threat, marches on the enemy base instead
    }

    [Fact]
    public void Unit_prefers_a_nemesis_threat_over_a_closer_ordinary_hostile_threat()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);

        var closeKaiju = Threat(30f); // closer, but ordinary Hostile
        var fartherAlien = new ExternalThreat { Id = 2, Kind = ThreatKind.Alien, Position = new WorldPos(200f, 0f, 0f), Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f }; // farther, but Nemesis
        s.Threats.Add(closeKaiju);
        s.Threats.Add(fartherAlien);
        s.ThreatRelations.Set(0, ThreatKind.Alien, Relation.Nemesis);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(200f, u.OrderTargetPos.Value.X, 3); // the farther Nemesis threat wins over the closer ordinary Hostile one
    }

    [Fact]
    public void Player_ordered_Hold_units_are_never_diverted()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        s.Threats.Add(Threat(10f)); // right next to the unit and its own base
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)) { Order = UnitOrder.Hold };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, u.State);
    }

    // --- Task62: Nemesis threats waive ThreatDivertRadius entirely (any base type qualifies) ---

    [Fact]
    public void Nemesis_threat_far_beyond_ThreatDivertRadius_is_diverted_to_when_faction_owns_a_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Navy, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 }; // any BaseType qualifies
        s.Bases.Add(ownBase);
        var nemesisThreat = new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(5000f, 0f, 0f), Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f };
        s.Threats.Add(nemesisThreat); // 5000 >> ThreatDivertRadius(600)
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Nemesis);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.Equal(5000f, u.OrderTargetPos.Value.X, 3); // distance limit waived for Nemesis
    }

    [Fact]
    public void Ordinary_hostile_threat_beyond_ThreatDivertRadius_is_still_not_diverted_to()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(150, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        s.Threats.Add(Threat(5000f)); // ordinary Hostile (default ThreatRelations), far beyond 600
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(150f, u.OrderTargetPos.Value.X, 3); // radius still applies to plain Hostile relations
    }

    [Fact]
    public void Nemesis_threat_is_not_diverted_to_when_faction_owns_no_base_at_all()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(enemyBase); // faction 0 owns zero bases
        var nemesisThreat = new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(50f, 0f, 0f), Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f };
        s.Threats.Add(nemesisThreat);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Nemesis);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(300f, u.OrderTargetPos.Value.X, 3); // no owned base to divert "toward" -> falls back to enemy base targeting
    }

    [Fact]
    public void Player_ordered_units_are_never_diverted_even_by_an_unlimited_range_nemesis_threat()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var nemesisThreat = new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(9000f, 0f, 0f), Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f };
        s.Threats.Add(nemesisThreat);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Nemesis);
        var rallyHoldUnit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0))
        {
            Order = UnitOrder.RallyHold,
            RallyPoint = new WorldPos(20f, 0f, 0f)
        };
        s.Units.Add(rallyHoldUnit);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        // AssignAdvance must not touch RallyHold units at all (Task48 contract), regardless of the
        // Task62 unlimited-range Nemesis rule: OrderTargetPos stays unset, RallyPoint is untouched.
        Assert.False(rallyHoldUnit.OrderTargetPos.HasValue);
        Assert.Equal(20f, rallyHoldUnit.RallyPoint.Value.X, 3);
    }
}
