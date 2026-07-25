using ICities;
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

        /// <summary>メインスレッド。ユニット見た目の同期のみ（CS実体操作は行わない）。一時停止中も動く。</summary>
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try { MilitaryManager.OnMainVisualUpdate(); }
            catch (System.Exception e) { ModConfig.LogError("OnMainVisualUpdate: " + e); }
        }
    }
}
