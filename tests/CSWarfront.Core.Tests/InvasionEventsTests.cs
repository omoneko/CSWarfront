using CSWarfront.Core;
using Xunit;

/// <summary>
/// InvasionEvents（Task94: Workshopコメント要望「敵ユニットが都市の外からスポーンして攻めてくる
/// オプション」）のテスト。
/// </summary>
public class InvasionEventsTests
{
    /// <summary>勢力5・勢力0が基地1つを所有する標準状態。</summary>
    private static WarState DefendedState()
    {
        var s = new WarState();
        for (byte i = 0; i < 5; i++) s.Factions.Add(new Faction(i, "F" + i));
        LandUnitRoster.RegisterAll(s.Types);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void SpawnWave_creates_a_hostile_wave_at_the_map_edge()
    {
        var s = DefendedState();
        int spawned = InvasionEvents.SpawnWave(s);

        Assert.True(spawned > 0, "expected a wave to spawn");
        Assert.Equal(spawned, s.Units.Count);

        // 侵略者役 = 基地を持たない勢力（基地所有の勢力0ではない）。
        byte attackerId = s.Units[0].FactionId;
        Assert.NotEqual((byte)0, attackerId);

        // 防衛側（勢力0）と敵対関係になっている。
        Assert.True(s.Relations.Get(attackerId, 0).IsHostile());

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
    public void SpawnWave_does_nothing_when_no_faction_owns_a_base()
    {
        var s = DefendedState();
        s.Bases.Clear();

        Assert.Equal(0, InvasionEvents.SpawnWave(s));
        Assert.Empty(s.Units);
    }

    [Fact]
    public void SpawnWave_does_not_override_a_nemesis_relation()
    {
        var s = DefendedState();
        // 事前に全ての非防衛勢力を勢力0の宿敵に設定しておく（侵略者役がどれになっても検証できるように）。
        for (byte f = 1; f < 5; f++) s.Relations.Set(f, 0, Relation.Nemesis);

        InvasionEvents.SpawnWave(s);

        byte attackerId = s.Units[0].FactionId;
        Assert.Equal(Relation.Nemesis, s.Relations.Get(attackerId, 0)); // Hostileへ格下げされない
    }

    [Fact]
    public void SpawnWave_scales_tier_with_the_defenders_unlocked_tier()
    {
        var s = DefendedState();
        s.Factions[0].UnlockedTier = 3;

        InvasionEvents.SpawnWave(s);

        Assert.Contains(s.Units, u => u.TypeKey.EndsWith("_T3"));
        Assert.DoesNotContain(s.Units, u => u.TypeKey.EndsWith("_T1"));
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
