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
    /// Build() は Options 画面を開くたびに再実行されうるため、毎回すべてのコントロールを新規生成する
    /// 前提で書かれている（古い参照は次の Build() 呼び出しで上書きされる）。
    /// </summary>
    internal static class OptionsRelationsPage
    {
        private const string GroupTitle = "勢力の関係";
        private static readonly string[] RelationLabels = { "敵対", "中立", "同盟" }; // Relation enum の宣言順と一致させる
        private static readonly Relation[] RelationValues = { Relation.Hostile, Relation.Neutral, Relation.Allied };

        // Build() 実行中に生成した10行分のドロップダウンと、対応する (a,b) ペア。
        // 「全て敵対に戻す」ボタン(OnResetAllClick)が押された際に選択値を再同期するために保持する。
        private static readonly List<UIDropDown> _dropdowns = new List<UIDropDown>();
        private static readonly List<byte> _pairA = new List<byte>();
        private static readonly List<byte> _pairB = new List<byte>();
        private static UILabel _noteLabel;

        /// <summary>Mod.OnSettingsUIから呼ぶ。渡された helper 配下に「勢力の関係」グループを構築する。</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                _dropdowns.Clear();
                _pairA.Clear();
                _pairB.Clear();

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

                object resetButtonObj = group.AddButton("全て敵対に戻す", OnResetAllClick);

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
                        : "都市が読み込まれていないため、勢力の関係を編集できません。都市を読み込んだ後にもう一度開いてください。";
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.Build error: " + e);
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

        /// <summary>「全て敵対に戻す」ボタン。MilitaryManager.TryResetRelationsToAllHostile
        /// （Core.RelationPresets.ApplyAllHostileへの薄いラッパー）を呼んでから、10行のドロップダウンの
        /// 選択表示を敵対へ再同期する。State未初期化なら何もしない（ラッパーがfalseを返すのみ）。</summary>
        private static void OnResetAllClick()
        {
            try
            {
                if (!MilitaryManager.TryResetRelationsToAllHostile()) return;

                for (int i = 0; i < _dropdowns.Count; i++)
                {
                    if (_dropdowns[i] != null) _dropdowns[i].selectedIndex = IndexOfRelation(Relation.Hostile);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsRelationsPage.OnResetAllClick error: " + e);
            }
        }
    }
}
