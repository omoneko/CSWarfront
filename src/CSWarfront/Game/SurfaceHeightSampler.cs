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
    /// </summary>
    internal sealed class SurfaceHeightSampler : IHeightSampler
    {
        public float SampleHeight(float x, float z)
        {
            // RoadGraphBuilder/CoverMapBuilderと同じ防御方針: Singleton未生成（レベルロード直後の
            // ごく短い間隙等）ならこのtickだけ諦める。呼び出し元(MovementStep)は本来のX/Y補間結果を
            // そのまま採用済みのため、ここで例外を投げずに"それらしい"値を返す必要がある。
            // WarState.Height自体は非nullのまま維持する（MilitaryManagerが毎tick作り直すことはない）ため、
            // 呼び出し側で例外を握りつぶすのがこのクラスの責務になる。
            if (!Singleton<TerrainManager>.exists) return 0f;

            try
            {
                return Singleton<TerrainManager>.instance.SampleDetailHeight(x, z);
            }
            catch (System.Exception e)
            {
                // MovementStepはsimスレッド上でtickごとに大量に呼ばれるため、例外が続いても
                // ログを埋め尽くさないよう詳細メッセージのみ（間引きはあえてしない＝発生自体が
                // 想定外であり、Nothing may throw into the game loopの原則を優先する）。
                ModConfig.LogError("SurfaceHeightSampler.SampleHeight error: " + e);
                return 0f;
            }
        }
    }
}
