using ColossalFramework;
using CSWarfront.Core;

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
    /// 検証済みシグネチャ（Assembly-CSharp.dllをILSpyでデコンパイルして確認、Task53）:
    ///  - TerrainManager.SampleDetailHeight(float x, float z): float
    ///    実装は m_patches[...].m_simDetailIndex 経由で「detail heightmap」（1080区画のraw/final
    ///    heightmapの4倍解像度、4321刻み）を参照する。この detail heightmap は道路・建物の建設で
    ///    地形が実際にフラット化/変形された結果を反映した"見た目どおり"の高さであり、
    ///    SampleRawHeight/SampleFinalHeight/SampleBlockHeightが参照する粗い（1081刻み、約8m/セル）
    ///    control heightmap（建設前の生の地形、または建設とは独立した制御点）とは別物。
    ///    そのため道路の盛土・橋・建物の基礎などで実際に変化した地表を反映するのは
    ///    SampleDetailHeightの方であり、これを採用する（SampleRawHeightは不採用＝建設を無視した
    ///    生の地形しか返さないため、まさにこのTask53が修正したいバグの原因そのものになる）。
    ///  - TerrainManager.instance: Singleton&lt;TerrainManager&gt;.instance（RoadGraphBuilder/
    ///    CoverMapBuilderと同じColossalFramework.Singletonパターン）。
    ///
    /// ハードニング（Task53追記）: このマップの実測地表は約270であり、旧実装（失敗時に0fを返す
    /// float SampleHeight）だと、TerrainManagerが一時的に未生成/例外を投げた瞬間にMovementStepが
    /// その0fをそのままユニットのYへ採用し、1tickだけ地表の約270下へテレポートする可視グリッチに
    /// なっていた。TrySampleHeight形式にし、失敗時はfalseを返してMovementStep側にY補間フォールバックを
    /// 委ねる（このクラスは決して失敗値をheightに"それらしい"値として書き込まない）。
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
                height = Singleton<TerrainManager>.instance.SampleDetailHeight(x, z);
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
