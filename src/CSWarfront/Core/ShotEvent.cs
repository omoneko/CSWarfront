namespace CSWarfront.Core
{
    /// <summary>発砲の見た目の種別（Task42）。Game層がこれを見てトレーサー（銃撃）/直射（戦車）/
    /// 曲射（砲兵の放物線弾道）のどれを描くかを選ぶ。</summary>
    public enum ShotKind { Gunfire, DirectFire, IndirectFire }

    /// <summary>
    /// 1回分の「見える発砲」を表す軽量イベント（Task42）。
    ///
    /// 設計上の前提: ダメージは毎simtick（実時間で秒間60回程度）、経過ゲーム内時間(dt)に比例する
    /// 連続的な期待値として適用される（CombatStep/BaseCombatStep参照）。ダメージ適用のたびに1つ
    /// エフェクトを出すと発砲が洪水のようになってしまうため、ShotEventはダメージ計算そのものとは
    /// 別に「間引かれた表現専用のイベント」として扱う。ダメージ計算のロジック・数値には一切影響しない。
    ///
    /// UnitInstance.FireCooldown（攻撃側1体ごとのアキュムレータ、乱数不使用）で間引かれるため、
    /// 1体の攻撃ユニットにつき、そのUnitType.FireIntervalHoursごとに最大1件しか積まれない
    /// （決定的シミュレーションの前提を崩さない）。
    ///
    /// WarState.RecentShotsに積まれ、Game層（MilitaryManager.OnMainVisualUpdate）が毎フレーム
    /// ロック内でコピーして消費する。非永続化（WarStateSerializerには一切書き出さない）。
    /// </summary>
    public struct ShotEvent
    {
        public WorldPos From;
        public WorldPos To;
        public ShotKind Kind;
        public byte FactionId;

        public ShotEvent(WorldPos from, WorldPos to, ShotKind kind, byte factionId)
        {
            From = from; To = to; Kind = kind; FactionId = factionId;
        }
    }
}
