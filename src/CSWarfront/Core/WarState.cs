using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>MODの論理状態の集約。Game層はこれを1つ保持し、Coreの各stepに渡す。</summary>
    public class WarState
    {
        public List<Faction> Factions = new List<Faction>();
        public RelationMatrix Relations = new RelationMatrix(5);
        public List<UnitInstance> Units = new List<UnitInstance>();
        public List<MilitaryBase> Bases = new List<MilitaryBase>();
        public UnitTypeRegistry Types = new UnitTypeRegistry();
        public uint NextInstanceId = 1;

        /// <summary>Game層から供給される道路網（実行時のみ・非永続化）。未供給ならnull。</summary>
        public RoadGraph Roads;

        /// <summary>Game層から供給される遮蔽物（建物/Prop）マップ（実行時のみ・非永続化、Task44）。
        /// 未供給ならnull＝CoverSeekStepは遮蔽移動を一切行わない（RoadsのRoadGraphと同じパターン）。</summary>
        public CoverMap Cover;

        /// <summary>Game層から供給される地表高さサンプラー（実行時のみ・非永続化、Task53）。
        /// 未供給ならnull＝MovementStepは従来どおりウェイポイント/目標のYを補間する（既存の挙動・
        /// テストへの後方互換フォールバック）。供給されていれば、道路/建物建設後の"見た目の"地表
        /// （TerrainManager.SampleDetailHeight相当）へユニットのYをスナップし、路面へのめり込みを防ぐ。
        /// RoadGraph/CoverMapと同じパターン: WarStateとライフサイクルを共にする（Stateごと破棄される）ため、
        /// Reset()で個別にnullへ戻す必要はない。</summary>
        public IHeightSampler Height;

        /// <summary>
        /// Task42: 直近1tick分の「見える発砲」イベントのトランジェント・バッファ（非永続化）。
        /// CombatStep/BaseCombatStepがダメージを実適用したタイミングでAddShotを通じて積む
        /// （UnitInstance.FireCooldownで間引かれるため、攻撃側1体につき最大1件/tick）。
        /// Game層のMilitaryManager.OnSimTickは各tickの先頭（戦闘stepより前）で必ずClear()し、
        /// OnMainVisualUpdateが_stateLock内でコピーしてから消費すること。ここに積みっぱなしにすると
        /// 際限なく肥大化するため、消費側がクリアする契約になっている。
        /// WarStateSerializerには一切書き出さない（見た目専用データでセーブ不要）。
        /// </summary>
        public List<ShotEvent> RecentShots = new List<ShotEvent>();

        /// <summary>1tickあたりRecentShotsへ追加できる最大件数（Task42）。大規模乱戦が発生しても
        /// 表現バッファ・後段のGameObject生成が際限なく増えないようにする防御的上限。</summary>
        public const int MaxRecentShotsPerTick = 200;

        /// <summary>
        /// Task51: 直近1tick分の「ユニット撃破」イベントのトランジェント・バッファ（非永続化）。
        /// RecentShotsと全く同じ契約: CombatStepがユニットをUnitState.Deadへ遷移させたタイミングで
        /// AddKillを通じて積む。Game層のMilitaryManager.OnSimTickは各tickの先頭で必ずClear()し、
        /// OnMainVisualUpdateが_stateLock内でコピーしてから消費すること。
        /// WarStateSerializerには一切書き出さない（見た目・音専用データでセーブ不要）。
        /// </summary>
        public List<KillEvent> RecentKills = new List<KillEvent>();

        /// <summary>1tickあたりRecentKillsへ追加できる最大件数（Task51）。RecentShotsと同じ防御的上限。</summary>
        public const int MaxRecentKillsPerTick = 200;

        /// <summary>
        /// Task54: 発砲/被弾から追跡する「戦闘域」の集合（実行時のみ・非永続化）。RoadGraph/Coverと違い
        /// Game層から供給されるのではなく、Core自身がCombatStep/BaseCombatStepからの報告で維持する
        /// （WarStateSerializerには一切書き出さない＝セーブ/ロードのたびに空へ戻る。ロード直後は
        /// 一時的にゾーンが消えるが、戦闘が続いていればすぐ報告が再開し数tickで復元される。実害は無い）。
        /// フィールド初期化子で構築するため、newされたWarStateはnullを心配せず即座に使える。
        /// </summary>
        public CombatZoneTracker CombatZones = new CombatZoneTracker();

        public uint AllocInstanceId() { return NextInstanceId++; }

        /// <summary>発砲イベントを1件積む（Task42）。MaxRecentShotsPerTickに達していれば黙って捨てる
        /// （例外にしない＝大規模乱戦でシミュレーションを止めないため）。</summary>
        public void AddShot(ShotEvent e)
        {
            if (RecentShots.Count >= MaxRecentShotsPerTick) return;
            RecentShots.Add(e);
        }

        /// <summary>撃破イベントを1件積む（Task51）。MaxRecentKillsPerTickに達していれば黙って捨てる
        /// （AddShotと同じ防御方針）。</summary>
        public void AddKill(KillEvent e)
        {
            if (RecentKills.Count >= MaxRecentKillsPerTick) return;
            RecentKills.Add(e);
        }

        public UnitInstance FindUnit(uint id)
        {
            for (int i = 0; i < Units.Count; i++)
                if (Units[i].InstanceId == id) return Units[i];
            return null;
        }

        public Faction FindFaction(byte id)
        {
            for (int i = 0; i < Factions.Count; i++)
                if (Factions[i].Id == id) return Factions[i];
            return null;
        }
    }
}
