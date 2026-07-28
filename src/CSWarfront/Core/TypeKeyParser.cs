using System;
using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// "&lt;Category&gt;_T&lt;tier&gt;" 形式のTypeKey（LandUnitRoster.TypeKeyが組み立てる形式、例:
    /// "Tank_T3"）を解析する小さな純ロジックヘルパー（Task50）。
    ///
    /// Game層のUnitAssetBindings（アセット割り当ての「同カテゴリ他Tierへのフォールバック」、
    /// Task50フィードバック1「Tier2になるとモデル設定が反映されない」対応）から使われる。
    /// UnitAssetBindings自体はGame層（UnityEngineを直接は参照しないが、CSWarfront.Core.Testsの
    /// コンパイル対象フォルダ外＝Core\**\*.csのみ）に置かれているため、テスト可能な形にするために
    /// パース処理と探索順序の組み立てだけをこのCoreクラスへ切り出した
    /// （UnitAssetBindings.TryGetはこの結果を使って辞書検索するだけの薄いGlueに留める）。
    /// </summary>
    public static class TypeKeyParser
    {
        /// <summary>ロスターが実際に使うTierの範囲（LandUnitRoster: Tier1〜5）。</summary>
        public const byte MinTier = 1;
        public const byte MaxTier = 5;

        /// <summary>
        /// typeKeyを "&lt;Category&gt;_T&lt;tier&gt;" として解析する。末尾の "_T&lt;digits&gt;" を
        /// 区切りとして切り出し、前半をUnitCategory名、後半をTier数値として解釈する
        /// （LandUnitRoster.TypeKeyの組み立て方 category + "_T" + tier の逆変換）。
        /// UnitCategoryの列挙子名自体に "_T" を含むものは無いため誤分割は起きない。
        /// 解析できない場合（区切りが無い/Tier部が数値でない/カテゴリ名が不明）はfalseを返す
        /// （例外は投げない）。
        /// </summary>
        public static bool TryParse(string typeKey, out UnitCategory category, out byte tier)
        {
            category = default(UnitCategory);
            tier = 0;
            if (string.IsNullOrEmpty(typeKey)) return false;

            int splitIndex = typeKey.LastIndexOf("_T", StringComparison.Ordinal);
            if (splitIndex <= 0 || splitIndex + 2 >= typeKey.Length) return false;

            string categoryPart = typeKey.Substring(0, splitIndex);
            string tierPart = typeKey.Substring(splitIndex + 2);

            if (!byte.TryParse(tierPart, out tier)) return false;
            return TryParseCategory(categoryPart, out category);
        }

        private static bool TryParseCategory(string value, out UnitCategory category)
        {
            // .NET 3.5(Game層のビルドターゲット)にはEnum.TryParse<T>が無いため、
            // 既知の値を線形探索する（UnitCategoryは23件のみでコストは無視できる）。
            foreach (UnitCategory c in (UnitCategory[])Enum.GetValues(typeof(UnitCategory)))
            {
                if (string.Equals(c.ToString(), value, StringComparison.Ordinal))
                {
                    category = c;
                    return true;
                }
            }
            category = default(UnitCategory);
            return false;
        }

        /// <summary>
        /// 指定Tierに対する「同カテゴリ内の他Tierを探すフォールバック順」を返す（Task50）。
        /// 直近の下位Tierから順に1まで下り、その後は直近の上位Tierから順に5まで上る:
        ///   例: tier=4 -&gt; [3, 2, 1, 5]
        ///   例: tier=1 -&gt; [2, 3, 4, 5]（下位が無いため上位のみ）
        ///   例: tier=5 -&gt; [4, 3, 2, 1]（上位が無いため下位のみ）
        /// tier自身は含まない（呼び出し側が先にexact-key一致を試す前提のため）。
        /// MinTier(1)〜MaxTier(5)の範囲外のtierを渡しても例外にはならない
        /// （単に一方向の探索のみが行われる、または空配列になる）。
        /// </summary>
        public static byte[] FallbackTierOrder(byte tier)
        {
            var order = new List<byte>(MaxTier - MinTier);
            for (int t = tier - 1; t >= MinTier; t--) order.Add((byte)t);
            for (int t = tier + 1; t <= MaxTier; t++) order.Add((byte)t);
            return order.ToArray();
        }
    }
}
