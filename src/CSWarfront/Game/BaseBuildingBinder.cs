using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>MVP用に2基地シナリオを配置する（後日、実建物への紐付けに置換）。</summary>
    public static class BaseBuildingBinder
    {
        public static void SeedTwoBaseScenario(WarState s)
        {
            if (s.Bases.Count > 0) return; // 二重配置防止

            var redBase = new MilitaryBase(1, BaseType.Army, new WorldPos(-300, 0, 0));
            redBase.OwnerFactionId = 0; redBase.IsHeadquarters = true;
            redBase.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
            s.Bases.Add(redBase);
            s.FindFaction(0).HomeBaseId = 1;
            s.FindFaction(0).AddTreasury(200f);

            var blueBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0));
            blueBase.OwnerFactionId = 1; blueBase.IsHeadquarters = true;
            blueBase.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
            s.Bases.Add(blueBase);
            s.FindFaction(1).HomeBaseId = 2;
            s.FindFaction(1).AddTreasury(200f);
        }
    }
}
