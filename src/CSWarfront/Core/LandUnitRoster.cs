using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 陸上7兵種（Tank/Apc/MechInfantry/Artillery/DroneInfantry/Infantry/AntiAir）× Tier1〜5、
    /// 計35種のUnitType定義（Task28）。Sea/Airロスターは移動方式が異なるため別途用意する（本タスク対象外）。
    ///
    /// 各カテゴリのTier1基礎ステータスのみをここで保持し、Tier2以降はTierScalingで機械的に導出する
    /// （Tierごとに個別の値を書き並べると将来の調整でズレる／矛盾する恐れがあるため、単一の真実源＝
    /// このテーブル＋TierScaling、という構成にしている）。
    ///
    /// キー形式は "&lt;Category&gt;_T&lt;tier&gt;"（例: "Tank_T3"）で固定する。既存セーブ/テストが
    /// 参照する "Tank_T1" と衝突しないよう、UnitCategory.ToString() の綴りを直接使う。
    /// </summary>
    public static class LandUnitRoster
    {
        private struct BaseStats
        {
            public readonly UnitCategory Category;
            public readonly float Hp, Attack, Range, Armor, SpeedKmh, Splash, Cost, BuildTime;

            public BaseStats(UnitCategory category, float hp, float attack, float range, float armor,
                float speedKmh, float splash, float cost, float buildTime)
            {
                Category = category; Hp = hp; Attack = attack; Range = range; Armor = armor;
                SpeedKmh = speedKmh; Splash = splash; Cost = cost; BuildTime = buildTime;
            }
        }

        // Tier1基礎ステータス（design table, task-28指定値）。SplashはTierScalingの対象外（Tierで伸びない）。
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.Infantry,      60f, 20f,  40f,  1f,  5f,  0f, 20f, 4f),
            new BaseStats(UnitCategory.MechInfantry,   90f, 26f,  45f,  4f, 35f,  0f, 40f, 6f),
            new BaseStats(UnitCategory.Apc,           110f, 22f,  50f,  6f, 45f,  0f, 45f, 6f),
            new BaseStats(UnitCategory.Tank,          140f, 40f,  60f, 10f, 40f,  0f, 60f, 8f),
            new BaseStats(UnitCategory.Artillery,      70f, 55f, 160f,  2f, 25f, 30f, 70f, 9f),
            new BaseStats(UnitCategory.DroneInfantry,  50f, 30f,  90f,  1f, 20f,  0f, 55f, 7f),
            new BaseStats(UnitCategory.AntiAir,        80f, 15f, 120f,  3f, 30f,  0f, 50f, 7f),
        };

        /// <summary>"&lt;Category&gt;_T&lt;tier&gt;" 形式のキーを組み立てる（例: Tank, 3 -&gt; "Tank_T3"）。</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>7カテゴリ×Tier1〜5、計35件のUnitTypeを生成する。</summary>
        public static IEnumerable<UnitType> All()
        {
            for (int i = 0; i < Bases.Length; i++)
            {
                for (byte tier = 1; tier <= 5; tier++)
                    yield return Build(Bases[i], tier);
            }
        }

        /// <summary>指定カテゴリ・Tierの1件を生成する。カテゴリがロスターに無い場合はnull。
        /// MvpUnitTypesの後方互換ラッパーからも使われる。</summary>
        public static UnitType Get(UnitCategory category, byte tier)
        {
            for (int i = 0; i < Bases.Length; i++)
                if (Bases[i].Category == category) return Build(Bases[i], tier);
            return null;
        }

        /// <summary>All()の35件を丸ごと登録する。</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Land, b.Category, tier,
                TierScaling.Hp(b.Hp, tier),
                TierScaling.Attack(b.Attack, tier),
                TierScaling.Range(b.Range, tier),
                TierScaling.Armor(b.Armor, tier),
                SpeedCalibration.UnitsPerGameHourFromKmh(TierScaling.SpeedKmh(b.SpeedKmh, tier)),
                b.Splash,
                TierScaling.Cost(b.Cost, tier),
                TierScaling.BuildTime(b.BuildTime, tier),
                "");
        }
    }
}
