using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task74: 「Optionで指定した建物アセットを建てると、その建物がその種別の基地として機能する」方式の
    /// 割り当てストア。基地種別（<see cref="BaseType"/>、Army/Navy/AirForce/MissileBase）ごとに、
    /// プレイヤーがOptionsで指定した「基地として機能させたい既存の建物アセット名」を1件だけ保持する。
    ///
    /// UnitAssetBindings（勢力別・ユニット/基地の見た目モデル割り当て。ファイル: unit-assets.txt）とは
    /// 完全に独立した専用ファイル（&lt;modDir&gt;\base-buildings.txt）に保存する。理由: こちらは
    /// 「見た目の割り当て」ではなく「どのアセットを建てればそれ自体が基地になるか」という配置認識の
    /// ルールであり、勢力別に分ける概念も無い（電力タブの複製プレハブと同じく、常に
    /// WarfrontSettings.BuildFactionId所属の基地として登録される。BasePlacementWatcher.ProcessCreated
    /// 参照）。UnitAssetBindingsの複雑なTier/勢力/複製フォールバックの仕組みを持ち込む必要が無いため、
    /// あえて独立させ、シンプルな1種別=1アセット名のみのフォーマットにした。
    ///
    /// ファイル形式（1行、UTF-8）: "baseType=assetName"（baseTypeはCSWarfront.Core.BaseTypeのenum名、
    /// 例: "Army=Some Custom Building"）。4種別のうち、実際に指定されているものだけが行として存在する
    /// （未指定の種別は行自体が無く、その種別は従来どおり電力タブの複製建物でのみ配置できる＝
    /// coexistence: 本クラスの指定はあくまで追加の配置経路であり、電力タブの複製プレハブ登録・機能を
    /// 一切変更しない）。
    ///
    /// 壊れている/存在しないファイルは常に「割り当て無し」として扱い、例外を外へ投げない
    /// （ここでの失敗がロード自体を止めてはならない、UnitAssetBindingsと同じ方針）。
    /// メインスレッド専用という制約は無いが、呼び出しは全てメインスレッド（Options UI/ロード処理）から
    /// 行われる想定。BasePlacementWatcher（simスレッド）からは<see cref="TryGet"/>/<see cref="TryMatch"/>
    /// という読み取り専用メソッドのみを呼ぶ（Dictionaryへの書き込みはOptions UI操作時のみ、メインスレッド
    /// 限定で発生し、simスレッドとの同時書き込みは無い前提。UnitAssetBindingsも同じ前提で運用されている）。
    /// </summary>
    internal static class BaseBuildingDesignation
    {
        private const string FileName = "base-buildings.txt";

        private static readonly Dictionary<BaseType, string> _designations = new Dictionary<BaseType, string>();
        private static string _filePath;

        /// <summary>いずれかの基地種別に指定が1件でもあるか。BasePlacementWatcherが「指定建物が
        /// 1件も無ければ何もできない」早期returnの判定に使う（Task82で電力タブの複製プレハブ機構を
        /// 撤去した現在、基地配置経路はこの指定建物のみ）。</summary>
        public static bool HasAny { get { return _designations.Count > 0; } }

        /// <summary>起動時（WarfrontLoadingExtension.LoadModAssets、UnitAssetBindings.Loadと同じ箇所）に
        /// 一度呼ぶ。冪等ではない（毎回ファイルから読み直す）。</summary>
        public static void Load(string modDirectory)
        {
            _designations.Clear();
            _filePath = null;

            try
            {
                if (string.IsNullOrEmpty(modDirectory))
                {
                    ModConfig.LogError("BaseBuildingDesignation.Load: modDirectory is empty, running in-memory only (designations will not be saved)");
                    return;
                }

                _filePath = Path.Combine(modDirectory, FileName);
                if (!File.Exists(_filePath))
                {
                    ModConfig.Log("BaseBuildingDesignation.Load: '" + _filePath + "' not found, starting with 0 designations");
                    return;
                }

                string[] lines = File.ReadAllLines(_filePath, Encoding.UTF8);
                int parsed = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq >= line.Length - 1) continue; // キー/値どちらかが空なら無視

                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    if (string.IsNullOrEmpty(value)) continue;

                    BaseType type;
                    if (!TryParseBaseType(key, out type)) continue; // 未知のキーは無視（将来の互換性）

                    _designations[type] = value;
                    parsed++;
                }

                ModConfig.Log("BaseBuildingDesignation.Load: loaded " + parsed + " designated building(s) from '" + _filePath + "'");
            }
            catch (Exception e)
            {
                // 壊れたファイル・アクセス権限エラー等は「割り当て無し」として継続する（ロードを止めない）。
                ModConfig.LogError("BaseBuildingDesignation.Load error (continuing with no designations): " + e);
                _designations.Clear();
            }
        }

        /// <summary>指定<paramref name="type"/>の指定建物アセット名を返す。未指定ならfalse。</summary>
        public static bool TryGet(BaseType type, out string assetName)
        {
            return _designations.TryGetValue(type, out assetName);
        }

        /// <summary>指定<paramref name="type"/>へ指定建物アセットを設定し、直ちに保存する。</summary>
        public static void Set(BaseType type, string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return;
            _designations[type] = assetName;
            ModConfig.Log("BaseBuildingDesignation.Set: " + type + " = " + assetName);
            Save();
        }

        /// <summary>指定<paramref name="type"/>の指定を解除し（既定＝電力タブの複製建物のみへ戻す）、
        /// 直ちに保存する。指定が無ければ何もしない（no-op、Save呼び出しも省略）。</summary>
        public static void Clear(BaseType type)
        {
            if (_designations.Remove(type))
            {
                ModConfig.Log("BaseBuildingDesignation.Clear: cleared designation for " + type + " (only the Electricity tab duplicate building remains usable)");
                Save();
            }
        }

        /// <summary>
        /// 建物のInfo.name（<paramref name="assetName"/>）が、いずれかの基地種別の指定建物と一致するか。
        /// 一致すればそのBaseTypeを返す。BasePlacementWatcher.ProcessCreated/ReconcileBases、
        /// BaseHiddenSync.ApplyPending、CoverMapBuilder.Buildが基地判定の唯一の経路として使う
        /// （Task82: 電力タブの複製プレハブとの一致判定=WarfrontBasePrefab.TryMatchは撤去済み）。
        /// 4種別のうち複数が同じアセット名を指定することは無い想定だが（UIは1アセット=1種別のみ許容）、
        /// 万一重複していても最初に見つかった種別を返すだけで例外にはならない。
        /// </summary>
        public static bool TryMatch(string assetName, out BaseType type)
        {
            type = default(BaseType);
            if (string.IsNullOrEmpty(assetName)) return false;

            foreach (KeyValuePair<BaseType, string> kv in _designations)
            {
                if (kv.Value == assetName) { type = kv.Key; return true; }
            }
            return false;
        }

        private static bool TryParseBaseType(string key, out BaseType type)
        {
            switch (key)
            {
                case "Army": type = BaseType.Army; return true;
                case "Navy": type = BaseType.Navy; return true;
                case "AirForce": type = BaseType.AirForce; return true;
                case "MissileBase": type = BaseType.MissileBase; return true;
                default: type = default(BaseType); return false;
            }
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                {
                    ModConfig.LogError("BaseBuildingDesignation.Save: modDirectory unresolved, skipping save (valid for this session only)");
                    return;
                }

                // File.WriteAllLines(path, lines, encoding) はTargetFrameworkVersion v3.5環境では確実に
                // 存在するとは限らないため、UnitAssetBindings.WriteBindingsToFileと同じくStreamWriterを使う。
                using (StreamWriter writer = new StreamWriter(_filePath, false, Encoding.UTF8))
                {
                    foreach (KeyValuePair<BaseType, string> kv in _designations)
                    {
                        writer.WriteLine(kv.Key + "=" + kv.Value);
                    }
                }

                ModConfig.Log("BaseBuildingDesignation.Save: saved " + _designations.Count + " designation(s) to '" + _filePath + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseBuildingDesignation.Save error: " + e);
            }
        }
    }
}
