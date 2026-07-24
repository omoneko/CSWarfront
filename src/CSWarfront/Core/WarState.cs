using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>MODの論理状態の集約。Game層はこれを1つ保持し、Coreの各stepに渡す。</summary>
    public class WarState
    {
        public List<Faction> Factions = new List<Faction>();
        public RelationMatrix Relations = new RelationMatrix(5);
        public List<UnitInstance> Units = new List<UnitInstance>();
        public List<MilitaryBase> Bases = new List<MilitaryBase>();
        public UnitTypeRegistry Types = new UnitTypeRegistry();
        public uint NextInstanceId = 1;

        public uint AllocInstanceId() { return NextInstanceId++; }

        public UnitInstance FindUnit(uint id)
        {
            for (int i = 0; i < Units.Count; i++)
                if (Units[i].InstanceId == id) return Units[i];
            return null;
        }

        public Faction FindFaction(byte id)
        {
            for (int i = 0; i < Factions.Count; i++)
                if (Factions[i].Id == id) return Factions[i];
            return null;
        }
    }
}
