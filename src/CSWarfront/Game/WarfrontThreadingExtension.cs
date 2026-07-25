using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// CS実体操作（車両/建物の生成・バッファ書込）はすべてsimスレッド専用APIであるため、
    /// OnAfterSimulationTick（simスレッド）のみを使用する。メインスレッド側（OnUpdate）で行う
    /// CS実体操作は存在しない（存在させてはならない。バッファ破壊によるIndexOutOfRangeの原因）。
    /// </summary>
    public class WarfrontThreadingExtension : ThreadingExtensionBase
    {
        public override void OnAfterSimulationTick()
        {
            try { MilitaryManager.OnSimTick(); }
            catch (System.Exception e) { ModConfig.LogError("OnSimTick: " + e); }
        }
    }
}
