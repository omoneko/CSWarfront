using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Core.IWaterSamplerのGame層実装（Task61: 海上戦力の追加）。CSのTerrainManagerを叩いて、
    /// 座標が水面かどうか・その水面の高さを返す。SurfaceHeightSamplerと同じ供給パターン
    /// （sim スレッド専用、MovementStep.Advanceから呼ばれる想定、失敗時は例外を投げずfalseで通知）。
    ///
    /// 検証済みシグネチャ（Assembly-CSharp.dllをilspycmdで逆コンパイルして確認、Task61）:
    ///  - TerrainManager.HasWater(Vector2 position): bool
    ///      position.x = ワールドX、position.y = ワールドZ（Vector2へ(x,z)を詰めて渡す規約。
    ///      TerrainManagerの座標系はSurfaceHeightSamplerが使うSampleDetailHeight(Vector3)とは異なり、
    ///      HasWater/WaterLevelはワールド座標をそのままVector2で受け取る——内部で
    ///      Mathf.FloorToInt((position.x + 8640f) * 16f) 等のグリッド変換を行っている）。
    ///      水シミュレーションのセルに閾値以上の水深（隣接ブロック高さとの差 >= MIN_WATER_AMOUNT(8)、
    ///      TerrainManager.MIN_WATER_AMOUNT定数）があればtrue。
    ///  - TerrainManager.WaterLevel(Vector2 position): float
    ///      HasWaterと全く同じグリッド変換・閾値判定を内部で行い、水面が無ければ0fを返し、あれば
    ///      水面の高さ（ワールド単位、内部rawハイト値 * 1/64fで換算済み）を返す。
    ///  - TerrainManager.instance: Singleton&lt;TerrainManager&gt;.instance（SurfaceHeightSamplerと同じ
    ///    ColossalFramework.Singletonパターン）。
    ///
    /// ハードニング: SurfaceHeightSamplerと同じ方針で、TerrainManager未生成/例外時はfalseを返し、
    /// 呼び出し元（Core.MovementStep）に安全側フォールバック（water==null時と同じ「移動を許可する」/
    /// 「Yを従来のまま維持する」）を委ねる。ログはSingleton未生成・例外いずれも初回のみ記録し、
    /// 成功したら次回の失敗でまた1回だけ記録する（simスレッドから高頻度に呼ばれるためログスパムを防ぐ）。
    /// </summary>
    internal sealed class WaterSampler : IWaterSampler
    {
        /// <summary>Task88（ユーザー要望「海上戦力について、喫水を考慮した行動範囲に」）: 艦艇が
        /// 航行可能とみなす最低水深（メートル）。IsWaterはHasWater（水があるか、閾値は僅か約0.125m）
        /// に加えて「水面高さ − 水底の地形高さ >= この値」を要求する。これにより波打ち際・浅瀬には
        /// 進入できず、艦艇はある程度深い水域だけを行動範囲にする（岸に張り付く見た目の防止）。
        /// 実機の海岸勾配に合わせた較正値（深すぎると既存の海軍基地沖でスポーン地点が見つからなく
        /// なるため控えめに設定。プレイテストで要調整）。</summary>
        internal const float MinNavigableDepth = 2f;

        private static bool _failureAlreadyLogged;

        public bool IsWater(float x, float z)
        {
            if (!Singleton<TerrainManager>.exists)
            {
                LogFailureOnce("TerrainManager not ready");
                return false;
            }

            try
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                Vector2 pos = new Vector2(x, z);
                if (!tm.HasWater(pos))
                {
                    _failureAlreadyLogged = false;
                    return false;
                }

                // Task88: 喫水チェック。水深 = 水面高さ − 水底（地形）高さ。SampleDetailHeight(Vector3)は
                // 水底でも地形の高さを返す（水面ではない。SurfaceHeightSamplerと同じ検証済みAPI）。
                float waterLevel = tm.WaterLevel(pos);
                float seabed = tm.SampleDetailHeight(new Vector3(x, 0f, z));
                bool navigable = (waterLevel - seabed) >= MinNavigableDepth;
                _failureAlreadyLogged = false;
                return navigable;
            }
            catch (System.Exception e)
            {
                LogFailureOnce("IsWater error: " + e);
                return false;
            }
        }

        public bool TrySampleWaterLevel(float x, float z, out float level)
        {
            level = 0f;
            if (!Singleton<TerrainManager>.exists)
            {
                LogFailureOnce("TerrainManager not ready");
                return false;
            }

            try
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                Vector2 pos = new Vector2(x, z);
                if (!tm.HasWater(pos)) return false; // 陸地。levelは書き込まない契約（呼び出し側は使わない）。

                level = tm.WaterLevel(pos);
                _failureAlreadyLogged = false;
                return true;
            }
            catch (System.Exception e)
            {
                LogFailureOnce("TrySampleWaterLevel error: " + e);
                level = 0f;
                return false;
            }
        }

        private static void LogFailureOnce(string message)
        {
            if (_failureAlreadyLogged) return;
            _failureAlreadyLogged = true;
            ModConfig.LogError("WaterSampler." + message);
        }
    }
}
