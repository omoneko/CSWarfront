using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// マウス位置のスクリーン座標からワールド上の「クリック地点」を求める共用ヘルパー（Task77）。
    /// UnitCommandInput（集結地点）と MissileLaunchTargeting（ミサイル目標）の両方から使う。
    ///
    /// 手順:
    ///  1. Physics.Raycast — ユニット/建物などMOD自前コライダーへの精密ヒット（従来経路）。
    ///  2. 外れたら Core.TerrainRaycast — TerrainManagerの高さサンプリングとの交差計算。
    ///     CS1の地形はUnity物理コライダーを持たないため、開けた地面のクリックは必ずこちらで
    ///     確定する（従来はここが無く「raycast hit nothing」で全て却下されていた＝Task77の根本原因）。
    ///
    /// スレッド注記: メインスレッド専用（Camera/Input/Physicsを触るため）。SurfaceHeightSamplerの
    /// TerrainManager.SampleDetailHeight(Vector3)は読み取り専用APIであり、simスレッドと並行して
    /// メインスレッドから読んでも安全（ログ抑制フラグの競合は無害）。
    /// </summary>
    internal static class GroundClickRaycast
    {
        private const float MaxRaycastDistance = 10000f; // 従来のUnitCommandInput/MissileLaunchTargetingと同じ値

        private static readonly SurfaceHeightSampler _sampler = new SurfaceHeightSampler();

        /// <summary>現在のマウス位置からクリック地点のワールド座標の取得を試みる。
        /// カメラ未準備・地形交差も不成立の場合はfalseを返し、reasonに却下理由
        /// （ログ用の短い英語句）を格納する。</summary>
        public static bool TryGetPoint(out Vector3 point, out string reason)
        {
            point = default(Vector3);

            Camera cam = Camera.main;
            if (cam == null)
            {
                reason = "camera not ready";
                return false;
            }

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, MaxRaycastDistance))
            {
                point = hit.point;
                reason = null;
                return true;
            }

            WorldPos terrainHit;
            if (TerrainRaycast.TryFind(
                new WorldPos(ray.origin.x, ray.origin.y, ray.origin.z),
                ray.direction.x, ray.direction.y, ray.direction.z,
                _sampler, MaxRaycastDistance, out terrainHit))
            {
                point = new Vector3(terrainHit.X, terrainHit.Y, terrainHit.Z);
                reason = null;
                return true;
            }

            reason = "no collider hit and terrain intersection failed";
            return false;
        }
    }
}
