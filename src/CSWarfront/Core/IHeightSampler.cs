namespace CSWarfront.Core
{
    /// <summary>
    /// Task53:「ユニットが地面にめり込む」不具合の修正。地形の生の高さではなく、道路/建物建設後の
    /// "見た目の"地表（roads on embankments, terrain modified by construction, bridges等を含む）を
    /// サンプリングするための、UnityEngine非依存の薄いシーム（RoadGraph/CoverMapと同じ供給パターン）。
    ///
    /// Coreはこのインターフェースを消費するだけで、実装は一切知らない。Game層
    /// （src/CSWarfront/Game/SurfaceHeightSampler.cs）がCSのTerrainManagerを叩いて実装する。
    ///
    /// Task53ハードニング: 旧SampleHeight(float,float):floatは、TerrainManagerが一時的に未生成/例外を
    /// 投げた場合に0fを返していた。マップの地表は0f付近とは限らない（実測で約270）ため、その0fが
    /// そのままユニットのYへ採用されると1tickだけ地表の遥か下へテレポートする可視バグになる。
    /// Try形式にすることで「サンプリング失敗」を呼び出し側（MovementStep）が明示的に判別できるようにし、
    /// 失敗時はY補間へフォールバックさせ、失敗値を絶対にYへ採用しないようにする。
    /// </summary>
    public interface IHeightSampler
    {
        /// <summary>マップ座標(x, z)における地表の高さ(y)の取得を試みる。呼び出し側（MovementStep）は
        /// sim スレッドから呼ぶ想定（Game層の実装がTerrainManager読み取りを行うため）。
        /// 成功すればtrueを返しheightに値を格納する。失敗（地形システム未生成・例外等）した場合は
        /// falseを返す。呼び出し側はfalseの場合、heightの値を一切使ってはならない
        /// （既存のY補間・スナップ挙動を維持すること）。</summary>
        bool TrySampleHeight(float x, float z, out float height);
    }
}
