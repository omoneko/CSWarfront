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

        /// <summary>発砲したユニットのUnitInstance.InstanceId（Task43）。Game層がFrom側の
        /// 発射高さ（モデル中央）を求めるために使う。0という値は使われない
        /// （UnitInstance.InstanceIdはWarState.AllocInstanceIdが1から払い出す）。</summary>
        public uint AttackerId;

        /// <summary>着弾先のUnitInstance.InstanceId（Task43）。ユニット同士の交戦（CombatStep）では
        /// 標的ユニットのInstanceId、基地攻め（BaseCombatStep）では基地には論理ユニットIDが無いため0。
        /// Game層はTargetId==0を「基地（または不明な対象）」として扱い、ユニットより大きい既定の
        /// 着弾高さを使う。</summary>
        public uint TargetId;

        /// <summary>発砲したユニットの兵科（Task51、兵科別射撃音）。Game層（WarfrontSounds.ShotSoundFor）が
        /// これを見て銃撃/重機関銃/砲撃/対空ミサイルのどの音を鳴らすかを選ぶ。ダメージ計算・命中判定には
        /// 一切使わない（ShotKindと同じく見た目・音専用の付随データ）。</summary>
        public UnitCategory Category;

        public ShotEvent(WorldPos from, WorldPos to, ShotKind kind, byte factionId, uint attackerId, uint targetId,
            UnitCategory category)
        {
            From = from; To = to; Kind = kind; FactionId = factionId;
            AttackerId = attackerId; TargetId = targetId; Category = category;
        }
    }
}
