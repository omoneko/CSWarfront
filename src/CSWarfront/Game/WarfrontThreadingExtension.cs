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

            // SpeedCalibration.InGameHoursPerRealSecond の実機較正診断（Task26）。
            try { SpeedCalibrationDiagnostics.AccumulateRealSeconds(realTimeDelta); }
            catch (System.Exception e) { ModConfig.LogError("SpeedCalibrationDiagnostics: " + e); }

            try
            {
                BaseInfoPanel.EnsureCreated();
                BaseInfoPanel.UpdateVisibility();
            }
            catch (System.Exception e) { ModConfig.LogError("BaseInfoPanel update: " + e); }

            // Task36: モデル設定パネルは常設ではなくトグル開閉式のため、ここではEnsureCreated（冪等）のみ
            // 呼んで下地を用意する。表示/非表示自体はBaseInfoPanelの「モデル設定」ボタンが駆動する。
            // Task47: UpdateGameMenuStateはEscメニューが開いている間だけ表示中のパネルを一時的に隠し、
            // 閉じたら戻す（トグル状態そのものは変更しない）。
            try
            {
                AssetAssignPanel.EnsureCreated();
                AssetAssignPanel.UpdateGameMenuState();
            }
            catch (System.Exception e) { ModConfig.LogError("AssetAssignPanel update: " + e); }

            // ユニットのクリック選択とステータスパネル（Task31）。位置同期（OnMainVisualUpdate、上）の
            // 後に行うことで、raycastが今フレームの最新位置に配置済みの当たり判定と一致する。
            try
            {
                UnitSelection.Update();
                UnitInfoPanel.EnsureCreated();
                UnitInfoPanel.UpdateVisibility();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitInfoPanel update: " + e); }

            // 範囲選択（Task48）。UnitSelection.Update（単発クリック、上）の直後に呼ぶことで、
            // 「単発クリックがそのままドラッグへ発展した場合」を同一フレーム内で正しく検知できる。
            try
            {
                UnitBoxSelection.EnsureCreated();
                UnitBoxSelection.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitBoxSelection update: " + e); }

            // 部隊コマンドのホットキー入力（Task48）。UnitBoxSelection.Update（上）の後に呼ぶことで、
            // 同じフレームで確定した選択（SelectedIds）を対象にコマンドを出せるようにする。
            try
            {
                UnitCommandInput.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("UnitCommandInput update: " + e); }

            // Task63: 弾道ミサイルの発射地点指定（右クリックターゲティング）。UnitCommandInputと同じ
            // 「選択/コマンド系ホットキー入力」フェーズにまとめる。
            try
            {
                MissileLaunchTargeting.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("MissileLaunchTargeting update: " + e); }

            // Task102: 軍事建設パネル（軍事建物9種のワンクリック配置。ホットキー/常駐ボタンで開閉）。
            try
            {
                MilitaryBuildPanel.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("MilitaryBuildPanel update: " + e); }

            // Task106: 塹壕ライン敷設のターゲティング（2点右クリック）。
            try
            {
                TrenchLineTargeting.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("TrenchLineTargeting update: " + e); }

            // Task62: コマンド発行時の画面中央トースト。UnitCommandInputがShow()を呼ぶ側なので、
            // ここでは冪等な生成とフェード/非表示の時間経過管理だけを毎フレーム行う。
            try
            {
                CommandToast.EnsureCreated();
                CommandToast.Update();
            }
            catch (System.Exception e) { ModConfig.LogError("CommandToast update: " + e); }
        }
    }
}
