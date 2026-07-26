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

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }
    }
}
