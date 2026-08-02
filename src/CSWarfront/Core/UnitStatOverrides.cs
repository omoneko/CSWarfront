using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>兵科1種ぶんの基礎値上書き（Tier1基準値。TierScalingは上書き後の値にも通常どおり
    /// かかる）。nullのフィールドは「上書きしない＝ロスター既定値のまま」。</summary>
    public struct UnitStatOverride
    {
        public float? Hp, Attack, Range, Armor, SpeedKmh, Splash, Cost, BuildTime, Accuracy, FireIntervalHours;
        public float? AmmoCombatHours; // Task99: 弾薬ゲージの連続射撃可能時間（0=弾薬無限）
    }

    /// <summary>
    /// UnitType基礎値の外部上書きの置き場（Task92、ユーザー要望「UnitType定義のXML/JSON外出し
    /// （Workshop公開後にユーザーがバランスをいじれるように）」設計書§4.3）。
    ///
    /// Game層のUnitStatsFileがMODフォルダの unit-stats.xml を読み、ロスター構築より前にここへ
    /// Setする。各ロスター（LandUnitRoster/NavalUnitRoster/AirUnitRoster）はBuild時に
    /// このクラス経由で基礎値を解決する。ファイルが無い/項目が無い場合はロスターの
    /// ハードコード既定値がそのまま使われる（＝完全に後方互換）。
    ///
    /// UnityEngine非依存・ファイルIOなし（読むのはGame層の責務）。静的状態を持つため、
    /// テストでは使用後にClear()すること。
    /// </summary>
    public static class UnitStatOverrides
    {
        private static readonly Dictionary<UnitCategory, UnitStatOverride> _map =
            new Dictionary<UnitCategory, UnitStatOverride>();

        public static void Set(UnitCategory category, UnitStatOverride o) { _map[category] = o; }
        public static void Clear() { _map.Clear(); }
        public static int Count { get { return _map.Count; } }

        public static float Hp(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Hp.HasValue ? o.Hp.Value : def; }
        public static float Attack(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Attack.HasValue ? o.Attack.Value : def; }
        public static float Range(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Range.HasValue ? o.Range.Value : def; }
        public static float Armor(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Armor.HasValue ? o.Armor.Value : def; }
        public static float SpeedKmh(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.SpeedKmh.HasValue ? o.SpeedKmh.Value : def; }
        public static float Splash(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Splash.HasValue ? o.Splash.Value : def; }
        public static float Cost(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Cost.HasValue ? o.Cost.Value : def; }
        public static float BuildTime(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.BuildTime.HasValue ? o.BuildTime.Value : def; }
        public static float Accuracy(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Accuracy.HasValue ? o.Accuracy.Value : def; }
        public static float FireInterval(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.FireIntervalHours.HasValue ? o.FireIntervalHours.Value : def; }
        public static float AmmoHours(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.AmmoCombatHours.HasValue ? o.AmmoCombatHours.Value : def; }
    }
}
