namespace CSWarfront.Core
{
    /// <summary>
    /// Task53:「ユニットが地面にめり込む」不具合の修正。地形の生の高さではなく、道路/建物建設後の
    /// "見た目の"地表（roads on embankments, terrain modified by construction, bridges等を含む）を
    /// サンプリングするための、UnityEngine非依存の薄いシーム（RoadGraph/CoverMapと同じ供給パターン）。
    ///
    /// Coreはこのインターフェースを消費するだけで、実装は一切知らない。Game層
    /// （src/CSWarfront/Game/SurfaceHeightSampler.cs）がCSのTerrainManagerを叩いて実装する。
    /// </summary>
    public interface IHeightSampler
    {
        /// <summary>マップ座標(x, z)における地表の高さ(y)を返す。呼び出し側（MovementStep）は
        /// sim スレッドから呼ぶ想定（Game層の実装がTerrainManager読み取りを行うため）。</summary>
        float SampleHeight(float x, float z);
    }
}
