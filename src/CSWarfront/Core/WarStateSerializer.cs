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
        // v6 -> v7: 基地ブロックのさらに末尾に AutoLaunchMissiles (bool) を追加（Task90：ミサイル基地の
        //           自動発射のON/OFF切替）。v6以前を読んだ場合は既定値true（従来の全自動発射挙動）。
        // v7 -> v8: ペイロード全体の末尾に (a)飛翔中ミサイル（MissilesInFlight + NextMissileId）、
        //           (b)ユニットの部隊命令（InstanceIdキーのOrder/RallyPoint並列ブロック）を追加
        //           （Task92：「ロードで飛行中ミサイルが消える／命令がAI制御へ戻る」の解消）。
        //           v7以前を読んだ場合はどちらも従来どおり（飛翔中なし・全員AiControlled）。
        // v8 -> v9: ペイロード全体の末尾に (a)勢力の3資源（Manpower/Production/SupplyStock、Idキーの
        //           並列ブロック）、(b)ユニットの弾薬/積載（Ammo/SupplyLoad、InstanceIdキーの並列
        //           ブロック）を追加（Task99: 経済・補給システム）。v8以前を読んだ場合は
        //           資源=初期付与相当（Manpower/Production各200、SupplyStock200）・弾薬満タン・
        //           積載0で復元される（Invaderは資源0のまま＝使わないので不問）。
        // v9 -> v10: ペイロード全体の末尾に (a)基地の築城/備蓄状態（BaseIdキー並列:
        //           StoredSupplies/FortAmmo/RailConnected）、(b)ユニットの搭乗状態
        //           （InstanceIdキー並列: CarriedByUnitId）を追加（Task101: Update3）。
        //           v9以前は既定値（備蓄0・築城弾薬満タン・レール未接続・未搭乗）。
        // バイナリ形式は位置依存のため、既存フィールドの間には挿入せず必ず末尾に追記すること。
        private const int Version = 10;

        /// <summary>v8以前のセーブに与える3資源の既定値（新規ゲームの初期付与と同額）。</summary>
        private const float LegacyResourceGrant = 200f;

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
                    w.Write(b.AutoLaunchMissiles); // v7で追加（Task90）。
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

                // v8（Task92）: 飛翔中ミサイル。着弾間際でセーブしても続きから飛ぶ。
                w.Write(s.MissilesInFlight.Count);
                foreach (var m in s.MissilesInFlight)
                {
                    w.Write(m.Id); w.Write(m.FactionId);
                    WritePos(w, m.From); WritePos(w, m.To);
                    w.Write(m.Progress); w.Write(m.Intercepted);
                }
                w.Write(s.NextMissileId);

                // v8（Task92）: 部隊命令（Order/RallyPoint）。ユニットブロック本体は互換のため触らず、
                // InstanceIdをキーにした並列ブロックとして末尾に追記する。
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write((int)u.Order);
                    w.Write(u.RallyPoint.HasValue);
                    WritePos(w, u.RallyPoint.HasValue ? u.RallyPoint.Value : new WorldPos(0, 0, 0));
                }

                // v9（Task99）: 勢力の3資源（Idキーの並列ブロック。勢力ブロック本体は互換のため触らない）。
                w.Write(s.Factions.Count);
                foreach (var f in s.Factions)
                {
                    w.Write(f.Id);
                    w.Write(f.Manpower); w.Write(f.Production); w.Write(f.SupplyStock);
                }

                // v9（Task99）: ユニットの弾薬/積載（InstanceIdキーの並列ブロック）。
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write(u.Ammo); w.Write(u.SupplyLoad);
                }

                // v10（Task101）: 基地の築城/備蓄状態（BaseIdキーの並列ブロック）。
                w.Write(s.Bases.Count);
                foreach (var b in s.Bases)
                {
                    w.Write(b.BaseId);
                    w.Write(b.StoredSupplies); w.Write(b.FortAmmo); w.Write(b.RailConnected);
                }

                // v10（Task101）: ユニットの搭乗状態（InstanceIdキーの並列ブロック）。
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write(u.CarriedByUnitId.HasValue);
                    w.Write(u.CarriedByUnitId.HasValue ? u.CarriedByUnitId.Value : 0u);
                }

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
                // Task95: Invader実装以前のセーブ（5勢力）にはInvader勢力が居ないため、ここで補完する
                // （冪等。Invader実装後のセーブはfcount=6で上のループが既に復元している）。
                // 関係はRelationMatrix/ThreatRelationsがハードコードで常時Hostileを返すため永続化不要
                // （下の5x5固定ブロックは従来のまま）。
                InvasionEvents.EnsureInvaderFaction(s);
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
                    b.AutoLaunchMissiles = version >= 7 ? r.ReadBoolean() : true; // v6以前は既定値true（従来の全自動発射）
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

                // v8（Task92）: 飛翔中ミサイル＋部隊命令。v7以前にはこのブロックが無いため、
                // その場合は従来どおり（飛翔中なし・全員AiControlled/RallyPointなし）で復元される。
                if (version >= 8)
                {
                    int mcount = r.ReadInt32();
                    for (int i = 0; i < mcount; i++)
                    {
                        var m = new MissileInFlight
                        {
                            Id = r.ReadUInt32(),
                            FactionId = r.ReadByte(),
                            From = ReadPos(r),
                            To = ReadPos(r),
                            Progress = r.ReadSingle(),
                            Intercepted = r.ReadBoolean()
                        };
                        s.MissilesInFlight.Add(m);
                    }
                    s.NextMissileId = r.ReadUInt32();

                    int ocount = r.ReadInt32();
                    for (int i = 0; i < ocount; i++)
                    {
                        uint iid = r.ReadUInt32();
                        var order = (UnitOrder)r.ReadInt32();
                        bool hasRally = r.ReadBoolean();
                        var rally = ReadPos(r);
                        UnitInstance u = s.FindUnit(iid);
                        if (u == null) continue; // 整合性が崩れたセーブでも例外にしない（防御的）
                        u.Order = order;
                        if (hasRally) u.RallyPoint = rally;
                    }
                }

                // v9（Task99）: 3資源＋弾薬/積載。v8以前は既定値（バージョンコメント参照）。
                if (version >= 9)
                {
                    int frcount = r.ReadInt32();
                    for (int i = 0; i < frcount; i++)
                    {
                        byte fid = r.ReadByte();
                        float manpower = r.ReadSingle(), production = r.ReadSingle(), supply = r.ReadSingle();
                        Faction f = s.FindFaction(fid);
                        if (f == null) continue; // 防御的（部隊命令ブロックと同じ規約）
                        f.AddManpower(manpower);
                        f.AddProduction(production);
                        f.AddSupply(supply);
                    }

                    int uacount = r.ReadInt32();
                    for (int i = 0; i < uacount; i++)
                    {
                        uint iid = r.ReadUInt32();
                        float ammo = r.ReadSingle(), load = r.ReadSingle();
                        UnitInstance u = s.FindUnit(iid);
                        if (u == null) continue;
                        u.Ammo = ammo;
                        u.SupplyLoad = load;
                    }
                }
                else
                {
                    // 旧セーブへの初期付与（新規ゲームと同額。既存の軍を経済停止で即枯渇させないため）。
                    // 弾薬はUnitInstanceの既定値（満タン）のままでよい。
                    foreach (Faction f in s.Factions)
                    {
                        if (f.Id == Faction.InvaderFactionId) continue;
                        f.AddManpower(LegacyResourceGrant);
                        f.AddProduction(LegacyResourceGrant);
                        f.AddSupply(LegacyResourceGrant);
                    }
                }

                // v10（Task101）: 築城/備蓄＋搭乗。v9以前は既定値
                // （StoredSupplies0/FortAmmo1/RailConnected false/未搭乗）のままでよい。
                if (version >= 10)
                {
                    int bscount = r.ReadInt32();
                    for (int i = 0; i < bscount; i++)
                    {
                        ushort bid = r.ReadUInt16();
                        float stored = r.ReadSingle(); float fortAmmo = r.ReadSingle(); bool rail = r.ReadBoolean();
                        MilitaryBase b = FindBaseById(s, bid);
                        if (b == null) continue; // 防御的
                        b.StoredSupplies = stored;
                        b.FortAmmo = fortAmmo;
                        b.RailConnected = rail;
                    }

                    int uccount = r.ReadInt32();
                    for (int i = 0; i < uccount; i++)
                    {
                        uint iid = r.ReadUInt32();
                        bool carried = r.ReadBoolean(); uint carrier = r.ReadUInt32();
                        UnitInstance u = s.FindUnit(iid);
                        if (u == null) continue;
                        if (carried) u.CarriedByUnitId = carrier;
                    }
                }
            }
            return s;
        }

        private static MilitaryBase FindBaseById(WarState s, ushort baseId)
        {
            for (int i = 0; i < s.Bases.Count; i++)
                if (s.Bases[i].BaseId == baseId) return s.Bases[i];
            return null;
        }

        private static void WritePos(BinaryWriter w, WorldPos p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        private static WorldPos ReadPos(BinaryReader r) { return new WorldPos(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    }
}
