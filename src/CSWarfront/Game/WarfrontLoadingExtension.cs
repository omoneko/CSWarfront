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
        public override void OnLevelUnloading()
        {
            try { MilitaryManager.Reset(); }
            catch (System.Exception e) { ModConfig.LogError("OnLevelUnloading: " + e); }
        }
    }
}
