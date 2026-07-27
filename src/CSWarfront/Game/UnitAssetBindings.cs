using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSWarfront.Game
{
    /// <summary>
    /// 「ユニット種別（TypeKey） → サブスクライブ済みプロップ名」の割り当てを保持・永続化する（Task36）。
    /// セーブゲームではなくMODディレクトリ直下の単純なテキストファイルへ保存するグローバル設定
    /// （プレイヤーの「自分が持っているプロップ」というメンタルモデルに合わせ、セーブ間で共有する）。
    /// 1行 "typeKey=propName" 形式、UTF-8。壊れている/存在しないファイルは常に「割り当て無し」として扱い、
    /// 例外を外へ投げない（ここでの失敗がロード自体を止めてはならない）。
    /// メインスレッド専用という制約は無いが、呼び出しは全てメインスレッド（UI/ロード処理）から行われる想定。
    /// </summary>
    internal static class UnitAssetBindings
    {
        private const string FileName = "unit-assets.txt";

        private static readonly Dictionary<string, string> _bindings = new Dictionary<string, string>();

        // 解決済みファイルパス。modDirectory が取得できなかった場合は null のままとなり、
        // Set() はメモリ内のみで保持し保存はスキップする（EnsureRegisteredと同様、ロード自体は止めない）。
        private static string _filePath;

        public static int Count { get { return _bindings.Count; } }

        /// <summary>起動時（WarfrontLoadingExtension.OnLevelLoaded）に一度呼ぶ。冪等ではない
        /// （毎回ファイルから読み直す。呼び出し側は1レベルロードにつき1回のみ呼ぶ想定）。</summary>
        public static void Load(string modDirectory)
        {
            _bindings.Clear();
            _filePath = null;

            try
            {
                if (string.IsNullOrEmpty(modDirectory))
                {
                    ModConfig.LogError("UnitAssetBindings.Load: modDirectory が空のためメモリ内のみで動作します（割り当ては保存されません）");
                    return;
                }

                _filePath = Path.Combine(modDirectory, FileName);
                if (!File.Exists(_filePath))
                {
                    ModConfig.Log("UnitAssetBindings.Load: '" + _filePath + "' が無いため割り当て0件で開始");
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
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;

                    _bindings[key] = value;
                    parsed++;
                }

                ModConfig.Log("UnitAssetBindings.Load: '" + _filePath + "' から " + parsed + " 件の割り当てを読み込みました");
            }
            catch (Exception e)
            {
                // 壊れたファイル・アクセス権限エラー等は「割り当て無し」として継続する（ロードを止めない）。
                ModConfig.LogError("UnitAssetBindings.Load error（割り当て無しとして継続）: " + e);
                _bindings.Clear();
            }
        }

        public static bool TryGet(string typeKey, out string propName)
        {
            if (string.IsNullOrEmpty(typeKey))
            {
                propName = null;
                return false;
            }
            return _bindings.TryGetValue(typeKey, out propName);
        }

        /// <summary>指定TypeKeyへプロップ名を割り当て、直ちに保存する。</summary>
        public static void Set(string typeKey, string propName)
        {
            if (string.IsNullOrEmpty(typeKey) || string.IsNullOrEmpty(propName)) return;
            _bindings[typeKey] = propName;
            ModConfig.Log("UnitAssetBindings.Set: " + typeKey + " = " + propName);
            Save();
        }

        /// <summary>割り当てを解除し（既定フォールバックへ戻す）、直ちに保存する。</summary>
        public static void Clear(string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return;
            if (_bindings.Remove(typeKey))
            {
                ModConfig.Log("UnitAssetBindings.Clear: " + typeKey + " を既定に戻しました");
                Save();
            }
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                {
                    ModConfig.LogError("UnitAssetBindings.Save: modDirectory 未解決のため保存をスキップ（今回のセッション内のみ有効）");
                    return;
                }

                // File.WriteAllLines(path, lines, encoding) は .NET 4.0 以降の追加オーバーロードであり、
                // 本プロジェクトの TargetFrameworkVersion v3.5 環境では確実に存在するとは限らないため、
                // .NET 1.1 から存在する StreamWriter を明示的に使う（WarStateSerializer等、既存コードの
                // File.ReadAllText/File.Exists 止まりの使用実績に対し、書き込みはより保守的な経路を選ぶ）。
                using (StreamWriter writer = new StreamWriter(_filePath, false, Encoding.UTF8))
                {
                    foreach (KeyValuePair<string, string> kv in _bindings)
                    {
                        writer.WriteLine(kv.Key + "=" + kv.Value);
                    }
                }

                ModConfig.Log("UnitAssetBindings.Save: '" + _filePath + "' へ " + _bindings.Count + " 件を保存しました");
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.Save error: " + e);
            }
        }
    }
}
