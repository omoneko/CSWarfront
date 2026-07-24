using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// MOD情報のみを提供するエントリポイント。ICities.IUserMod にはライフサイクルフックが無いため、
    /// 実際の初期化（MilitaryManager.EnsureInitialized）は <see cref="WarfrontThreadingExtension"/>.OnUpdate
    /// （ゲームが assembly をスキャンして自動登録する ThreadingExtensionBase）で行う。
    /// </summary>
    public class Mod : IUserMod
    {
        public string Name => "CS Warfront";
        public string Description => "5勢力のTier制軍事シミュレーション（陸海空・基地・勢力圏・占領）。";
    }
}
