using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーによる弾道ミサイル操作（基地パネルからの手動建造発注・発射地点指定、Task63）向けの
    /// MilitaryManager 追加メンバー。MilitaryManager.cs の500行制限のため分離した partial class
    /// （Task34のMilitaryManagerManualProduction.csと同じ方針）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。全メソッドは _stateLock を短時間だけ
    /// 保持してCoreへ委譲するだけの薄いラッパーで、Unity API には一切触れない。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによるミサイル手動建造発注（Task63）。
        /// Core.MissileStockpile.TryBuildMissile へ _stateLock 内で委譲するだけの薄いラッパー。
        /// </summary>
        public static MissileBuildResult TryQueueMissileBuild(ushort baseId)
        {
            lock (_stateLock)
            {
                if (State == null) return MissileBuildResult.BaseNotFound;
                return MissileStockpile.TryBuildMissile(State, baseId);
            }
        }

        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによるミサイル発射（Task63）。
        /// target はUI側（UnitCommandInputの集結地点指定と同じraycast経路）が求めたワールド座標。
        /// Core.MissileStep.TryLaunch へ _stateLock 内で委譲するだけの薄いラッパー。
        /// </summary>
        public static LaunchResult TryLaunchMissile(ushort baseId, Vector3 target)
        {
            lock (_stateLock)
            {
                if (State == null) return LaunchResult.BaseNotFound;
                return MissileStep.TryLaunch(State, baseId, new WorldPos(target.x, target.y, target.z));
            }
        }
    }
}
