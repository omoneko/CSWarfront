using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using CSWarfront.Game.Audio;
using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// レベル（ゲームセッション）のライフサイクルに合わせてMilitaryManager/BasePlacementWatcherの
    /// 静的状態を初期化する。CSはLoadingExtensionBaseのサブクラスを自動検出するため、明示的な登録は
    /// 不要（WarfrontThreadingExtensionと同様）。
    /// OnLevelUnloading（メインメニューへ戻る／別セーブへ移る際に呼ばれる）でリセットすることで、
    /// 同一プロセス内でのセーブ復元後→新規ゲーム開始という遷移でセッション状態が次のゲームへ
    /// 持ち越されるのを防ぐ（Task16レビューImportant）。重い処理はここでは行わない。
    /// </summary>
    public class WarfrontLoadingExtension : LoadingExtensionBase
    {
        /// <summary>
        /// ゲームプレイ可能なモード（NewGame/LoadGame及びシナリオ由来の対応モード）でのみ、
        /// 電力タブの軍事基地プレハブを登録し、基地建物の設置/解体イベント購読を開始する
        /// （アセット/テーマ/マップエディタ等では不要）。
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
                    BasePlacementWatcher.Subscribe();
                    LoadModAssets(); // Task36: ユニットモデル割り当て／Task51: 発砲音・撃破音の読込
                }
            }
            catch (System.Exception e) { ModConfig.LogError("OnLevelLoaded: " + e); }
        }

        public override void OnLevelUnloading()
        {
            try
            {
                // イベント購読解除を先に行ってから MilitaryManager.Reset() で pending リスト等を
                // クリアする（Reset後にイベントが飛んで pending に積まれるのを避けるための順序）。
                BasePlacementWatcher.Unsubscribe();
                MilitaryManager.Reset();
            }
            catch (System.Exception e) { ModConfig.LogError("OnLevelUnloading: " + e); }
        }

        /// <summary>
        /// Task36: UnitAssetBindings（TypeKey→サブスクライブ済みプロップ名の割り当て）を、Task51:
        /// WarfrontSounds（発砲音・撃破音のwav読込）を、それぞれMODディレクトリから読み込む/初期化する。
        /// Mod.cs（IUserMod.OnEnabled、MissileDisasterと同様のパターン）ではなくここで行うのは、
        /// 本クラスが既に WarfrontBasePrefab.EnsureRegistered() と同じタイミング（ゲームプレイ可能な
        /// LoadModeでのOnLevelLoaded）でプレハブ登録を行っており、資産読み込みも同じタイミングで
        /// 十分だからである。modPath が取得できない場合はログのみで継続し、両者ともメモリ内のみで
        /// 動作する（UnitAssetBindingsの割り当てやWarfrontSoundsの音は使えないが、ロード自体は止めない）。
        /// </summary>
        private static void LoadModAssets()
        {
            try
            {
                PluginManager.PluginInfo info =
                    Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly());
                if (info == null || string.IsNullOrEmpty(info.modPath))
                {
                    ModConfig.LogError("LoadModAssets: PluginManager から modPath を取得できませんでした（メモリ内のみで動作）");
                }
                string modPath = info != null ? info.modPath : null;
                UnitAssetBindings.Load(modPath);
                WarfrontSounds.Initialize(modPath); // Task51
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LoadModAssets error: " + e);
            }
        }
    }
}
