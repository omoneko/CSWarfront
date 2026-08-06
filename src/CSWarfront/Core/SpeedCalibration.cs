namespace CSWarfront.Core
{
    /// <summary>
    /// Calibration constants for CS's real-time ↔ in-game-time relationship, and conversion utilities for
    /// defining unit speeds (the core's internal representation = map distance per in-game hour) in km/h
    /// terms (Task26).
    ///
    /// [Background] MvpUnitTypes' Speed is "map distance (m) / in-game time", consumed directly by
    /// MovementStep.Advance as stepLen = type.Speed * dt (dt in in-game hours, derived by
    /// MilitaryManager.OnSimTick from the delta of SimulationManager.instance.m_currentGameTime). To match
    /// the apparent speed to real-world km/h, the conversion km/h -> Speed requires knowing "how many
    /// in-game hours pass per real second at 1x speed" (InGameHoursPerRealSecond).
    ///
    /// [Derivation of InGameHoursPerRealSecond (live-DLL reflection research; details in
    /// task-26-report.md, verified by disassembling Assembly-CSharp.dll with ILDASM)]
    ///  - SimulationManager.SimulationStep() advances SimulationMetaData.m_currentDateTime (and, via the
    ///    interpolation in Update(), SimulationManager.m_currentGameTime — the value
    ///    MilitaryManager.OnSimTick reads) by m_timePerFrame per processed "frame".
    ///  - m_timePerFrame is a TimeSpan constant hard-coded in Awake():
    ///    TimeSpan.FromTicks(1_476_562_500) = 147.65625 seconds = 0.041015625 hours
    ///    (field value confirmed directly via reflection; 0x58028e44 ticks).
    ///  - SimulationManager.SIMULATION_DAY_FRAMES = 585 (likewise a hard-coded constant, 0x249).
    ///    585 frames × 147.65625 s ≈ 86,378.9 s ≈ 24.00 hours, showing m_timePerFrame is calibrated so
    ///    that "585 frames ≈ one calendar day" — strong corroboration that the frame-advance reading above
    ///    is correct.
    ///  - One frame = one SimulationStep() call (at simulation speed 1x, get_FinalSimulationSpeed()=1),
    ///    and ISimulationManager.OnAfterSimulationTick (the trigger that drives MilitaryManager.OnSimTick
    ///    via WarfrontThreadingExtension.OnAfterSimulationTick) fires exactly once at the end of
    ///    SimulationStep(). Hence at 1x, dt per tick = 0.041015625 hours ≈ 0.041h — closely matching the
    ///    dt≈0.04h/tick actually observed in the live logs (the measurement this task investigated).
    ///  - SimulationStep() calls are driven by SimulationManager.FixedUpdate() (Unity's fixed-timestep
    ///    callback, capped by m_maxFramesBehind=14) incrementing m_updateCounter and pulsing the sim
    ///    thread. So tick frequency = FixedUpdate() frequency = 1 / Time.fixedDeltaTime (in the normal
    ///    case where the sim keeps up).
    ///  - The actual Time.fixedDeltaTime value is a Unity project setting; no set_fixedDeltaTime call
    ///    exists in either C# assembly (Assembly-CSharp.dll / ColossalManaged.dll — checked with ildasm,
    ///    so no code override). The setting itself lives in Unity's binary project settings, unreadable
    ///    via .NET reflection, so the Unity default of 50Hz (0.02s) is assumed. This is the least certain
    ///    part of the constant.
    ///  - Therefore: InGameHoursPerRealSecond = 50 [ticks/s] × 0.041015625 [hours/tick]
    ///    = 2.05078125 (assuming ~2.05 in-game hours pass per real second at 1x).
    ///    For reference: the old Speed=250 (map distance / in-game hour) converts via this constant to
    ///    250 * 2.05078125 * 3.6 ≈ 1845.7 km/h — consistent with the "way too fast" reports.
    ///  - Because this constant contains the unverified Time.fixedDeltaTime assumption, it must be
    ///    verified in-game. The calibration diagnostic log in Game/MilitaryManager.cs
    ///    ("SpeedCalibration measured: ...", which accumulates ~10 seconds of measured in-game vs real
    ///    time from OnSimTick/OnUpdate and prints once) compares the measured value with this constant.
    /// </summary>
    public static class SpeedCalibration
    {
        /// <summary>In-game hours that pass per real second at 1x speed. Derivation in the class comment.</summary>
        public const float InGameHoursPerRealSecond = 2.05078125f;

        /// <summary>
        /// Converts a real-world speed given in km/h into the core's internal Speed representation
        /// (map distance / in-game hour; map unit = meter).
        /// Derivation: metresPerRealSecond = kmh * 1000 / 3600 (km/h -> m/s)
        ///             unitsPerGameHour   = metresPerRealSecond / InGameHoursPerRealSecond
        ///             (converting "distance per real second" into "distance per in-game hour")
        /// kmh=0 -> 0; linear (proportional) in kmh.
        /// </summary>
        public static float UnitsPerGameHourFromKmh(float kmh)
        {
            float metresPerRealSecond = kmh * 1000f / 3600f;
            return metresPerRealSecond / InGameHoursPerRealSecond;
        }

        /// <summary>
        /// Inverse of UnitsPerGameHourFromKmh (Task31: for showing UnitType.Speed in km/h on the unit info
        /// panel). The same formula (speed * InGameHoursPerRealSecond * 3.6) was already inlined in
        /// Game/MilitaryManager.cs's diagnostics (LogDiagnostics) and
        /// Game/SpeedCalibrationDiagnostics.TryLog; extracted into the core in reusable form.
        /// </summary>
        public static float KmhFromUnitsPerGameHour(float unitsPerGameHour)
        {
            return unitsPerGameHour * InGameHoursPerRealSecond * 3.6f;
        }
    }
}
