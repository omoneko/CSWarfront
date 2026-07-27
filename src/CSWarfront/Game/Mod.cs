using System;
using ColossalFramework.UI;
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

        // Task40: 「モデル割り当てを開く」ボタンの下に出す状態メッセージ。ICities.UIHelperBase には
        // AddLabel相当のメソッドが無い（ICities.dllをリフレクションで確認: AddGroup/AddButton/AddSpace/
        // AddCheckbox/AddSlider/AddDropdown/AddTextfieldのみ）ため、AddButtonが返すUIComponent（実体は
        // UIButton）の親パネルへ直接UILabelを追加する（同パネルはautoLayoutで縦に積むため、追加順で
        // ボタンの下に配置される）。
        private static UILabel _modelAssignHintLabel;
        private static bool _loggedOptionsPanelUnavailable;

        /// <summary>Mod Options 画面（建設先勢力の選択、およびTask40のモデル割り当て起動ボタン）。
        /// ゲームが自動検出して呼ぶ。メインメニュー（マップ未ロード）からも呼ばれる想定のため、
        /// ここから直接 AssetAssignPanel を開く経路は「UIViewが無い」「PropInfoがまだ1つも
        /// ロードされていない」の両方を想定してガードする（詳細はOnOpenAssetAssignPanelClicked参照）。</summary>
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

                UIHelperBase modelGroup = helper.AddGroup("Model assignment (per-faction)");
                object buttonObj = modelGroup.AddButton("モデル割り当てを開く", OnOpenAssetAssignPanelClicked);
                _modelAssignHintLabel = CreateHintLabel(buttonObj);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OnSettingsUI error: " + e);
            }
        }

        /// <summary>ボタンの親パネル（UIHelperの内部実装が自動レイアウトする1グループ分のUIPanel）へ、
        /// 状態表示用のUILabelを追加する。取得に失敗しても致命的ではない（ログのみ表示できなくなるだけ）。</summary>
        private static UILabel CreateHintLabel(object buttonObj)
        {
            try
            {
                UIComponent button = buttonObj as UIComponent;
                if (button == null || button.parent == null) return null;

                UILabel label = button.parent.AddUIComponent<UILabel>();
                label.textScale = 0.8f;
                label.textColor = new UnityEngine.Color32(255, 190, 120, 255);
                label.wordWrap = true;
                label.autoHeight = true;
                label.width = 500f;
                label.text = "";
                return label;
            }
            catch (Exception e)
            {
                ModConfig.LogError("Mod.CreateHintLabel error: " + e);
                return null;
            }
        }

        /// <summary>
        /// Task40: 「モデル割り当てを開く」ボタンのクリックハンドラ。Task41でプロップ以外（建物/車両/樹木）
        /// にも対応した。想定される3つの文脈:
        ///   (1) ゲーム内（マップ読み込み後）: 通常通りパネルが開き、サブスクライブ済みアセットが
        ///       一覧に表示される（WarfrontThreadingExtension.OnUpdateが毎フレームEnsureCreated()して
        ///       いるため、ここでのEnsureCreated()は何もしない冪等呼び出しになる）。
        ///   (2) メインメニュー（マップ未ロード）でUIView自体は存在する場合: AssetAssignPanel.Build()は
        ///       UIView.GetAView()にだけ依存するため成功し、パネル自体は開く。ただしPrefabCollection
        ///       &lt;PropInfo/BuildingInfo/VehicleInfo/TreeInfo&gt; はレベルロード時にしか populate
        ///       されないため、一覧は0件になる（AssetCatalog.GetNamesは走査結果が0件のリストを返すだけで
        ///       例外にはならない）。この場合はパネルを開いた上で、0件である旨をヒントラベルに表示する。
        ///   (3) UIView自体が取得できない極端なケース（理論上、通常は起きない）: EnsureCreated()後も
        ///       AssetAssignPanel.IsCreated が false のままなので、パネルを開かずヒントラベルと
        ///       ログ（1回だけ）で「ゲーム内で開いてください」と案内する。
        /// いずれの分岐でも例外を外へ投げない。
        /// </summary>
        private static void OnOpenAssetAssignPanelClicked()
        {
            try
            {
                AssetAssignPanel.EnsureCreated();

                if (!AssetAssignPanel.IsCreated)
                {
                    if (!_loggedOptionsPanelUnavailable)
                    {
                        _loggedOptionsPanelUnavailable = true;
                        ModConfig.LogError("Mod.OnOpenAssetAssignPanelClicked: AssetAssignPanel を生成できませんでした（UIView未準備）。");
                    }
                    SetHint("パネルを開けませんでした。ゲーム内（マップ読み込み後）でもう一度お試しください。");
                    return;
                }

                AssetAssignPanel.Show();

                SetHint(AssetAssignPanel.HasAnyProps()
                    ? ""
                    : "現在利用可能なアセット（プロップ/建物/車両/樹木）が0件です（メインメニューから開いた場合など）。マップを読み込んだ後にもう一度開くと、サブスクライブ済みのアセットが一覧に表示されます。");
            }
            catch (Exception e)
            {
                ModConfig.LogError("Mod.OnOpenAssetAssignPanelClicked error: " + e);
            }
        }

        /// <summary>Options画面が再度開かれOnSettingsUIが再実行された場合、古いラベル参照が既に
        /// 破棄済みのUnityオブジェクトを指している可能性があるため、念のためtry/catchで握る
        /// （失敗してもヒント表示だけが出ないだけで、機能自体には影響しない）。</summary>
        private static void SetHint(string text)
        {
            try
            {
                if (_modelAssignHintLabel != null) _modelAssignHintLabel.text = text;
            }
            catch (Exception e)
            {
                ModConfig.LogError("Mod.SetHint error: " + e);
            }
        }
    }
}
