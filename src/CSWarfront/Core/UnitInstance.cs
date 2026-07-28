using System.Collections.Generic;

namespace CSWarfront.Core
{
    public enum UnitState { Idle, Moving, Engaging, Dead }

    /// <summary>実行時の1体。表現(車両ID)はGame層が別に保持し、ここには論理状態のみ。</summary>
    public class UnitInstance
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public float CurrentHP;
        public WorldPos Position;
        public UnitState State;
        public uint? TargetId;
        public WorldPos? OrderTargetPos;

        /// <summary>道路経路の残り（実行時のみ・非永続化）。null/空なら直線移動へフォールバック。</summary>
        public List<WorldPos> Path;
        /// <summary>次に目指す Path の要素番号。</summary>
        public int PathIndex;
        /// <summary>この経路が向かう最終目的地。OrderTargetPosが変わったら経路を捨てるための記録。</summary>
        public WorldPos? PathTarget;

        /// <summary>次の経路探索(A*)試行までの残り時間（ゲーム内時間・実行時のみ・非永続化）。
        /// FindPath失敗時に立て、失敗し続けるユニットが毎tick予算を消費するのを防ぐ（Task23レビュー）。</summary>
        public float PathRetryCooldown;

        /// <summary>次の発砲エフェクト(ShotEvent)を出すまでの残りゲーム内時間（実行時のみ・非永続化、Task42）。
        /// このユニットが実際にダメージを与えたtickでのみ dt 分だけ減算し、0以下になった時点で
        /// ShotEventを1つ積んでから UnitType.FireIntervalHours にリセットする（CombatStep/BaseCombatStep
        /// 参照）。ダメージを与えていないtickでは一切減算しない（＝待機中のユニットが「発砲権」を
        /// 溜め込んで、次に交戦した瞬間に不自然な連射をすることがない）。既定値0のため、初めて
        /// ダメージを与えたtickで即座に1発目が出る。</summary>
        public float FireCooldown;

        /// <summary>交戦中に遮蔽（建物/Prop）から狙う立ち位置（実行時のみ・非永続化、Task44）。
        /// CoverSeekStepが設定/クリアする。setされている間はMovementStepがPath/OrderTargetPosの代わりに
        /// ここへ向けて移動する。nullなら従来通りの移動（進軍経路/直線）。</summary>
        public WorldPos? CoverDestination;

        /// <summary>次にCoverSeekStepが遮蔽を再評価するまでの残りゲーム内時間（実行時のみ・非永続化、Task44）。
        /// 毎tick探索するとコストが高く、かつ僅かなスコア差で立ち位置が頻繁に切り替わるジッタの原因になるため、
        /// CoverSeekStep.CoverReevaluateHoursごとにのみ再評価する。既定値0のため、交戦を始めた最初のtickで
        /// 即座に評価される。</summary>
        public float CoverReevaluateCooldown;

        public UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)
        {
            InstanceId = id; TypeKey = typeKey; FactionId = factionId;
            CurrentHP = hp; Position = pos; State = UnitState.Idle;
        }

        public bool IsAlive => State != UnitState.Dead && CurrentHP > 0f;

        public void ClearPath()
        {
            Path = null;
            PathIndex = 0;
            PathTarget = null;
            PathRetryCooldown = 0f;
        }
    }
}
