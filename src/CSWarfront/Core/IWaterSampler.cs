namespace CSWarfront.Core
{
    /// <summary>
    /// Task61: 海上ユニット(Domain.Sea)の移動範囲・水面高さを判定するための、UnityEngine非依存の薄い
    /// シーム（IHeightSampler/RoadGraph/CoverMapと同じ供給パターン）。Coreはこのインターフェースを
    /// 消費するだけで、実装は一切知らない。Game層（src/CSWarfront/Game/WaterSampler.cs）が
    /// CSのTerrainManagerを叩いて実装する。
    ///
    /// MVPの既知の制約: 海上ユニットの移動（MovementStepのSea分岐）はA*等の水上経路探索を一切行わない、
    /// 単純な直線移動である。直線移動の次ステップが陸地に踏み込む場合はその場で停止する（呼び出し側
    /// MovementStep参照）ため、岬や半島の裏側にいる目標へは物理的に到達できないことがある
    /// （海軍専用のパスファインディングは将来課題）。
    /// </summary>
    public interface IWaterSampler
    {
        /// <summary>マップ座標(x, z)における水面の高さ(y)の取得を試みる。水がない地点、または
        /// 判定に失敗した場合はfalseを返す。呼び出し側はfalseの場合、levelの値を一切使ってはならない。</summary>
        bool TrySampleWaterLevel(float x, float z, out float level);

        /// <summary>マップ座標(x, z)が水面（航行可能）かどうかを返す。IHeightSamplerと違いTry形式ではない：
        /// 「判定できない」場合もfalse（=水ではない、陸地扱い）とすることで、海上ユニットが不明な地形へ
        /// 誤って踏み込むより、その場で足止めされる方を安全側とする（MovementStepのSea分岐参照）。</summary>
        bool IsWater(float x, float z);
    }
}
