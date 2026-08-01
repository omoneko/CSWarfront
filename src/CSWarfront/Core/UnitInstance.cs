using System.Collections.Generic;

namespace CSWarfront.Core
{
    public enum UnitState { Idle, Moving, Engaging, Dead }

    /// <summary>プレイヤーが個別部隊/選択範囲へ与える指揮コマンド（Task48・実行時のみ・非永続化）。
    ///   AiControlled - 既定。AI(InvasionOrders)が目標基地を割り当てる従来通りの挙動。
    ///   FreeAdvance  - 自由進撃。各自の最高速度で最寄りの敵拠点へ進み通常通り交戦する。AIが目標基地を
    ///                  更新することはあるが、プレイヤーが別命令を出すまでこのモードのまま。
    ///   Hold         - 停止。その場から一切動かない（移動系フィールドはUnitCommandsが割り当て時にクリア
    ///                  する）が、射程内の敵には引き続き応戦する（受動防御）。
    ///   RallyHold    - 集結待機。RallyPointへ移動し到着後は停止、移動中・停止後を問わず射程内の敵にしか
    ///                  応戦しない（追撃や遮蔽移動、拠点への進撃は一切しない）。
    /// </summary>
    public enum UnitOrder { AiControlled, FreeAdvance, Hold, RallyHold }

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

        /// <summary>KamikazeStepがロックした外部脅威（ゴジラ/エイリアン）のExternalThreat.Id
        /// （実行時のみ・非永続化、Task79）。TargetIdはUnitInstance向け、こちらはExternalThreat向けと
        /// 対象の種類ごとに別フィールドへ分ける（両者のID空間は別物のため、1つのuint?フィールドへ
        /// 混在させると衝突・誤読の余地が生まれるのを避けた）。自爆ドローン以外のカテゴリでは常にnullの
        /// まま無視される。MovementStepのダイブ移動先の解決、KamikazeStepの起爆判定の両方から参照される。</summary>
        public uint? TargetThreatId;

        /// <summary>KamikazeStepがロックした敵対基地のMilitaryBase.BaseId（実行時のみ・非永続化、Task79）。
        /// TargetId/TargetThreatIdと同じ「対象の種類ごとに別フィールド」方針。ユニット目標・外部脅威の
        /// どちらも見つからなかった場合にのみ設定される（優先順位: ユニット→外部脅威→基地）。
        /// 自爆ドローン以外のカテゴリでは常にnullのまま無視される。MovementStepのダイブ移動先の解決、
        /// KamikazeStepの起爆判定の両方から参照される。</summary>
        public ushort? TargetBaseId;

        public WorldPos? OrderTargetPos;

        /// <summary>Task86: 航空ユニットの交戦パス移動（レーストラック航過）の離脱点
        /// （実行時のみ・非永続化）。MovementStepAirPass.AdvanceAirPassが設定/消費する。
        /// 設定されている間は交戦アンカーの有無に関わらずこの点まで飛び切る（境界での
        /// ふらつき防止）。航空以外のカテゴリでは常にnullのまま無視される。</summary>
        public WorldPos? AirPassEgress;

        /// <summary>プレイヤーの指揮コマンド（Task48）。既定はAiControlled。
        /// Task92: v8でRallyPointとともに永続化されるようになった（「ロードで命令がAI制御へ戻る」の解消）。</summary>
        public UnitOrder Order;

        /// <summary>Order==RallyHold の目的地（実行時のみ・非永続化、Task48）。UnitCommands.ApplyRallyが設定する。</summary>
        public WorldPos? RallyPoint;

        /// <summary>道路経路の残り（実行時のみ・非永続化）。null/空なら直線移動へフォールバック。</summary>
        public List<WorldPos> Path;
        /// <summary>次に目指す Path の要素番号。</summary>
        public int PathIndex;
        /// <summary>この経路が向かう最終目的地。OrderTargetPosが変わったら経路を捨てるための記録。</summary>
        public WorldPos? PathTarget;

        /// <summary>次の経路探索(A*)試行までの残り時間（ゲーム内時間・実行時のみ・非永続化）。
        /// FindPath失敗時に立て、失敗し続けるユニットが毎tick予算を消費するのを防ぐ（Task23レビュー）。</summary>
        public float PathRetryCooldown;

        /// <summary>Task98: スタック検知の基準位置（実行時のみ・非永続化）。ここからMinProgressDistance
        /// 以上動くたびに現在位置へ更新され、StuckHoursが0へ戻る（StuckCleanupStep参照）。</summary>
        public WorldPos? StuckAnchor;

        /// <summary>Task98: StuckAnchorからほぼ動けていない状態が続いているゲーム内時間
        /// （実行時のみ・非永続化。ロードで0に戻っても判定が遅れるだけで実害なし）。</summary>
        public float StuckHours;

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

        /// <summary>CoverDestinationに到達した際、その場に留まって撃ち続けるか（true）、それとも
        /// 遮蔽から遮蔽へ前進を続けるか（false）（実行時のみ・非永続化、Task45）。
        /// CoverSeekStepが交戦中（脅威＝TargetId）のユニットにはtrueを、進軍中（脅威＝OrderTargetPos、
        /// まだ交戦していない）のユニットにはfalseを設定する。MovementStepはCoverArrivalDistance以内に
        /// 入った時、trueなら停止したままにし、falseならCoverDestinationをクリアして次の遮蔽へ
        /// 移れるようにする（cover-to-coverのbounding advance）。</summary>
        public bool CoverHold;

        /// <summary>交戦中（State==Engaging）の現在のCoverDestination/CoverHoldが、どのTargetIdに対して
        /// 決定されたものかを覚えておく（実行時のみ・非永続化、Task50）。CoverSeekStepは、交戦中の間
        /// TargetIdがこの値と一致し続ける限り遮蔽の再評価を一切行わない（見つからなかった場合も含め、
        /// 判断済みの結果をそのまま維持する）。これにより「同じ相手と戦い続けている間、僅かなスコア差で
        /// 遮蔽位置が頻繁に切り替わり、建物の陰でせわしなく動き回って見える」不具合を防ぐ。
        /// TargetIdが変わった（新しい相手と交戦を始めた）、または交戦をやめた瞬間にnullへ戻り、
        /// 次の交戦開始時に改めて評価される。</summary>
        public uint? CoverTargetId;

        /// <summary>CoverDestinationでCoverHold==trueのまま実際に静止し続けている経過ゲーム内時間
        /// （実行時のみ・非永続化、Task52）。MovementStep.AdvanceTowardCoverが計測し、
        /// MovementStep.MaxCoverHoldHoursを超えたら強制的にCoverDestinationを解放する
        /// （「隠れている間は動かないが、いつまでも隠れ続けはしない」というTask52の保証）。
        /// 移動中（まだCoverDestinationへ到達していない間）は0のまま維持され、到達後の静止時間だけを
        /// 計測する。新しい遮蔽が決定された瞬間（CoverSeekStep）に0へリセットされる。</summary>
        public float CoverHoldTimer;

        /// <summary>State==EngagingでTargetIdが同じ相手を指し続けている経過ゲーム内時間
        /// （実行時のみ・非永続化、Task52）。CoverSeekStepが毎tick加算/リセットし、
        /// CoverSeekStep.MaxEngageHoldHoursを超えるとMovementStepはEngagingのままでも
        /// OrderTargetPos/Pathへの移動を許可する（射程内ならCombatStepが移動しながらでも
        /// 撃ち合いを継続する）。TargetIdが変わった、または交戦が終わった瞬間に0へリセットされる。</summary>
        public float EngageHoldTimer;

        /// <summary>膠着ウォッチドッグ（実行時のみ・非永続化、Task52）: 直近のチェックポイント時点での
        /// OrderTargetPosまでの水平距離。nullは未計測（次tickで初期化される）を表す。
        /// CoverSeekStep.StallEpsilon以上縮まるたびに更新され、StallTimerも同時に0へリセットされる。</summary>
        public float? LastObjectiveDistance;

        /// <summary>膠着ウォッチドッグ（実行時のみ・非永続化、Task52）: LastObjectiveDistanceから
        /// CoverSeekStep.StallEpsilon以上前進できていない経過ゲーム内時間。
        /// CoverSeekStep.StallTimeoutHoursを超えるとCoverSuppressionRemainingがセットされる。</summary>
        public float StallTimer;

        /// <summary>膠着ウォッチドッグが発動した後、遮蔽探索を完全に無視して道路経路のみに従う
        /// 残りゲーム内時間（実行時のみ・非永続化、Task52）。CoverSeekStep.Advanceが毎tick
        /// dt分だけ減算し、0になったら通常の遮蔽ロジックへ自動的に戻る
        /// （「suppresses cover after a stall and re-enables it later」の実装）。</summary>
        public float CoverSuppressionRemaining;

        /// <summary>UnitCategory.Carrierのみが使う: 現在建造中の艦載機の進捗（0..1、Task64）。
        /// 0fは「建造中でない（次tickで新しい機体の建造着手を判定する）」を意味し、CarrierAirWing.Advanceが
        /// 建造着手の瞬間に微小な正の値へ設定することで「建造中」と区別する（MilitaryBase.MissileBuildProgress
        /// と同じ実行時のみ・非永続化パターン）。空母以外のユニットでは常に0のまま無視される。</summary>
        public float CarrierBuildProgress;

        /// <summary>UnitCategory.Carrierのみが使う: これまでにこの空母が建造着手した艦載機の累計数
        /// （実行時のみ・非永続化、Task64）。CarrierAirWing.NextBuildCategoryが「艦載機id＋この累計数」を
        /// ハッシュして次に建造する兵科を決定的に選ぶための入力になる（seedにより偏らせつつも
        /// System.Randomは使わない）。1回の建造に着手するたびに1つずつ増える。</summary>
        public uint CarrierBuildCounter;

        /// <summary>Task78: 海上ユニット(Domain.Sea)が直線移動・迂回(±30/60/90度)のいずれも水域に
        /// 着地できず完全に足止めされている継続ゲーム内時間（実行時のみ・非永続化）。
        /// MovementStep.AdvanceSeaが移動に成功するたび、または目的地(OrderTargetPos)が変わった
        /// （＝新しい命令を受けた）瞬間に0へリセットする。MovementStep.SeaBlockedIdleHoursを
        /// 超えたらState=Idleへ遷移し、以後は目的地が変わるまで一切移動を試みない
        /// （毎tick迂回を探索し続けて見た目がスピンし続けるのを防ぐ、Task52のStallTimer相当のガード）。</summary>
        public float SeaBlockedHours;

        /// <summary>SeaBlockedHoursの起点判定に使う、直近にAdvanceSeaが処理した目的地（実行時のみ・
        /// 非永続化、Task78）。OrderTargetPos/RallyPointがこれと異なればMovementStepは「新しい命令」
        /// とみなしSeaBlockedHoursを0へリセットする。</summary>
        public WorldPos? SeaLastObjective;

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
