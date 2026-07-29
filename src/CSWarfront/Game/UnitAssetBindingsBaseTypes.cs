using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// UnitAssetBindings のうち、Task66で新設した「基地種別（陸軍/海軍/空軍/ミサイル）ごとのモデル
    /// 割り当てキー」関連の定数・解決・複製処理だけを分離した partial class
    /// （UnitAssetBindings.cs 側の500行制限のため。AssetAssignPanel/AssetAssignPanelControls と同じ方針）。
    /// フィールドは全て UnitAssetBindings.cs 側で宣言されている（partial class は private メンバーも
    /// 全パーツで共有するため、_bindings/MakeKey/Save/TryGetExact 等をそのまま使える）。
    /// </summary>
    internal static partial class UnitAssetBindings
    {
        /// <summary>
        /// Task60時点の軍事拠点モデル割り当て用の特別な TypeKey。<b>ユニット種別ではない</b>
        /// （LandUnitRoster.All()には含まれず、UnitType/UnitCategoryとしての実体も持たない）。
        /// Task66で陸軍/海軍/空軍/ミサイルの4種別キー（<see cref="ArmyBaseTypeKey"/>等）に分割した後は、
        /// UIから直接選択されることはなくなった（_typeKeysには含まれない）が、<b>後方互換フォールバック</b>
        /// として引き続き解決に使われる: 新キーに割り当てが無い場合、この旧キーの割り当てが
        /// 「全基地種別に共通のフォールバック」として使われる（<see cref="TryGetForBase"/>参照）。
        /// これにより、Task66以前に作られた既存の unit-assets.txt の "MilitaryBase" 行は捨てずに済む。
        /// </summary>
        public const string BaseTypeKey = "MilitaryBase";

        /// <summary>Task66: 基地種別ごとのモデル割り当てキー。UIのドロップダウンにはこの4件が
        /// （<see cref="BaseTypeKey"/> の代わりに）先頭へ表示される。文字列値はセーブ済みファイルの
        /// キー名としてそのまま使われるため、リリース後に変更しないこと。</summary>
        public const string ArmyBaseTypeKey = "ArmyBase";
        public const string NavyBaseTypeKey = "NavyBase";
        public const string AirBaseTypeKey = "AirBase";
        public const string MissileBaseTypeKey = "MissileBase";

        /// <summary>UI（AssetAssignPanel/OptionsModelAssignPage）のドロップダウンで生のキー文字列の
        /// 代わりに表示する日本語ラベル。生のキー文字列をそのまま出すと「これは35種のユニットの1つ」
        /// という誤解を招くため、常にこのラベルへ差し替えて表示する。</summary>
        public const string ArmyBaseDisplayName = "陸軍基地";
        public const string NavyBaseDisplayName = "海軍基地";
        public const string AirBaseDisplayName = "空軍基地";
        public const string MissileBaseDisplayName = "ミサイル基地";

        /// <summary>指定<see cref="BaseType"/>に対応する割り当てキー（<see cref="ArmyBaseTypeKey"/>等）を返す。</summary>
        public static string BaseTypeKeyFor(BaseType type)
        {
            switch (type)
            {
                case BaseType.Navy: return NavyBaseTypeKey;
                case BaseType.AirForce: return AirBaseTypeKey;
                case BaseType.MissileBase: return MissileBaseTypeKey;
                case BaseType.Army:
                default:
                    return ArmyBaseTypeKey;
            }
        }

        /// <summary>typeKeyが基地種別キー（Army/Navy/Air/MissileBaseTypeKeyのいずれか）であれば
        /// 対応するBaseTypeを返す。ユニットのTypeKeyや旧レガシーキー（<see cref="BaseTypeKey"/>自身）
        /// に対してはfalseを返す（レガシーキーはUIから選択されるキーではなくフォールバック専用のため、
        /// 「これは基地選択エントリである」判定には含めない）。</summary>
        public static bool TryGetBaseTypeForKey(string typeKey, out BaseType type)
        {
            switch (typeKey)
            {
                case ArmyBaseTypeKey: type = BaseType.Army; return true;
                case NavyBaseTypeKey: type = BaseType.Navy; return true;
                case AirBaseTypeKey: type = BaseType.AirForce; return true;
                case MissileBaseTypeKey: type = BaseType.MissileBase; return true;
                default: type = default(BaseType); return false;
            }
        }

        /// <summary>基地種別キー用の表示ラベル（typeKeyが基地種別キーでなければtypeKeyをそのまま返す）。</summary>
        public static string DisplayNameForBaseKey(string typeKey)
        {
            switch (typeKey)
            {
                case ArmyBaseTypeKey: return ArmyBaseDisplayName;
                case NavyBaseTypeKey: return NavyBaseDisplayName;
                case AirBaseTypeKey: return AirBaseDisplayName;
                case MissileBaseTypeKey: return MissileBaseDisplayName;
                default: return typeKey;
            }
        }

        /// <summary>Task66: UI（AssetAssignPanel/OptionsModelAssignPage）がラベル表示・現在の割り当て表示
        /// で使う共通の振り分けヘルパー。typeKeyが基地種別キー（<see cref="TryGetBaseTypeForKey"/>が
        /// trueを返す）なら<see cref="TryGetForBase"/>（旧統合キーへのフォールバック込み）を、それ以外
        /// （ユニットのTypeKey）なら通常の<see cref="TryGet"/>（Tierフォールバック込み）を使う。
        /// これにより、UI側は「今どちらの種類のキーを見ているか」を意識せず、常に実効値（フォールバック
        /// 込みで実際に適用される値）を表示できる。</summary>
        public static bool TryGetEffective(byte factionId, string typeKey, out AssetKind kind, out string assetName)
        {
            BaseType baseType;
            if (TryGetBaseTypeForKey(typeKey, out baseType))
            {
                return TryGetForBase(factionId, baseType, out kind, out assetName);
            }
            return TryGet(factionId, typeKey, out kind, out assetName);
        }

        /// <summary>
        /// Task66: 基地（軍事拠点）専用の解決。resolution順序（後方互換の要）:
        ///   1. 指定<paramref name="baseType"/>の専用キー（<see cref="BaseTypeKeyFor"/>）・勢力別exact
        ///   2. 同キー・レガシー/全勢力共通exact
        ///   3. 旧統合キー（<see cref="BaseTypeKey"/>、"MilitaryBase"）・勢力別exact
        ///   4. 旧統合キー・レガシー/全勢力共通exact
        ///   5. 無し
        /// Task60以前（基地種別の区別が無かった頃）に作られた "MilitaryBase" 行を無駄にしないための
        /// フォールバックであり、Task66で新設した種別別キー（1・2）が常に優先される。
        /// Tierフォールバック（TryGet参照）は基地には無関係のため、ここでは一切行わない
        /// （基地種別キーは "&lt;Category&gt;_T&lt;tier&gt;" 形式ではないため、そもそもTypeKeyParser.TryParse
        /// が解析に失敗し素通りするだけで実害は無いが、専用メソッドとして明示することで意図を明確にする）。
        /// </summary>
        public static bool TryGetForBase(byte factionId, BaseType baseType, out AssetKind kind, out string assetName)
        {
            string typeKey = BaseTypeKeyFor(baseType);
            if (TryGetExact(factionId, typeKey, out kind, out assetName)) return true;
            if (TryGetExact(factionId, BaseTypeKey, out kind, out assetName)) return true;

            kind = AssetKind.Prop;
            assetName = null;
            return false;
        }

        /// <summary>
        /// Task66: コピー元が基地種別キー（ArmyBaseTypeKey等）の場合専用の複製処理
        /// （UnitAssetBindings.CopyToから分岐して呼ばれる）。
        /// 拠点には「カテゴリ」も「Tier」も「他のユニット種別」も存在しないため、CopyScopeのうち
        /// 「勢力」次元を持つ2つ（AllFactionsSameType=全勢力（同じ種別）／AllFactionsAllTypes=全勢力・
        /// 全種別）だけを「同じ基地種別を他の全勢力へ複製する」という意味に読み替えて対応する
        /// （基地種別をまたいだ複製＝Army→Navy等は行わない。要件「全勢力（同じ種別）は基地種別ごとに動く」
        /// に従い、<paramref name="fromTypeKey"/>自身をそのまま書き込み先キーとして使う）。
        /// 「種別」次元を持つ2つ（SameCategoryAllTiers=同カテゴリの全Tier／AllUnitTypes=全ユニット種別）は
        /// 拠点に対して意味を成さない（ユニット種別ではないため）ので、要件通り何も書き込まず0を返す
        /// （呼び出し元のUIはwritten==0の場合ApplyBindingChange相当の反映処理を呼ばないため、
        /// ユーザーには「何も起きなかった」だけが見え、誤って他のユニット種別へ書き込まれることは無い）。
        /// </summary>
        private static int CopyBaseTo(byte fromFaction, string fromTypeKey, AssetKind kind, string name, CopyScope scope)
        {
            bool allFactions = scope == CopyScope.AllFactionsSameType || scope == CopyScope.AllFactionsAllTypes;
            if (!allFactions)
            {
                ModConfig.Log("UnitAssetBindings.CopyTo: " + fromTypeKey + " はscope=" + scope +
                    " に対応しないためスキップしました（同カテゴリ/全ユニット種別はユニット専用のスコープです）");
                return 0;
            }

            int written = 0;
            for (byte f = 0; f < WarfrontSettings.MaxFactions; f++)
            {
                if (f == fromFaction) continue; // コピー元自身はスキップ
                _bindings[MakeKey(f, fromTypeKey)] = new Binding { Kind = kind, Name = name };
                written++;
            }

            if (written > 0) Save();
            ModConfig.Log("UnitAssetBindings.CopyTo: faction=" + fromFaction + " " + fromTypeKey + " (" +
                AssetKindUtil.ToPrefix(kind) + ":" + name + ") を scope=" + scope + " へ複製し（同じ基地種別で他の全勢力へ）、" +
                written + " 件を書き込みました");
            return written;
        }
    }
}
