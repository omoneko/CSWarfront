using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task47: 「複製適用」で選べる複製先の範囲。値はUI（floating panel / Optionsサブページ）両方の
    /// スコープドロップダウンの選択インデックスと一致させる（並び変更・挿入は両UIの再確認が必要）。
    /// </summary>
    internal enum CopyScope
    {
        /// <summary>同カテゴリの全Tier（同じ勢力、例: Tank_T1〜T5）。</summary>
        SameCategoryAllTiers = 0,
        /// <summary>全ユニット種別（同じ勢力の35キー全て）。</summary>
        AllUnitTypes = 1,
        /// <summary>全勢力（同じ種別、faction 0..4）。</summary>
        AllFactionsSameType = 2,
        /// <summary>全勢力・全種別。</summary>
        AllFactionsAllTypes = 3
    }

    /// <summary>CopyScope ⇔ 表示ラベルの変換。AssetKindUtil と同じ方針の小さなヘルパー。</summary>
    internal static class CopyScopeUtil
    {
        /// <summary>スコープドロップダウンの選択インデックス0..3の並びと完全に一致させること。</summary>
        public static readonly CopyScope[] All =
        {
            CopyScope.SameCategoryAllTiers, CopyScope.AllUnitTypes,
            CopyScope.AllFactionsSameType, CopyScope.AllFactionsAllTypes
        };

        public static string DisplayNameJa(CopyScope scope)
        {
            switch (scope)
            {
                case CopyScope.SameCategoryAllTiers: return "同カテゴリの全Tier";
                case CopyScope.AllUnitTypes: return "全ユニット種別";
                case CopyScope.AllFactionsSameType: return "全勢力（同じ種別）";
                case CopyScope.AllFactionsAllTypes: return "全勢力・全種別";
                default: return scope.ToString();
            }
        }
    }

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
    /// 解決順序（Task50でTierフォールバックを追加）:
    ///   1. 勢力別・exact-key（faction, typeKeyそのもの）の割り当て
    ///   2. レガシー/全勢力共通・exact-keyの割り当て
    ///   3. 同カテゴリ・他Tierへのフォールバック（typeKeyが "&lt;Category&gt;_T&lt;tier&gt;" として
    ///      解析できる場合のみ）。直近の下位Tierから1まで、その後は直近の上位Tierから5まで
    ///      （TypeKeyParser.FallbackTierOrder参照、例: Tank_T4 未割当なら T3→T2→T1→T5の順）を試し、
    ///      各Tier候補について「勢力別 → レガシー/全勢力共通」の順に確認する。
    ///   4. 無し（既定モデル）
    /// これにより「Tier1にだけモデルを割り当てれば、Tier2以降にもそのモデルが自動的に適用される」
    /// （5Tierすべてを手作業で割り当てる必要がない）。ただし特定Tierへの明示的な割り当て（手順1/2）は
    /// 常にこのフォールバック（手順3）より優先される。
    ///
    /// 基地（軍事拠点）専用の解決は<see cref="TryGetForBase"/>が別に持つ（Tierフォールバックは無関係のため
    /// TryGetは経由しない）。Task60では基地種別を区別しない単一キー（<see cref="BaseTypeKey"/>、
    /// "MilitaryBase"）のみだったが、Task66で陸軍/海軍/空軍/ミサイルの4種別キー（<see cref="ArmyBaseTypeKey"/>
    /// 等）へ分割した。解決順序: 1. 種別別キー・勢力別exact → 2. 種別別キー・レガシー/全勢力共通exact →
    /// 3. 旧統合キー（"MilitaryBase"）・勢力別exact → 4. 旧統合キー・レガシー/全勢力共通exact → 5. 無し。
    /// これにより、Task66以前に保存された "faction|MilitaryBase=..." 行は「種別別の明示的割り当てが
    /// 無い基地種別すべてに共通のフォールバック」として引き続き機能する（unit-assets.txtの書き換え不要）。
    ///
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
    internal static partial class UnitAssetBindings
    {
        // Task66: 基地種別キー定数（BaseTypeKey/ArmyBaseTypeKey等）・表示名・BaseTypeKeyFor/
        // TryGetBaseTypeForKey/DisplayNameForBaseKey・TryGetForBase・CopyBaseTo は500行制限のため
        // UnitAssetBindingsBaseTypes.cs（同じ partial class、Game/UnitAssetBindingsBaseTypes.cs）へ分離した。

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
        /// 指定勢力・種別の割り当てを解決する。解決順序（クラス冒頭コメント参照、Task50でTier
        /// フォールバックを追加）: 勢力別exact → レガシー/全勢力共通exact → 同カテゴリ他Tier
        /// フォールバック（勢力別優先） → 無し。
        /// </summary>
        public static bool TryGet(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            kind = AssetKind.Prop;
            assetName = null;
            if (string.IsNullOrEmpty(typeKey)) return false;

            if (TryGetExact(factionId, typeKey, out kind, out assetName)) return true;

            // Task50: exact-keyが無ければ、同カテゴリの他Tierへフォールバックする（「Tier1にだけ
            // モデルを割り当てれば全Tierに効く」を実現する。パース/探索順序の組み立ては
            // CSWarfront.Core.TypeKeyParser（純ロジック、Core.Testsでテスト済み）に委譲する）。
            // typeKeyが"<Category>_T<tier>"として解析できない場合（基地種別キー等）はTryParseが
            // falseを返して素通りするだけで、例外や誤マッチは起きない。
            UnitCategory category;
            byte tier;
            if (TypeKeyParser.TryParse(typeKey, out category, out tier))
            {
                byte[] fallbackTiers = TypeKeyParser.FallbackTierOrder(tier);
                for (int i = 0; i < fallbackTiers.Length; i++)
                {
                    string fallbackKey = LandUnitRoster.TypeKey(category, fallbackTiers[i]);
                    if (TryGetExact(factionId, fallbackKey, out kind, out assetName)) return true;
                }
            }

            kind = AssetKind.Prop;
            assetName = null;
            return false;
        }

        // Task66: TryGetEffective/TryGetForBase は UnitAssetBindingsBaseTypes.cs（同じ partial class）へ
        // 分離した（500行制限）。TryGetExact（下）はどちらからも呼ばれる共有ヘルパーのためこちらに残す。

        /// <summary>勢力別exact → レガシー/全勢力共通exactの2段のみを見る（フォールバック無し）内部ヘルパー。
        /// TryGet（Tierフォールバック込み）とTryGetForBase（旧統合キーフォールバック込み）の両方から
        /// 共有される最小単位。</summary>
        private static bool TryGetExact(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            kind = AssetKind.Prop;
            assetName = null;

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

        /// <summary>
        /// Task47: 「複製適用」。指定(勢力,TypeKey)の現在の割り当てを、scopeで指定した範囲の全(勢力,TypeKey)
        /// へまとめて複製する。コピー元自体は書き込み対象から除外する（既に同じ値のため無駄な書き込み/
        /// ログを避ける）。保存はループ内でSet()を都度呼ばず、全件の変更を終えてから1回だけ行う
        /// （書き込み件数分ディスクI/Oが走るのを避けるため）。呼び出し元（AssetAssignPanel/
        /// Options page）はUnitVisuals.DestroyAll()を自分で呼ぶこと（このメソッドは永続化のみ担当し、
        /// 見た目の再生成トリガーには関与しない＝floating panelとOptionsページの両方から呼ばれるため、
        /// 副作用はどちらのUIからも明示的に呼ぶ形に統一する）。
        /// </summary>
        /// <returns>実際に書き込んだ(勢力,TypeKey)の件数。コピー元に割り当てが無い、またはTypeKeyが
        /// 不明な場合は0を返し、何も変更しない。</returns>
        public static int CopyTo(byte fromFaction, string fromTypeKey, CopyScope scope)
        {
            // Task66: コピー元が基地種別キー（Army/Navy/Air/MissileBaseTypeKey）の場合は専用の複製処理へ
            // 分岐する。LandUnitRoster.All() を線形探索する下のユニット向けロジックはこの仮想的な"種別"を
            // 決して含まないため、そのまま流すと必ず0件（TryGetCategory失敗）になってしまう。
            // 「現在の割り当て」表示（TryGetForBase、旧統合キーへのフォールバックを含む）と同じ実効値を
            // 複製できるよう、コピー元の解決も TryGet ではなく TryGetForBase を使う。
            BaseType fromBaseType;
            bool isBaseKey = TryGetBaseTypeForKey(fromTypeKey, out fromBaseType);

            AssetKind kind;
            string name;
            bool hasSource = isBaseKey
                ? TryGetForBase(fromFaction, fromBaseType, out kind, out name)
                : TryGet(fromFaction, fromTypeKey, out kind, out name);

            if (!hasSource)
            {
                ModConfig.Log("UnitAssetBindings.CopyTo: コピー元 faction=" + fromFaction + " " + fromTypeKey + " に割り当てが無いためスキップしました");
                return 0;
            }

            if (isBaseKey)
            {
                return CopyBaseTo(fromFaction, fromTypeKey, kind, name, scope);
            }

            UnitCategory fromCategory;
            if (!TryGetCategory(fromTypeKey, out fromCategory))
            {
                ModConfig.LogError("UnitAssetBindings.CopyTo: 不明なTypeKey '" + fromTypeKey + "' のためスキップしました");
                return 0;
            }

            bool allTypes = scope == CopyScope.AllUnitTypes || scope == CopyScope.AllFactionsAllTypes;
            bool allFactions = scope == CopyScope.AllFactionsSameType || scope == CopyScope.AllFactionsAllTypes;

            int written = 0;
            foreach (UnitType t in LandUnitRoster.All())
            {
                if (!allTypes)
                {
                    bool sameCategory = scope == CopyScope.SameCategoryAllTiers && t.Category == fromCategory;
                    bool sameType = scope == CopyScope.AllFactionsSameType && t.TypeKey == fromTypeKey;
                    if (!sameCategory && !sameType) continue;
                }

                if (allFactions)
                {
                    for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
                    {
                        if (f == fromFaction && t.TypeKey == fromTypeKey) continue; // コピー元自身はスキップ
                        _bindings[MakeKey(f, t.TypeKey)] = new Binding { Kind = kind, Name = name };
                        written++;
                    }
                }
                else
                {
                    if (t.TypeKey == fromTypeKey) continue; // コピー元自身はスキップ
                    _bindings[MakeKey(fromFaction, t.TypeKey)] = new Binding { Kind = kind, Name = name };
                    written++;
                }
            }

            if (written > 0) Save();
            ModConfig.Log("UnitAssetBindings.CopyTo: faction=" + fromFaction + " " + fromTypeKey + " (" +
                AssetKindUtil.ToPrefix(kind) + ":" + name + ") を scope=" + scope + " へ複製し、" + written + " 件を書き込みました");
            return written;
        }

        // Task66: CopyBaseTo（コピー元が基地種別キーの場合の専用複製処理）は
        // UnitAssetBindingsBaseTypes.cs（同じ partial class）へ分離した（500行制限）。
        // _bindings/MakeKey/Save は private static だが partial class 内では全パーツで共有されるため
        // 問題なくそちらから呼べる。

        /// <summary>TypeKeyからUnitCategoryを逆引きする（LandUnitRoster.All()を線形探索、35件のみなので
        /// コストは無視できる）。見つからない場合はfalse。</summary>
        private static bool TryGetCategory(string typeKey, out UnitCategory category)
        {
            foreach (UnitType t in LandUnitRoster.All())
            {
                if (t.TypeKey == typeKey)
                {
                    category = t.Category;
                    return true;
                }
            }
            category = default(UnitCategory);
            return false;
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
