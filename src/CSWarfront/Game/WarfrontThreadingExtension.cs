using ICities;
namespace CSWarfront.Game
{
    public class WarfrontThreadingExtension : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try
            {
                MilitaryManager.EnsureInitialized();
                if (!SimulationManager.instance.SimulationPaused)
                    MilitaryManager.OnMainUpdate(simulationTimeDelta);
            }
            catch (System.Exception e) { ModConfig.LogError("OnUpdate: " + e); }
        }

        public override void OnAfterSimulationTick()
        {
            try { MilitaryManager.OnSimTick(); }
            catch (System.Exception e) { ModConfig.LogError("OnSimTick: " + e); }
        }
    }
}
