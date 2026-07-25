using System;
using ICities;
namespace CSWarfront.Game
{
    /// <summary>
    /// MOD情報とMod Optionsを提供するエントリポイント。ICities.IUserMod には多くのライフサイクルフックが無いため、
    /// 実際の初期化（MilitaryManager.EnsureInitialized）は <see cref="WarfrontThreadingExtension"/>.OnAfterSimulationTick
    /// （ゲームが assembly をスキャンして自動登録する ThreadingExtensionBase、simスレッド）で行う。
    /// </summary>
    public class Mod : IUserMod
    {
        public string Name => "CS Warfront";
        public string Description =>
            "5勢力のTier制軍事シミュレーション（陸海空・基地・勢力圏・占領）。電力タブから軍事基地を建設可能。";

        /// <summary>Mod Options 画面（建設先勢力の選択）。ゲームが自動検出して呼ぶ。</summary>
        public void OnSettingsUI(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup("Base placement");
                group.AddDropdown(
                    "Faction to build for (electricity tab military base)",
                    WarfrontSettings.FactionNames,
                    WarfrontSettings.BuildFactionId,
                    i => WarfrontSettings.SetBuildFactionId(i));
            }
            catch (Exception e)
            {
                ModConfig.LogError("OnSettingsUI error: " + e);
            }
        }
    }
}
