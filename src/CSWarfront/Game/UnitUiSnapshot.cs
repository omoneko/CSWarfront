using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// ユニット情報パネル（Game/UI/UnitInfoPanel）向けの読み取り専用スナップショット（Task31）。
    /// UIが WarState / UnitInstance / UnitType へ直接触れずに済むよう、MilitaryManager.TryGetUnitSnapshot
    /// が _stateLock 内で値をコピーして渡す。BaseUiSnapshot（Game/BaseUiSnapshot.cs、Task25/30）と
    /// 同じパターンを踏襲する。
    /// </summary>
    public struct UnitUiSnapshot
    {
        public string TypeKey;
        public byte Tier;
        public byte FactionId;
        public float CurrentHP;
        public float MaxHP;
        public float Attack;
        public float Range;
        public float Armor;
        /// <summary>UnitType.Speed（マップ距離/ゲーム内時間）をSpeedCalibration.KmhFromUnitsPerGameHourで
        /// km/hへ逆変換した値（Task26の較正定数を利用、表示専用）。</summary>
        public float SpeedKmh;
        /// <summary>実効命中率（Task38）。UnitType.Accuracyそのものではなく、CombatSynergy.AccuracyFor
        /// を通した「ドローン観測支援バフ適用後」の値。プレイヤーがドローン観測支援の効果を
        /// UI上で確認できるようにするため。</summary>
        public float Accuracy;
        /// <summary>Accuracy が CombatSynergy（ドローン観測支援）によって素の値から引き上げられているか
        /// （Task38）。UnitInfoPanelが「命中: 85%（観測支援）」の注記を出すかどうかの判定に使う。</summary>
        public bool AccuracyBoosted;
        public UnitState State;
        public uint? TargetId;
        /// <summary>Path内の次要素番号。Path未設定（直線移動フォールバック）なら0。</summary>
        public int PathIndex;
        /// <summary>Pathの要素数。Path未設定なら0（UIはこれで「直進」表示に切り替える）。</summary>
        public int PathCount;
        /// <summary>プレイヤーの指揮コマンド（Task48）。UnitInfoPanelが「自由進撃/停止/集結待機/AI」の
        /// いずれかとして表示する。</summary>
        public UnitOrder Order;

        /// <summary>Task99: 弾薬ゲージ（0..1）。HasAmmoGauge=falseなら表示しない（弾薬無限の兵科）。</summary>
        public float Ammo;
        public bool HasAmmoGauge;

        /// <summary>Task99: 補給トラックの積載量（0..1）。IsSupplyTruck=trueのときのみ表示する。</summary>
        public float SupplyLoad;
        public bool IsSupplyTruck;
    }

    /// <summary>
    /// UnitUiSnapshot の組み立てロジック（Task31）。MilitaryManager.TryGetUnitSnapshot の _stateLock 内
    /// から呼ばれる想定 — 呼び出し側がロックを保持していること（このクラス自体はロックしない）。
    /// MilitaryManager.cs の500行制限のため分離（BaseUiSnapshotBuilderと同じ理由、Task30踏襲）。
    /// </summary>
    internal static class UnitUiSnapshotBuilder
    {
        /// <summary>type が null（型未登録等の異常系）でも例外を投げず0埋めで返す。
        /// state は Accuracy（Task38、CombatSynergy.AccuracyFor経由の実効命中率）の算出に使う。</summary>
        public static UnitUiSnapshot Build(WarState state, UnitInstance unit, UnitType type)
        {
            float effectiveAccuracy = type != null ? CombatSynergy.AccuracyFor(state, unit, type) : 0f;
            return new UnitUiSnapshot
            {
                TypeKey = unit.TypeKey,
                Tier = type != null ? type.Tier : (byte)0,
                FactionId = unit.FactionId,
                CurrentHP = unit.CurrentHP,
                MaxHP = type != null ? type.MaxHP : 0f,
                Attack = type != null ? type.Attack : 0f,
                Range = type != null ? type.Range : 0f,
                Armor = type != null ? type.Armor : 0f,
                // Task83: 実効速度（全体倍率込み）を表示する。type.Speed単体の表示だと実際の移動と食い違う。
                SpeedKmh = type != null
                    ? SpeedCalibration.KmhFromUnitsPerGameHour(type.Speed * MovementStep.GlobalSpeedMultiplier)
                    : 0f,
                Accuracy = effectiveAccuracy,
                // 素の命中率(type.Accuracy)より高ければ、CombatSynergy(ドローン観測支援)が効いている
                // ことを意味する（AccuracyForは非該当の場合、type.Accuracyをそのまま返す規約のため）。
                AccuracyBoosted = type != null && effectiveAccuracy > type.Accuracy,
                State = unit.State,
                TargetId = unit.TargetId,
                PathIndex = unit.PathIndex,
                PathCount = unit.Path != null ? unit.Path.Count : 0,
                Order = unit.Order,
                Ammo = unit.Ammo, // Task99
                HasAmmoGauge = type != null && type.AmmoCombatHours > 0f
                    && unit.FactionId != Faction.InvaderFactionId,
                SupplyLoad = unit.SupplyLoad,
                IsSupplyTruck = type != null && type.Category == UnitCategory.SupplyTruck
            };
        }
    }
}
