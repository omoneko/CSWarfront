using System.IO;
using CSWarfront.Core;
using Xunit;

public class WarStateSerializerTests
{
    private static WarState Sample()
    {
        var s = new WarState();
        var red = new Faction(0, "Red"); red.AddTreasury(123.5f); red.HomeBaseId = 200; red.IsPlayer = true;
        var blue = new Faction(1, "Blue"); blue.AddTreasury(10f);
        s.Factions.Add(red); s.Factions.Add(blue);
        s.Relations.Set(0, 1, Relation.Hostile);
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 5));
        b.OwnerFactionId = 0; b.CurrentHP = 250f; b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f) { Progress = 0.3f });
        s.Bases.Add(b);
        var u = new UnitInstance(7, "Tank_T1", 1, 80f, new WorldPos(1, 2, 3));
        u.State = UnitState.Moving; u.OrderTargetPos = new WorldPos(40, 0, 5);
        s.Units.Add(u);
        s.NextInstanceId = 8;
        return s;
    }

    [Fact]
    public void Roundtrip_preserves_logical_state()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Equal(2, r.Factions.Count);
        Assert.Equal(123.5f, r.FindFaction(0).Treasury, 3);
        Assert.True(r.FindFaction(0).IsPlayer);
        Assert.Equal((ushort)200, r.FindFaction(0).HomeBaseId.Value);
        Assert.Equal(Relation.Hostile, r.Relations.Get(0, 1));
        Assert.Single(r.Bases);
        Assert.Equal(250f, r.Bases[0].CurrentHP, 3);
        Assert.Single(r.Bases[0].Queue);
        Assert.Equal(0.3f, r.Bases[0].Queue[0].Progress, 3);
        Assert.Single(r.Units);
        Assert.Equal(80f, r.FindUnit(7).CurrentHP, 3);
        Assert.Equal(UnitState.Moving, r.FindUnit(7).State);
        Assert.True(r.FindUnit(7).OrderTargetPos.HasValue);
        Assert.Equal((uint)8, r.NextInstanceId);
    }

    [Fact]
    public void Deserialize_empty_returns_fresh_state()
    {
        var types = new UnitTypeRegistry();
        var r = WarStateSerializer.Deserialize(null, types);
        Assert.NotNull(r);
        Assert.Empty(r.Factions);
    }

    [Fact]
    public void Roundtrip_v2_preserves_CaptureGraceHours()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        s.Bases[0].CaptureGraceHours = 17.5f;
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Bases);
        Assert.Equal(17.5f, r.Bases[0].CaptureGraceHours, 3);
    }

    /// <summary>旧形式（v1、基地ブロック末尾にCaptureGraceHoursが無い）を読んでも
    /// 例外にならず、CaptureGraceHoursが既定値0で復元されることを保証する。</summary>
    [Fact]
    public void Deserialize_v1_format_defaults_CaptureGraceHours_to_zero()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1); // version 1（旧形式）
            w.Write(0); // factions count
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                    w.Write((int)Relation.Neutral);
            w.Write(1); // bases count
            w.Write((ushort)200); w.Write((int)BaseType.Army);
            w.Write(true); w.Write((byte)0); // owner
            w.Write(40f); w.Write(0f); w.Write(5f); // pos
            w.Write(500f); w.Write(false); // influence radius, isHq
            w.Write(500f); w.Write(250f); // maxHp, currentHp
            w.Write(0); // queue count
            // 注意: v1にはCaptureGraceHoursが無いのでここで終わり
            w.Write(0); // units count
            w.Write((uint)1); // nextInstanceId
            w.Flush();
            bytes = ms.ToArray();
        }

        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Bases);
        Assert.Equal(0f, r.Bases[0].CaptureGraceHours, 3);
        Assert.Equal(250f, r.Bases[0].CurrentHP, 3);
        Assert.True(r.Bases[0].AutoProduce); // v1 has no AutoProduce byte either -> default true
    }

    // --- Task34: v2 -> v3, AutoProduce appended at the end of the per-base block ---

    [Fact]
    public void Roundtrip_v3_preserves_AutoProduce_false()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        s.Bases[0].AutoProduce = false;
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Bases);
        Assert.False(r.Bases[0].AutoProduce);
    }

    [Fact]
    public void Roundtrip_v3_preserves_AutoProduce_true()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        s.Bases[0].AutoProduce = true;
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Bases);
        Assert.True(r.Bases[0].AutoProduce);
    }

    /// <summary>旧形式（v2、基地ブロック末尾にAutoProduceが無い）を読んでも例外にならず、
    /// AutoProduceが既定値true（AI自動生産の従来動作を維持）で復元されることを保証する。</summary>
    [Fact]
    public void Deserialize_v2_format_defaults_AutoProduce_to_true()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(2); // version 2（AutoProduce導入前）
            w.Write(0); // factions count
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                    w.Write((int)Relation.Neutral);
            w.Write(1); // bases count
            w.Write((ushort)200); w.Write((int)BaseType.Army);
            w.Write(true); w.Write((byte)0); // owner
            w.Write(40f); w.Write(0f); w.Write(5f); // pos
            w.Write(500f); w.Write(false); // influence radius, isHq
            w.Write(500f); w.Write(250f); // maxHp, currentHp
            w.Write(0); // queue count
            w.Write(3.5f); // CaptureGraceHours (v2 field)
            // 注意: v2にはAutoProduceが無いのでここで終わり
            w.Write(0); // units count
            w.Write((uint)1); // nextInstanceId
            w.Flush();
            bytes = ms.ToArray();
        }

        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Bases);
        Assert.Equal(3.5f, r.Bases[0].CaptureGraceHours, 3);
        Assert.True(r.Bases[0].AutoProduce);
    }

    // --- Task35: v3 -> v4, ResearchPoints/UnlockedTier appended at the end of the per-faction block ---

    [Fact]
    public void Roundtrip_v4_preserves_ResearchPoints_and_UnlockedTier()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        s.Factions[0].AddResearchPoints(340f);
        s.Factions[0].UnlockedTier = 3;
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Equal(340f, r.FindFaction(0).ResearchPoints, 3);
        Assert.Equal((byte)3, r.FindFaction(0).UnlockedTier);
        // second faction, never touched, keeps its defaults
        Assert.Equal(0f, r.FindFaction(1).ResearchPoints, 3);
        Assert.Equal((byte)1, r.FindFaction(1).UnlockedTier);
    }

    /// <summary>旧形式（v3、勢力ブロック末尾にResearchPoints/UnlockedTierが無い）を読んでも例外にならず、
    /// ResearchPointsが0f・UnlockedTierが1（従来のTier1のみ解禁）で復元されることを保証する。</summary>
    [Fact]
    public void Deserialize_v3_format_defaults_ResearchPoints_to_zero_and_UnlockedTier_to_one()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(3); // version 3（ResearchPoints/UnlockedTier導入前）
            w.Write(1); // factions count
            w.Write((byte)0); w.Write("Red");
            w.Write(50f); // treasury
            w.Write(false); w.Write((ushort)0); // no home base
            w.Write(true); w.Write(false); // isPlayer, eliminated
            // 注意: v3にはResearchPoints/UnlockedTierが無いのでここで終わり
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                    w.Write((int)Relation.Neutral);
            w.Write(0); // bases count
            w.Write(0); // units count
            w.Write((uint)1); // nextInstanceId
            w.Flush();
            bytes = ms.ToArray();
        }

        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Single(r.Factions);
        Assert.Equal(50f, r.FindFaction(0).Treasury, 3);
        Assert.Equal(0f, r.FindFaction(0).ResearchPoints, 3);
        Assert.Equal((byte)1, r.FindFaction(0).UnlockedTier);
    }

    // --- Task59: v4 -> v5, ThreatRelations (5 factions x ThreatKind) appended at the very end ---

    [Fact]
    public void Roundtrip_v5_preserves_ThreatRelations()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Neutral);
        s.ThreatRelations.Set(1, ThreatKind.Alien, Relation.Nemesis);
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Equal(Relation.Neutral, r.ThreatRelations.Get(0, ThreatKind.Kaiju));
        Assert.Equal(Relation.Nemesis, r.ThreatRelations.Get(1, ThreatKind.Alien));
        // untouched entries keep their default (Hostile)
        Assert.Equal(Relation.Hostile, r.ThreatRelations.Get(0, ThreatKind.Alien));
        Assert.Equal(Relation.Hostile, r.ThreatRelations.Get(2, ThreatKind.Kaiju));
    }

    /// <summary>旧形式（v4、ペイロード末尾にThreatRelationsが無い）を読んでも例外にならず、
    /// 全エントリが既定値Hostile（Task58までの「常に無条件敵対」）で復元されることを保証する。</summary>
    [Fact]
    public void Deserialize_v4_format_defaults_ThreatRelations_to_hostile()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(4); // version 4（ThreatRelations導入前）
            w.Write(1); // factions count
            w.Write((byte)0); w.Write("Red");
            w.Write(50f); // treasury
            w.Write(false); w.Write((ushort)0); // no home base
            w.Write(true); w.Write(false); // isPlayer, eliminated
            w.Write(0f); w.Write((byte)1); // ResearchPoints, UnlockedTier (v4 fields)
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                    w.Write((int)Relation.Neutral);
            w.Write(0); // bases count
            w.Write(0); // units count
            w.Write((uint)1); // nextInstanceId
            // 注意: v4にはThreatRelationsが無いのでここで終わり
            w.Flush();
            bytes = ms.ToArray();
        }

        var r = WarStateSerializer.Deserialize(bytes, types);

        for (byte f = 0; f < 5; f++)
        {
            Assert.Equal(Relation.Hostile, r.ThreatRelations.Get(f, ThreatKind.Kaiju));
            Assert.Equal(Relation.Hostile, r.ThreatRelations.Get(f, ThreatKind.Alien));
        }
    }
}
