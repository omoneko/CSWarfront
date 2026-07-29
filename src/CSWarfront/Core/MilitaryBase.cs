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

        /// <summary>完成済みの弾道ミサイル備蓄数（Task63、BaseType.MissileBase専用）。
        /// MissileStockpile.Advanceが生産完了時に加算し、BallisticMissiles.TryLaunchが発射時に消費する。
        /// v6でWarStateSerializerが基地ブロック末尾に永続化する。旧バージョンのセーブは既定値0で復元される
        /// （MissileBaseはこのタスク以前は配置可能なプレハブが存在しなかったため実害は無い）。</summary>
        public int StockpiledMissiles;

        /// <summary>現在建造中のミサイルの進捗（0..1、Task63）。0fは「建造中でない」を意味し、
        /// MissileStockpile.TryBuildMissileが建造を開始する瞬間に微小な正の値へ設定することで
        /// 「建造中」と区別する（MissileStockpile.IsBuilding参照）。v6でWarStateSerializerが
        /// 基地ブロック末尾に永続化する。旧バージョンのセーブは既定値0（建造中でない）で復元される。</summary>
        public float MissileBuildProgress;

        /// <summary>AI自動発射（MissileDoctrine）のクールダウン残り（ゲーム内時間、Task63）。
        /// ランタイムのみ・非永続化（UnitInstance.FireCooldown等と同じ方針：セーブ/ロードのたびに
        /// 0へ戻ってもゲームバランス上の実害は無い）。</summary>
        public float MissileLaunchCooldownRemaining;

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }

        /// <summary>この基地がどの領域(Domain)のユニットを生産できるか（Task61）。Typeから機械的に導出する
        /// 派生値であり、独立フィールドとしては持たない（=WarStateSerializerのフォーマット変更は不要。
        /// BaseTypeは既にv1から永続化済みで、SpawnableDomainsはロード時にそこから毎回再計算される）。
        /// Army→Land、Navy→Sea、AirForce→Air。MissileBase（Task63で実装）はユニットを一切生産しない
        /// （ミサイルを備蓄するのみ、MissileStockpile参照）ため None。ManualProduction.TryEnqueueは
        /// これによりMissileBaseへの通常ユニット発注を自動的にWrongDomainとして拒否する。</summary>
        public DomainMask SpawnableDomains
        {
            get
            {
                switch (Type)
                {
                    case BaseType.Navy: return DomainMask.Sea;
                    case BaseType.AirForce: return DomainMask.Air;
                    case BaseType.MissileBase: return DomainMask.None;
                    case BaseType.Army:
                    default:
                        return DomainMask.Land;
                }
            }
        }
    }
}
