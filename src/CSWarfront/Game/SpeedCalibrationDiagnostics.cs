using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// CSWarfront.Core.SpeedCalibration.InGameHoursPerRealSecond（DLLリフレクション調査から導出した
    /// 仮定込みの定数、Unity既定Time.fixedDeltaTime=50Hzを仮定）を実機で検証するための較正診断（Task26）。
    /// MilitaryManager.OnSimTick（simスレッド）でゲーム内時間dtの積算を、
    /// WarfrontThreadingExtension.OnUpdate（メインスレッド）で実時間の積算を受け取り、
    /// 実時間がCalibWindowSeconds秒ぶんたまった時点で実測比率をセッション中1回だけログする。
    /// 2つのスレッドから触れるため専用ロックで保護する（MilitaryManager._stateLockとは無関係な
    /// 単純カウンタのため、別ロックにして状態操作をブロックしないようにしている）。
    /// </summary>
    internal static class SpeedCalibrationDiagnostics
    {
        private static readonly object _lock = new object();
        private static float _gameHoursAccum;
        private static float _realSecondsAccum;
        private static bool _logged;
        private const float WindowSeconds = 10f;

        /// <summary>simスレッド（MilitaryManager.OnSimTick）から、既に計算済みのdt（ゲーム内時間）を渡す。</summary>
        internal static void AccumulateGameHours(float dt)
        {
            lock (_lock)
            {
                if (_logged) return;
                _gameHoursAccum += dt;
            }
            TryLog();
        }

        /// <summary>メインスレッド（WarfrontThreadingExtension.OnUpdate）から実時間の経過を渡す。
        /// 一時停止中もOnUpdateは動くため実時間だけ積み上がることがあるが、ログはセッション中1回だけ
        /// 出すだけなので実害はない。</summary>
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
                // 実測比率でTank_T1の速度をkm/hに逆変換する（想定定数ではなく実測値を使う）。
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

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に積算をクリアし、次セッションで
        /// 再度較正診断を実行できるようにする。</summary>
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
