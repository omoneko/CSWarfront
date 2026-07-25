using System;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 電力（Electricity）タブに独自の「軍事基地」BuildingInfo を実行時登録する。
    /// アセットエディタを使わず、既存の電力系プレハブの GameObject を Instantiate で複製することで、
    /// setter非公開の m_UICategory 等の private フィールドを一切のリフレクション無しで引き継ぐ
    /// （.superpowers/sdd/research-power-tab-building.md §6 で検証済みの経路）。
    /// Task18でこのプレハブの配置をロジック上の基地（MilitaryBase）に紐付ける。本タスクではプレハブ登録のみ。
    /// </summary>
    public static class WarfrontBasePrefab
    {
        public const string PrefabName = "CSWarfront Military Base";
        private const string CollectionName = "CSWarfront";

        public static BuildingInfo Prefab { get; private set; }
        public static bool IsRegistered { get { return Prefab != null; } }

        /// <summary>冪等。ゲームロード毎（OnLevelLoaded）に呼ばれる想定。</summary>
        public static void EnsureRegistered()
        {
            if (IsRegistered)
            {
                ModConfig.Log("WarfrontBasePrefab.EnsureRegistered: already registered ('" + Prefab.name + "'), skip");
                return;
            }

            try
            {
                BuildingInfo source = FindElectricitySource();
                if (source == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: no BuildingInfo with m_class.m_service == Electricity found among " +
                        PrefabCollection<BuildingInfo>.LoadedCount() + " loaded prefabs; aborting registration");
                    return;
                }
                ModConfig.Log("WarfrontBasePrefab: source prefab chosen = '" + source.name + "' (category='" + SafeCategory(source) + "')");

                GameObject sourceGo = source.gameObject;
                if (sourceGo == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: source prefab '" + source.name + "' has null gameObject; aborting");
                    return;
                }

                GameObject cloneGo = (GameObject)UnityEngine.Object.Instantiate(sourceGo);
                BuildingInfo clone = cloneGo.GetComponent<BuildingInfo>();
                if (clone == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: clone GameObject has no BuildingInfo component; aborting");
                    UnityEngine.Object.Destroy(cloneGo);
                    return;
                }

                clone.name = PrefabName;
                cloneGo.name = PrefabName;
                clone.m_prefabInitialized = false;
                ModConfig.Log("WarfrontBasePrefab: clone created and renamed to '" + PrefabName + "'");

                if (clone.m_class == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: cloned prefab '" + PrefabName + "' has null m_class after clone; aborting");
                    return;
                }
                if (clone.m_class.m_service != ItemClass.Service.Electricity)
                {
                    ModConfig.LogError("WarfrontBasePrefab: cloned prefab m_class.m_service = " + clone.m_class.m_service +
                        " (expected Electricity); aborting");
                    return;
                }

                PrefabCollection<BuildingInfo>.InitializePrefabs(CollectionName, clone, null);
                ModConfig.Log("WarfrontBasePrefab: InitializePrefabs('" + CollectionName + "', '" + PrefabName + "', null) done");

                PrefabCollection<BuildingInfo>.BindPrefabs();
                ModConfig.Log("WarfrontBasePrefab: BindPrefabs() done");

                RefreshPanels();

                Prefab = clone;
                ModConfig.Log("WarfrontBasePrefab: registration COMPLETE, Prefab.name='" + Prefab.name + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.EnsureRegistered exception: " + e);
            }
        }

        private static BuildingInfo FindElectricitySource()
        {
            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            for (uint i = 0; i < (uint)count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info != null && info.m_class != null && info.m_class.m_service == ItemClass.Service.Electricity)
                {
                    return info;
                }
            }
            return null;
        }

        private static string SafeCategory(BuildingInfo info)
        {
            try { return info.category; }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// OnLevelLoaded 時点でパネルが既にPopulate済みの場合に備え、明示的に再描画させる。
        /// パネルが未生成でも失敗させない（null ガード＋個別 try/catch でログのみ）。
        /// </summary>
        private static void RefreshPanels()
        {
            try
            {
                ElectricityGroupPanel groupPanel = UnityEngine.Object.FindObjectOfType<ElectricityGroupPanel>();
                if (groupPanel != null)
                {
                    groupPanel.RefreshPanel();
                    ModConfig.Log("WarfrontBasePrefab: ElectricityGroupPanel.RefreshPanel() called");
                }
                else
                {
                    ModConfig.Log("WarfrontBasePrefab: ElectricityGroupPanel not found via FindObjectOfType (may not be instantiated yet)");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab: ElectricityGroupPanel refresh failed: " + e);
            }

            try
            {
                ElectricityPanel panel = UnityEngine.Object.FindObjectOfType<ElectricityPanel>();
                if (panel != null)
                {
                    panel.RefreshPanel();
                    ModConfig.Log("WarfrontBasePrefab: ElectricityPanel.RefreshPanel() called");
                }
                else
                {
                    ModConfig.Log("WarfrontBasePrefab: ElectricityPanel not found via FindObjectOfType (may not be instantiated yet)");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab: ElectricityPanel refresh failed: " + e);
            }
        }
    }
}
