using System;
using ColossalFramework;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// セーブ/ロード（SerializeLocked/LoadAndRebuild）向けの MilitaryManager 追加メンバー。
    /// MilitaryManager.cs の500行制限のため分離した partial class（Task34のMilitaryManagerManualProduction
    /// 等と同じ方針）。_stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// セーブ用：_stateLock を保持したまま WarState をシリアライズする。
        /// OnSimTick が State.Units 等を書き換えている最中の
        /// 「Collection was modified」例外（＝セーブ静かに失敗＝データ消失）を防ぐ。
        /// 呼び出し側（OnSaveData）は _stateLock を保持していないこと（再入不可のため）。
        ///
        /// Task72: このMODが立てた「見た目だけの」フラグ（CombatRoadBlockerのNetSegment.PathFailed、
        /// BaseHiddenSyncのBuilding.Hidden）をセーブデータへ焼き込まないよう、シリアライズの前後で
        /// 一時的に外して戻す。
        ///
        /// 重要（ilspycmdでSimulationManager/LoadingManager/AsyncTaskを逆コンパイルして確認した
        /// 実際の保存順序、詳細はtask-72-report.md）: このメソッド（延いてはWarStateDataExtension.
        /// OnSaveData）は、SimulationManager.Data.Serializeが「①全MODのOnSaveData()を呼ぶ →
        /// ②その後でBuildingManager.Data/NetManager.Data等バニラの各マネージャのSerialize
        /// （実際にBuilding.m_flags/NetSegment.m_flagsをストリームへ書く箇所）を呼ぶ」という順序の
        /// ①の中で呼ばれる。しかも①②は同一のAsyncTask.Execute()（LoadingManager.SaveSimulationDataの
        /// コルーチンをwhile(m_Action.MoveNext())で最後まで同期的に回し切る、yieldは末尾の1箇所のみ）
        /// の中で連続して起きる。つまりこのメソッド内でクリア→即座にfinallyで戻す旧実装は、②が
        /// Building.m_flags/NetSegment.m_flagsを読み取るより前に戻してしまうため無意味だった
        /// （セーブファイルには結局Hidden/PathFailedが焼き込まれ続けていた＝要件で疑われた「漏れ」は
        /// CombatRoadBlocker側にも実在していた）。
        ///
        /// 修正: 戻す処理はここで同期的に行わず、Singleton&lt;SimulationManager&gt;.instance.AddAction
        /// で「今のSaving AsyncTaskが完全に完了した後の次のアクション」として積む。
        /// SimulationManager.SimulationStep先頭の`while(m_hasActions){...}`ループは、Dequeueして
        /// 実行中のActionの中で新たにAddActionが呼ばれるとm_hasActionsが再びtrueに戻るため、
        /// ループを継続して同フレーム内・かつ通常のOnSimTickより前に続けてそのActionも実行する。
        /// これにより「バニラがBuilding/NetSegmentのフラグをストリームへ読み取り終えた直後」という
        /// タイミングを外部からのIL改変無しで確実に取れる（詳細はCombatRoadBlocker.ReblockAfterSave/
        /// BaseHiddenSync.ReapplyAfterSaveのコメントも参照）。
        /// </summary>
        public static byte[] SerializeLocked()
        {
            lock (_stateLock)
            {
                if (State == null) EnsureInitialized();

                CombatRoadBlocker.UnblockAllForSave();
                BaseHiddenSync.UnhideAllForSave();
                try
                {
                    return CSWarfront.Core.WarStateSerializer.Serialize(State);
                }
                finally
                {
                    ScheduleReapplySaveFlags();
                }
            }
        }

        /// <summary>
        /// SerializeLockedのfinallyから呼ぶ。バニラのBuilding/NetSegmentフラグ書き込み
        /// （SimulationManager.Data.Serialize内、このメソッドの戻り先よりさらに後）が終わった直後に
        /// 実行されるよう、simスレッドの次のアクションとして予約する（Task72、コメントはSerializeLocked
        /// 参照）。SimulationManagerが万一存在しない状況（理論上は起こらないはずだが、セーブ処理自体が
        /// SimulationManagerの存在を前提にしている以上、無いことの方が異常）に備え、フォールバックとして
        /// その場で同期的に戻す（タイミングは正しくない可能性があるが、フラグを永久に外れたままにする
        /// よりは安全側）。
        /// </summary>
        private static void ScheduleReapplySaveFlags()
        {
            if (Singleton<SimulationManager>.exists)
            {
                Singleton<SimulationManager>.instance.AddAction("CSWarfront.ReapplySaveFlags", delegate
                {
                    try { CombatRoadBlocker.ReblockAfterSave(); }
                    catch (Exception e) { ModConfig.LogError("CSWarfront.ReapplySaveFlags (road): " + e); }
                    try { BaseHiddenSync.ReapplyAfterSave(); }
                    catch (Exception e) { ModConfig.LogError("CSWarfront.ReapplySaveFlags (base hidden): " + e); }
                });
            }
            else
            {
                CombatRoadBlocker.ReblockAfterSave();
                BaseHiddenSync.ReapplyAfterSave();
            }
        }

        /// <summary>
        /// セーブデータからの復元専用エントリ（save/loadスレッドから呼ばれる）。State差し替えのみを
        /// 行う。生存ユニットの見た目（GameObject）は次回以降の OnMainVisualUpdate が
        /// State.Unitsをスナップショットして UnitVisuals.Sync に渡すことで自動的に再生成される
        /// （宣言的reconcileのため、respawn用の特別なフラグ・処理は不要＝Task19で削除）。
        /// </summary>
        public static void LoadAndRebuild(WarState restored)
        {
            lock (_stateLock)
            {
                State = restored;
                // セーブから復元＝基地建物はCSが既に復元済み（BaseIdはstable buildingId）。
                // BasePlacementWatcher.ProcessPending は復元済みBaseIdをIdempotencyチェックで
                // スキップするため、EventBuildingCreated の再発火があっても二重登録はしない。
            }
        }
    }
}
