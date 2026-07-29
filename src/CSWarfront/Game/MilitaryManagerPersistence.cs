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
        /// </summary>
        public static byte[] SerializeLocked()
        {
            lock (_stateLock)
            {
                if (State == null) EnsureInitialized();
                // Task54: このMODが立てた戦闘域の道路封鎖(PathFailedビット)をセーブデータへ
                // 焼き込まないよう、シリアライズの前後で一時的に外して戻す（_stateLock保持中なので
                // simスレッドとは競合しない。CombatRoadBlocker.OwnedはRAM上の集合なのでこの間も
                // 覚えたまま＝戻すのは同じセグメント集合）。
                CombatRoadBlocker.UnblockAllForSave();
                try
                {
                    return CSWarfront.Core.WarStateSerializer.Serialize(State);
                }
                finally
                {
                    CombatRoadBlocker.ReblockAfterSave();
                }
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
