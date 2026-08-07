using ICities;
using CSWarfront.Core;
namespace CSWarfront.Game.Serialization
{
    /// <summary>Persists WarState into save data. On load, restores state and regenerates the
    /// presentation.</summary>
    public class WarStateDataExtension : SerializableDataExtensionBase
    {
        private const string DataId = "CSWarfront.WarState.v1";

        public override void OnSaveData()
        {
            try
            {
                // Serialize while holding _stateLock (prevents a "Collection was modified" exception
                // while State.Units is being changed by OnSimTick etc. = a silently failed save = data
                // loss).
                byte[] bytes = MilitaryManager.SerializeLocked();
                serializableDataManager.SaveData(DataId, bytes);
            }
            catch (System.Exception e) { ModConfig.LogError("Save: " + e); }
        }

        public override void OnLoadData()
        {
            try
            {
                byte[] bytes = serializableDataManager.LoadData(DataId);
                if (bytes == null || bytes.Length == 0) return; // a new game is left to the default initialization
                var types = new UnitTypeRegistry();
                UnitStatsFile.EnsureLoaded(); // Task92: apply unit-stats.xml overrides before building the rosters
                LandUnitRoster.RegisterAll(types); // 7 land branches x Tier 1-5 (Task28). Tank_T1 from old saves resolves via the same key.
                NavalUnitRoster.RegisterAll(types); // 2 naval kinds x Tier 1-5 (Task61). Needed to restore saves containing naval/air units.
                AirUnitRoster.RegisterAll(types);   // 3 air kinds x Tier 1-5 (Task61).
                WarState restored = WarStateSerializer.Deserialize(bytes, types);
                // Task88: faction names are display-only mod-defined values (color names), so any old
                // names left in the save ("Faction 3" etc.) are always overwritten with the current
                // WarfrontSettings.FactionNames.
                string[] names = WarfrontSettings.FactionNames;
                for (int i = 0; i < restored.Factions.Count; i++)
                {
                    var f = restored.Factions[i];
                    if (f.Id < names.Length) f.Name = names[f.Id];
                }
                // The state swap and presentation (vehicle) regeneration happen inside the same lock
                // (see MilitaryManager).
                MilitaryManager.LoadAndRebuild(restored);
            }
            catch (System.Exception e) { ModConfig.LogError("Load: " + e); }
        }
    }
}
