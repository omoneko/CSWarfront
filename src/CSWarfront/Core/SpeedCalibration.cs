namespace CSWarfront.Core
{
    /// <summary>
    /// CSの実時間↔ゲーム内時間の較正定数と、km/h基準でユニット速度（Core内部表現＝
    /// マップ距離/ゲーム内時間）を定義するための変換ユーティリティ（Task26）。
    ///
    /// 【背景】MvpUnitTypesのSpeedは「マップ距離(m) / ゲーム内時間」であり、
    /// MovementStep.Advanceが stepLen = type.Speed * dt（dtはゲーム内時間、
    /// MilitaryManager.OnSimTickがSimulationManager.instance.m_currentGameTimeの差分から算出）
    /// として直接消費する。そのため「見た目の速さ」を現実のkm/hに合わせるには、
    /// 「1x速度で実時間1秒あたりゲーム内時間が何時間進むか」（InGameHoursPerRealSecond）が
    /// 分かっていないと、km/h -> Speed の変換ができない。
    ///
    /// 【InGameHoursPerRealSecond の導出根拠（実機DLLリフレクション調査。詳細は
    /// task-26-report.md、ILDASMでAssembly-CSharp.dllを逆アセンブルして確認）】
    ///  - SimulationManager.SimulationStep()は、処理した「フレーム」1つにつき
    ///    SimulationMetaData.m_currentDateTime（延いてはUpdate()内の補間を経てSimulationManager.
    ///    m_currentGameTime、つまりMilitaryManager.OnSimTickが読む値）をm_timePerFrameぶん進める。
    ///  - m_timePerFrameはAwake()内でハードコードされたTimeSpan定数：
    ///    TimeSpan.FromTicks(1_476_562_500) = 147.65625秒 = 0.041015625時間
    ///    （リフレクションでフィールド値を直接確認済み。0x58028e44 tick）。
    ///  - SimulationManager.SIMULATION_DAY_FRAMES = 585（同じくハードコード定数、0x249）。
    ///    585フレーム × 147.65625秒 ≈ 86,378.9秒 ≈ 24.00時間 となり、m_timePerFrameが
    ///    「585フレーム ≈ 暦1日」になるよう校正されていることが分かる。これは上記フレーム進行の
    ///    解釈が正しいことの強い裏付けである。
    ///  - 1フレーム = 1 SimulationStep()呼び出し（シミュレーション速度1x、
    ///    get_FinalSimulationSpeed()=1の場合）であり、ISimulationManager.OnAfterSimulationTick
    ///    （WarfrontThreadingExtension.OnAfterSimulationTick経由でMilitaryManager.OnSimTickを
    ///    駆動する発火点）はSimulationStep()の末尾で1回だけ呼ばれる。よって1x速度時、
    ///    tickあたりのdt = 0.041015625時間 ≈ 0.041h。これは実機ログで観測された
    ///    dt≈0.04h/tick（本タスクの調査対象の実測値）とほぼ一致する。
    ///  - SimulationManager.SimulationStep()の呼び出しは、SimulationManager.FixedUpdate()
    ///    （Unity固定タイムステップコールバック、m_maxFramesBehind=14でキャップ）が
    ///    m_updateCounterをインクリメントしてsimスレッドをパルスすることで駆動される。
    ///    つまりtick頻度 = FixedUpdate()の呼び出し頻度 = 1 / Time.fixedDeltaTime（sim側が
    ///    追いつけている通常時）。
    ///  - Time.fixedDeltaTimeの実値はUnityのプロジェクト設定であり、C#コード側（
    ///    Assembly-CSharp.dll / ColossalManaged.dll）のどちらにもset_fixedDeltaTime呼び出しは
    ///    見つからなかった（ildasmで確認済み＝コードでの上書きは無い）。ただし実際の設定値自体は
    ///    Unityのバイナリプロジェクト設定に格納されており、.NETリフレクションでは読めないため、
    ///    Unity既定値である50Hz（0.02秒）と仮定する。これが本定数の中で最も不確実な部分である。
    ///  - 上記より： InGameHoursPerRealSecond = 50 [tick/秒] × 0.041015625 [時間/tick]
    ///    = 2.05078125 （1x速度時、実測1秒でゲーム内時間が約2.05時間進む、との仮定）。
    ///    参考: 旧Speed=250（マップ距離/ゲーム内時間）は、この定数で換算すると
    ///    250 * 2.05078125 * 3.6 ≈ 1845.7 km/h に相当していた（「速すぎる」という報告と整合）。
    ///  - この定数はUnityのTime.fixedDeltaTimeという未検証の仮定を含むため、実機で必ず検証する
    ///    こと。Game/MilitaryManager.csの較正診断ログ（"SpeedCalibration measured: ..."、
    ///    OnSimTick/OnUpdateから実測のゲーム内時間経過と実時間経過を約10秒分積算して1回だけ出力）で
    ///    実測値と本定数を比較できる。
    /// </summary>
    public static class SpeedCalibration
    {
        /// <summary>1x速度時、実時間1秒あたりに進むゲーム内時間（時間）。導出根拠はクラスコメント参照。</summary>
        public const float InGameHoursPerRealSecond = 2.05078125f;

        /// <summary>
        /// km/hで指定された現実世界の速度を、Core内部のSpeed表現（マップ距離/ゲーム内時間、
        /// マップ単位=メートル）に変換する。
        /// 導出: metresPerRealSecond = kmh * 1000 / 3600 （km/h -> m/s）
        ///       unitsPerGameHour   = metresPerRealSecond / InGameHoursPerRealSecond
        ///       （「実時間1秒あたりの移動距離」を「ゲーム内時間1時間あたりの移動距離」に変換）
        /// kmh=0 -> 0、kmhに対して線形（比例）。
        /// </summary>
        public static float UnitsPerGameHourFromKmh(float kmh)
        {
            float metresPerRealSecond = kmh * 1000f / 3600f;
            return metresPerRealSecond / InGameHoursPerRealSecond;
        }

        /// <summary>
        /// UnitsPerGameHourFromKmh の逆変換（Task31: ユニット情報パネルにUnitType.Speedをkm/h表示するため）。
        /// Game/MilitaryManager.cs の診断ログ（LogDiagnostics）や
        /// Game/SpeedCalibrationDiagnostics.TryLog で既に同じ式（speed * InGameHoursPerRealSecond * 3.6）が
        /// インライン展開されていたものを、再利用可能な形でCoreへ切り出したもの。
        /// </summary>
        public static float KmhFromUnitsPerGameHour(float unitsPerGameHour)
        {
            return unitsPerGameHour * InGameHoursPerRealSecond * 3.6f;
        }
    }
}
