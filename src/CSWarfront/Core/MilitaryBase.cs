using System.Collections.Generic;
namespace CSWarfront.Core
{
    public partial class MilitaryBase
    {
        public ushort BaseId;
        public BaseType Type;
        public byte? OwnerFactionId;
        public WorldPos Position;
        public float InfluenceRadius = 500f;
        public bool IsHeadquarters;
        public float MaxHP = 500f;
        public float CurrentHP = 500f;
        public List<ProductionOrder> Queue = new List<ProductionOrder>();

        /// <summary>trueならAI（ProductionPlanning.Advance）がこの基地のキューを自動補充する。
        /// falseの場合はプレイヤーが手動でしか発注できない（Task34：指揮モード第一歩）。
        /// 既定はtrue（既存の全自動挙動を維持）。</summary>
        public bool AutoProduce = true;

        /// <summary>プレイヤーが手動発注する際のキュー上限（Task34）。AI自動生産の上限
        /// ProductionPlanning.QueueCap（2）より大きく、プレイヤーはまとめて複数発注できる。</summary>
        public const int ManualQueueCap = 5;

        /// <summary>新設基地の占領猶予（ゲーム内時間、残り時間）。0なら保護なし。
        /// 通常のコンストラクタでは0（既存の挙動・テストへの影響なし）。Game層がプレイヤー配置基地の
        /// 登録時に NewBaseGraceHours を設定する。</summary>
        public float CaptureGraceHours;

        /// <summary>新設基地に与える占領猶予期間（ゲーム内1日）。</summary>
        public const float NewBaseGraceHours = 24f;

        /// <summary>拠点の自衛射撃ダメージ（ゲーム内1時間あたり、Task29）。BaseDefenseStepが参照する。
        /// これらはランタイム既定値であり、WarStateSerializerには追加しない
        /// （ロードされた基地は単に既定値を再度受け取るだけで、セーブフォーマットを変える必要がないため）。</summary>
        public const float DefaultDefenseAttack = 35f;

        /// <summary>拠点の自衛射撃射程（Task29）。戦車の射程(60)より長く設定し、
        /// 接近してくる敵が近づく前から迎撃できるようにする。</summary>
        public const float DefaultDefenseRange = 120f;

        public float DefenseAttack = DefaultDefenseAttack;
        public float DefenseRange = DefaultDefenseRange;

        /// <summary>直近の経済tickでこの基地から実際に加算された収入（Task35、ゲーム内EconomyIntervalHours
        /// あたりの額）。UIがCSバッファ/WarStateへ直接触れずに済むよう、MilitaryManager.OnSimTickが
        /// TerritoryIncome.ForBaseの計算結果をここへキャッシュする。ランタイムのみ・非永続化
        /// （WarStateSerializerには追加しない）。</summary>
        public float LastIncome;

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }
    }
}
