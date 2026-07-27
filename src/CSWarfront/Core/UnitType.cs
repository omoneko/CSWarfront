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

        /// <summary>命中率（0..1、Task38）。CombatStep/BaseCombatStep/BaseDefenseStepがダメージへ乗じる
        /// 期待値ベースの乗数であり、ランダムな命中/ミスの抽選ではない（本プロジェクトは決定的シミュレーション
        /// が前提のため）。「命中率40%」は「1tickごとに0.4倍のダメージが確実に入る」という意味で、
        /// 十分な時間をかけた場合の統計的な結果は乱数抽選と同じだが、連続ミス/連続命中という不公平な
        /// 「運」の要素を排除できる。</summary>
        public float Accuracy { get; private set; }

        public UnitType(string typeKey, Domain domain, UnitCategory category, byte tier,
            float maxHp, float attack, float range, float armor, float speed,
            float splashRadius, float cost, float buildTime, string assetPrefabName,
            float accuracy)
        {
            TypeKey = typeKey; Domain = domain; Category = category; Tier = tier;
            MaxHP = maxHp; Attack = attack; Range = range; Armor = armor; Speed = speed;
            SplashRadius = splashRadius; Cost = cost; BuildTime = buildTime;
            AssetPrefabName = assetPrefabName ?? "";
            Accuracy = accuracy;
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
