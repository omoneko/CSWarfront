using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Core.IHeightSamplerのGame層実装（Task53、「ユニットが地面にめり込む」不具合の修正）。
    /// CSのTerrainManagerを叩いて、道路/建物建設後の"見た目の"地表（roads on embankments,
    /// terrain modified by construction, bridges等を含む）を返す。
    ///
    /// スレッド注記: MovementStep.Advance（MilitaryManager.OnSimTickのsimスレッド）から呼ばれる想定。
    /// DevelopmentSampler/RoadGraphBuilderと同じ前提＝TerrainManagerの読み取り専用APIをsimスレッドで
    /// 呼ぶこと自体はCSの制約に反しない（メインスレッド専用なのはGameObject生成/描画/UI操作の方）。
    ///
    /// 検証済みシグネチャ（Assembly-CSharp.dllをILSpyでデコンパイルして再確認、Task55）:
    ///  - TerrainManager.SampleDetailHeight(Vector3 worldPos): float ← 採用するのはこちら。
    ///    実装（ILSpy逆コンパイル結果）:
    ///      float x = worldPos.x / 4f + 2160f;
    ///      float z = worldPos.z / 4f + 2160f;
    ///      return SampleDetailHeight(x, z) * (1f / 64f);
    ///    ワールド座標→detailヒートマップのグリッド座標（0..4320、4321刻み）への変換と、
    ///    raw格納値→ワールド高さ単位への1/64スケール変換の両方をこの中で行っている。
    ///  - TerrainManager.SampleDetailHeight(float x, float z): float ← Task53はこちらを誤って
    ///    採用していた（バグの根本原因）。実装（ILSpy逆コンパイル結果、抜粋）:
    ///      int num3 = Mathf.Clamp((int)x, 0, 4320);
    ///      int num4 = Mathf.Clamp((int)z, 0, 4320);
    ///      ... GetDetailHeight(...)で上記グリッド座標を直接インデックスに使い、1/64スケールなしで返す
    ///    つまりこのオーバーロードの引数x/zは「detailグリッド座標そのもの」であり、ワールド座標では
    ///    ない。Task53はここへワールド座標のx/zをそのまま渡していたため、(a) 座標変換
    ///    (/4+2160)が欠落し全く別の位置を参照し、(b) 1/64のスケール変換も欠落して返り値が
    ///    最大64倍過大になっていた。これが「ユニットが空中戦を始める」（地表よりはるかに高い
    ///    Yへスナップする）不具合の根本原因である。TrySampleHeightは例外を投げないため
    ///    （インデックスはClampされている）、このバグはログにエラーを一切残さず、ユーザーの
    ///    output_log.txtにもSurfaceHeightSampler関連のエラーは存在しなかった（Task55調査で確認）。
    ///  - TerrainManager.instance: Singleton&lt;TerrainManager&gt;.instance（RoadGraphBuilder/
    ///    CoverMapBuilderと同じColossalFramework.Singletonパターン、Task53から変更なし）。
    ///
    /// 上記のSampleDetailHeight(Vector3)を採用する理由（Task53の元々の意図はそのまま維持）:
    ///  detail heightmapは道路・建物の建設で地形が実際にフラット化/変形された結果を反映した
    ///  "見た目どおり"の高さであり、SampleRawHeight/SampleFinalHeight/SampleBlockHeightが参照する
    ///  粗い（1081刻み、約8m/セル）control heightmap（建設前の生の地形）とは別物。道路の盛土・橋・
    ///  建物の基礎などで実際に変化した地表を反映するのはSampleDetailHeightの方である。
    ///
    /// ハードニング（Task53導入、Task55でも維持）: このマップの実測地表は約270であり、旧実装（失敗時に
    /// 0fを返すfloat SampleHeight）だと、TerrainManagerが一時的に未生成/例外を投げた瞬間にMovementStep
    /// がその0fをそのままユニットのYへ採用し、1tickだけ地表の約270下へテレポートする可視グリッチに
    /// なっていた。TrySampleHeight形式にし、失敗時はfalseを返してMovementStep側にY補間フォールバックを
    /// 委ねる（このクラスは決して失敗値をheightに"それらしい"値として書き込まない）。
    ///
    /// 多層防御（Task55追記）: 上記のような「例外は投げないが値が荒唐無稽」なバグ自体の再発を防ぐため、
    /// Core.MovementStep側にもMaxSurfaceDeviationによる乖離クランプを追加した（このクラスの契約
    /// （成功したら正しい高さを返す）が将来また崩れても、被害を機械的に抑える保険）。
    /// </summary>
    internal sealed class SurfaceHeightSampler : IHeightSampler
    {
        // RoadGraphBuilder/CoverMapBuilderと同じ間引きパターン（Task23/Task44）: MovementStepは
        // simスレッド上でtickごとに大量に呼ばれるため、失敗が続く間ログを埋め尽くさないよう最初の
        // 1回だけ記録する。成功したら次に失敗した際にまた1回だけ記録する（抑制状態をリセット）。
        // simスレッド専用アクセスのためロック不要。
        private static bool _failureAlreadyLogged;

        public bool TrySampleHeight(float x, float z, out float height)
        {
            // RoadGraphBuilder/CoverMapBuilderと同じ防御方針: Singleton未生成（レベルロード直後の
            // ごく短い間隙等）ならこのtickだけ諦める。ハードニング: ここで"それらしい"値（0f等）を
            // 返さず、失敗をfalseで明示し、呼び出し元(MovementStep)に既存のY補間結果をそのまま
            // 採用させる（0fがそのままYに採用され地表の遥か下へテレポートする不具合の再発防止）。
            if (!Singleton<TerrainManager>.exists)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("SurfaceHeightSampler.TrySampleHeight: TerrainManager not ready; falling back to interpolation");
                    _failureAlreadyLogged = true;
                }
                height = default(float);
                return false;
            }

            try
            {
                // Task55: SampleDetailHeight(float, float)はdetailグリッド座標を要求する内部向けの
                // オーバーロードであり、ワールド座標ではない。ワールド座標のx/zからワールド単位の
                // 高さを得るにはSampleDetailHeight(Vector3)を使う（座標変換と1/64スケール変換の両方を
                // 内部で行ってくれる。上のクラスdocコメントのILSpy逆コンパイル結果を参照）。
                height = Singleton<TerrainManager>.instance.SampleDetailHeight(new Vector3(x, 0f, z));
                _failureAlreadyLogged = false; // 成功したので次の失敗はまた1回だけログする
                return true;
            }
            catch (System.Exception e)
            {
                // 例外が続いてもログを埋め尽くさないよう最初の1回だけ記録する（間引きなしで毎tick
                // 出すと"Nothing may throw into the game loop"の原則がログスパムに変わってしまう）。
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("SurfaceHeightSampler.TrySampleHeight error: " + e);
                    _failureAlreadyLogged = true;
                }
                height = default(float);
                return false;
            }
        }
    }
}
