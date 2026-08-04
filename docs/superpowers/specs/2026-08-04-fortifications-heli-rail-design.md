# CS:WARFRONT Update 3 設計: 野戦築城・ヘリコプター・鉄道輸送

日付: 2026-08-04
ステータス: 承認済み（ユーザー確認済み、Q&A 3問で仕様確定）
前提: Update 2（3資源経済＋補給、2026-08-03-economy-supply-design.md）の上に構築する。
モデルはmodels.blendに作成済み（掩蔽壕16×16/砲兵陣地24×24/補給拠点32×24/塹壕16×32※新モデル/
輸送ヘリ/攻撃ヘリ/貨物駅96×48/貨物列車83.9m）。塹壕はモデル差し替えがあるため再エクスポート必須。

## 0. 全体構成 — 3フェーズ・セーブv10を1回だけ

- Phase A: 野戦築城＋補給網拡張（Bunker/ArtilleryPost/SupplyDepot/Trench）
- Phase B: ヘリコプター（TransportHelicopter/AttackHelicopter）
- Phase C: 鉄道輸送（CargoStation＋MilitaryTrain）

## 1. 野戦築城（Phase A）

実装形態: **MilitaryBase/BaseTypeの拡張**（Bunker/ArtilleryPost/SupplyDepot/Trench/CargoStation を
BaseTypeへ追加）。Options指定建物方式（BaseBuildingDesignation）・BasePlacementWatcher・
serializer基地ブロック・BaseVisuals・基地パネルUIを再利用する。所有＝建設時のBuildFactionId。
生産キュー・収入・ミサイル等の既存基地機能はこれらの種別では無効（SpawnableDomains=None）。

| 種別 | HP | HP0の扱い | 機能 |
|---|---|---|---|
| Bunker（掩蔽壕） | 300 | 機能停止（OwnerFactionId=null化・占領不可・再稼働なし） | 歩兵3体分の射撃＋歩兵守備+50% |
| ArtilleryPost（砲兵陣地） | 250 | 同上 | 砲兵1体分の射撃 |
| SupplyDepot（補給拠点） | 400 | **占領**（既存Occupation、StoredSuppliesごと移る） | 備蓄＋200m自動補給＋トラック積出 |
| Trench（塹壕） | - | 攻撃対象外（BaseCombatStep/ミサイル等から除外） | 歩兵守備+50% |
| CargoStation（貨物駅、Phase C） | 400 | 占領（StoredSuppliesごと） | 鉄道輸送の端点＋備蓄＋トラック積出 |

### 1.1 FortCombatStep（新Core step）

- Bunker: Attack=歩兵T1×3（18×3=54/h）、Range=120m、対象=敵対陸上ユニットのみ、命中率0.75。
  **建物非貫通**: 射線（砲台→目標の線分）を16m間隔でサンプリングし、CoverMap（建物遮蔽データ）に
  遮蔽物ヒットがあれば射撃不可。CoverMap未供給（テスト等）は射線クリア扱い。
- ArtilleryPost: Attack=砲兵T1（55/h）、Range=120m、Splash=30m、命中率0.35（曲射・射線判定なし）。
- 弾薬: MilitaryBase.FortAmmo（0..1、v10永続化）。Bunker=12h/ArtilleryPost=4h（歩兵/砲兵と同じ）。
  射撃tickだけ消費。補給網（§1.3）から回復。弾切れ=射撃停止のみ。
- ShotEvent発行（既存FireEffects相当の間引き、発砲位置=施設中心）。HP0（機能停止）は射撃しない。
- Invaderに対しても通常どおり射撃する（防衛設備としての主用途）。

### 1.2 守備ボーナス（FortDefenseBonus）

- Trench（16×32m）/Bunker（16×16m）の**上面矩形内**（回転は建物角度を適用、判定は中心距離の
  簡易円=対角半径でよい: Trench r=18m, Bunker r=12m）にいる歩兵系（Infantry/MechInfantry）は
  被ダメージ÷1.5（+50%守備）。**敵味方問わず**（敵に取られると逆用される）。
- 適用箇所: CombatStep（通常射撃・対空は対象外=歩兵はAir非対象）・KamikazeStep起爆・
  FortCombatStep自身・ThreatBeamStep等の脅威ダメージは対象外（怪獣光線は塹壕を無視する）。
- 機能停止したBunkerも地形としてのボーナスは残す（塹壕と同じ扱い）。

### 1.3 補給網の拡張（SupplyDepot/CargoStation備蓄）

- MilitaryBase.StoredSupplies（float、v10永続化）: Depot上限300／CargoStation上限500。
- **補給の流れ**: 勢力プール（基地）→トラック/輸送ヘリ/列車→Depot/Station（StoredSupplies）→
  ①Depot200m圏の自動補給（25%/h、StoredSupplies消費。Stationは自動補給なし）
  ②トラックがDepot/Stationで再積載（基地より近ければ優先）
- 従来の「基地200m圏自動補給（勢力プール消費）」「トラックの基地積載→前線直送」は不変。
- トラックの新挙動: 配送対象ユニットがいない時、最寄りの「備蓄に空きがあるDepot」へ物資を運ぶ
  （基地で積載→Depotで荷下ろし→StoredSuppliesへ）。
- 占領: OccupationはStoredSuppliesに触れない（基地オブジェクトに載ったまま）＝自動的に奪取になる。

### 1.4 歩兵の陣地志向AI（FortSeekStep、新Core step）

- 対象: AiControlled/FreeAdvanceの歩兵系で、敵対ユニットが600m以内にいるもの。
- 300m以内の自軍所有（Trenchは所有不問だが敵占有中=敵歩兵が乗っている場合は除く）の
  Trench/Bunkerのうち、敵方向に最も近いものへ移動（CoverSeekStepと同じ「立ち位置上書き」方式、
  既にボーナス圏内なら動かない）。交戦は移動中も通常どおり。

## 2. ヘリコプター（Phase B）

- UnitCategory末尾に TransportHelicopter / AttackHelicopter を追加。Domain=Air、巡航高度60m
  （固定翼の120mより低い）。**レーストラック航過はしない**（ホバリング型＝通常の陸上ユニットと
  同じ「接近して継続射撃」移動。AirCombat.DamageMultiplier=1）。
- **対ヘリ規則**（TargetingRules.CanTargetHelicopter）: ヘリを攻撃できるのは
  Tank（機銃・継続射撃）/AntiAir（SAM・離散命中ロール）/AirSuperiority（空戦）のみ。
  他兵科はヘリを狙えない（TargetSearchでカテゴリ例外として除外）。ヘリは対空ミサイル対象。
- **AttackHelicopter**: 航空基地の通常生産兵科（Tier1-5、AI編成AirCategoriesへ追加・手動発注可）。
  HP90/Attack45/Range100/速度220km/h/Cost220/弾薬3h/命中0.8。CanTargetDomains=Land
  （地上の攻撃・補給ユニット両方。ヘリ同士・固定翼・艦船は狙わない）。拠点・脅威攻撃は不可
  （CanAttackBase/CanAttackThreat=false）。弾切れ→帰還→再武装→再出撃（既存ロジック）。
  相性: vs Tank/Apc 1.6、vs Infantry系 1.2、vs SupplyTruck 1.5。被弾側: AntiAir→Heli 2.5、
  AirSuperiority→Heli 2.0、Tank→Heli 0.6。
- **TransportHelicopter**: 自動維持（陸軍基地1機/勢力上限6機、人的資源+生産力消費、
  トラックと同じ別枠・戦闘150体に数えない）。非武装（CanTargetDomains=None）・HP60・
  速度220km/h・弾薬無限。TransportHeliStep（新Core step、トラックと同じステートレス導出）:
  1. 基地で物資60積載（勢力プールから）＋基地100m内のIdle歩兵系を最大3体搭乗
  2. 最寄りの「備蓄に空きがあるDepot」（無ければ前線=味方交戦ユニットの重心付近）へ直行（道路不要）
  3. 物資をStoredSuppliesへ荷下ろし、歩兵を降機（着陸点周囲に展開、Order/State復元）
  4. 基地へ帰投
- **搭乗（CarriedByUnitId）**: UnitInstance.CarriedByUnitId（uint?、v10永続化）。搭乗中は
  全step（移動/戦闘/補給/スタック）から除外・非攻撃対象（TargetSearch除外）・位置は毎tick
  運搬役に追従。運搬役が死亡したら搭乗ユニットも即死亡（KillEventなしの無音消滅、積荷ロスト）。

## 3. 鉄道輸送（Phase C）

- **RailGraph**: RoadGraphと同じCoreグラフ/A*を、Game層RailGraphBuilderがNetManagerの
  レール区間（ItemClass.Service.PublicTransport + SubService.PublicTransportTrain のNetSegment）
  から構築。12hごと再構築。state.Rails（実行時のみ）。
- **CargoStation**: Options指定建物方式。BasePlacementWatcherが配置時に100m以内のRailGraph
  ノードへスナップ（RailNodePos記録）。レール未接続なら「機能停止」フラグ＝輸送に使われない
  （備蓄・占領は機能する）。
- **MilitaryTrain**（UnitCategory末尾追加）: 非武装・HP500・速度160km/h・弾薬無限。
  レール経路（RailGraph A*）専用移動（MovementStepに専用分岐、直線フォールバック無し＝
  経路が無ければ動かない）。攻撃対象になり得る（対地攻撃可能な全兵科から）。
  撃破=積荷（物資・搭乗ユニット）全損。
- **TrainStep**（新Core step）: 勢力ごとに、自軍の稼働CargoStationペア（RailGraphで接続・
  2km以上離れている）1組につき列車1編成を自動維持（人的資源+生産力消費、上限=ペア数かつ
  勢力4編成）。運行サイクル（ステートレス導出）:
  1. 基地側駅（自軍基地に最も近い方）で物資200積載（勢力プール）＋駅150m内の「前線へ向かう」
     陸上ユニット（AiControlled/FreeAdvanceのMoving、目的地がもう一方の駅の方が1km以上近い）を搭乗
  2. レール走行でもう一方の駅へ
  3. 物資を駅StoredSuppliesへ・ユニット降車（Order/目的地は保持したまま自走再開）
  4. 折り返し（以後往復）
- **発動条件**はサイクル1の搭乗条件そのもの（前線が遠いユニットだけが乗る）。物資輸送は常時
  （到着駅の備蓄に空きがある限り）。

## 4. 共通

- セーブv10追記: 基地{StoredSupplies, FortAmmo, RailConnected}、ユニット{CarriedByUnitId}。
  v9以前は既定値（備蓄0・弾薬満タン・未搭乗）。
- Invaderは築城・ヘリ・鉄道を一切使わない（生産・維持・搭乗の全経路で除外）。
- モデル: Unit_TransportHeli/Unit_AttackHeli/Unit_MilitaryTrain/Fort_Bunker/Fort_ArtilleryPost/
  Fort_SupplyDepot/Fort_Trench(新モデル・要再エクスポート)/Fort_CargoStation。
  基地種別モデルはBaseVisualsの既存キー方式に追加、ユニットはUnitMeshSourceへ追加。
- unit-stats.xml: AttackHelicopter/TransportHelicopter/MilitaryTrain行を追加。
- UI: 基地パネルに施設種別表示＋StoredSupplies/FortAmmo表示。ヘリ・列車はユニットパネル既存表示。
- テスト: 全新step（FortCombat/FortDefense/FortSeek/TransportHeli/Train/補給網拡張）をxunitで
  カバー。決定的・乱数不使用。

## 決定事項の経緯（Q&A）

1. 築城は基地方式・Depot占領=備蓄奪取・Bunker/ArtPostはHP0で機能停止・Trenchは敵味方不問の地形効果
2. 輸送ヘリ=自動維持（陸軍基地1機/上限6機）、攻撃ヘリ=航空基地の通常生産兵科
3. 対ヘリ=戦車・対空・**戦闘機**（当初案の戦車対空のみから戦闘機を追加、ユーザー指示）
4. 列車=CSの鉄道運行に干渉しない自前ビジュアル・RailGraph方式、駅はレール100m内スナップ
