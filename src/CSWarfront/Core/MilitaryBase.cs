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

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }
    }
}
