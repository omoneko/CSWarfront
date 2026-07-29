using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task70: UnitAssetBindings のうち、「全て初期化」（<see cref="ClearAll"/>）と「セット登録（1-3）」
    /// （<see cref="SaveToSlot"/>/<see cref="LoadFromSlot"/>/<see cref="SlotExists"/>）だけを分離した
    /// partial class（UnitAssetBindings.cs 側の500行制限のため。UnitAssetBindingsBaseTypesと同じ方針）。
    /// フィールド（_bindings/_anyFactionBindings/_filePath/_modDirectory）とファイルIOヘルパー
    /// （ParseFileInto/WriteBindingsToFile/Save）は全て UnitAssetBindings.cs 側で宣言されているが、
    /// partial class は private メンバーも全パーツで共有するためそのまま使える。
    ///
    /// セットスロットのファイル: modDirectory 直下に "unit-assets-set&lt;slot&gt;.txt"（slot=1..3）として、
    /// メインファイル（unit-assets.txt）と全く同じ行フォーマットで保存する（別形式は発明しない。
    /// パース/シリアライズは ParseFileInto/WriteBindingsToFile を共有する）。
    ///
    /// 読込（LoadFromSlot）はテーブル全体を置き換える（REPLACE semantics）: 現在の割り当て（勢力別+
    /// 全勢力共通(レガシー)の両方）を全て破棄し、スロットファイルの内容だけに差し替える。マージ
    /// （既存の割り当てにスロットの内容を上書き加算していく方式）ではない。置き換え後は
    /// unit-assets.txt にも即座に反映する（Save()を呼ぶ）ため、次回レベルロード時もこの状態を維持する。
    ///
    /// スロットファイルが存在しない、または壊れている（パース時に例外）場合は false を返し、現在の
    /// メモリ内状態・unit-assets.txt はどちらも一切変更しない（先に一時辞書へパースしてから成功時のみ
    /// _bindings/_anyFactionBindingsへ反映する二段構えのため、パース失敗時に中途半端な状態にはならない）。
    ///
    /// 全メソッドは例外を外へ投げない（呼び出し元はUI＝メインスレッドのイベントハンドラのため、
    /// ここでの失敗がゲームループを止めてはならない）。
    /// </summary>
    internal static partial class UnitAssetBindings
    {
        private const int MinSlot = 1;
        private const int MaxSlot = 3;
        private const string SlotFileNamePrefix = "unit-assets-set";
        private const string SlotFileNameSuffix = ".txt";

        /// <summary>全ての割り当て（勢力別・全勢力共通(レガシー)・基地種別キーを含む全キー）を消去し、
        /// 既定モデルへ戻す（個別の「既定に戻す」＝<see cref="Clear"/>の一括版）。直ちに保存する。</summary>
        /// <returns>実際に削除した件数（勢力別+全勢力共通の合計）。元々0件だった場合は保存をスキップして0を返す。</returns>
        public static int ClearAll()
        {
            try
            {
                int removed = _bindings.Count + _anyFactionBindings.Count;
                if (removed == 0) return 0;

                _bindings.Clear();
                _anyFactionBindings.Clear();
                Save();

                ModConfig.Log("UnitAssetBindings.ClearAll: 全ての割り当て(" + removed + "件)を既定モデルへ初期化しました");
                return removed;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.ClearAll error: " + e);
                return 0;
            }
        }

        /// <summary>現在の割り当てテーブル全体を、指定スロット(1..3)のファイルへ保存する（既存の
        /// unit-assets.txtとは別ファイル、"ギア設定"のように後で<see cref="LoadFromSlot"/>で戻せる）。</summary>
        /// <returns>成功したか。slotが範囲外、modDirectory未解決、またはIOエラーの場合はfalse
        /// （現在の割り当てには一切影響しない）。</returns>
        public static bool SaveToSlot(int slot)
        {
            try
            {
                if (!IsValidSlot(slot))
                {
                    ModConfig.LogError("UnitAssetBindings.SaveToSlot: 不正なslot=" + slot + "（1〜3のみ有効）");
                    return false;
                }
                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("UnitAssetBindings.SaveToSlot: modDirectory 未解決のため保存できません（今回のセッションのみ有効）");
                    return false;
                }

                string path = SlotPath(slot);
                WriteBindingsToFile(path, _bindings, _anyFactionBindings);

                ModConfig.Log("UnitAssetBindings.SaveToSlot: 現在の割り当て(勢力別" + _bindings.Count + "件 + 全勢力共通" +
                    _anyFactionBindings.Count + "件)をセット" + slot + "（'" + path + "'）へ保存しました");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.SaveToSlot(slot=" + slot + ") error: " + e);
                return false;
            }
        }

        /// <summary>指定スロット(1..3)のファイルから割り当てテーブル全体を読み込み、現在のテーブルを
        /// まるごと置き換える（REPLACE。マージではない）。置き換え後は unit-assets.txt にも反映する。</summary>
        /// <returns>成功したか。slotが範囲外、modDirectory未解決、スロットファイルが存在しない、または
        /// 壊れている（パース時に例外）場合はfalseを返し、現在の割り当ては一切変更しない。</returns>
        public static bool LoadFromSlot(int slot)
        {
            try
            {
                if (!IsValidSlot(slot))
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: 不正なslot=" + slot + "（1〜3のみ有効）");
                    return false;
                }
                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: modDirectory 未解決のため読込できません");
                    return false;
                }

                string path = SlotPath(slot);
                if (!File.Exists(path))
                {
                    ModConfig.Log("UnitAssetBindings.LoadFromSlot: セット" + slot + "（'" + path + "'）が存在しないため読込をスキップしました（現在の割り当てを維持）");
                    return false;
                }

                // 先に一時辞書へパースし、成功した場合のみ本体へ反映する（壊れたファイルで現在の状態を
                // 巻き込まないため。ParseFileIntoはFile.ReadAllLines等の例外をそのまま呼び出し元へ
                // 伝播させる想定のため、ここでcatchして安全側に倒す）。
                Dictionary<string, Binding> newBindings = new Dictionary<string, Binding>();
                Dictionary<string, Binding> newAnyFactionBindings = new Dictionary<string, Binding>();
                int parsed;
                try
                {
                    ParseFileInto(path, newBindings, newAnyFactionBindings, out parsed);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("UnitAssetBindings.LoadFromSlot: セット" + slot + "（'" + path + "'）の読込に失敗したため現在の割り当てを維持します: " + e);
                    return false;
                }

                // ここまで来て初めてテーブル全体を置き換える（REPLACE semantics）。
                _bindings.Clear();
                _anyFactionBindings.Clear();
                foreach (KeyValuePair<string, Binding> kv in newBindings) _bindings[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, Binding> kv in newAnyFactionBindings) _anyFactionBindings[kv.Key] = kv.Value;

                Save(); // unit-assets.txt にも反映（次回レベルロード時もこのセットの内容を維持するため）

                ModConfig.Log("UnitAssetBindings.LoadFromSlot: セット" + slot + "（'" + path + "'）から" + parsed +
                    "件を読み込み、既存の割り当てテーブル全体を置き換えました");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.LoadFromSlot(slot=" + slot + ") error（現在の割り当てを維持）: " + e);
                return false;
            }
        }

        /// <summary>指定スロット(1..3)のファイルが存在するか（UIのドロップダウンラベルに「（空）」を
        /// 添えるかどうかの判定に使う、Task70）。slot範囲外・modDirectory未解決の場合もfalse。</summary>
        public static bool SlotExists(int slot)
        {
            try
            {
                if (!IsValidSlot(slot)) return false;
                if (string.IsNullOrEmpty(_modDirectory)) return false;
                return File.Exists(SlotPath(slot));
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.SlotExists(slot=" + slot + ") error: " + e);
                return false;
            }
        }

        private static bool IsValidSlot(int slot)
        {
            return slot >= MinSlot && slot <= MaxSlot;
        }

        private static string SlotPath(int slot)
        {
            return Path.Combine(_modDirectory, SlotFileNamePrefix + slot + SlotFileNameSuffix);
        }
    }
}
