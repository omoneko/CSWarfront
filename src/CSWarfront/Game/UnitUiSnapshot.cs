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
        public UnitState State;
        public uint? TargetId;
        /// <summary>Path内の次要素番号。Path未設定（直線移動フォールバック）なら0。</summary>
        public int PathIndex;
        /// <summary>Pathの要素数。Path未設定なら0（UIはこれで「直進」表示に切り替える）。</summary>
        public int PathCount;
    }

    /// <summary>
    /// UnitUiSnapshot の組み立てロジック（Task31）。MilitaryManager.TryGetUnitSnapshot の _stateLock 内
    /// から呼ばれる想定 — 呼び出し側がロックを保持していること（このクラス自体はロックしない）。
    /// MilitaryManager.cs の500行制限のため分離（BaseUiSnapshotBuilderと同じ理由、Task30踏襲）。
    /// </summary>
    internal static class UnitUiSnapshotBuilder
    {
        /// <summary>type が null（型未登録等の異常系）でも例外を投げず0埋めで返す。</summary>
        public static UnitUiSnapshot Build(UnitInstance unit, UnitType type)
        {
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
                SpeedKmh = type != null ? SpeedCalibration.KmhFromUnitsPerGameHour(type.Speed) : 0f,
                State = unit.State,
                TargetId = unit.TargetId,
                PathIndex = unit.PathIndex,
                PathCount = unit.Path != null ? unit.Path.Count : 0
            };
        }
    }
}
