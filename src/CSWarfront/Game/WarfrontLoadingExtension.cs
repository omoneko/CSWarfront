using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// レベル（ゲームセッション）のライフサイクルに合わせてMilitaryManagerの静的状態を初期化する。
    /// CSはLoadingExtensionBaseのサブクラスを自動検出するため、明示的な登録は不要
    /// （WarfrontThreadingExtensionと同様）。
    /// OnLevelUnloading（メインメニューへ戻る／別セーブへ移る際に呼ばれる）でリセットすることで、
    /// 同一プロセス内でのセーブ復元後→新規ゲーム開始という遷移でLoadedFromSave等の
    /// セッション状態が次のゲームへ持ち越されるのを防ぐ（Task16レビューImportant）。
    /// 重い処理はここでは行わない。
    /// </summary>
    public class WarfrontLoadingExtension : LoadingExtensionBase
    {
        /// <summary>
        /// ゲームプレイ可能なモード（NewGame/LoadGame及びシナリオ由来の対応モード）でのみ、
        /// 電力タブの軍事基地プレハブを登録する（アセット/テーマ/マップエディタ等では不要）。
        /// LoadMode の各メンバーは ICities.dll から検証済み（research-power-tab-building.md §3）。
        /// </summary>
        public override void OnLevelLoaded(LoadMode mode)
        {
            try
            {
                if (mode == LoadMode.NewGame || mode == LoadMode.LoadGame ||
                    mode == LoadMode.NewGameFromScenario)
                {
                    WarfrontBasePrefab.EnsureRegistered();
                }
            }
            catch (System.Exception e) { ModConfig.LogError("OnLevelLoaded: " + e); }
        }

        public override void OnLevelUnloading()
        {
            try { MilitaryManager.Reset(); }
            catch (System.Exception e) { ModConfig.LogError("OnLevelUnloading: " + e); }
        }
    }
}
