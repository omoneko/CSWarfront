using System;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task41: ユニットの見た目モデルとして割り当てられるアセットの種類。プロップに加え、建物/車両/樹木の
    /// Workshopアセットも借用できるようにするための識別子（<see cref="AssetCatalog"/> がこの種類ごとに
    /// PrefabCollection&lt;T&gt; を使い分けて列挙・解決する）。
    /// 数値はファイル保存形式（UnitAssetBindings）やUIドロップダウンの選択インデックスと結びつくため、
    /// 既存の並び順を変更しないこと（末尾への追加のみ許容）。
    /// </summary>
    internal enum AssetKind : byte
    {
        Prop = 0,
        Building = 1,
        Vehicle = 2,
        Tree = 3
    }

    /// <summary>
    /// AssetKind ⇔ 文字列（保存用プレフィックス／UI表示ラベル）の変換をまとめた小さなヘルパー。
    /// UnitAssetBindings（ファイル形式のkindプレフィックス）と AssetAssignPanel（種別ドロップダウンの
    /// ラベル・現在の割り当て表示）の両方から使う。
    /// </summary>
    internal static class AssetKindUtil
    {
        /// <summary>種別ドロップダウンの選択インデックス0..3の並びと完全に一致させること。</summary>
        public static readonly AssetKind[] All = { AssetKind.Prop, AssetKind.Building, AssetKind.Vehicle, AssetKind.Tree };

        /// <summary>UnitAssetBindingsのファイル保存形式で使う小文字プレフィックス（"kind:assetName"）。</summary>
        public static string ToPrefix(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Building: return "building";
                case AssetKind.Vehicle: return "vehicle";
                case AssetKind.Tree: return "tree";
                default: return "prop";
            }
        }

        /// <summary>保存形式のプレフィックス文字列（大文字小文字無視）をAssetKindへ解決する。
        /// 未知のプレフィックスの場合はfalseを返す（呼び出し側はkindプレフィックス無しの
        /// レガシー行として扱うこと）。</summary>
        public static bool TryParsePrefix(string prefix, out AssetKind kind)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                if (string.Equals(prefix, "prop", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Prop; return true; }
                if (string.Equals(prefix, "building", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Building; return true; }
                if (string.Equals(prefix, "vehicle", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Vehicle; return true; }
                if (string.Equals(prefix, "tree", StringComparison.OrdinalIgnoreCase)) { kind = AssetKind.Tree; return true; }
            }
            kind = AssetKind.Prop;
            return false;
        }

        /// <summary>UI（種別ドロップダウン、現在の割り当て表示）向けの日本語ラベル。</summary>
        public static string DisplayNameJa(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Building: return "建物";
                case AssetKind.Vehicle: return "車両";
                case AssetKind.Tree: return "樹木";
                default: return "プロップ";
            }
        }

        /// <summary>「現在の割り当て」表示用のラベルを組み立てる。プロップは従来通り名前のみ、
        /// それ以外の種別は名前の前に "[建物]" 等の種別タグを付けて区別できるようにする。</summary>
        public static string Describe(AssetKind kind, string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return assetName;
            return kind == AssetKind.Prop ? assetName : "[" + DisplayNameJa(kind) + "]" + assetName;
        }
    }
}
