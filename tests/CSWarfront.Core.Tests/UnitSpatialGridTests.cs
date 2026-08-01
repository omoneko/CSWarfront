using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>
/// UnitSpatialGrid（Task97: 交戦判定の空間グリッド化・O(N²)対策）のテスト。
/// 中核となる性質は「結果が総当たり版TargetSearch.FindNearestHostileと完全に同一」であること
/// （決定的シミュレーションの維持。タイブレークのリスト先頭優先を含む）。
/// </summary>
public class UnitSpatialGridTests
{
    /// <summary>fmix32風の決定的ハッシュで座標を散らす（テスト内乱数は使わない、Core全体の方針）。</summary>
    private static float Hash01(uint a, uint b)
    {
        unchecked
        {
            uint h = a * 2654435761u + b;
            h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>複数勢力・宿敵関係・死亡ユニット・セル境界跨ぎ・負座標を含む散布状態。</summary>
    private static WarState ScatteredState(int unitCount)
    {
        var s = new WarState();
        for (byte i = 0; i < 5; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 5);
        s.Relations.Set(0, 1, Relation.Nemesis);
        s.Relations.Set(2, 3, Relation.Neutral); // 敵対でないペアも混ぜる
        s.Types.Register(MvpUnitTypes.Tank_T1());

        for (uint i = 0; i < (uint)unitCount; i++)
        {
            float x = (Hash01(i, 1u) - 0.5f) * 4000f; // ±2000m（セル256mを大きく跨ぐ）
            float z = (Hash01(i, 2u) - 0.5f) * 4000f;
            byte faction = (byte)(Hash01(i, 3u) * 5f);
            var u = new UnitInstance(100 + i, "Tank_T1", faction, 100f, new WorldPos(x, 0, z));
            if (Hash01(i, 4u) < 0.15f) { u.CurrentHP = 0f; u.State = UnitState.Dead; } // 死亡も混ぜる
            s.Units.Add(u);
        }
        return s;
    }

    [Fact]
    public void Grid_search_matches_linear_search_exactly()
    {
        var s = ScatteredState(120);
        s.UnitGrid.Build(s.Units);

        float[] ranges = { 60f, 250f, 600f, 5000f }; // セル内・セル跨ぎ・広域の各ケース
        foreach (float range in ranges)
        {
            for (int i = 0; i < s.Units.Count; i++)
            {
                UnitInstance self = s.Units[i];
                if (!self.IsAlive) continue;

                UnitInstance linear = TargetSearch.FindNearestHostile(self, s.Units, s.Relations, range,
                    DomainMask.All, s.Types);
                UnitInstance grid = TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, range,
                    DomainMask.All, s.Types);

                Assert.Same(linear, grid);
            }
        }
    }

    [Fact]
    public void Grid_search_prefers_the_lower_list_index_on_distance_ties()
    {
        // 等距離の敵2体（自分の東西に同距離で、別セルに入るよう十分離す）。
        // 総当たり版はリスト先頭優先＝先に追加した方を返す。グリッド版も同じでなければならない。
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var east = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(300f, 0, 0));
        var west = new UnitInstance(3, "Tank_T1", 1, 100f, new WorldPos(-300f, 0, 0));
        s.Units.Add(self); s.Units.Add(east); s.Units.Add(west);
        s.UnitGrid.Build(s.Units);

        UnitInstance linear = TargetSearch.FindNearestHostile(self, s.Units, s.Relations, 500f,
            DomainMask.All, s.Types);
        UnitInstance grid = TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 500f,
            DomainMask.All, s.Types);

        Assert.Same(east, linear); // リスト先頭優先の前提確認
        Assert.Same(linear, grid);
    }

    [Fact]
    public void Grid_search_finds_targets_across_cell_boundaries()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        // セル境界(256m)ぎりぎりの両側。範囲60でも見つからなければならない。
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(255f, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(258f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        Assert.Same(enemy, TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.All, s.Types));
    }

    [Fact]
    public void Grid_search_applies_the_domain_filter()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Domain.Land
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(30f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        // 対空専用（Airのみ狙える）なら、射程内の陸上ユニットは対象外。
        Assert.Null(TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.Air, s.Types));
        Assert.Same(enemy, TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.Land, s.Types));
    }

    [Fact]
    public void Units_killed_after_build_disappear_from_subsequent_searches()
    {
        // CombatStepは同一tick内で先に倒された敵を以後の探索から除外する（総当たり版と同じ挙動）。
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(30f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        enemy.CurrentHP = 0f; // Build後に死亡（State遷移前でもIsAlive=falseになる）
        Assert.Null(TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.All, s.Types));
    }
}
