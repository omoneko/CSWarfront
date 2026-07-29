using CSWarfront.Core;
using Xunit;

// Task65: ThreatAuraStep — threats (Godzilla/Alien) hurt any unit merely standing nearby, regardless of
// that faction's ThreatRelations toward the threat (a rampaging monster does not check IFF). Damage is
// flat-per-hour and ignores UnitType.Armor entirely (unlike ThreatCombatStep.ThreatArmor, which only
// applies to the unit-attacks-threat direction).
//
// Worked numbers (see ThreatAuraStep.GodzillaAuraDamage doc comment for the full rationale):
//   Tank_T1 (HP=140): 140 / 90 = 1.555..h ~= 1.6h to die standing in a Godzilla aura.
//   Tank_T5 (HP=336, TierScaling.Hp(140,5)=140*2.4): 336 / 90 = 3.733..h ~= 3.7h.
public class ThreatAuraStepTests
{
    private static WarState OneUnitOneThreat(float distance, ThreatKind kind = ThreatKind.Kaiju,
        float radius = 10f, float unitHp = 140f, float armor = 10f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, unitHp, new WorldPos(distance, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 1,
            Kind = kind,
            Position = new WorldPos(0f, 0f, 0f),
            Radius = radius,
            MaxHP = 65000f,
            CurrentHP = 65000f
        });
        return s;
    }

    [Fact]
    public void Unit_within_radius_plus_margin_loses_HP_scaled_by_dt()
    {
        // Radius(10) + AuraMargin(60) = 70 -> distance 65 is in range.
        var s = OneUnitOneThreat(65f);
        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var half = OneUnitOneThreat(65f);
        ThreatAuraStep.Advance(half, 0.5f);
        float halfDmg = 140f - half.FindUnit(1).CurrentHP;

        Assert.Equal(ThreatAuraStep.GodzillaAuraDamage * 0.5f, halfDmg, 3);
    }

    [Fact]
    public void Unit_outside_radius_plus_margin_is_untouched()
    {
        // Radius(10) + AuraMargin(60) = 70 -> distance 71 is out of range.
        var s = OneUnitOneThreat(71f);
        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Armor_is_ignored_heavily_armored_unit_takes_full_flat_damage()
    {
        var lightArmor = OneUnitOneThreat(65f, armor: 0f);
        var heavyArmor = OneUnitOneThreat(65f, armor: 10000f); // absurdly high armor, still irrelevant here
        ThreatAuraStep.Advance(lightArmor, 1f);
        ThreatAuraStep.Advance(heavyArmor, 1f);

        Assert.Equal(lightArmor.FindUnit(1).CurrentHP, heavyArmor.FindUnit(1).CurrentHP, 3);
        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, heavyArmor.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Applies_regardless_of_ThreatRelations_even_when_Allied()
    {
        var s = OneUnitOneThreat(65f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Allied);
        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Applies_regardless_of_ThreatRelations_even_when_Neutral()
    {
        var s = OneUnitOneThreat(65f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Neutral);
        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Kaiju_and_Alien_rates_differ()
    {
        var kaiju = OneUnitOneThreat(65f, ThreatKind.Kaiju);
        var alien = OneUnitOneThreat(65f, ThreatKind.Alien);
        ThreatAuraStep.Advance(kaiju, 1f);
        ThreatAuraStep.Advance(alien, 1f);

        float kaijuDmg = 140f - kaiju.FindUnit(1).CurrentHP;
        float alienDmg = 140f - alien.FindUnit(1).CurrentHP;

        Assert.Equal(ThreatAuraStep.GodzillaAuraDamage, kaijuDmg, 3);
        Assert.Equal(ThreatAuraStep.AlienAuraDamage, alienDmg, 3);
        Assert.NotEqual(kaijuDmg, alienDmg);
    }

    [Fact]
    public void Unit_dies_and_a_kill_is_recorded_when_HP_reaches_zero()
    {
        var s = OneUnitOneThreat(65f, unitHp: ThreatAuraStep.GodzillaAuraDamage * 0.5f);
        ThreatAuraStep.Advance(s, 1f); // dmg = GodzillaAuraDamage > remaining HP

        UnitInstance u = s.FindUnit(1);
        Assert.Equal(UnitState.Dead, u.State);
        Assert.True(u.CurrentHP <= 0f); // CombatStep's own death pass doesn't clamp either; overkill goes negative

        Assert.Single(s.RecentKills);
        KillEvent k = s.RecentKills[0];
        Assert.Equal((byte)0, k.FactionId);
        Assert.Equal(UnitCategory.Tank, k.Category);
    }

    [Fact]
    public void Defeated_threats_no_longer_apply_aura_damage()
    {
        var s = OneUnitOneThreat(65f);
        s.Threats[0].CurrentHP = 0f;
        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Dead_units_are_skipped_and_not_re_killed()
    {
        var s = OneUnitOneThreat(65f);
        s.FindUnit(1).State = UnitState.Dead;
        s.FindUnit(1).CurrentHP = 0f;
        ThreatAuraStep.Advance(s, 1f);

        Assert.Empty(s.RecentKills); // already-dead unit does not generate a second KillEvent
    }

    [Fact]
    public void Multiple_units_in_range_are_all_damaged_independently()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 140f, new WorldPos(30f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 140f, new WorldPos(-40f, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(0f, 0f, 0f),
            Radius = 10f, MaxHP = 65000f, CurrentHP = 65000f
        });

        ThreatAuraStep.Advance(s, 1f);

        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(140f - ThreatAuraStep.GodzillaAuraDamage, s.FindUnit(2).CurrentHP, 3);
    }
}
