using CSWarfront.Core;
using Xunit;

public class OccupationTests
{
    private static WarState FallenBaseScenario()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1; b.CurrentHP = 0f; b.MaxHP = 500f; b.InfluenceRadius = 500f;
        b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f)); // 奪取される備蓄
        s.Bases.Add(b);
        s.Factions[1].HomeBaseId = 200; // BlueのHQ
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(40, 0, 0))); // 攻撃側Red圏内
        return s;
    }

    [Fact]
    public void Fallen_base_transfers_to_attacker_with_queue_and_heals()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.Equal((byte)0, s.Bases[0].OwnerFactionId.Value); // Redへ
        Assert.Equal(500f, s.Bases[0].CurrentHP, 3);           // 回復
        Assert.Single(s.Bases[0].Queue);                        // 生産キュー奪取
    }

    [Fact]
    public void Losing_hq_eliminates_faction()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.True(s.FindFaction(1).Eliminated); // Blue脱落
    }
}
