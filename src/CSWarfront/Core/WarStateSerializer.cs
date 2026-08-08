using System.IO;
namespace CSWarfront.Core
{
    /// <summary>Round-trips the WarState's logical state to bytes (presentation references are not saved).</summary>
    public static class WarStateSerializer
    {
        // v1 -> v2: appended CaptureGraceHours (float) to the tail of the base block (Task24).
        // v2 -> v3: appended AutoProduce (bool) further at the tail of the base block (Task34).
        // v3 -> v4: appended ResearchPoints (float) / UnlockedTier (byte) to the tail of the faction block (Task35).
        // v4 -> v5: appended ThreatRelations (5 factions × 2 ThreatKinds, int=(int)Relation) at the very
        //           end of the payload (Task59). Reading v4 or older finds no appended block, so
        //           ThreatRelations stays at the constructor default of all-Hostile (preserving the
        //           unconditional hostility of Task58 and earlier).
        // v5 -> v6: appended StockpiledMissiles (int) / MissileBuildProgress (float) further at the tail of
        //           the base block (Task63: missile-base stockpile and build progress). Reading v5 or older
        //           restores both at their defaults of 0 (no stockpile, not building) — harmless, since no
        //           placeable MissileBase prefab existed before Task63.
        // v6 -> v7: appended AutoLaunchMissiles (bool) further at the tail of the base block (Task90:
        //           missile-base auto-launch toggle). Reading v6 or older defaults to true (the previous
        //           fully automatic launching).
        // v7 -> v8: appended, at the very end of the payload, (a) missiles in flight (MissilesInFlight +
        //           NextMissileId) and (b) unit commands (a parallel block of Order/RallyPoint keyed by
        //           InstanceId) (Task92: fixes "missiles in flight vanish on load / orders revert to AI
        //           control"). Reading v7 or older behaves as before (nothing in flight; everyone
        //           AiControlled).
        // v8 -> v9: appended, at the very end of the payload, (a) the factions' three resources
        //           (Manpower/Production/SupplyStock, a parallel block keyed by Id) and (b) unit
        //           ammo/cargo (Ammo/SupplyLoad, a parallel block keyed by InstanceId) (Task99: economy
        //           and supply). Reading v8 or older restores resources at the initial-grant amounts
        //           (200 Manpower/Production each, 200 SupplyStock), full ammo and zero cargo (Invaders
        //           stay at 0 resources = unused, so it does not matter).
        // v9 -> v10: appended, at the very end of the payload, (a) base fortification/stock state
        //           (parallel by BaseId: StoredSupplies/FortAmmo/RailConnected) and (b) unit carry state
        //           (parallel by InstanceId: CarriedByUnitId) (Task101: Update 3). v9 and older get the
        //           defaults (no stock, full fort ammo, rail unconnected, not carried).
        // The binary format is position-dependent: never insert between existing fields — always append at
        // the tail.
        private const int Version = 11;

        /// <summary>Default three-resource grant given to v8-or-older saves (same amounts as a new game's
        /// initial grant).</summary>
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
                    w.Write(f.ResearchPoints); w.Write(f.UnlockedTier); // added in v4 (Task35), appended at the block tail.
                }
                // relations (fixed 5x5)
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
                    w.Write(b.CaptureGraceHours); // added in v2; appended at the block tail (position-dependent format).
                    w.Write(b.AutoProduce); // added in v3 (Task34); appended further at the tail for the same reason.
                    w.Write(b.StockpiledMissiles); w.Write(b.MissileBuildProgress); // added in v6 (Task63).
                    w.Write(b.AutoLaunchMissiles); // added in v7 (Task90).
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
                // threat relations (fixed 5 factions × ThreatKindCount; added in v5, Task59). Appended at the payload tail.
                for (int f = 0; f < 5; f++)
                    for (int k = 0; k < ThreatRelations.ThreatKindCount; k++)
                        w.Write((int)s.ThreatRelations.Get((byte)f, (ThreatKind)k));

                // v8 (Task92): missiles in flight — a save moments before impact resumes mid-flight.
                w.Write(s.MissilesInFlight.Count);
                foreach (var m in s.MissilesInFlight)
                {
                    w.Write(m.Id); w.Write(m.FactionId);
                    WritePos(w, m.From); WritePos(w, m.To);
                    w.Write(m.Progress); w.Write(m.Intercepted);
                }
                w.Write(s.NextMissileId);

                // v8 (Task92): unit commands (Order/RallyPoint). The unit block itself is untouched for
                // compatibility; appended as a parallel block keyed by InstanceId.
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write((int)u.Order);
                    w.Write(u.RallyPoint.HasValue);
                    WritePos(w, u.RallyPoint.HasValue ? u.RallyPoint.Value : new WorldPos(0, 0, 0));
                }

                // v9 (Task99): the factions' three resources (parallel block keyed by Id; the faction block
                // itself is untouched for compatibility).
                w.Write(s.Factions.Count);
                foreach (var f in s.Factions)
                {
                    w.Write(f.Id);
                    w.Write(f.Manpower); w.Write(f.Production); w.Write(f.SupplyStock);
                }

                // v9 (Task99): unit ammo/cargo (parallel block keyed by InstanceId).
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write(u.Ammo); w.Write(u.SupplyLoad);
                }

                // v10 (Task101): base fortification/stock state (parallel block keyed by BaseId).
                w.Write(s.Bases.Count);
                foreach (var b in s.Bases)
                {
                    w.Write(b.BaseId);
                    w.Write(b.StoredSupplies); w.Write(b.FortAmmo); w.Write(b.RailConnected);
                }

                // v10 (Task101): unit carry state (parallel block keyed by InstanceId).
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId);
                    w.Write(u.CarriedByUnitId.HasValue);
                    w.Write(u.CarriedByUnitId.HasValue ? u.CarriedByUnitId.Value : 0u);
                }

                // v11 (Task114): the saved defense layout (rebuild-destroyed-fortifications feature).
                w.Write(s.DefenseLayout.Count);
                foreach (var e in s.DefenseLayout)
                {
                    w.Write((byte)e.Type);
                    WritePos(w, e.Position);
                    w.Write(e.Angle);
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
                int version = r.ReadInt32(); // branches on v2+ (CaptureGraceHours), v3+ (AutoProduce), v4+ (ResearchPoints/UnlockedTier), v5+ (ThreatRelations), v6+ (StockpiledMissiles/MissileBuildProgress)
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
                        f.ResearchPoints = 0f; // v3 and older: default 0 (research not started)
                        f.UnlockedTier = 1;    // v3 and older: default 1 (only tier 1 unlocked — the old behavior)
                    }
                    s.Factions.Add(f);
                }
                // Task95: saves from before the Invader existed (five factions) have no Invader faction, so
                // it is filled in here (idempotent; post-Invader saves have fcount=6 and the loop above has
                // already restored it). Its relations need no persistence — RelationMatrix/ThreatRelations
                // hard-code permanent hostility (the fixed 5x5 block below is unchanged).
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
                    b.AutoProduce = version >= 3 ? r.ReadBoolean() : true; // v2 and older: default true (the old fully automatic behavior)
                    if (version >= 6)
                    {
                        b.StockpiledMissiles = r.ReadInt32();
                        b.MissileBuildProgress = r.ReadSingle();
                    }
                    else
                    {
                        b.StockpiledMissiles = 0; // v5 and older: default 0 (empty stockpile)
                        b.MissileBuildProgress = 0f; // v5 and older: default 0 (not building)
                    }
                    b.AutoLaunchMissiles = version >= 7 ? r.ReadBoolean() : true; // v6 and older: default true (the old auto-launch)
                    // Task101 (user request): trenches are always unowned terrain (old saves that stored an
                    // owner are normalized on load too).
                    if (b.Type == BaseType.Trench) b.OwnerFactionId = null;
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

                // threat relations (added in v5, Task59). v4-and-older formats lack this block; in that
                // case nothing is read and s.ThreatRelations keeps its constructor default of all-Hostile
                // (the backward-compatible "always unconditionally hostile" behavior of Task58 and earlier).
                if (version >= 5)
                {
                    for (int f = 0; f < 5; f++)
                        for (int k = 0; k < ThreatRelations.ThreatKindCount; k++)
                            s.ThreatRelations.Set((byte)f, (ThreatKind)k, (Relation)r.ReadInt32());
                }

                // v8 (Task92): missiles in flight + unit commands. v7 and older lack this block; those
                // saves restore as before (nothing in flight, everyone AiControlled without a RallyPoint).
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
                        if (u == null) continue; // no exception even for an inconsistent save (defensive)
                        u.Order = order;
                        if (hasRally) u.RallyPoint = rally;
                    }
                }

                // v9 (Task99): the three resources + ammo/cargo. v8 and older use the defaults (see the
                // version comments).
                if (version >= 9)
                {
                    int frcount = r.ReadInt32();
                    for (int i = 0; i < frcount; i++)
                    {
                        byte fid = r.ReadByte();
                        float manpower = r.ReadSingle(), production = r.ReadSingle(), supply = r.ReadSingle();
                        Faction f = s.FindFaction(fid);
                        if (f == null) continue; // defensive (same convention as the unit-command block)
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
                    // Initial grant for old saves (the same amounts as a new game — so an existing army is
                    // not instantly starved by a halted economy). Ammo can stay at the UnitInstance default
                    // (full).
                    foreach (Faction f in s.Factions)
                    {
                        if (f.Id == Faction.InvaderFactionId) continue;
                        f.AddManpower(LegacyResourceGrant);
                        f.AddProduction(LegacyResourceGrant);
                        f.AddSupply(LegacyResourceGrant);
                    }
                }

                // v10 (Task101): fortification/stock + carry state. v9 and older keep the defaults
                // (StoredSupplies 0 / FortAmmo 1 / RailConnected false / not carried).
                if (version >= 10)
                {
                    int bscount = r.ReadInt32();
                    for (int i = 0; i < bscount; i++)
                    {
                        ushort bid = r.ReadUInt16();
                        float stored = r.ReadSingle(); float fortAmmo = r.ReadSingle(); bool rail = r.ReadBoolean();
                        MilitaryBase b = FindBaseById(s, bid);
                        if (b == null) continue; // defensive
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

                // v11 (Task114): the saved defense layout. v10 and older simply have none saved.
                if (version >= 11)
                {
                    int dcount = r.ReadInt32();
                    for (int i = 0; i < dcount; i++)
                    {
                        byte t = r.ReadByte();
                        WorldPos p = ReadPos(r);
                        float angle = r.ReadSingle();
                        s.DefenseLayout.Add(new DefenseLayoutEntry { Type = (BaseType)t, Position = p, Angle = angle });
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
