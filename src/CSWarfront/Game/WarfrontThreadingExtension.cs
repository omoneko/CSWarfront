using ICities;
using CSWarfront.Game.UI;
namespace CSWarfront.Game
{
    /// <summary>
    /// スレッド分担（Task19で変更）:
    ///  - sim スレッド（OnAfterSimulationTick）: Core判断ロジック＋CS実体（建物等）バッファ読み取り専用。
    ///    CS建物バッファの空きスロット割当・空間グリッドはsimスレッドが所有するため、他スレッドから
    ///    同時に触るとバッファが破壊されCS自身のシミュレーションコードがIndexOutOfRangeException
    ///    （捕捉されないポップアップ）を投げる。
    ///  - メインスレッド（OnUpdate）: ユニットの見た目（Unity GameObject）の同期専用。
    ///    CS実体には一切触れない（new GameObject/AddComponent/Destroy/transform書込等のUnity
    ///    オブジェクトAPIはメインスレッドでのみ呼び出し可能なため、ここに置く）。
    /// </summary>
    public class WarfrontThreadingExtension : ThreadingExtensionBase
    {
        public override void OnAfterSimulationTick()
        {
            try { MilitaryManager.OnSimTick(); }
            catch (System.Exception e) { ModConfig.LogError("OnSimTick: " + e); }
        }

        /// <summary>
        /// メインスレッド。ユニット見た目の同期に加え（Task25）、基地情報パネル（Game/UI/BaseInfoPanel）の
        /// 生成・表示更新もここから駆動する。BaseInfoPanel の各メソッドは内部で例外を必ず握るため、
        /// ここでの try/catch は他の main-thread 処理と同様の多重防御。一時停止中も動く。
        /// </summary>
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try { MilitaryManager.OnMainVisualUpdate(); }
            catch (System.Exception e) { ModConfig.LogError("OnMainVisualUpdate: " + e); }

            try
            {
                BaseInfoPanel.EnsureCreated();
                BaseInfoPanel.UpdateVisibility();
            }
            catch (System.Exception e) { ModConfig.LogError("BaseInfoPanel update: " + e); }
        }
    }
}
