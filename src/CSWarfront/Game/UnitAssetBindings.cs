using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSWarfront.Game
{
    /// <summary>
    /// 「(勢力ID, ユニット種別TypeKey) → サブスクライブ済みアセット(種類+名前)」の割り当てを保持・永続化する
    /// （Task36で導入、Task40で勢力別に拡張、Task41でプロップ以外の種類（建物/車両/樹木）にも対応）。
    /// セーブゲームではなくMODディレクトリ直下の単純なテキストファイルへ保存するグローバル設定
    /// （プレイヤーの「自分が持っているアセット」というメンタルモデルに合わせ、セーブ間で共有する）。
    ///
    /// ファイル形式（1行、UTF-8）:
    ///   "factionId|typeKey=kind:assetName"  … 勢力別の割り当て（Task41で値にkindプレフィックスを追加）
    ///   "factionId|typeKey=assetName"       … kindプレフィックス無しの値。後方互換のため
    ///                                          AssetKind.Prop（プロップ）として読み込む。
    ///   "typeKey=kind:assetName" / "typeKey=assetName" … レガシー行（factionIdプレフィックス無し）。
    ///                                          「全勢力共通のフォールバック」として読み込む（後方互換）。
    ///
    /// 解決順序: 勢力別の割り当て → レガシー/全勢力共通の割り当て → 無し（既定モデル）。
    /// kindプレフィックスの解析は AssetKindUtil.TryParsePrefix が行う。値の先頭が既知のkind名+':'で
    /// 始まらない場合は、値全体を（kindプレフィックス無しとして）AssetKind.Propの名前とみなす
    /// （既存プロップ名にたまたま':'が含まれていても誤解析しない）。
    ///
    /// Set()は常に新形式（"kind:assetName"）で書き込む。レガシー行は Set/Clear では作られない（新規保存は
    /// 必ず勢力別・kindプレフィックス付き形式）が、既存ファイルに残っている場合は読み込み続け、保存時も
    /// 読み込んだキー形式（factionIdプレフィックスの有無）のまま書き戻す（互換性維持のため消さない。
    /// 値側は毎回 kind プレフィックス付きで正規化して書き戻す＝再保存後は全行が新形式の値になる）。
    ///
    /// 壊れている/存在しないファイルは常に「割り当て無し」として扱い、例外を外へ投げない
    /// （ここでの失敗がロード自体を止めてはならない）。
    /// メインスレッド専用という制約は無いが、呼び出しは全てメインスレッド（UI/ロード処理）から行われる想定。
    /// </summary>
    internal static class UnitAssetBindings
    {
        private const string FileName = "unit-assets.txt";

        private struct Binding
        {
            public AssetKind Kind;
            public string Name;
        }

        // 勢力別の割り当て。キーは MakeKey(factionId, typeKey) = "factionId|typeKey"。
        private static readonly Dictionary<string, Binding> _bindings = new Dictionary<string, Binding>();

        // レガシー行（factionIdプレフィックス無し）。キーは typeKey そのもの。全勢力共通のフォールバック。
        private static readonly Dictionary<string, Binding> _anyFactionBindings = new Dictionary<string, Binding>();

        // 解決済みファイルパス。modDirectory が取得できなかった場合は null のままとなり、
        // Set() はメモリ内のみで保持し保存はスキップする（EnsureRegisteredと同様、ロード自体は止めない）。
        private static string _filePath;

        public static int Count { get { return _bindings.Count + _anyFactionBindings.Count; } }

        /// <summary>起動時（WarfrontLoadingExtension.OnLevelLoaded）に一度呼ぶ。冪等ではない
        /// （毎回ファイルから読み直す。呼び出し側は1レベルロードにつき1回のみ呼ぶ想定）。</summary>
        public static void Load(string modDirectory)
        {
            _bindings.Clear();
            _anyFactionBindings.Clear();
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
                    string rawValue = line.Substring(eq + 1);
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(rawValue)) continue;

                    Binding binding;
                    ParseValue(rawValue, out binding);

                    byte factionId;
                    string typeKey;
                    if (TryParseFactionKey(key, out factionId, out typeKey))
                    {
                        _bindings[MakeKey(factionId, typeKey)] = binding;
                    }
                    else
                    {
                        // レガシー行（Task36形式）。全勢力共通のフォールバックとして扱う。
                        _anyFactionBindings[key] = binding;
                    }
                    parsed++;
                }

                ModConfig.Log("UnitAssetBindings.Load: '" + _filePath + "' から " + parsed + " 件の割り当てを読み込みました（勢力別 " +
                    _bindings.Count + " 件 / 全勢力共通(レガシー) " + _anyFactionBindings.Count + " 件）");
            }
            catch (Exception e)
            {
                // 壊れたファイル・アクセス権限エラー等は「割り当て無し」として継続する（ロードを止めない）。
                ModConfig.LogError("UnitAssetBindings.Load error（割り当て無しとして継続）: " + e);
                _bindings.Clear();
                _anyFactionBindings.Clear();
            }
        }

        /// <summary>
        /// 指定勢力・種別の割り当てを解決する。解決順序: 勢力別 → 全勢力共通(レガシー) → 無し。
        /// </summary>
        public static bool TryGet(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            kind = AssetKind.Prop;
            assetName = null;
            if (string.IsNullOrEmpty(typeKey)) return false;

            Binding binding;
            if (_bindings.TryGetValue(MakeKey(factionId, typeKey), out binding))
            {
                kind = binding.Kind;
                assetName = binding.Name;
                return true;
            }
            if (_anyFactionBindings.TryGetValue(typeKey, out binding))
            {
                kind = binding.Kind;
                assetName = binding.Name;
                return true;
            }

            kind = AssetKind.Prop;
            assetName = null;
            return false;
        }

        /// <summary>指定(勢力, TypeKey)へアセット（種類+名前）を割り当て、直ちに保存する。常に勢力別・
        /// kindプレフィックス付き形式で保存する（レガシー/全勢力共通のエントリはここでは作らない・
        /// 変更しない）。</summary>
        public static void Set(byte factionId, string typeKey, AssetKind kind, string assetName)
        {
            if (string.IsNullOrEmpty(typeKey) || string.IsNullOrEmpty(assetName)) return;
            _bindings[MakeKey(factionId, typeKey)] = new Binding { Kind = kind, Name = assetName };
            ModConfig.Log("UnitAssetBindings.Set: faction=" + factionId + " " + typeKey + " = " + AssetKindUtil.ToPrefix(kind) + ":" + assetName);
            Save();
        }

        /// <summary>指定(勢力, TypeKey)の勢力別割り当てのみを解除し（全勢力共通/既定フォールバックへ戻す）、
        /// 直ちに保存する。全勢力共通(レガシー)のエントリは変更しない。</summary>
        public static void Clear(byte factionId, string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return;
            if (_bindings.Remove(MakeKey(factionId, typeKey)))
            {
                ModConfig.Log("UnitAssetBindings.Clear: faction=" + factionId + " " + typeKey + " を既定に戻しました");
                Save();
            }
        }

        /// <summary>値部分（"kind:assetName" または後方互換の "assetName" のみ）を解析する。
        /// 先頭が既知のkind名+':'であればそのkindとして扱い、そうでなければ値全体をAssetKind.Propの
        /// 名前として扱う（後方互換: Task36/Task40当時のファイルにkindプレフィックスは存在しない）。</summary>
        private static void ParseValue(string rawValue, out Binding binding)
        {
            int colon = rawValue.IndexOf(':');
            if (colon > 0)
            {
                string prefix = rawValue.Substring(0, colon);
                AssetKind parsedKind;
                if (AssetKindUtil.TryParsePrefix(prefix, out parsedKind))
                {
                    binding = new Binding { Kind = parsedKind, Name = rawValue.Substring(colon + 1) };
                    return;
                }
            }

            binding = new Binding { Kind = AssetKind.Prop, Name = rawValue };
        }

        /// <summary>"factionId|typeKey" 形式のキーを解析する。factionIdプレフィックスが無い/数値でない場合は
        /// false を返す（呼び出し側はその行をレガシー/全勢力共通として扱う）。</summary>
        private static bool TryParseFactionKey(string key, out byte factionId, out string typeKey)
        {
            factionId = 0;
            typeKey = null;

            int bar = key.IndexOf('|');
            if (bar <= 0 || bar >= key.Length - 1) return false;

            string prefix = key.Substring(0, bar);
            byte parsed;
            if (!byte.TryParse(prefix, out parsed)) return false;

            factionId = parsed;
            typeKey = key.Substring(bar + 1);
            return !string.IsNullOrEmpty(typeKey);
        }

        private static string MakeKey(byte factionId, string typeKey)
        {
            return factionId.ToString() + "|" + typeKey;
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
                    // レガシー/全勢力共通の行は読み込んだキー形式（factionIdプレフィックス無し）のまま
                    // 書き戻す。値は毎回 kind プレフィックス付きの新形式へ正規化する。
                    foreach (KeyValuePair<string, Binding> kv in _anyFactionBindings)
                    {
                        writer.WriteLine(kv.Key + "=" + AssetKindUtil.ToPrefix(kv.Value.Kind) + ":" + kv.Value.Name);
                    }
                    // 勢力別の行はキーが既に "factionId|typeKey" 形式。
                    foreach (KeyValuePair<string, Binding> kv in _bindings)
                    {
                        writer.WriteLine(kv.Key + "=" + AssetKindUtil.ToPrefix(kv.Value.Kind) + ":" + kv.Value.Name);
                    }
                }

                ModConfig.Log("UnitAssetBindings.Save: '" + _filePath + "' へ 勢力別" + _bindings.Count +
                    "件 + 全勢力共通" + _anyFactionBindings.Count + "件 を保存しました");
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitAssetBindings.Save error: " + e);
            }
        }
    }
}
