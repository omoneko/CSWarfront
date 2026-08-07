using System;
using System.Reflection;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Reads the MissileDisaster mod's impact beacon (MissileDisaster.Game.ImpactBeacon, which
    /// publishes the impacts of all warhead types as {id, x, z, destructionRadius, burnRadius,
    /// isNuclear}) via reflection, and applies damage to units through
    /// Core.DisasterImpactStep.ApplyImpact for each new impact (Task94, addressing the Workshop
    /// comment "units do not die from missile disasters").
    ///
    /// Same policy as ExternalThreatBridge's BeamLogAdapter:
    ///  - No build-time reference. Resolution is attempted only once; failure (not installed /
    ///    old version) is cached and the feature is disabled.
    ///  - The first read baselines the current ID; past impacts are not applied.
    ///  - Never throws an exception into the game loop, no matter what happens.
    /// Called every tick from the sim thread inside _stateLock (no internal throttling — impacts
    /// are rare events, and the CurrentId() call costs only about one lock acquisition).
    /// </summary>
    internal static class DisasterImpactBridge
    {
        private const string AssemblyName = "MissileDisaster";
        private const string TypeName = "MissileDisaster.Game.ImpactBeacon";
        private const int Stride = 6;

        private static bool _resolveAttempted;
        private static bool _available;
        private static MethodInfo _currentIdMethod;
        private static MethodInfo _snapshotMethod;
        private static bool _errorLogged;
        private static long _lastConsumedId = -1;

        public static void Advance(WarState state)
        {
            if (!EnsureResolved()) return;

            try
            {
                long current = (long)_currentIdMethod.Invoke(null, null);
                if (_lastConsumedId < 0)
                {
                    _lastConsumedId = current; // baseline: past impacts are not applied
                    return;
                }
                if (current <= _lastConsumedId) return;

                float[] snap = (float[])_snapshotMethod.Invoke(null, null);
                for (int s = 0; s + Stride - 1 < snap.Length; s += Stride)
                {
                    long id = (long)snap[s];
                    if (id <= _lastConsumedId) break; // newest-first, so stop once an already-consumed ID is reached

                    int hits = DisasterImpactStep.ApplyImpact(state,
                        snap[s + 1], snap[s + 2], snap[s + 3], snap[s + 4], snap[s + 5] >= 0.5f);
                    if (hits > 0)
                    {
                        ModConfig.Log("DisasterImpactBridge: missile impact hit " + hits + " unit(s).");
                    }
                }
                _lastConsumedId = current;
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    ModConfig.LogError("DisasterImpactBridge: read error, disabling for the rest of this session: " + e);
                }
                _available = false;
            }
        }

        private static bool EnsureResolved()
        {
            if (_resolveAttempted) return _available;
            _resolveAttempted = true;

            try
            {
                Assembly asm = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == AssemblyName) { asm = assemblies[i]; break; }
                }
                if (asm == null)
                {
                    _available = false; // not installed: not an error
                    return false;
                }

                Type type = asm.GetType(TypeName);
                if (type == null)
                {
                    // Older version of MissileDisaster (before the generic beacon was added): the feature is simply disabled.
                    ModConfig.Log("DisasterImpactBridge: ImpactBeacon not found (older MissileDisaster?); unit damage from disaster missiles is disabled.");
                    _available = false;
                    return false;
                }

                _currentIdMethod = type.GetMethod("CurrentId", BindingFlags.Public | BindingFlags.Static);
                _snapshotMethod = type.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static);
                if (_currentIdMethod == null || _snapshotMethod == null)
                {
                    ModConfig.LogError("DisasterImpactBridge: ImpactBeacon members not found. Disabling.");
                    _available = false;
                    return false;
                }

                _available = true;
                ModConfig.Log("DisasterImpactBridge: detected MissileDisaster impact beacon.");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("DisasterImpactBridge: resolve error: " + e);
                _available = false;
                return false;
            }
        }
    }
}
