namespace CSWarfront.Core
{
    /// <summary>データ駆動のユニット定義（1種別×1Tier）。実行時は不変。</summary>
    public class UnitType
    {
        public string TypeKey { get; private set; }
        public Domain Domain { get; private set; }
        public UnitCategory Category { get; private set; }
        public byte Tier { get; private set; }
        public float MaxHP { get; private set; }
        public float Attack { get; private set; }
        public float Range { get; private set; }
        public float Armor { get; private set; }
        public float Speed { get; private set; }
        public float SplashRadius { get; private set; }
        public float Cost { get; private set; }
        public float BuildTime { get; private set; }
        public string AssetPrefabName { get; private set; }

        /// <summary>命中率（0..1、Task38）。CombatStep/BaseCombatStepがダメージへ乗じる
        /// 期待値ベースの乗数であり、ランダムな命中/ミスの抽選ではない（本プロジェクトは決定的シミュレーション
        /// が前提のため）。「命中率40%」は「1tickごとに0.4倍のダメージが確実に入る」という意味で、
        /// 十分な時間をかけた場合の統計的な結果は乱数抽選と同じだが、連続ミス/連続命中という不公平な
        /// 「運」の要素を排除できる。</summary>
        public float Accuracy { get; private set; }

        /// <summary>発砲エフェクトの間引き間隔（ゲーム内時間、Task42）。ダメージは毎tick連続的に適用されるが
        /// 見た目（ShotEvent）はこの間隔ごとに最大1回しか出さない（UnitInstance.FireCooldownで管理）。
        /// TierScalingの対象外＝Tierを上げても発砲頻度の見た目は変わらない（単純さ優先の意図的な設計）。</summary>
        public float FireIntervalHours { get; private set; }

        /// <summary>発砲エフェクトの種別（Task42）。Game層のCombatFxが銃撃/直射/曲射のどれを描くか選ぶ。</summary>
        public ShotKind ShotKind { get; private set; }

        /// <summary>このユニットが攻撃対象にできる領域（Task61: 海上/航空戦力の追加に伴う対空判定の実質化）。
        /// TargetSearch/CombatStepが、射程・敵対関係に加えてこのマスクで候補を絞り込む。
        /// 陸上ユニットは既定でLandのみ（AntiAirだけLand|Air）、海上ユニットはLand|Sea、
        /// 航空ユニットはAll（Land|Sea|Air）。既定コンストラクタ引数は無く、全ロスターが明示的に指定する。</summary>
        public DomainMask CanTargetDomains { get; private set; }

        // Task79: 旧IsOneShotフラグ（「1回攻撃した瞬間に自壊する」、自爆ドローン専用）は撤廃した。
        // 自爆ドローンはもはや「攻撃してから自壊する」のではなく、そもそも通常の射撃パイプライン
        // （CombatStep/BaseCombatStep/ThreatCombatStep）に一切乗らず、専用のKamikazeStepが
        // 突進・体当たり起爆を完結して扱う（UnitCategoryFlags.IsKamikaze参照）。射撃系3ステップは
        // type.Category.IsKamikaze()を見て早期continueするため、このフラグを見る分岐はもう存在しない
        // （旧: CombatStep.cs/BaseCombatStep.csのif(type.IsOneShot)分岐、Task61）。

        /// <summary>Task99: 弾薬ゲージの「連続射撃可能時間」（ゲーム内時間）。射撃している間だけ
        /// UnitInstance.Ammo が dt/AmmoCombatHours ずつ減る（AmmoRules）。0=弾薬無限（弾薬制の
        /// 対象外。空母・自爆ドローン等）。既定0のオプション引数のため既存の呼び出し元は不変。</summary>
        public float AmmoCombatHours { get; private set; }

        public UnitType(string typeKey, Domain domain, UnitCategory category, byte tier,
            float maxHp, float attack, float range, float armor, float speed,
            float splashRadius, float cost, float buildTime, string assetPrefabName,
            float accuracy, float fireIntervalHours, ShotKind shotKind,
            DomainMask canTargetDomains, float ammoCombatHours = 0f)
        {
            TypeKey = typeKey; Domain = domain; Category = category; Tier = tier;
            MaxHP = maxHp; Attack = attack; Range = range; Armor = armor; Speed = speed;
            SplashRadius = splashRadius; Cost = cost; BuildTime = buildTime;
            AssetPrefabName = assetPrefabName ?? "";
            Accuracy = accuracy;
            FireIntervalHours = fireIntervalHours;
            ShotKind = shotKind;
            CanTargetDomains = canTargetDomains;
            AmmoCombatHours = ammoCombatHours;
        }
    }

    /// <summary>
    /// 後方互換の薄いラッパー（Task28）。実体はLandUnitRoster（陸上7兵種×Tier1〜5）に置き換わった。
    /// Tank_T1/Infantry_T1というキー・呼び出し形は既存のセーブ/テスト/診断ログ（
    /// Game/SpeedCalibrationDiagnostics.cs）が参照し続けるため残してあるが、値そのものは
    /// LandUnitRosterのTier1基礎ステータス表が真実源であり、ここでは重複定義しない。
    /// 新規コードはLandUnitRoster.RegisterAll / LandUnitRoster.Get を直接使うこと。
    /// </summary>
    public static class MvpUnitTypes
    {
        public static UnitType Tank_T1() { return LandUnitRoster.Get(UnitCategory.Tank, 1); }
        public static UnitType Infantry_T1() { return LandUnitRoster.Get(UnitCategory.Infantry, 1); }
    }
}
