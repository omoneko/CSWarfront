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

        /// <summary>直近の経済tickでこの基地から実際に加算された収入（Task35、ゲーム内EconomyIntervalHours
        /// あたりの額）。UIがCSバッファ/WarStateへ直接触れずに済むよう、MilitaryManager.OnSimTickが
        /// TerritoryIncome.ForBaseの計算結果をここへキャッシュする。ランタイムのみ・非永続化
        /// （WarStateSerializerには追加しない）。</summary>
        public float LastIncome;

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }

        /// <summary>この基地がどの領域(Domain)のユニットを生産できるか（Task61）。Typeから機械的に導出する
        /// 派生値であり、独立フィールドとしては持たない（=WarStateSerializerのフォーマット変更は不要。
        /// BaseTypeは既にv1から永続化済みで、SpawnableDomainsはロード時にそこから毎回再計算される）。
        /// Army→Land、Navy→Sea、AirForce→Air。MissileBase（Task61時点では未実装のプレースホルダ用途）は
        /// 便宜上Landとしておく（他の3種と異なりプレイヤーが配置できるプレハブが存在しないため実害は無い）。</summary>
        public DomainMask SpawnableDomains
        {
            get
            {
                switch (Type)
                {
                    case BaseType.Navy: return DomainMask.Sea;
                    case BaseType.AirForce: return DomainMask.Air;
                    case BaseType.Army:
                    case BaseType.MissileBase:
                    default:
                        return DomainMask.Land;
                }
            }
        }
    }
}
