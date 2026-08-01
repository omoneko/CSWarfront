using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>MODの論理状態の集約。Game層はこれを1つ保持し、Coreの各stepに渡す。</summary>
    public class WarState
    {
        public List<Faction> Factions = new List<Faction>();
        public RelationMatrix Relations = new RelationMatrix(5);

        /// <summary>勢力ごとの外部脅威（KAIJU/Alien、Task59）との関係。既定は全てHostile
        /// （ゴジラ/エイリアンMOD導入前・v4以前のセーブロード時の後方互換に合わせた既定値）。
        /// WarStateSerializerがformat v5でこの表を末尾に永続化する。</summary>
        public ThreatRelations ThreatRelations = new ThreatRelations(5);
        public List<UnitInstance> Units = new List<UnitInstance>();
        public List<MilitaryBase> Bases = new List<MilitaryBase>();
        public UnitTypeRegistry Types = new UnitTypeRegistry();
        public uint NextInstanceId = 1;

        /// <summary>Game層から供給される道路網（実行時のみ・非永続化）。未供給ならnull。</summary>
        public RoadGraph Roads;

        /// <summary>Game層（SeaGridBuilder）から供給される海上航行グリッド（実行時のみ・非永続化、
        /// Task92）。未供給ならnull＝海上ユニットは従来の直線＋壁沿い迂回のみで移動する。</summary>
        public SeaGrid SeaNav;

        /// <summary>Task94: 外部襲来イベント（InvasionEvents）の判定タイマー（実行時のみ・非永続化。
        /// ロードで0に戻っても「次の判定が最大6時間遅れる」だけで実害なし）。</summary>
        public float InvasionCheckAccum;

        /// <summary>Task97: 交戦判定用の空間グリッド（実行時のみ・非永続化、simスレッド専用）。
        /// CombatStep/KamikazeStepが各Advanceの先頭でBuildして使う（総当たりO(N²)の回避）。</summary>
        public UnitSpatialGrid UnitGrid = new UnitSpatialGrid();

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

        /// <summary>Game層から供給される水面サンプラー（実行時のみ・非永続化、Task61）。未供給ならnull＝
        /// MovementStepのSea分岐は「常に水上」とみなして自由に移動する（Height/RoadGraphと同じ
        /// パターン：Game層実装が無いテスト環境でも既存の直線移動テストが素直に書けるようにするための
        /// 安全側フォールバック）。</summary>
        public IWaterSampler Water;

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

        /// <summary>
        /// Task58: 他MOD（ゴジラ災害/エイリアン侵略）由来の「外部脅威」の集合（実行時のみ・非永続化）。
        /// RoadGraph/Coverと同じパターン: Game層(ExternalThreatBridge)が毎tick、生きている他MODの
        /// 状態（IsActive/位置）から再同期する（新規出現の追加、既存の位置更新、消えた分の除去）。
        /// HPはCSWarfrontがここで独自に管理する（相手MODはHP/被弾APIを公開していないため）。
        /// WarStateSerializerには一切書き出さない＝セーブ/ロードのたびに空へ戻るが、Game層が次tickで
        /// 相手MODの現在状態から即座に復元するため実害は無い。
        /// </summary>
        public List<ExternalThreat> Threats = new List<ExternalThreat>();

        /// <summary>
        /// Task63: 飛翔中の弾道ミサイル（実行時のみ・非永続化）。RoadGraph/Threatsと同じ「セーブに含めない」
        /// 方針だが、こちらはGame層が再同期する対象ではなく、Core自身（BallisticMissiles.TryLaunch/
        /// MissileStep.Advance）が発射〜着弾/迎撃までの一生を完結して管理する。セーブ/ロードで
        /// 飛翔中のミサイルは失われる（着弾も迎撃もしないまま消える）— 意図的な既知の制約（MVP）。
        /// </summary>
        public List<MissileInFlight> MissilesInFlight = new List<MissileInFlight>();

        /// <summary>Task63: 飛翔中ミサイルのID払い出し用カウンタ。UnitInstanceのNextInstanceIdとは別の
        /// 名前空間（ミサイルはUnitInstanceではないため衝突しても実害は無いが、混同を避けるため分離した）。
        /// Task92: MissilesInFlightとともにv8で永続化されるようになった（「ロードで飛行中ミサイルが
        /// 消える」の解消）。</summary>
        public uint NextMissileId = 1;

        /// <summary>Task63: 決定的な迎撃判定（BallisticMissiles.TryIntercept）のハッシュ種に使う、
        /// simtickごとに単調増加するカウンタ。実行時のみ・非永続化（セーブ/ロードのたびに0へ戻っても
        /// 「同じ入力には同じ結果」という決定性の性質自体は変わらないため実害は無い）。</summary>
        public uint TickCounter;

        /// <summary>
        /// Task63: 直近1tick分の「弾道ミサイルの着弾/迎撃」イベントのトランジェント・バッファ（非永続化）。
        /// RecentShots/RecentKillsと同じ設計思想: MissileStep.Advanceが着弾/迎撃を解決したタイミングで
        /// AddImpactを通じて積む。Game層（MissileVisuals想定）が毎フレームロック内でコピーしてから
        /// 消費すること。WarStateSerializerには一切書き出さない（見た目・音専用データでセーブ不要）。
        /// </summary>
        public List<MissileImpactEvent> RecentImpacts = new List<MissileImpactEvent>();

        /// <summary>1tickあたりRecentImpactsへ追加できる最大件数。RecentShots/RecentKillsと同じ防御的上限。</summary>
        public const int MaxRecentImpactsPerTick = 200;

        public uint AllocInstanceId() { return NextInstanceId++; }

        /// <summary>Task63: 飛翔中ミサイルのIDを1つ払い出す。</summary>
        public uint AllocMissileId() { return NextMissileId++; }

        /// <summary>ミサイルの着弾/迎撃イベントを1件積む（Task63）。MaxRecentImpactsPerTickに達していれば
        /// 黙って捨てる（AddShot/AddKillと同じ防御方針）。</summary>
        public void AddImpact(MissileImpactEvent e)
        {
            if (RecentImpacts.Count >= MaxRecentImpactsPerTick) return;
            RecentImpacts.Add(e);
        }

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
