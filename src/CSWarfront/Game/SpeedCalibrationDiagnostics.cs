using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Calibration diagnostics (Task26) for verifying CSWarfront.Core.SpeedCalibration.
    /// InGameHoursPerRealSecond (a constant with built-in assumptions derived from DLL reflection
    /// investigation, assuming Unity's default Time.fixedDeltaTime = 50Hz) on the actual game.
    /// Receives the accumulation of in-game time dt from MilitaryManager.OnSimTick (sim thread)
    /// and the accumulation of real time from WarfrontThreadingExtension.OnUpdate (main thread),
    /// and once CalibWindowSeconds of real time has accumulated, logs the measured ratio exactly
    /// once per session. Because it is touched from two threads it is protected by a dedicated
    /// lock (these are simple counters unrelated to MilitaryManager._stateLock, so a separate lock
    /// is used to avoid blocking state operations).
    /// </summary>
    internal static class SpeedCalibrationDiagnostics
    {
        private static readonly object _lock = new object();
        private static float _gameHoursAccum;
        private static float _realSecondsAccum;
        private static bool _logged;
        private const float WindowSeconds = 10f;

        /// <summary>Called from the sim thread (MilitaryManager.OnSimTick) with the already-computed
        /// dt (in-game time).</summary>
        internal static void AccumulateGameHours(float dt)
        {
            lock (_lock)
            {
                if (_logged) return;
                _gameHoursAccum += dt;
            }
            TryLog();
        }

        /// <summary>Called from the main thread (WarfrontThreadingExtension.OnUpdate) with elapsed
        /// real time. Since OnUpdate keeps running while paused, real time alone may keep
        /// accumulating, but the log is only emitted once per session, so this causes no real harm.</summary>
        internal static void AccumulateRealSeconds(float realTimeDelta)
        {
            lock (_lock)
            {
                if (_logged) return;
                _realSecondsAccum += realTimeDelta;
            }
            TryLog();
        }

        private static void TryLog()
        {
            float measured;
            float tankKmh;
            lock (_lock)
            {
                if (_logged) return;
                if (_realSecondsAccum < WindowSeconds || _realSecondsAccum <= 0f) return;

                measured = _gameHoursAccum / _realSecondsAccum;
                // Convert Tank_T1's speed back to km/h using the measured ratio (using the measured
                // value, not the assumed constant).
                tankKmh = MvpUnitTypes.Tank_T1().Speed * measured * 3.6f;
                _logged = true;
            }

            try
            {
                ModConfig.Log(string.Format(
                    "SpeedCalibration measured: inGameHoursPerRealSecond={0:0.00} (assumed {1:0.00}) -> Tank_T1 ≈ {2:0}km/h",
                    measured, SpeedCalibration.InGameHoursPerRealSecond, tankKmh));
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("SpeedCalibrationDiagnostics.TryLog error: " + e);
            }
        }

        /// <summary>Clears the accumulators on level unload (via MilitaryManager.Reset) so the
        /// calibration diagnostics can run again in the next session.</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _gameHoursAccum = 0f;
                _realSecondsAccum = 0f;
                _logged = false;
            }
        }
    }
}
