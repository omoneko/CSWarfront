using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using ICities;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task49: Mod Options（Game/Mod.cs、OnSettingsUI）内に直接構築する「勢力の関係」サブページ。
    /// 5勢力の全ての異なるペア（0-1, 0-2, 0-3, 0-4, 1-2, 1-3, 1-4, 2-3, 2-4, 3-4 の10組）について、
    /// 敵対/中立/同盟のドロップダウンを1行ずつ並べる。値は MilitaryManager.TryGetRelation/TrySetRelation
    /// 経由で Core.WarState.Relations を直接読み書きする（Relations は既存のシリアライザ（format v4）で
    /// 25ペア全て永続化済みのため、ここでの変更はセーブと一緒にそのまま永続化される。データ形式の変更は無い）。
    ///
    /// このページはメインメニュー（MilitaryManager.State が null、まだ都市が読み込まれていない）からも
    /// 開かれ得る。その場合 TryGetRelation/TrySetRelation は false を返す（読み書きできる WarState が
    /// 存在しない）ため、ここでは例外を投げず、ドロップダウンを「敵対」表示のまま isEnabled=false で
    /// 無効化し、その旨を説明する注記ラベルを表示するに留める。
    ///
    /// OptionsModelAssignPage と同じ規約: 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    ///
    /// Task52バグ修正: 「勢力の関係がOptionsから変更できない」不具合の根本原因は、CSの
    /// OptionsMainPanel（Assembly-CSharp.dll、ILSpyで逆コンパイルして確認済み）が各MODの
    /// OnSettingsUI を「Options画面を開くたび」ではなく、OptionsMainPanel.Awake()（＝Options画面の
    /// プレハブが最初にインスタンス化された、通常は都市読み込み前のメインメニューの時点）で
    /// ただ一度だけ呼び、以後はロケール変更(OnLocaleChanged)かMOD有効/無効の変更
    /// (RefreshPlugins、eventPluginsChanged/eventPluginsStateChanged)の時だけ再構築する、という
    /// 実装だったこと。つまりBuild()（＝このOnSettingsUI呼び出し）はMilitaryManager.Stateがまだnull
    /// （都市未読み込み）の状態で一度だけ実行され、10個のドロップダウンはそのタイミングの
    /// stateReady(false)を元にisEnabled=falseへ固定される。その後、都市を読み込んでStateが
    /// 用意されても、Options画面を開き直すだけではBuild()は二度と呼ばれない（前述の通り
    /// Awake/ロケール変更/MOD有効化変更でしか再実行されない）ため、ドロップダウンは
    /// 無効化されたまま＝ユーザーからは「敵対から変更できない」ように見え続ける。
    /// このコメント自体も含め、旧実装は「Build()はOptions画面を開くたびに再実行されうる」という
    /// 誤った前提で書かれていた（OptionsModelAssignPage.csにも同じ誤った前提のコメントがあるが、
    /// そちらは本タスクのスコープ外）。
    ///
    /// 修正: Unity/CSのUIComponentは、祖先のisVisibleが変化すると子孫までOnVisibilityChanged()を
    /// 再帰的に伝播し、各階層でeventVisibilityChangedを発火する（UIComponent、ColossalManaged.dllを
    /// 逆コンパイルして確認済み）。Options画面でタブを切り替える際、UITabContainer.SelectPageByIndex
    /// は選択された1個の子（＝このMODの全グループを内包する単一のUIComponent）のisVisibleを
    /// true/falseへ切り替えるだけだが、それが配下の「勢力の関係」グループのパネルまで伝播するため、
    /// グループパネル自身のeventVisibilityChangedを購読すれば「このMODのOptionsタブが選択される
    /// たび」に確実にフックできる（Build()自体が再実行されるかどうかに依存しない）。
    /// RefreshFromState() がこのイベントで毎回、(1) 現在のStateから10個のドロップダウンの選択値を
    /// 読み直し、(2) isEnabledをstateReadyへ同期し、(3) 注記ラベルを更新する。これにより
    /// 「都市未読み込みで一度だけ構築された古いUI」が残っていても、次に都市を読み込んでOptionsの
    /// このタブを開いた瞬間に正しい状態へ更新される。
    /// </summary>
    internal static class OptionsRelationsPage
    {
        private const string GroupTitle = "Faction Relations";
        // Task59: 宿敵(Nemesis)を末尾に追加。Relation enumの宣言順（Hostile, Neutral, Allied, Nemesis）と一致させる。
        private static readonly string[] RelationLabels = { "Hostile", "Neutral", "Allied", "Nemesis" };
        private static readonly Relation[] RelationValues = { Relation.Hostile, Relation.Neutral, Relation.Allied, Relation.Nemesis };

        // Build() 実行中に生成した10行分のドロップダウンと、対応する (a,b) ペア。
        // 「全て敵対に戻す」ボタン(OnResetAllClick)が押された際に選択値を再同期するために保持する。
        private static readonly List<UIDropDown> _dropdowns = new List<UIDropDown>();
        private static readonly List<byte> _pairA = new List<byte>();
        private static readonly List<byte> _pairB = new List<byte>();

        // Task59: KAIJU/Alienとの関係ドロップダウン。ゴジラ災害/エイリアン侵略MODが実際に導入されている
        // 場合のみ、それぞれ最大5行（勢力ごと）構築する（ExternalThreatBridge.IsGodzillaModPresent /
        // IsAlienModPresentで判定、Build()時点で1回だけ確認すれば十分＝MOD導入状態はゲーム再起動なしに
        // 変わらないため、勢力関係(State)のような「都市未読み込みでは無効」という時間的な変化は無い）。
        private static readonly List<UIDropDown> _threatDropdowns = new List<UIDropDown>();
        private static readonly List<byte> _threatFactionId = new List<byte>();
        private static readonly List<ThreatKind> _threatKind = new List<ThreatKind>();

        private static UILabel _noteLabel;

        /// <summary>Mod.OnSettingsUIから呼ぶ。渡された helper 配下に「勢力の関係」グループを構築する。</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                _dropdowns.Clear();
                _pairA.Clear();
                _pairB.Clear();
                _threatDropdowns.Clear();
                _threatFactionId.Clear();
                _threatKind.Clear();

                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;

                string[] names = WarfrontSettings.FactionNames;
                bool stateReady = MilitaryManager.State != null;

                for (byte a = 0; a < WarfrontSettings.MaxFactions; a++)
                {
                    for (byte b = (byte)(a + 1); b < WarfrontSettings.MaxFactions; b++)
                    {
                        byte pairA = a; // ループ変数のクロージャ捕獲対策（forは1変数を使い回すため、必ずローカルへコピーする）
                        byte pairB = b;

                        Relation current;
                        if (!MilitaryManager.TryGetRelation(pairA, pairB, out current)) current = Relation.Hostile;

                        string label = names[pairA] + " ↔ " + names[pairB]; // "Red ↔ Blue"
                        UIDropDown dd = group.AddDropdown(label, RelationLabels, IndexOfRelation(current),
                            i => OnRelationChanged(pairA, pairB, i)) as UIDropDown;

                        if (dd != null)
                        {
                            dd.isEnabled = stateReady;
                            _dropdowns.Add(dd);
                            _pairA.Add(pairA);
                            _pairB.Add(pairB);
                        }
                    }
                }

                // Task59: KAIJU/Alienとの関係。導入されているMODのぶんだけ（0/1/2個）行を追加する。
                bool godzillaPresent = ExternalThreatBridge.IsGodzillaModPresent;
                bool alienPresent = ExternalThreatBridge.IsAlienModPresent;

                if (godzillaPresent) BuildThreatRows(group, names, ThreatKind.Kaiju, "KAIJU", stateReady);
                if (alienPresent) BuildThreatRows(group, names, ThreatKind.Alien, "Alien", stateReady);

                object resetButtonObj = group.AddButton("Reset All to Hostile", OnResetAllClick);

                if (groupPanel != null)
                {
                    _noteLabel = groupPanel.AddUIComponent<UILabel>();
                    _noteLabel.textScale = 0.8f;
                    _noteLabel.textColor = new Color32(255, 190, 120, 255);
                    _noteLabel.wordWrap = true;
                    _noteLabel.autoHeight = true;
                    _noteLabel.width = 500f;
                    _noteLabel.text = stateReady
                        ? ""
                        : "No city is loaded, so faction relations cannot be edited. Please open this again after loading a city.";

                    // Task52バグ修正: CSはこのMOD全体のOnSettingsUIをOptions画面を開くたびには
                    // 再実行しない（クラス冒頭のコメント参照）。代わりに、Options内でこのMODのタブが
                    // 選択される（＝祖先コンポーネントのisVisibleがtrueへ変わり、それがこの
                    // グループパネルまで伝播する）たびに発火するeventVisibilityChangedを購読し、
                    // その時点のMilitaryManager.Stateを元にドロップダウンの選択値・有効/無効・
                    // 注記ラベルを再同期する。これにより「都市未読み込み時に一度だけ無効化された
                    // まま固定される」不具合を、Build()自体の再実行に頼らずに解消する。
                    groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.Build error: " + e);
            }
        }

        /// <summary>Task59: 指定したThreatKindについて、勢力の数(WarfrontSettings.MaxFactions)ぶんの
        /// 「勢力名 ↔ 表示名」行を1本ずつ構築する。呼び出し元(Build)がMODの導入を確認済みの場合のみ呼ぶ。</summary>
        private static void BuildThreatRows(UIHelperBase group, string[] names, ThreatKind kind, string displayName, bool stateReady)
        {
            for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
            {
                byte factionId = f; // クロージャ捕獲対策
                ThreatKind capturedKind = kind;

                Relation current;
                if (!MilitaryManager.TryGetThreatRelation(factionId, capturedKind, out current)) current = Relation.Hostile;

                string label = names[factionId] + " ↔ " + displayName;
                UIDropDown dd = group.AddDropdown(label, RelationLabels, IndexOfRelation(current),
                    i => OnThreatRelationChanged(factionId, capturedKind, i)) as UIDropDown;

                if (dd != null)
                {
                    dd.isEnabled = stateReady;
                    _threatDropdowns.Add(dd);
                    _threatFactionId.Add(factionId);
                    _threatKind.Add(capturedKind);
                }
            }
        }

        private static int IndexOfRelation(Relation r)
        {
            for (int i = 0; i < RelationValues.Length; i++)
                if (RelationValues[i] == r) return i;
            return 0;
        }

        private static void OnRelationChanged(byte a, byte b, int selectedIndex)
        {
            try
            {
                if (selectedIndex < 0 || selectedIndex >= RelationValues.Length) return;
                MilitaryManager.TrySetRelation(a, b, RelationValues[selectedIndex]);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnRelationChanged error: " + e);
            }
        }

        private static void OnThreatRelationChanged(byte factionId, ThreatKind kind, int selectedIndex)
        {
            try
            {
                if (selectedIndex < 0 || selectedIndex >= RelationValues.Length) return;
                MilitaryManager.TrySetThreatRelation(factionId, kind, RelationValues[selectedIndex]);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnThreatRelationChanged error: " + e);
            }
        }

        /// <summary>グループパネルのeventVisibilityChangedハンドラ（Task52バグ修正）。
        /// isVisible==trueの時だけ（＝Options内でこのMODのタブが選択された/表示された時だけ）
        /// RefreshFromStateを呼ぶ。非表示化(false)の際は何もしない。</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>現在のMilitaryManager.Stateを元に、10行のドロップダウンの選択値・isEnabled、
        /// および注記ラベルを再同期する（Task52バグ修正）。都市を読み込んだ後にOptionsのこのタブを
        /// 開き直した時、あるいは「全て敵対に戻す」ボタンを押した後の再同期の両方で使う共通処理。
        /// 例外はここで握りつぶし、UIコールバックからゲームループへ例外を伝播させない。</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = MilitaryManager.State != null;

                for (int i = 0; i < _dropdowns.Count; i++)
                {
                    UIDropDown dd = _dropdowns[i];
                    if (dd == null) continue;

                    dd.isEnabled = stateReady;

                    Relation current;
                    if (!MilitaryManager.TryGetRelation(_pairA[i], _pairB[i], out current)) current = Relation.Hostile;
                    int idx = IndexOfRelation(current);
                    // 値が変わっていない時にselectedIndexへ書き戻すとeventSelectedIndexChanged経由で
                    // OnRelationChangedが不要に再発火する（ログが増えるだけで実害は無いが避ける）。
                    if (dd.selectedIndex != idx) dd.selectedIndex = idx;
                }

                // Task59: KAIJU/Alien行も同じ規約で再同期する（構築済みの行のみ＝MOD導入判定はBuild()時点で
                // 固定されているため、ここではドロップダウンの個数自体は増減しない）。
                for (int i = 0; i < _threatDropdowns.Count; i++)
                {
                    UIDropDown dd = _threatDropdowns[i];
                    if (dd == null) continue;

                    dd.isEnabled = stateReady;

                    Relation current;
                    if (!MilitaryManager.TryGetThreatRelation(_threatFactionId[i], _threatKind[i], out current)) current = Relation.Hostile;
                    int idx = IndexOfRelation(current);
                    if (dd.selectedIndex != idx) dd.selectedIndex = idx;
                }

                if (_noteLabel != null)
                {
                    _noteLabel.text = stateReady
                        ? ""
                        : "No city is loaded, so faction relations cannot be edited. Please open this again after loading a city.";
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.RefreshFromState error: " + e);
            }
        }

        /// <summary>「全て敵対に戻す」ボタン。MilitaryManager.TryResetRelationsToAllHostile
        /// （Core.RelationPresets.ApplyAllHostileへの薄いラッパー）を呼んでから、RefreshFromStateで
        /// 10行のドロップダウンの選択表示を（すべて敵対になったはずの）現在値へ再同期する。
        /// State未初期化なら何もしない（ラッパーがfalseを返すのみ）。</summary>
        private static void OnResetAllClick()
        {
            try
            {
                if (!MilitaryManager.TryResetRelationsToAllHostile()) return;
                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnResetAllClick error: " + e);
            }
        }
    }
}
