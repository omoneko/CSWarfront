using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// MilitaryBase の論理座標に実在するCS建物を1つ配置する（可視化 Task16）。
    /// 呼び出しは MilitaryManager がメインスレッド・BuildingManager準備後にのみ行う。
    /// プレハブの厳密な選定は見た目のチューニング（Task14）の範囲であり、ここではベストエフォート。
    /// </summary>
    public static class BaseBuildingPlacer
    {
        // MVP暫定: 基本消防署は多くのマップで早期にロードされる想定の代表例として採用。
        // 見つからない場合はロード済みプレハブから先頭の非nullを使う（Task14で調整）。
        private const string PreferredPrefabName = "Fire House01";

        public static bool TryPlace(WorldPos pos, out ushort buildingId)
        {
            buildingId = 0;

            if (!Singleton<BuildingManager>.exists)
            {
                ModConfig.LogError("BuildingManager not ready yet; base placement will retry");
                return false;
            }
            if (!Singleton<SimulationManager>.exists)
            {
                ModConfig.LogError("SimulationManager not ready yet; base placement will retry");
                return false;
            }

            BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(PreferredPrefabName);
            if (info == null) info = FindAnyLoadedBuilding();
            if (info == null)
            {
                ModConfig.LogError("No loaded BuildingInfo prefab found for base placement");
                return false;
            }

            Vector3 position = new Vector3(pos.X, pos.Y, pos.Z);
            if (Singleton<TerrainManager>.exists)
            {
                // 基地の論理座標はY=0固定のため、実際の地形高さへ補正する（Task14で要検証）。
                position.y = Singleton<TerrainManager>.instance.SampleDetailHeight(position);
            }

            SimulationManager sim = Singleton<SimulationManager>.instance;
            ushort id;
            bool ok = Singleton<BuildingManager>.instance.CreateBuilding(
                out id,
                ref sim.m_randomizer,
                info,
                position,
                0f,
                info.GetLength(),
                sim.m_currentBuildIndex);

            if (!ok)
            {
                ModConfig.LogError("CreateBuilding failed for base at (" + pos.X + "," + pos.Y + "," + pos.Z + ") using prefab " + info.name);
                return false;
            }

            sim.m_currentBuildIndex++;
            ModConfig.Log("Placed base building '" + info.name + "' id=" + id + " at (" + pos.X + "," + pos.Y + "," + pos.Z + ")");
            buildingId = id;
            return true;
        }

        private static BuildingInfo FindAnyLoadedBuilding()
        {
            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info != null) return info;
            }
            return null;
        }
    }
}
