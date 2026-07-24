using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>地上ユニットをCS車両として表現する。位置取得と誘導、撃破時の撤去を担う。</summary>
    public static class LandUnitSpawner
    {
        private static readonly Dictionary<uint, ushort> _vehicleByInstance = new Dictionary<uint, ushort>();

        public static bool HasRepresentation(uint instanceId) { return _vehicleByInstance.ContainsKey(instanceId); }

        public static void Spawn(uint instanceId, CompletedUnit c)
        {
            // MVP: 既定の車両プレハブ名を1つ流用（例: 消防車/装甲車風）。アセット割当は後日UI化。
            VehicleInfo info = FindDefaultLandVehicle();
            if (info == null) { ModConfig.LogError("既定車両プレハブ未取得"); return; }
            Vector3 pos = ToVec(c.SpawnPos);
            VehicleManager vm = Singleton<VehicleManager>.instance;
            ushort vid;
            if (vm.CreateVehicle(out vid, ref Singleton<SimulationManager>.instance.m_randomizer,
                info, pos, TransferManager.TransferReason.None, false, false))
            {
                _vehicleByInstance[instanceId] = vid;
            }
        }

        public static void UpdateMovementAndCleanup(WarState state)
        {
            VehicleManager vm = Singleton<VehicleManager>.instance;
            var toRemove = new List<uint>();
            foreach (var u in state.Units)
            {
                ushort vid;
                if (!_vehicleByInstance.TryGetValue(u.InstanceId, out vid)) continue;
                if (u.State == UnitState.Dead)
                {
                    vm.ReleaseVehicle(vid);
                    toRemove.Add(u.InstanceId);
                    continue;
                }
                // 位置をCoreへ反映
                Vector3 p = vm.m_vehicles.m_buffer[vid].GetLastFramePosition();
                u.Position = new WorldPos(p.x, p.y, p.z);
                // 誘導は MVP 簡略：目標方向へ直接テレポート漸進（本格パスファインディングは後日）
                if (u.OrderTargetPos.HasValue && u.State == UnitState.Moving)
                {
                    // NOTE: MVPでは車両AIの目的地設定に置換予定。ここでは位置補間で前進を可視化。
                }
            }
            foreach (var id in toRemove) _vehicleByInstance.Remove(id);
        }

        private static VehicleInfo FindDefaultLandVehicle()
        {
            // MVP: PrefabCollection から適当な地上車両を1つ。実装時にゲーム内に存在する確実な名前へ調整する。
            return PrefabCollection<VehicleInfo>.FindLoaded("Fire Truck");
        }

        private static Vector3 ToVec(WorldPos p) { return new Vector3(p.X, p.Y, p.Z); }
    }
}
