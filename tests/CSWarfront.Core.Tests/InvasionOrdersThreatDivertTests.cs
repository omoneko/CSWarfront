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
}
