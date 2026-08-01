using System;
using System.Reflection;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// MissileDisaster MODの着弾ビーコン（MissileDisaster.Game.ImpactBeacon、全弾種の着弾を
    /// {id, x, z, destructionRadius, burnRadius, isNuclear} で公開）をリフレクションで読み、
    /// 新着1件につきCore.DisasterImpactStep.ApplyImpactでユニットへ被害を適用する（Task94、
    /// Workshopコメント対応「ミサイル災害でユニットが死なない」）。
    ///
    /// ExternalThreatBridgeのBeamLogAdapterと同じ方針:
    ///  - ビルド時参照なし。解決は1回だけ試み、失敗（未導入/旧バージョン）はキャッシュして無効化。
    ///  - 最初の読み取りで現在IDをベースライン化し、過去の着弾へは適用しない。
    ///  - 何が起きてもゲームループへ例外を投げない。
    /// simスレッド・_stateLock内から毎tick呼ぶ（内部で間引かない——着弾は稀なイベントで、
    /// CurrentId()の呼び出しはロック1回ぶんの軽さのため）。
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
                    _lastConsumedId = current; // ベースライン: 過去の着弾は適用しない
                    return;
                }
                if (current <= _lastConsumedId) return;

                float[] snap = (float[])_snapshotMethod.Invoke(null, null);
                for (int s = 0; s + Stride - 1 < snap.Length; s += Stride)
                {
                    long id = (long)snap[s];
                    if (id <= _lastConsumedId) break; // 新しい順なので既読IDに達したら終了

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
                    _available = false; // 未導入: エラーではない
                    return false;
                }

                Type type = asm.GetType(TypeName);
                if (type == null)
                {
                    // 旧バージョンのMissileDisaster（汎用ビーコン追加前）: 単に機能無効。
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
