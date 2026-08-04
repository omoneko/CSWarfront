# Update 3（野戦築城・ヘリ・鉄道）実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 仕様書 docs/superpowers/specs/2026-08-04-fortifications-heli-rail-design.md の3フェーズを実装する。

**Architecture:** 築城=BaseType拡張（既存基地インフラ再利用）、ヘリ/列車=UnitCategory拡張＋専用step、
鉄道=RoadGraph流用のRailGraph。全新ロジックはCore純関数＋xunit、Game層は配線とCS読取のみ。

**Tech Stack:** C#7.3/net35（Game）+ net8.0（tests）、xunit、Blender MCP（モデル再エクスポート）。

## Global Constraints

- 乱数不使用・決定的。System.Random禁止。ファイル500行以内。sim/mainスレッド分離。
- セーブはv10を1回だけ（基地{StoredSupplies,FortAmmo,RailConnected}、ユニット{CarriedByUnitId}）。
- Invaderは築城・ヘリ・鉄道の全経路で除外。
- 数値は仕様書§1-3の表・本文の値を使用（すべてpublic const）。

---

### Task A1: BaseType拡張と築城の基本規則

**Files:** Core/BaseType.cs（enum追加: Bunker/ArtilleryPost/SupplyDepot/Trench/CargoStation）、
Core/FortificationRules.cs（新規）、Core/Occupation.cs、Core/BaseCombatStep.cs、
Core/MissileStep系（対象除外確認）、Game/BaseBuildingDesignation.cs（指定キー追加）、
Game/BasePlacementWatcher.cs（HP初期値）、Test: FortificationRulesTests.cs

**Interfaces (Produces):**
- `FortificationRules.IsFortification(BaseType)`＝5種true
- `FortificationRules.IsTargetable(BaseType)`＝Trenchのみfalse（BaseCombatStep/ミサイル/Kamikaze除外）
- `FortificationRules.IsCapturable(BaseType)`＝Bunker/ArtilleryPostのみfalse（HP0でOwner=null化＝機能停止。Occupationに分岐追加）
- `FortificationRules.DefaultMaxHP(BaseType)`: Bunker300/ArtPost250/Depot400/Station400/Trench 1e9
- 築城はSpawnableDomains=None・収入/生産/ミサイル対象外（既存コードはSpawnableDomainsとBaseType.MissileBase判定なので自然に除外、確認のみ）
- Trenchは基地HP自然回復・占領猶予も対象外

- [ ] FortificationRulesテスト→実装→Occupation/BaseCombatStep統合→全緑→コミット

### Task A2: 備蓄（StoredSupplies）と補給網拡張

**Files:** Core/MilitaryBase.cs（StoredSupplies）、Core/ResupplyStep.cs、Core/SupplyTruckStep.cs、
Test: ResupplyStepTests/SupplyTruckStepTests拡張

**Interfaces (Produces):**
- `MilitaryBase.StoredSupplies`（float）、`FortificationRules.StoredSupplyCap(BaseType)`: Depot300/Station500/他0
- ResupplyStep.Advance: Depot（稼働中=Owner有り）200m圏の味方ユニットへ25%/h、**StoredSupplies消費**（勢力プールではなく）。基地圏は従来どおり勢力プール。IsNearResupplyPointは「基地or Depot」を返す2値に拡張（消費元を区別するためTryFindResupplySource(state,u,type,out MilitaryBase depot)へ改名。depot==null=基地/空母=勢力プール）
- SupplyTruckStep: 積載元=最寄りの「基地（勢力プール）or StoredSupplies>0のDepot/Station」。配送対象なしの積載済トラックは「空きのあるDepot」へ荷下ろし（新しいAdvanceLoadedTruck分岐）
- 占領時の備蓄移転はフィールドが基地に載ったままなので処理不要（テストで確認）

- [ ] テスト（Depot圏回復と備蓄消費/トラックのDepot積載/暇なトラックのDepot備蓄輸送/占領で備蓄移転）→実装→全緑→コミット

### Task A3: FortCombatStep（掩蔽壕・砲兵陣地の射撃）

**Files:** Core/FortCombatStep.cs（新規）、Core/MilitaryBase.cs（FortAmmo=1f）、
Core/ResupplyStep.cs（FortAmmo回復）、Game/MilitaryManagerSimTick.cs（配線）、
Test: FortCombatStepTests.cs

**Interfaces (Produces):**
- 定数: BunkerAttack=54/BunkerRange=120/BunkerAccuracy=0.75/BunkerAmmoHours=12、
  ArtAttack=55/ArtRange=120/ArtSplash=30/ArtAccuracy=0.35/ArtAmmoHours=4、LosSampleStep=16
- `FortCombatStep.Advance(state, dt)`: 稼働中（Owner有り・HP>0）のBunker/ArtPostが敵対陸上ユニットへ
  dtスケール連続ダメージ（既存CombatStepと同じ期待値方式・UnitSpatialGrid使用）。Bunkerは
  CoverMapの遮蔽サンプリング（16m間隔、`state.Cover.HasCoverAt(x,z)`相当の既存API確認）で
  射線判定、ArtPostはSplash半径内の全敵対陸上ユニットへ。FortAmmo消費・弾切れ停止。
  ShotEventはFireCooldown相当の間引き（基地にFortFireCooldownフィールド、非永続）
- ResupplyStep: 自軍基地/Depot 200m圏内の稼働築城もFortAmmoを25%/hで回復（供給元規則はユニットと同じ）。実際には築城は動かないので「自分がDepot圏内にあるか」で判定
- Invaderへも通常射撃（Relations既定Hostileで自然に成立）

- [ ] テスト（射撃・射線遮蔽・splash・弾薬消費/枯渇/回復・機能停止後は撃たない）→実装→SimTick配線→全緑→コミット

### Task A4: 守備ボーナスと歩兵の陣地志向

**Files:** Core/FortDefenseBonus.cs（新規）、Core/CombatStep.cs・KamikazeStep.cs（被ダメ軽減適用）、
Core/FortSeekStep.cs（新規）、Game/MilitaryManagerSimTick.cs、Test: FortDefenseBonusTests/FortSeekStepTests

**Interfaces (Produces):**
- `FortDefenseBonus.Multiplier(state, target, targetType)`: 歩兵系（Infantry/MechInfantry）が
  Trench(r=18)/Bunker(r=12)中心圏内→1/1.5f、他1.0。**所有・稼働状態不問**（機能停止Bunkerも有効）
- CombatStep通常射撃・対空命中ダメージ・KamikazeStep起爆・FortCombatStepのダメージへ乗算
- `FortSeekStep.Advance(state, dt)`: AiControlled/FreeAdvanceの歩兵系で600m内に敵対ユニットがいる者を、
  300m内の最寄りTrench/Bunker（自軍所有 or Trench所有不問。既に圏内なら不動）へ
  CoverDestination方式（既存CoverSeekStepのフィールド流用）で移動。定数EnemyRadius=600/SeekRadius=300

- [ ] テスト→実装→配線（CoverSeekStepの後）→全緑→コミット

### Task B1: ヘリ兵科と対ヘリ規則

**Files:** Core/UnitCategory.cs（TransportHelicopter/AttackHelicopter追加）、Core/AirUnitRoster.cs
（AttackHelicopter T1-5: HP90/Attack45/Range100/220km/h/Cost220/Acc0.8/CanTarget=Land）、
Core/LandUnitRoster.cs（TransportHelicopterはAirドメインなのでAirUnitRosterへ両方登録。
TransportHeli: HP60/Attack0/220km/h/Cost80/CanTarget=None）、Core/TargetingRules.cs、
Core/TargetSearch.cs＋UnitSpatialGrid.cs（ヘリ標的例外）、Core/CombatMatchup.cs、
Core/AntiAirCombat.cs（SAM対象にヘリ）、Core/AmmoRules.cs（AttackHeli=3h/TransportHeli=0）、
Core/MovementStep系（ヘリ高度60m・レーストラック非適用）、Core/AiProductionPolicy.cs
（AirCategoriesへAttackHelicopter追加、targets再配分{0.35,0.30,0.15,0.20}）、
Core/UnitCosts.cs（ヘリshare=0.2）、Test: HelicopterRulesTests.cs

**Interfaces (Produces):**
- `TargetingRules.IsHelicopter(UnitCategory)`＝2種true
- `TargetingRules.CanTargetHelicopter(UnitCategory attacker)`＝Tank/AntiAir/AirSuperiorityのみtrue。
  TargetSearch/UnitSpatialGridの候補判定に「target isヘリ→CanTargetHelicopter(attacker)必須」を追加
  （Domainマスク判定の後段。攻撃ヘリ自身のCanTarget=Landは地上のみ＝ヘリ同士交戦なし）
- Tank: CanTargetDomainsへAirを**追加しない**——ヘリ限定なのでTargetSearchのヘリ例外側で
  「attacker=Tank かつ target=ヘリ」を許可する双方向例外にする
- AirCombat.DamageMultiplier: ヘリは1.0（レーストラック補正なし）。MovementStepAirPass: ヘリは対象外
  （通常の地上型接近移動＝AdvanceAirの巡航のみ、高度HeliAltitude=60）
- Matchup: AttackHeli→{Tank1.6,Apc1.6,MechInf1.2,Inf1.2,SupplyTruck1.5}、
  AntiAir→Heli2.5、AirSuperiority→Heli2.0、Tank→Heli0.6
- CanAttackBase/CanAttackThreat: AttackHeli=false/false、TransportHeli=false/false

- [ ] テスト（対ヘリ可否・戦車がヘリを撃てる・歩兵は撃てない・SAM判定・攻撃ヘリの対地）→実装→全緑→コミット

### Task B2: 輸送ヘリ兵站と搭乗

**Files:** Core/UnitInstance.cs（CarriedByUnitId）、Core/TransportHeliStep.cs（新規）、
各step（搭乗中除外: Movement/Combat/Kamikaze/Resupply/SupplyTruck/Stuck/AssignAdvance/TargetSearch）、
Game/MilitaryManagerSimTick.cs、Game/UnitVisuals（搭乗中非表示=Deadと同様スキップ）、
Test: TransportHeliStepTests.cs

**Interfaces (Produces):**
- `UnitInstance.CarriedByUnitId`（uint?、非null=搭乗中）。`UnitInstance.IsCarried`ヘルパ
- 全stepの先頭ループに`if (u.IsCarried) continue;`（TargetSearch/Gridは候補除外）。
  位置追従と運搬役死亡時の道連れはTransportHeliStepが毎tick処理
- `TransportHeliStep`定数: MaxHelisPerFaction=6/HelisPerArmyBase=1/CargoSupply=60/MaxPassengers=3/
  PickupRadius=100/DropRadius=60
- サイクル（ステートレス）: 空荷→基地200m圏で物資積載＋Idle歩兵系搭乗→目的地（空きDepot、無ければ
  味方交戦ユニット重心600m手前）→荷下ろし（Depot StoredSuppliesへ）＋降機（Order/State復元、
  周囲20mへ決定的散開）→帰投。維持スポーンは経済tick（MaintainHelis、トラックと同型）

- [ ] テスト（維持上限・積載/搭乗/降機・撃墜で道連れ・搭乗中は撃たれない）→実装→配線→全緑→コミット

### Task C1: RailGraphと貨物駅

**Files:** Game/RailGraphBuilder.cs（新規、RoadGraphBuilderのレール版: NetSegmentの
Service=PublicTransport&SubService=PublicTransportTrainを辺として既存Core RoadGraphクラスへ格納）、
Core/WarState.cs（`RoadGraph Rails`実行時のみ）、Game/BasePlacementWatcher.cs（CargoStation配置時
100m内レールノード探索→`MilitaryBase.RailConnected`設定）、Core/MilitaryBase.cs（RailConnected、v10）、
Game/MilitaryManagerSimTick.cs（12hごと再構築＋既存駅のRailConnected再判定）、
Test: 駅スナップはCore側ロジック（`CargoStationRules.IsOperational(base)`）のみテスト

- [ ] RailGraphBuilder実装（RoadGraphBuilderコピー改変）→駅接続判定→配線→全緑→コミット

### Task C2: 軍用列車

**Files:** Core/UnitCategory.cs（MilitaryTrain追加）、Core/LandUnitRoster.cs（MilitaryTrain_T1:
HP500/Attack0/160km/h/Cost150/CanTarget=None/AmmoHours0。Domain=Land・レール専用移動）、
Core/TrainStep.cs（新規）、Core/MovementStep.cs（MilitaryTrainはPath必須・直線フォールバック無し）、
Core/AiTargeting.cs＋CombatStep.cs（トラック同様の除外）、Game/MilitaryManagerSimTick.cs、
Test: TrainStepTests.cs

**Interfaces (Produces):**
- `TrainStep`定数: MaxTrainsPerFaction=4/CargoSupply=200/BoardRadius=150/MinStationDistance=2000/
  BoardDetourAdvantage=1000/UnloadRadius=60
- 運行（ステートレス、CarriedByUnitId再利用で搭乗）: 稼働駅ペア（RailGraphで経路あり・2km以上）を
  決定的に列挙（BaseId昇順ペア）→ペアごとに列車1編成維持（経済tickでスポーン、UnitCosts支払い）→
  基地側駅（自軍基地への最短距離が小さい方）で物資200＋条件を満たす陸上ユニット搭乗→
  RailGraph A*経路で走行→到着駅で降車・StoredSuppliesへ荷下ろし→折り返し
- 搭乗条件: AiControlled/FreeAdvanceのMoving陸上ユニット（トラック含む・列車自身除く）、
  駅150m内、`dist(unit.OrderTargetPos, 到着駅) + 1000 < dist(unit.OrderTargetPos, 現在駅)`

- [ ] テスト（ペア列挙・維持・搭乗条件・輸送→降車で目的地保持・撃破で全損）→実装→配線→全緑→コミット

### Task D1: セーブv10

**Files:** Core/WarStateSerializer.cs、Test: WarStateSerializerTests.cs

- v10追記ブロック: 基地{BaseIdキー並列: StoredSupplies, FortAmmo, RailConnected}＋
  ユニット{InstanceIdキー並列: CarriedByUnitId(hasValue+uint)}。v9以前は既定値
  （StoredSupplies0/FortAmmo1/RailConnected false/未搭乗）
- [ ] ラウンドトリップ＋v9互換テスト→実装→全緑→コミット

### Task D2: モデル・表示・stats・UI

**Files:** Blender MCPで8モデルをsrc/CSWarfront/Models/へエクスポート
（Unit_TransportHeli/Unit_AttackHeli/Unit_MilitaryTrain/Fort_Bunker/Fort_ArtilleryPost/
Fort_SupplyDepot/Fort_Trench※新モデル/Fort_CargoStation）、Game/UnitMeshSource.cs（3ユニット）、
Game/BaseVisuals系（築城5種のモデルキー）、Game/UnitStatsFile.cs（ヘリ2種+列車のテンプレ行）、
Game/UI/BaseInfoPanel.cs（築城: StoredSupplies/FortAmmo表示、生産UI非表示）、
Game/OptionsBaseBuildingPage.cs（築城5種の指定行追加）

- [ ] エクスポート（既存スクリプト方式・.blend非破壊）→表示配線→net35ビルド緑→コミット

### Task D3: 統合仕上げ

- [ ] SimTick順序確認（FortSeek→Movement→Resupply→Truck→Heli→Train→FortCombat→Combat）
- [ ] 全テスト緑×2＋build.ps1配置→docs/TODO.md・メモリ更新→コミット

## Self-Review
- 仕様§1.1-1.4=A1-A4、§2=B1-B2、§3=C1-C2、§4=D1-D3で全節カバー。
- 型名はFortificationRules/FortCombatStep/FortDefenseBonus/FortSeekStep/TransportHeliStep/
  TrainStep/CargoStationRulesで一貫。CarriedByUnitIdはB2定義・C2/D1で再利用。
- 実装中に確定する詳細（CoverMapの遮蔽API名、BaseVisualsのキー方式、レールのSubService定数名）は
  該当タスクで既存コードを読んで合わせる。
