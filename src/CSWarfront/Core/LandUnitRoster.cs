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

        // Tier1基礎ステータス（design table, task-28指定値、Task38で命中率(Accuracy)列を追加、
        // Task42で発砲エフェクトの間隔(FireIntervalHours)・種別(ShotKind)列を追加）。
        // SplashはTierScalingの対象外（Tierで伸びない）。FireIntervalHoursも同様にTierScalingの対象外
        // （Tierが上がっても発砲頻度の見た目は変えない、単純さ優先の意図的な設計。Task42仕様）。
        //
        // Task38: Artilleryは射程160→120、攻撃55→50に弱体化し、命中率0.35という低さで
        //   「当たれば強いが、当たらない」砲兵にした（DroneInfantryの観測支援を受けるまでは
        //   実効ダメージが他兵科より低く抑えられる）。
        //
        // Task42: 発砲エフェクトの間隔テーブル（design指定値）。
        //   Infantry/MechInfantry/Apc/DroneInfantry/AntiAir = Gunfire（銃撃トレーサー）
        //   Tank                                            = DirectFire（直射・戦車砲）
        //   Artillery                                       = IndirectFire（曲射・放物線弾道）
        //
        // Task43: ユーザーフィードバックにより発砲間隔を全面的に延長した（Gunfireは3点バーストの
        //   バースト間隔、DirectFire/IndirectFireは単発の発射間隔として扱う。Game/CombatFxがバースト展開）。
        //   Infantry     0.08 -> 0.40
        //   MechInfantry 0.08 -> 0.40
        //   Apc          0.10 -> 0.45
        //   DroneInfantry 0.12 -> 0.50
        //   AntiAir      0.10 -> 0.45
        //   Tank         0.25 -> 0.90
        //   Artillery    0.60 -> 2.00
        // Task43: Infantryの移動速度を1.5倍（5km/h -> 7.5km/h、市民の徒歩速度基準を離れ「駆け足」寄りに）。
        //   他カテゴリの速度はこのタスクの対象外のため据え置き。
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.Infantry,      60f, 20f,  40f,  1f,  7.5f,0f, 20f, 4f, 0.75f, 0.40f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.MechInfantry,   90f, 26f,  45f,  4f, 35f,  0f, 40f, 6f, 0.75f, 0.40f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.Apc,           110f, 22f,  50f,  6f, 45f,  0f, 45f, 6f, 0.70f, 0.45f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.Tank,          140f, 40f,  60f, 10f, 40f,  0f, 60f, 8f, 0.70f, 0.90f, ShotKind.DirectFire),
            new BaseStats(UnitCategory.Artillery,      70f, 50f, 120f,  2f, 25f, 30f, 70f, 9f, 0.35f, 2.00f, ShotKind.IndirectFire),
            new BaseStats(UnitCategory.DroneInfantry,  50f, 30f,  90f,  1f, 20f,  0f, 55f, 7f, 0.85f, 0.50f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.AntiAir,        80f, 15f, 120f,  3f, 30f,  0f, 50f, 7f, 0.60f, 0.45f, ShotKind.Gunfire),
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
            // Task61: 陸上ユニットは原則として対地(Land)のみを狙える。唯一の例外はAntiAirで、
            // これが「対空戦の本領」を持つ唯一の陸上兵科になる（Land|Air）。他の陸上兵科は
            // TargetSearch/CombatStepの領域フィルタにより航空ユニットを一切狙わない
            // （CombatMatchup側の数値相性だけでなく、そもそも交戦候補にすら挙がらない）。
            DomainMask canTarget = b.Category == UnitCategory.AntiAir
                ? DomainMask.Land | DomainMask.Air
                : DomainMask.Land;

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
                "",
                TierScaling.Accuracy(b.Accuracy, tier),
                b.FireIntervalHours,
                b.ShotKind,
                canTarget);
        }
    }
}
