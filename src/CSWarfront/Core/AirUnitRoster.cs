using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 航空戦力3種（AirSuperiority=戦闘機 / TacticalBomber=爆撃機 / SuicideDrone=自爆ドローン）×
    /// Tier1〜5、計15種のUnitType定義（Task61）。LandUnitRoster/NavalUnitRosterと同じ形。
    ///
    /// ユーザー要望によりスコープを絞り、UnitCategoryに存在する他の航空カテゴリ
    /// （GroundAttack/StrategicBomber/ElectronicWarfare/Awacs）はこのロスターに定義せず、
    /// 実装対象外のまま（UnitTypeRegistryに登録されないため選択できない）。
    ///
    /// Tier1基礎ステータス（design table, task-61指定値）:
    ///   AirSuperiority(戦闘機): HP120 Attack85  Range90 Armor4 Speed900km/h Splash0  Accuracy0.80
    ///                           Cost200 BuildTime14 Gunfire/0.30h
    ///   TacticalBomber(爆撃機): HP180 Attack120 Range70 Armor6 Speed650km/h Splash45 Accuracy0.65
    ///                           Cost260 BuildTime18 DirectFire/1.0h
    ///   SuicideDrone(自爆ドローン): HP40 Attack260 Range20 Armor0 Speed420km/h Splash35 Accuracy0.90
    ///                           Cost90 BuildTime6 DirectFire/0.5h、IsOneShot=true（後述）
    ///
    /// 対象領域（CanTargetDomains、Task61）: 航空ユニットは全てAll（Land|Sea|Air）——
    /// 「Air can hit everything」という仕様どおり、地上・海上・航空のいずれも交戦候補になり得る
    /// （実際の有効打はCombatMatchupの相性倍率で表現する。例: TacticalBomberはvs Air 0.2で事実上無力）。
    ///
    /// SuicideDrone（Task79再設計）: もはや「射撃してから自壊」ではなく、KamikazeStep（Core）が
    /// 専用の交戦フロー（射程内で目標をロック→MovementStepのダイブ移動で直接突入→DetonateDistance
    /// 以内でAttackを1回フル適用して自壊）を完結して扱う。CombatStep/BaseCombatStep/ThreatCombatStepは
    /// UnitCategoryFlags.IsKamikaze()を見て早期continueするため、この3種のうちSuicideDroneだけが
    /// 通常の射撃パイプライン（ShotEvent/dtスケールダメージ）に一切乗らない。Cost=90は変わらず
    /// 「1回分の使い捨てコスト」という設計のまま（旧UnitType.IsOneShotフラグは撤廃済み、
    /// KamikazeStep.cs/MovementStep.cs参照）。
    /// </summary>
    public static class AirUnitRoster
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

        // Task91（相対レートの現実寄り再較正）: 戦闘機は機関砲バーストを高頻度で（85/0.25h、
        // 1発あたり21）、爆撃機は1航過に1発の重い爆弾（125/1.5h、1発あたり187）という対比にした。
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.AirSuperiority, 120f, 85f,  90f, 4f, 900f,  0f, 200f, 14f, 0.80f, 0.25f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.TacticalBomber,  180f, 125f, 70f, 6f, 650f, 45f, 260f, 18f, 0.65f, 1.50f, ShotKind.DirectFire),
            // SuicideDrone: ShotKind/FireIntervalHoursはKamikazeStepでは参照されない（ShotEventを
            // 一切発行しないため）。旧ロスターの値をそのまま残してあるのは、他の2種と同じBaseStats
            // 形状を保つため（このカテゴリでは単に無視されるだけで実害は無い）。
            new BaseStats(UnitCategory.SuicideDrone,     40f, 260f, 20f, 0f, 420f, 35f,  90f,  6f, 0.90f, 0.50f, ShotKind.DirectFire),
            // Task101: 攻撃ヘリ（地上専任・ホバリング型＝レーストラック航過なし・低空60m）と
            // 輸送ヘリ（非武装の兵站機、TransportHeliStepが運用。生産キューには乗らない自動維持）。
            new BaseStats(UnitCategory.AttackHelicopter, 90f,  45f, 100f, 3f, 220f,  0f, 220f, 12f, 0.80f, 0.50f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.TransportHelicopter, 60f, 0f,  0f, 1f, 220f,  0f,  80f,  6f, 0f,    1f,    ShotKind.Gunfire),
        };

        /// <summary>"&lt;Category&gt;_T&lt;tier&gt;" 形式のキーを組み立てる（LandUnitRoster.TypeKeyと同じ形式）。</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>3カテゴリ×Tier1〜5、計15件のUnitTypeを生成する。</summary>
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

        /// <summary>All()の15件を丸ごと登録する。</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            // Task92: 基礎値はUnitStatOverrides（unit-stats.xml）で上書きできる（LandUnitRosterと同じ）。
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Air, b.Category, tier,
                TierScaling.Hp(UnitStatOverrides.Hp(b.Category, b.Hp), tier),
                TierScaling.Attack(UnitStatOverrides.Attack(b.Category, b.Attack), tier),
                TierScaling.Range(UnitStatOverrides.Range(b.Category, b.Range), tier),
                TierScaling.Armor(UnitStatOverrides.Armor(b.Category, b.Armor), tier),
                SpeedCalibration.UnitsPerGameHourFromKmh(TierScaling.SpeedKmh(UnitStatOverrides.SpeedKmh(b.Category, b.SpeedKmh), tier)),
                UnitStatOverrides.Splash(b.Category, b.Splash),
                TierScaling.Cost(UnitStatOverrides.Cost(b.Category, b.Cost), tier),
                TierScaling.BuildTime(UnitStatOverrides.BuildTime(b.Category, b.BuildTime), tier),
                "",
                TierScaling.Accuracy(UnitStatOverrides.Accuracy(b.Category, b.Accuracy), tier),
                UnitStatOverrides.FireInterval(b.Category, b.FireIntervalHours),
                b.ShotKind,
                TargetDomainsFor(b.Category),
                UnitStatOverrides.AmmoHours(b.Category, AmmoRules.DefaultCombatHours(b.Category))); // Task99
        }

        /// <summary>Task85（旧Task61の「Air can hit everything」を廃止）: 兵科ごとの標的ドメイン。
        ///  - 戦闘機: 航空のみ（戦闘機・爆撃機・自爆ドローンとの空戦専任。地上・海上は撃てない）。
        ///  - 爆撃機: 地上・海上（Task88: 当初は地上のみだったが、実機で「敵海上ユニットを無視する」と
        ///    ユーザーが指摘したため対艦爆撃を許可。航空だけは引き続き撃てない）。
        ///  - 自爆ドローン: 全領域（特攻兵器という位置づけのまま。ユーザー指定に含まれない兵科のため
        ///    従来のAllを維持する）。
        /// 拠点・KAIJUへの攻撃可否はTargetingRules（BaseCombatStep/ThreatCombatStep側）が担う。</summary>
        private static DomainMask TargetDomainsFor(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.AirSuperiority: return DomainMask.Air;
                case UnitCategory.TacticalBomber: return DomainMask.Land | DomainMask.Sea;
                case UnitCategory.AttackHelicopter: return DomainMask.Land; // Task101: 地上専任（ヘリ同士・固定翼・艦船は不可）
                case UnitCategory.TransportHelicopter: return DomainMask.None; // Task101: 非武装
                default: return DomainMask.All;
            }
        }
    }
}
