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
        public string Name => "CS:WARFRONT"; // Task93: ユーザー指定のMODタイトル（Workshopタイトルと統一）
        public string Description =>
            "A tier-based military simulation with 5 factions (land/sea/air, bases, territory, occupation). Building the building designated in Options turns it into a military base.";

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
                    "Faction to build for (designated base building)",
                    WarfrontSettings.FactionNames,
                    WarfrontSettings.BuildFactionId,
                    i => WarfrontSettings.SetBuildFactionId(i));

                AddUnitCommandHotkeyUI(helper);

                AddInvasionEventsUI(helper); // Task94: 外部襲来イベント（Workshopコメント要望）

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

        /// <summary>Task94（Workshopコメント要望）: 外部襲来イベントのON/OFFと頻度。
        /// ONにすると、ランダムなタイミングでマップ端に襲撃部隊（未使用勢力所属・防衛側と自動敵対）が
        /// スポーンし、最寄りの基地へ攻めてくる。自分の基地を建てて防衛するプレイ向け。</summary>
        private static void AddInvasionEventsUI(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Invasion events (waves attack from outside the city)");
            group.AddCheckbox("Enable invasion events", WarfrontSettings.InvasionEventsEnabled,
                v => WarfrontSettings.InvasionEventsEnabled = v);
            group.AddDropdown("Invasion frequency",
                new[] { "Low (about every 5 days)", "Medium (about every 2-3 days)", "High (about every day)" },
                WarfrontSettings.InvasionFrequencyIndex,
                i => { if (i >= 0 && i <= 2) WarfrontSettings.InvasionFrequencyIndex = i; });
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

            // Task102: 軍事建設パネル（軍事建物9種のワンクリック配置）の開閉キー。
            group.AddDropdown("Toggle military construction panel",
                keyNames, IndexOf(WarfrontSettings.BuildPanelKey),
                i => { if (i >= 0 && i < WarfrontSettings.KeyOptions.Length) WarfrontSettings.BuildPanelKey = WarfrontSettings.KeyOptions[i]; });

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
