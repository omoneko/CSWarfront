using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 海上戦力2種（Destroyer=ミサイル駆逐艦 / Carrier=空母）× Tier1〜5、計10種のUnitType定義（Task61）。
    /// LandUnitRosterと全く同じ形（Tier1基礎ステータス表＋TierScalingで機械的にTier2以降を導出、
    /// キー形式"&lt;Category&gt;_T&lt;tier&gt;"）だが、Domain=Seaであること、速度が桁違いに速いこと、
    /// 移動方式そのものが異なる（陸上のRoadGraph/CoverMapを一切使わず、MovementStepのSea分岐と
    /// WarState.IWaterSamplerで水面のみを移動する）点が違う。
    ///
    /// ユーザー要望によりスコープを絞り、UnitCategoryに存在する他の海上カテゴリ
    /// （Cruiser/Frigate/Minelayer/Minesweeper/Submarine/FastBoat/SuicideBoat/SeaDrone）は
    /// このロスターに定義せず、実装対象外のまま（UnitTypeRegistryに登録されないため選択できない）。
    ///
    /// Tier1基礎ステータス（design table, task-61指定値）:
    ///   Destroyer: HP260 Attack70 Range220 Armor14 Speed55km/h Splash20 Accuracy0.70 Cost180 BuildTime16
    ///              IndirectFire/1.2h（艦対艦ミサイルの曲射表現）
    ///   Carrier:   HP520 Attack30 Range120 Armor20 Speed45km/h Splash0  Accuracy0.60 Cost400 BuildTime30
    ///              DirectFire/2.0h（艦載火器の直射、空母自体は打撃力より耐久のプラットフォーム）
    ///
    /// 対象領域（CanTargetDomains、Task61）: 艦艇はLand|Sea（沿岸目標・他艦を攻撃できるが対空能力は
    /// 持たない）。CombatMatchup側でもCarrierは「打撃力より生存性のプラットフォーム」という設計意図から
    /// 全カテゴリに対し0.6の低倍率にしてある（CombatMatchup.cs参照）。
    /// </summary>
    public static class NavalUnitRoster
    {
        private struct BaseStats
        {
            public readonly UnitCategory Category;
            public readonly float Hp, Attack, Range, Armor, SpeedKmh, Splash, Cost, BuildTime, Accuracy;
            public readonly float FireIntervalHours;
            public readonly ShotKind ShotKind;

            public BaseStats(UnitCategory category, float hp, float attack, float range, float armor,
                float speedKmh, float splash, float cost, float buildTime, float accuracy,
                float fireIntervalHours, ShotKind shotKind)
            {
                Category = category; Hp = hp; Attack = attack; Range = range; Armor = armor;
                SpeedKmh = speedKmh; Splash = splash; Cost = cost; BuildTime = buildTime;
                Accuracy = accuracy;
                FireIntervalHours = fireIntervalHours; ShotKind = shotKind;
            }
        }

        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.Destroyer, 260f, 70f, 220f, 14f, 55f, 20f, 180f, 16f, 0.70f, 1.2f, ShotKind.IndirectFire),
            new BaseStats(UnitCategory.Carrier,   520f, 30f, 120f, 20f, 45f,  0f, 400f, 30f, 0.60f, 2.0f, ShotKind.DirectFire),
        };

        /// <summary>"&lt;Category&gt;_T&lt;tier&gt;" 形式のキーを組み立てる（LandUnitRoster.TypeKeyと同じ形式）。</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>2カテゴリ×Tier1〜5、計10件のUnitTypeを生成する。</summary>
        public static IEnumerable<UnitType> All()
        {
            for (int i = 0; i < Bases.Length; i++)
                for (byte tier = 1; tier <= 5; tier++)
                    yield return Build(Bases[i], tier);
        }

        /// <summary>指定カテゴリ・Tierの1件を生成する。ロスターに無いカテゴリならnull。</summary>
        public static UnitType Get(UnitCategory category, byte tier)
        {
            for (int i = 0; i < Bases.Length; i++)
                if (Bases[i].Category == category) return Build(Bases[i], tier);
            return null;
        }

        /// <summary>All()の10件を丸ごと登録する。</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Sea, b.Category, tier,
                TierScaling.Hp(b.Hp, tier),
                TierScaling.Attack(b.Attack, tier),
                TierScaling.Range(b.Range, tier),
                TierScaling.Armor(b.Armor, tier),
                SpeedCalibration.UnitsPerGameHourFromKmh(TierScaling.SpeedKmh(b.SpeedKmh, tier)),
                b.Splash,
                TierScaling.Cost(b.Cost, tier),
                TierScaling.BuildTime(b.BuildTime, tier),
                "",
                TierScaling.Accuracy(b.Accuracy, tier),
                b.FireIntervalHours,
                b.ShotKind,
                DomainMask.Land | DomainMask.Sea); // 艦艇は対地・対艦のみ。対空能力は持たない（Task61）。
        }
    }
}
