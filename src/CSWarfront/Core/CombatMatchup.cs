using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// 兵科の相性（じゃんけん相性）テーブル。CombatStepが「攻撃側→目標」のダメージ倍率として参照する。
    /// 表に無い組み合わせ（未定義ペア）は 1.0（相性なし）を返す。
    ///
    /// 非対称: 例えば Tank→Infantry は 1.1（戦車は歩兵に強い）だが、Infantry→Tank は 0.4
    /// （歩兵は素で戦車に弱い。対戦車ドローン(DroneInfantry)が対戦車の主役という設計意図）。
    ///
    /// 実装: UnitCategory の要素数×要素数の2次元配列を静的コンストラクタで1.0埋めしたうえで
    /// 表にある値だけ上書きする。配列インデックスなのでO(1)・決定的（乱数不使用）。
    ///
    /// 将来の拡張ポイント: Sea/Air ユニット実装時は、ここに
    /// vs Carrier/Cruiser/Destroyer/... や vs AirSuperiority/GroundAttack/... の倍率を追加すること。
    /// 特に AntiAir は現状すべての対地カテゴリに対して0.5（対地戦には弱い）としているが、
    /// 対空ユニットの本領は対空戦であり、航空ユニット実装時には vs Air系カテゴリへ
    /// 高倍率（例: 2.0以上）を追加するのを忘れないこと。
    /// </summary>
    public static class CombatMatchup
    {
        private static readonly int CategoryCount = Enum.GetValues(typeof(UnitCategory)).Length;
        private static readonly float[,] Table;

        static CombatMatchup()
        {
            Table = new float[CategoryCount, CategoryCount];
            for (int a = 0; a < CategoryCount; a++)
                for (int t = 0; t < CategoryCount; t++)
                    Table[a, t] = 1.0f;

            // Infantry
            Set(UnitCategory.Infantry, UnitCategory.Apc, 0.6f);
            Set(UnitCategory.Infantry, UnitCategory.Tank, 0.4f);
            Set(UnitCategory.Infantry, UnitCategory.MechInfantry, 0.9f);
            Set(UnitCategory.Infantry, UnitCategory.Artillery, 1.3f);
            Set(UnitCategory.Infantry, UnitCategory.DroneInfantry, 1.2f);
            Set(UnitCategory.Infantry, UnitCategory.AntiAir, 1.2f);

            // MechInfantry
            Set(UnitCategory.MechInfantry, UnitCategory.Infantry, 1.2f);
            Set(UnitCategory.MechInfantry, UnitCategory.Apc, 0.8f);
            Set(UnitCategory.MechInfantry, UnitCategory.Tank, 0.6f);
            Set(UnitCategory.MechInfantry, UnitCategory.Artillery, 1.3f);

            // Apc
            Set(UnitCategory.Apc, UnitCategory.Infantry, 1.4f);
            Set(UnitCategory.Apc, UnitCategory.Tank, 0.5f);
            Set(UnitCategory.Apc, UnitCategory.Artillery, 1.2f);

            // Tank
            Set(UnitCategory.Tank, UnitCategory.Infantry, 1.1f);
            Set(UnitCategory.Tank, UnitCategory.MechInfantry, 1.3f);
            Set(UnitCategory.Tank, UnitCategory.Apc, 1.4f);
            Set(UnitCategory.Tank, UnitCategory.Artillery, 1.5f);
            Set(UnitCategory.Tank, UnitCategory.DroneInfantry, 1.1f);
            Set(UnitCategory.Tank, UnitCategory.AntiAir, 1.3f);

            // Artillery
            Set(UnitCategory.Artillery, UnitCategory.Infantry, 1.6f);
            Set(UnitCategory.Artillery, UnitCategory.MechInfantry, 1.4f);
            Set(UnitCategory.Artillery, UnitCategory.Apc, 1.1f);
            Set(UnitCategory.Artillery, UnitCategory.Tank, 0.7f);
            Set(UnitCategory.Artillery, UnitCategory.Artillery, 1.2f);

            // DroneInfantry（対戦車ドローン）
            Set(UnitCategory.DroneInfantry, UnitCategory.Tank, 2.0f);
            Set(UnitCategory.DroneInfantry, UnitCategory.Apc, 1.7f);
            Set(UnitCategory.DroneInfantry, UnitCategory.MechInfantry, 1.2f);
            Set(UnitCategory.DroneInfantry, UnitCategory.Infantry, 0.6f);

            // AntiAir（対地には弱い。vs Air系カテゴリは下のTask61ブロックで追加）
            Set(UnitCategory.AntiAir, UnitCategory.Infantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.MechInfantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Apc, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Tank, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Artillery, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.DroneInfantry, 0.5f);

            // --- Task61: 海上/航空戦力の相性 ---
            // 対象の「対地カテゴリ」まとめ（AirSuperiority/AntiAirのvs地上倍率をループで一括設定するため）。
            UnitCategory[] groundCategories =
            {
                UnitCategory.Tank, UnitCategory.Apc, UnitCategory.MechInfantry, UnitCategory.Artillery,
                UnitCategory.DroneInfantry, UnitCategory.Infantry, UnitCategory.AntiAir
            };
            UnitCategory[] airCategories =
            {
                UnitCategory.AirSuperiority, UnitCategory.TacticalBomber, UnitCategory.SuicideDrone
            };

            // AirSuperiority（戦闘機）: 対空に強く(2.0)、対地に弱い(0.3)。制空権の専任機という設計。
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.AirSuperiority, airCategories[i], 2.0f);
            for (int i = 0; i < groundCategories.Length; i++)
                Set(UnitCategory.AirSuperiority, groundCategories[i], 0.3f);

            // AntiAir: 対空でついに本領を発揮する(2.5)。対地は既存の0.5のまま。
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.AntiAir, airCategories[i], 2.5f);

            // TacticalBomber（爆撃機）: 対地に強く（装甲車両1.6/歩兵1.2）、対空にはほぼ無力(0.2)。
            Set(UnitCategory.TacticalBomber, UnitCategory.Tank, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.Apc, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.MechInfantry, 1.6f);
            Set(UnitCategory.TacticalBomber, UnitCategory.Infantry, 1.2f);
            for (int i = 0; i < airCategories.Length; i++)
                Set(UnitCategory.TacticalBomber, airCategories[i], 0.2f);

            // Destroyer（ミサイル駆逐艦）: 対艦・対戦車（沿岸砲撃）に強い(1.4)。それ以外は既定の1.0のまま。
            Set(UnitCategory.Destroyer, UnitCategory.Carrier, 1.4f);
            Set(UnitCategory.Destroyer, UnitCategory.Destroyer, 1.4f);
            Set(UnitCategory.Destroyer, UnitCategory.Tank, 1.4f);

            // Carrier（空母）: 打撃力より生存性のプラットフォーム。全カテゴリに対し弱い(0.6)。
            for (int t = 0; t < CategoryCount; t++)
                Table[(int)UnitCategory.Carrier, t] = 0.6f;
        }

        private static void Set(UnitCategory attacker, UnitCategory target, float multiplier)
        {
            Table[(int)attacker, (int)target] = multiplier;
        }

        /// <summary>attacker が target を攻撃するときのダメージ倍率。未定義の組み合わせは1.0。</summary>
        public static float Multiplier(UnitCategory attacker, UnitCategory target)
        {
            return Table[(int)attacker, (int)target];
        }
    }
}
