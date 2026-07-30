using System;
using CSWarfront.Game.UI;
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

        /// <summary>Mod Options 画面（建設先勢力の選択、およびTask47でOptionsサブページ化した
        /// モデル割り当てUI）。ゲームが自動検出して呼ぶ。モデル割り当てUI自体の構築は
        /// OptionsModelAssignPage.Build（Game/UI/OptionsModelAssignPage.cs）に委譲する
        /// （Task40時点はフローティングパネルを開くボタンだったが、Task47でOptionsグループ内に
        /// 直接コントロール一式を並べる「サブページ」形式へ変更した）。</summary>
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

                AddUnitCommandHotkeyUI(helper);

                OptionsRelationsPage.Build(helper);

                AddFactionIconUI(helper);

                AddSoundUI(helper);

                OptionsBaseBuildingPage.Build(helper); // Task74: 指定した建物を建てるとその種別の基地として機能する方式

                OptionsModelAssignPage.Build(helper);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OnSettingsUI error: " + e);
            }
        }

        /// <summary>Task48: 範囲選択した部隊への指揮コマンド（自由進撃/停止/集結待機）のホットキー割り当てUI。
        /// MissileDisaster.ModSettingsのキーバインドドロップダウン（KeyOptions配列のインデックスで選択値を
        /// 管理する）と同じパターン。WarfrontSettingsはメモリ内保持のみ（クラス冒頭のコメント参照）なので、
        /// ここではGameSettings/SavedIntを一切経由せず、選択されたKeyCodeを直接プロパティへ代入するだけでよい。</summary>
        private static void AddUnitCommandHotkeyUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Unit commands (select units with a box drag, then press)");

            string[] keyNames = new string[WarfrontSettings.KeyOptions.Length];
            for (int i = 0; i < WarfrontSettings.KeyOptions.Length; i++)
                keyNames[i] = WarfrontSettings.KeyOptions[i].ToString();

            // Task76: 部隊選択モードのON/OFFトグルキー。ONの間だけドラッグでの範囲選択が働く
            // （単発クリック選択は常時有効、Game/UI/UnitBoxSelection参照）。
            group.AddDropdown("Toggle unit selection mode (drag-box select; single click always works)",
                keyNames, IndexOf(WarfrontSettings.SelectionModeKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.SelectionModeKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Free advance (march at full speed toward the nearest hostile base)",
                keyNames, IndexOf(WarfrontSettings.FreeAdvanceKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.FreeAdvanceKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Hold (stop in place, still fires at anything in range)",
                keyNames, IndexOf(WarfrontSettings.HoldKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.HoldKey = WarfrontSettings.KeyOptions[i]; });

            group.AddDropdown("Rally (then right-click a destination; units move there, stop, and fight defensively only)",
                keyNames, IndexOf(WarfrontSettings.RallyKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.RallyKey = WarfrontSettings.KeyOptions[i]; });
        }

        private static int IndexOf(UnityEngine.KeyCode key)
        {
            for (int i = 0; i < WarfrontSettings.KeyOptions.Length; i++)
                if (WarfrontSettings.KeyOptions[i] == key) return i;
            return 0;
        }

        /// <summary>Task49: ユニット上の勢力アイコン（小さな球、Game/UnitVisuals参照）の表示切り替え。
        /// WarfrontSettingsと同じくメモリ内保持のみ（セッションをまたいだ既定値=ONへのリセットは許容、MVP）。
        /// OFFにした場合、既に生成済みの球は次回 UnitVisuals.Sync() 時に破棄される
        /// （UnitVisuals.Sync内でWarfrontSettings.ShowFactionIconsを見て個別に破棄する）。</summary>
        private static void AddFactionIconUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Faction icons");
            group.AddCheckbox(
                "Show a small faction-colored marker above each unit",
                WarfrontSettings.ShowFactionIcons,
                v => WarfrontSettings.ShowFactionIcons = v);
        }

        /// <summary>Task51: 兵科別射撃音・撃破音の音量スライダーとミュートトグル。WarfrontSettingsと
        /// 同じくメモリ内保持のみ（セッションをまたいだ既定値=50%/ミュートOFFへのリセットは許容、MVP）。</summary>
        private static void AddSoundUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Firing sounds");
            group.AddSlider(
                "Sound volume",
                0f, 100f, 1f, WarfrontSettings.SoundVolume,
                v => WarfrontSettings.SoundVolume = (int)v);
            group.AddCheckbox(
                "Mute all firing/kill sounds",
                WarfrontSettings.SoundMuted,
                v => WarfrontSettings.SoundMuted = v);
        }
    }
}
