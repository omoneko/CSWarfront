using CSWarfront.Core;
using Xunit;

/// <summary>
/// InvasionEvents（Task94: Workshopコメント要望「敵ユニットが都市の外からスポーンして攻めてくる
/// オプション」、Task95: 実機フィードバックにより侵略者役を専用のInvader勢力へ変更）のテスト。
/// </summary>
public class InvasionEventsTests
{
    /// <summary>勢力5＋Invader・勢力0が基地1つを所有する標準状態。</summary>
    private static WarState DefendedState()
    {
        var s = new WarState();
        for (byte i = 0; i < 5; i++) s.Factions.Add(new Faction(i, "F" + i));
        InvasionEvents.EnsureInvaderFaction(s);
        LandUnitRoster.RegisterAll(s.Types);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void EnsureInvaderFaction_is_idempotent()
    {
        var s = new WarState();
        var first = InvasionEvents.EnsureInvaderFaction(s);
        var second = InvasionEvents.EnsureInvaderFaction(s);

        Assert.Same(first, second);
        Assert.Equal(Faction.InvaderFactionId, first.Id);
        Assert.Equal("Invader", first.Name);
        Assert.False(first.IsPlayer);
        Assert.Single(s.Factions);
    }

    [Fact]
    public void Invader_relations_are_hardcoded_hostile_and_cannot_be_changed()
    {
        var s = DefendedState();
        for (byte f = 0; f < 5; f++)
        {
            Assert.True(s.Relations.Get(Faction.InvaderFactionId, f).IsHostile());
            Assert.True(s.Relations.Get(f, Faction.InvaderFactionId).IsHostile());
        }

        // Setは黙って無視される（Options等のどの操作でも友好化できない）。
        s.Relations.Set(Faction.InvaderFactionId, 0, Relation.Allied);
        Assert.True(s.Relations.Get(Faction.InvaderFactionId, 0).IsHostile());

        // 外部脅威（KAIJU/Alien）に対しても常時Hostile（表外Idの既定）。
        Assert.True(s.ThreatRelations.Get(Faction.InvaderFactionId, ThreatKind.Kaiju).IsHostile());
    }

    [Fact]
    public void FactionStatus_never_eliminates_the_invader_faction()
    {
        var s = DefendedState(); // Invaderは基地を1つも所有しない
        FactionStatus.Refresh(s);

        Assert.False(s.FindFaction(Faction.InvaderFactionId).Eliminated);
        Assert.True(s.FindFaction(1).Eliminated); // 通常勢力は基地なし→従来どおりEliminated
    }

    [Fact]
    public void SpawnWave_creates_an_invader_wave_at_the_map_edge()
    {
        var s = DefendedState();
        int spawned = InvasionEvents.SpawnWave(s);

        Assert.True(spawned > 0, "expected a wave to spawn");
        Assert.Equal(spawned, s.Units.Count);

        // 侵略者役 = 専用のInvader勢力（Task95。既存勢力を使い回さない）。
        foreach (var u in s.Units)
            Assert.Equal(Faction.InvaderFactionId, u.FactionId);

        // 防衛側（勢力0）と敵対関係になっている（ハードコード）。
        Assert.True(s.Relations.Get(Faction.InvaderFactionId, 0).IsHostile());

        // スポーン位置はマップ端の辺上（±SpawnEdgeDistance付近、散らし60m以内）。
        foreach (var u in s.Units)
        {
            float ax = System.Math.Abs(u.Position.X);
            float az = System.Math.Abs(u.Position.Z);
            float m = System.Math.Max(ax, az);
            Assert.InRange(m, InvasionEvents.SpawnEdgeDistance - 100f, InvasionEvents.SpawnEdgeDistance + 100f);
        }
    }

    [Fact]
    public void SpawnWave_targets_a_city_base_via_normal_ai_advance()
    {
        var s = DefendedState();
        InvasionEvents.SpawnWave(s);

        // スポーン後は通常のAI進軍（AssignAdvance）がそのまま都市内の基地を目標にする。
        InvasionOrders.AssignAdvance(s, Faction.InvaderFactionId, 0.1f);
        foreach (var u in s.Units)
        {
            Assert.Equal(UnitState.Moving, u.State);
            Assert.True(u.OrderTargetPos.HasValue, "expected an advance target");
            Assert.Equal(0f, u.OrderTargetPos.Value.X, 1);
            Assert.Equal(0f, u.OrderTargetPos.Value.Z, 1);
        }
    }

    [Fact]
    public void Defenders_divert_to_intercept_invader_units()
    {
        var s = DefendedState();
        var defender = new UnitInstance(1000, "Tank_T1", 0, 100f, new WorldPos(50, 0, 50));
        s.Units.Add(defender);

        InvasionEvents.SpawnWave(s);

        // Task96: 基地持ち勢力のAIユニットは、敵基地の有無に関わらずInvader部隊の迎撃へ向かう。
        InvasionOrders.AssignAdvance(s, 0, 0.1f);

        Assert.Equal(UnitState.Moving, defender.State);
        Assert.True(defender.OrderTargetPos.HasValue, "expected an intercept target");
        bool pointsAtAnInvader = false;
        foreach (var u in s.Units)
        {
            if (u.FactionId != Faction.InvaderFactionId) continue;
            if (defender.OrderTargetPos.Value.HorizontalDistanceTo(u.Position) < 1f) pointsAtAnInvader = true;
        }
        Assert.True(pointsAtAnInvader, "expected the intercept target to be an invader unit position");
    }

    [Fact]
    public void Defenders_return_to_normal_once_the_wave_is_destroyed()
    {
        var s = DefendedState();
        var defender = new UnitInstance(1000, "Tank_T1", 0, 100f, new WorldPos(50, 0, 50));
        s.Units.Add(defender);

        InvasionEvents.SpawnWave(s);
        InvasionOrders.AssignAdvance(s, 0, 0.1f);
        WorldPos interceptTarget = defender.OrderTargetPos.Value;

        // 侵攻部隊を全滅させると、次の呼び出しから通常行動へ戻る（状態を持たない毎tick再判定）。
        foreach (var u in s.Units)
            if (u.FactionId == Faction.InvaderFactionId) { u.CurrentHP = 0f; u.State = UnitState.Dead; }
        InvasionOrders.AssignAdvance(s, 0, 0.1f);

        // 敵対所有基地が無いこの状態では自拠点への撤収（またはIdle）になり、迎撃地点は追わない。
        if (defender.OrderTargetPos.HasValue)
            Assert.True(defender.OrderTargetPos.Value.HorizontalDistanceTo(interceptTarget) > 100f,
                "expected the defender to stop chasing the dead wave's position");
    }

    [Fact]
    public void Baseless_factions_do_not_divert_to_invaders()
    {
        var s = DefendedState(); // 基地を持つのは勢力0だけ
        var bystander = new UnitInstance(1001, "Tank_T1", 2, 100f, new WorldPos(50, 0, 50));
        s.Units.Add(bystander);

        InvasionEvents.SpawnWave(s);
        InvasionOrders.AssignAdvance(s, 2, 0.1f);

        // 基地なし勢力は迎撃対象外（宿敵脅威と同じ規則）。敵対する勢力0の基地への通常進軍はする。
        if (bystander.OrderTargetPos.HasValue)
        {
            foreach (var u in s.Units)
            {
                if (u.FactionId != Faction.InvaderFactionId) continue;
                Assert.True(bystander.OrderTargetPos.Value.HorizontalDistanceTo(u.Position) > 1f,
                    "expected no intercept order for a base-less faction");
            }
        }
    }

    [Fact]
    public void SpawnWave_does_nothing_when_no_faction_owns_a_base()
    {
        var s = DefendedState();
        s.Bases.Clear();

        Assert.Equal(0, InvasionEvents.SpawnWave(s));
        Assert.Empty(s.Units);
    }

    [Fact]
    public void SpawnWave_scales_tier_with_the_defenders_unlocked_tier()
    {
        var s = DefendedState();
        s.Factions[0].UnlockedTier = 3;

        InvasionEvents.SpawnWave(s);

        Assert.Contains(s.Units, u => u.TypeKey.EndsWith("_T3"));
        Assert.DoesNotContain(s.Units, u => u.TypeKey.EndsWith("_T1"));
        Assert.Equal(3, s.FindFaction(Faction.InvaderFactionId).UnlockedTier);
    }

    private class AllWaterSampler : IWaterSampler
    {
        public bool IsWater(float x, float z) { return true; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return true; }
    }

    [Fact]
    public void SpawnWave_skips_when_the_edge_is_all_water()
    {
        var s = DefendedState();
        s.Water = new AllWaterSampler();

        Assert.Equal(0, InvasionEvents.SpawnWave(s));
        Assert.Empty(s.Units);
    }

    [Fact]
    public void Advance_does_nothing_when_disabled()
    {
        var s = DefendedState();
        for (int i = 0; i < 100; i++)
        {
            s.TickCounter++;
            InvasionEvents.Advance(s, InvasionEvents.CheckIntervalHours, false, 2);
        }
        Assert.Empty(s.Units);
    }

    [Fact]
    public void Advance_eventually_spawns_a_wave_when_enabled()
    {
        var s = DefendedState();
        int spawnedTotal = 0;
        // High頻度(0.21/判定)で100判定 → 決定的ハッシュでもほぼ確実に1回以上当選する。
        for (int i = 0; i < 100 && spawnedTotal == 0; i++)
        {
            s.TickCounter++;
            spawnedTotal += InvasionEvents.Advance(s, InvasionEvents.CheckIntervalHours, true, 2);
        }
        Assert.True(spawnedTotal > 0, "expected at least one invasion wave in 100 checks at high frequency");
    }

    [Fact]
    public void Advance_respects_the_check_interval()
    {
        var s = DefendedState();
        // 判定間隔未満のdtでは、何度呼んでも判定自体が走らない（＝絶対にスポーンしない）。
        for (int i = 0; i < 50; i++)
        {
            s.TickCounter++;
            InvasionEvents.Advance(s, InvasionEvents.CheckIntervalHours * 0.01f, true, 2);
        }
        Assert.Empty(s.Units);
    }
}
