using System.IO;
namespace CSWarfront.Core
{
    /// <summary>WarStateの論理状態をバイト列へ往復（表現参照は保存しない）。</summary>
    public static class WarStateSerializer
    {
        // v1 -> v2: 基地ブロック末尾に CaptureGraceHours (float) を追加（Task24）。
        // v2 -> v3: 基地ブロックのさらに末尾に AutoProduce (bool) を追加（Task34）。
        // v3 -> v4: 勢力ブロックの末尾に ResearchPoints (float) / UnlockedTier (byte) を追加（Task35）。
        // v4 -> v5: ペイロード全体の末尾に ThreatRelations（勢力5×ThreatKind2、int=(int)Relation）を
        //           追加（Task59）。v4以前を読んだ場合は追記ブロックが存在しないため、ThreatRelationsは
        //           コンストラクタ既定値の全Hostileのまま（Task58までの「常に無条件敵対」を維持）。
        // v5 -> v6: 基地ブロックのさらに末尾に StockpiledMissiles (int) / MissileBuildProgress (float) を
        //           追加（Task63：弾道ミサイル基地の備蓄・建造進捗）。v5以前を読んだ場合はどちらも
        //           既定値0（備蓄0発・建造中でない）で復元される（MissileBaseはTask63以前は配置可能な
        //           プレハブが存在しなかったため実害は無い）。
        // バイナリ形式は位置依存のため、既存フィールドの間には挿入せず必ず末尾に追記すること。
        private const int Version = 6;

        public static byte[] Serialize(WarState s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Version);
                // factions
                w.Write(s.Factions.Count);
                foreach (var f in s.Factions)
                {
                    w.Write(f.Id); w.Write(f.Name ?? "");
                    w.Write(f.Treasury);
                    w.Write(f.HomeBaseId.HasValue); w.Write(f.HomeBaseId.HasValue ? f.HomeBaseId.Value : (ushort)0);
                    w.Write(f.IsPlayer); w.Write(f.Eliminated);
                    w.Write(f.ResearchPoints); w.Write(f.UnlockedTier); // v4で追加（Task35）。ブロック末尾に追記。
                }
                // relations（5x5固定）
                for (int a = 0; a < 5; a++)
                    for (int b = 0; b < 5; b++)
                        w.Write((int)s.Relations.Get(a, b));
                // bases
                w.Write(s.Bases.Count);
                foreach (var b in s.Bases)
                {
                    w.Write(b.BaseId); w.Write((int)b.Type);
                    w.Write(b.OwnerFactionId.HasValue); w.Write(b.OwnerFactionId.HasValue ? b.OwnerFactionId.Value : (byte)0);
                    WritePos(w, b.Position);
                    w.Write(b.InfluenceRadius); w.Write(b.IsHeadquarters);
                    w.Write(b.MaxHP); w.Write(b.CurrentHP);
                    w.Write(b.Queue.Count);
                    foreach (var o in b.Queue) { w.Write(o.TypeKey ?? ""); w.Write(o.Cost); w.Write(o.BuildTime); w.Write(o.Progress); }
                    w.Write(b.CaptureGraceHours); // v2で追加。位置依存フォーマットのためブロック末尾に追記。
                    w.Write(b.AutoProduce); // v3で追加（Task34）。同じ理由でさらに末尾に追記。
                    w.Write(b.StockpiledMissiles); w.Write(b.MissileBuildProgress); // v6で追加（Task63）。
                }
                // units
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId); w.Write(u.TypeKey ?? ""); w.Write(u.FactionId); w.Write(u.CurrentHP);
                    WritePos(w, u.Position); w.Write((int)u.State);
                    w.Write(u.TargetId.HasValue); w.Write(u.TargetId.HasValue ? u.TargetId.Value : 0u);
                    w.Write(u.OrderTargetPos.HasValue);
                    WritePos(w, u.OrderTargetPos.HasValue ? u.OrderTargetPos.Value : new WorldPos(0, 0, 0));
                }
                w.Write(s.NextInstanceId);
                // threat relations（5勢力×ThreatKindCount固定、v5で追加。Task59）。ペイロード末尾に追記。
                for (int f = 0; f < 5; f++)
                    for (int k = 0; k < ThreatRelations.ThreatKindCount; k++)
                        w.Write((int)s.ThreatRelations.Get((byte)f, (ThreatKind)k));
                w.Flush();
                return ms.ToArray();
            }
        }

        public static WarState Deserialize(byte[] bytes, UnitTypeRegistry types)
        {
            var s = new WarState();
            s.Types = types;
            if (bytes == null || bytes.Length == 0) return s;
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                int version = r.ReadInt32(); // v2以降の分岐に使用（CaptureGraceHoursの有無）、v3以降（AutoProduceの有無）、v4以降（ResearchPoints/UnlockedTierの有無）、v5以降（ThreatRelationsの有無）、v6以降（StockpiledMissiles/MissileBuildProgressの有無）
                int fcount = r.ReadInt32();
                for (int i = 0; i < fcount; i++)
                {
                    byte id = r.ReadByte(); string name = r.ReadString();
                    var f = new Faction(id, name); f.AddTreasury(r.ReadSingle());
                    bool hasHome = r.ReadBoolean(); ushort home = r.ReadUInt16();
                    if (hasHome) f.HomeBaseId = home;
                    f.IsPlayer = r.ReadBoolean(); f.Eliminated = r.ReadBoolean();
                    if (version >= 4)
                    {
                        f.ResearchPoints = r.ReadSingle();
                        f.UnlockedTier = r.ReadByte();
                    }
                    else
                    {
                        f.ResearchPoints = 0f; // v3以前は既定値0（研究未着手）
                        f.UnlockedTier = 1;    // v3以前は既定値1（Tier1のみ解禁の従来挙動）
                    }
                    s.Factions.Add(f);
                }
                for (int a = 0; a < 5; a++)
                    for (int b = 0; b < 5; b++)
                        s.Relations.Set(a, b, (Relation)r.ReadInt32());
                int bcount = r.ReadInt32();
                for (int i = 0; i < bcount; i++)
                {
                    ushort baseId = r.ReadUInt16(); var type = (BaseType)r.ReadInt32();
                    bool hasOwner = r.ReadBoolean(); byte owner = r.ReadByte();
                    var pos = ReadPos(r);
                    var b = new MilitaryBase(baseId, type, pos);
                    if (hasOwner) b.OwnerFactionId = owner;
                    b.InfluenceRadius = r.ReadSingle(); b.IsHeadquarters = r.ReadBoolean();
                    b.MaxHP = r.ReadSingle(); b.CurrentHP = r.ReadSingle();
                    int qn = r.ReadInt32();
                    for (int q = 0; q < qn; q++)
                    {
                        var o = new ProductionOrder(r.ReadString(), r.ReadSingle(), r.ReadSingle());
                        o.Progress = r.ReadSingle(); b.Queue.Add(o);
                    }
                    b.CaptureGraceHours = version >= 2 ? r.ReadSingle() : 0f;
                    b.AutoProduce = version >= 3 ? r.ReadBoolean() : true; // v2以前は既定値true（従来の全自動挙動）
                    if (version >= 6)
                    {
                        b.StockpiledMissiles = r.ReadInt32();
                        b.MissileBuildProgress = r.ReadSingle();
                    }
                    else
                    {
                        b.StockpiledMissiles = 0; // v5以前は既定値0（備蓄0発）
                        b.MissileBuildProgress = 0f; // v5以前は既定値0（建造中でない）
                    }
                    s.Bases.Add(b);
                }
                int ucount = r.ReadInt32();
                for (int i = 0; i < ucount; i++)
                {
                    uint iid = r.ReadUInt32(); string tk = r.ReadString(); byte fac = r.ReadByte(); float hp = r.ReadSingle();
                    var pos = ReadPos(r);
                    var u = new UnitInstance(iid, tk, fac, hp, pos);
                    u.State = (UnitState)r.ReadInt32();
                    bool hasTarget = r.ReadBoolean(); uint tid = r.ReadUInt32(); if (hasTarget) u.TargetId = tid;
                    bool hasOrder = r.ReadBoolean(); var op = ReadPos(r); if (hasOrder) u.OrderTargetPos = op;
                    s.Units.Add(u);
                }
                s.NextInstanceId = r.ReadUInt32();

                // threat relations（v5で追加、Task59）。v4以前の形式にはこのブロックが存在しないため、
                // その場合は読み取らずs.ThreatRelations（コンストラクタ既定値＝全Hostile）をそのまま使う
                // （Task58までの「常に無条件敵対」という後方互換の挙動になる）。
                if (version >= 5)
                {
                    for (int f = 0; f < 5; f++)
                        for (int k = 0; k < ThreatRelations.ThreatKindCount; k++)
                            s.ThreatRelations.Set((byte)f, (ThreatKind)k, (Relation)r.ReadInt32());
                }
            }
            return s;
        }

        private static void WritePos(BinaryWriter w, WorldPos p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        private static WorldPos ReadPos(BinaryReader r) { return new WorldPos(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    }
}
