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
