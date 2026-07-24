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
            else
            {
                ModConfig.LogError("CreateVehicle failed for instance " + instanceId);
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
                // 車両IDはCSにより再利用されうる。自Mod関与外(車両自身のAI等)で
                // 既に消滅している場合、vidは陳腐化しているため位置を読まずに追跡から外す。
                if ((vm.m_vehicles.m_buffer[vid].m_flags & Vehicle.Flags.Created) == 0)
                {
                    ModConfig.LogError("Stale vehicle id for instance " + u.InstanceId + "; dropping representation");
                    // 表現(車両)を失ったユニットをDeadにしない場合、姿を消したまま戦闘/占領計算に
                    // 永久に関与し続ける「ゴースト」になる。simの死亡掃除(State.Units.RemoveAll)が
                    // 拾えるようここでStateをDeadにする（本メソッドは_stateLock内で呼ばれるため安全）。
                    u.State = UnitState.Dead;
                    toRemove.Add(u.InstanceId);
                    continue;
                }
                // Task15: Core（MovementStep）がu.Positionを論理的に所有・前進させるようになったため、
                // 「車両→Position」の読み取りから「Position→車両」の書き込みへ反転する（視覚追従）。
                // NOTE(視覚ジッター注意): CS車両の自前AI/物理（VehicleAI.SimulationStep等）が
                // 毎tick自身の位置・速度を再計算し得るため、ここでの直接上書きと競合してカクつく
                // （teleport風のジッター）可能性がある。現時点ではベストエフォートとし、
                // 論理ループ（Core）はこの視覚同期の成否に関わらず正しく回り続ける。
                Vector3 p = ToVec(u.Position);
                vm.m_vehicles.m_buffer[vid].m_frame0.m_position = p;
                vm.m_vehicles.m_buffer[vid].m_frame1.m_position = p;
                vm.m_vehicles.m_buffer[vid].m_frame2.m_position = p;
                vm.m_vehicles.m_buffer[vid].m_frame3.m_position = p;
            }
            foreach (var id in toRemove) _vehicleByInstance.Remove(id);
        }

        /// <summary>
        /// レベルアンロード時にインスタンス→車両IDの対応表をクリアする（Task16レビューImportant）。
        /// レベルが破棄されればCS側の車両も道連れに消えるため、ここでReleaseVehicleは呼ばない
        /// （既に無効なvidに対して呼ぶと不正な操作になりうる）。単にマップを空にするだけでよい。
        /// </summary>
        public static void ResetAll()
        {
            _vehicleByInstance.Clear();
        }

        private static VehicleInfo FindDefaultLandVehicle()
        {
            // MVP: PrefabCollection から適当な地上車両を1つ。実装時にゲーム内に存在する確実な名前へ調整する。
            return PrefabCollection<VehicleInfo>.FindLoaded("Fire Truck");
        }

        private static Vector3 ToVec(WorldPos p) { return new Vector3(p.X, p.Y, p.Z); }
    }
}
