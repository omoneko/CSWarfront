namespace CSWarfront.Core
{
    /// <summary>
    /// 勢力関係の既定プリセット。以前は Game/MilitaryManager.EnsureInitialized 内にインラインで
    /// 書かれていた「全ペアHostile」ロジックをここへ抽出し、Core（テスト可能）と Game（Options画面の
    /// 「全て敵対に戻す」ボタン）の両方から同じ実装を共有できるようにする。
    /// UnityEngine非依存・決定的（入力のみに依存し内部状態を持たない）。
    /// </summary>
    public static class RelationPresets
    {
        /// <summary>
        /// 0..count-1 の全ての異なる勢力ペアを Hostile に設定する。count は m の実サイズ以下であること
        /// （m のコンストラクタに渡した factionCount 以下を渡す想定）。m が null の場合は何もしない。
        /// </summary>
        public static void ApplyAllHostile(RelationMatrix m, int count)
        {
            if (m == null) return;

            for (byte i = 0; i < count; i++)
                for (byte j = (byte)(i + 1); j < count; j++)
                    m.Set(i, j, Relation.Hostile);
        }
    }
}
