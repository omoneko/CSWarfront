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

        public UnitType(string typeKey, Domain domain, UnitCategory category, byte tier,
            float maxHp, float attack, float range, float armor, float speed,
            float splashRadius, float cost, float buildTime, string assetPrefabName)
        {
            TypeKey = typeKey; Domain = domain; Category = category; Tier = tier;
            MaxHP = maxHp; Attack = attack; Range = range; Armor = armor; Speed = speed;
            SplashRadius = splashRadius; Cost = cost; BuildTime = buildTime;
            AssetPrefabName = assetPrefabName ?? "";
        }
    }

    /// <summary>MVPの既定ユニット定義（後日XML外出しに置換予定）。
    /// 全レートはゲーム内時間基準（Task21）: Attack=ダメージ/ゲーム内時間、Speed=マップ距離/ゲーム内時間、
    /// BuildTime=ゲーム内時間。
    /// Speedはkm/h基準で意図し、SpeedCalibration.UnitsPerGameHourFromKmhで変換する（Task26）。</summary>
    public static class MvpUnitTypes
    {
        /// <summary>「遅い車」相当（ユーザー指定：戦車はスポーツカーではない）＝40km/h基準。</summary>
        public static UnitType Tank_T1()
        {
            return new UnitType("Tank_T1", Domain.Land, UnitCategory.Tank, 1,
                100f, 40f, 60f, 5f, SpeedCalibration.UnitsPerGameHourFromKmh(40f), 0f, 50f, 8f, "");
        }

        /// <summary>市民の徒歩速度相当＝5km/h基準（今後の歩兵実装のためのテンプレート、Task26）。
        /// 注意: まだMilitaryManagerには未登録（ProductionPlanning.MvpUnitKeyは引き続き"Tank_T1"）。</summary>
        public static UnitType Infantry_T1()
        {
            return new UnitType("Infantry_T1", Domain.Land, UnitCategory.Infantry, 1,
                60f, 20f, 40f, 1f, SpeedCalibration.UnitsPerGameHourFromKmh(5f), 0f, 20f, 4f, "");
        }
    }
}
