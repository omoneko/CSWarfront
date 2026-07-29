namespace CSWarfront.Core
{
    /// <summary>
    /// ユニットが撃破された瞬間を表す軽量イベント（Task51、兵科別射撃音・撃破音）。ShotEventと同じ
    /// 設計思想: ダメージ計算そのものには一切影響しない、間引かれた表現専用のトランジェント・イベント。
    ///
    /// CombatStep.Advanceの死亡判定ループ（ユニットがUnitState.Deadへ遷移する、まさにその箇所）で
    /// ちょうど1件だけ積まれる。State != Dead を遷移条件にしているため、同一ユニットについて同tick内で
    /// 複数回積まれることはない（決定的）。
    ///
    /// WarState.RecentKillsに積まれ、Game層（MilitaryManager.OnMainVisualUpdate）が毎フレーム
    /// ロック内でコピーして消費する。非永続化（WarStateSerializerには一切書き出さない）。
    /// </summary>
    public struct KillEvent
    {
        /// <summary>撃破されたユニットの最終位置（撃破音を再生する位置）。</summary>
        public WorldPos Position;

        /// <summary>撃破されたユニット（victim）の所属勢力ID。</summary>
        public byte FactionId;

        /// <summary>撃破されたユニット（victim）の兵科（Task53、歩兵系の撃破音オミット）。
        /// Game層（CombatFx.SpawnKillSounds）がこれを見て、歩兵（Infantry/DroneInfantry）の
        /// 撃破では「車両撃破時」の爆発音を鳴らさないよう間引く。ダメージ計算・命中判定には
        /// 一切使わない（ShotEvent.Categoryと同じく見た目・音専用の付随データ）。
        /// UnitTypeが見つからない防御的ケースではdefault(UnitCategory)（Tank=0）が入る
        /// （その場合は撃破音を鳴らす側＝安全側のフォールバック）。</summary>
        public UnitCategory Category;

        public KillEvent(WorldPos position, byte factionId, UnitCategory category)
        {
            Position = position;
            FactionId = factionId;
            Category = category;
        }
    }
}
