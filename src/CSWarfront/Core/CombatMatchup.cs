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

            // AntiAir（対地には弱い。vs Air系カテゴリは航空ユニット実装時に追加すること）
            Set(UnitCategory.AntiAir, UnitCategory.Infantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.MechInfantry, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Apc, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Tank, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.Artillery, 0.5f);
            Set(UnitCategory.AntiAir, UnitCategory.DroneInfantry, 0.5f);
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
