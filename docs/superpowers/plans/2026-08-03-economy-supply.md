# 経済・補給システム（Update 2）実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 3資源経済（人的資源/資金/生産力）と弾薬・補給ロジスティクス（弾薬ゲージ・補給物資・補給トラック・基地圏内再補給・航空機再武装）を実装する。

**Architecture:** Core（UnityEngine非依存・決定的・xunit）に純ロジックを追加し、Game層はゾーン別発展度サンプリング・SimTick配線・UI表示のみ。セーブはv9を1回だけ上げる。

**Tech Stack:** C# 7.3 / net35（Game）+ net8.0（tests）、xunit。

## Global Constraints

- 乱数不使用（決定的シミュレーション）。System.Random禁止、必要ならfmix32ハッシュ。
- ファイル500行以内。新ステップは独立ファイル。
- CS実体はsimスレッド専用、Unity APIはメインスレッド専用。
- 旧セーブ（v8以前）は既定値で読める（資源は初期付与、弾薬満タン）。
- Invader勢力（Faction.InvaderFactionId=5）は弾薬無限・補給トラック無し。
- 仕様書: docs/superpowers/specs/2026-08-03-economy-supply-design.md

---

### Task A1: ゾーン別発展度→3資源の産出

**Files:**
- Modify: `src/CSWarfront/Core/Faction.cs`（Manpower/Production追加）
- Modify: `src/CSWarfront/Core/TerritoryIncome.cs`（ZoneKind・ZonedIncome）
- Modify: `src/CSWarfront/Game/DevelopmentSampler.cs`（Service種別の分類）
- Modify: `src/CSWarfront/Game/MilitaryManagerSimTick.cs`（経済tickの3資源加算）
- Modify: `src/CSWarfront/Game/MilitaryManager.cs`（初期付与）
- Test: `tests/CSWarfront.Core.Tests/TerritoryIncomeTests.cs`（既存を拡張）

**Interfaces (Produces):**
- `enum ZoneKind { Other, Residential, CommercialOffice, Industrial }`（DevelopmentSampleに`Zone`フィールド追加）
- `struct ZonedIncome { float Manpower, Funds, Production }`
- `TerritoryIncome.ZonedForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)` → ZonedIncome。スキャン半径は`TerritoryIncome.EconomyRadius = 1000f`（InfluenceRadius=500は他機構用に不変）
- `Faction.Manpower/Production { get; private set; }` + `AddManpower/AddProduction/TrySpendManpower/TrySpendProduction`（Treasuryと同じ規約）
- 初期付与: `InitialManpower = 200f`, `InitialProduction = 200f`（新規ゲーム時、資金200と並び）

**Steps:**
- [ ] ZoneKind/ZonedForBaseの失敗テスト（住宅→Manpowerのみ、商業→Fundsのみ、工業→Productionのみ、1000m外は無視）→実装→緑
- [ ] Faction資源プールのテスト（Add/TrySpendの境界）→実装→緑
- [ ] DevelopmentSampler: `buf[i].Info.m_class.m_service`で分類（Residential→Residential、Commercial/Office→CommercialOffice、Industrial→Industrial、他→Other）
- [ ] SimTick経済tick: `ZonedForBase`で3資源加算、`b.LastIncome`はFunds分を維持
- [ ] 全テスト緑→コミット

### Task A2: ユニットコストのM/P分解と資金代替

**Files:**
- Create: `src/CSWarfront/Core/UnitCosts.cs`
- Modify: `src/CSWarfront/Core/ProductionPlanning.cs`, `AiProductionPolicy.cs`, 手動生産（ManualProduction）
- Test: `tests/CSWarfront.Core.Tests/UnitCostsTests.cs`

**Interfaces (Produces):**
- `UnitCosts.FundsPerProduction = 2f`
- `UnitCosts.ManpowerShare(UnitCategory)`: Infantry/MechInfantry/DroneInfantry=0.6、Apc/Artillery/AntiAir=0.4、Tank=0.3、Sea/Air系=0.2、SupplyTruck=0.5、既定0.4
- `UnitCosts.ManpowerCost(UnitType) = Cost×share`、`ProductionCost(UnitType) = Cost×(1-share)`
- `UnitCosts.CanAfford(Faction f, UnitType t, float fundsCap)`: Manpower≥mc かつ Production+fundsCap/FundsPerProduction≥pc
- `UnitCosts.TryPay(Faction f, UnitType t, float fundsCap)`: 全額払える時のみ消費（生産力優先、不足分×2を資金から）。fundsCap=研究準備金を除いた資金上限

**Steps:**
- [ ] テスト（分解値・生産力のみで足りる/資金代替/払えない/all-or-nothing）→実装→緑
- [ ] ProductionPlanning: `f.TrySpend(type.Cost)`→`UnitCosts.TryPay(f, type, spendCap)`。AiProductionPolicy.ChooseTierHedgedの`t.Cost > spendCap`→`!UnitCosts.CanAfford(faction, t, spendCap)`（Decideシグネチャ内でfaction利用可）
- [ ] ManualProduction（Game/UI経由のCore入口）も同じ支払いへ
- [ ] 既存テスト修正（Treasury残高前提のテストにManpower/Production付与が必要になる）→全緑→コミット

### Task B1: 弾薬ゲージ

**Files:**
- Modify: `src/CSWarfront/Core/UnitType.cs`（AmmoCombatHours、ctor末尾追加）
- Modify: `src/CSWarfront/Core/UnitInstance.cs`（`float Ammo = 1f`）
- Create: `src/CSWarfront/Core/AmmoRules.cs`
- Modify: `CombatStep.cs`（通常射撃+対空射撃）、`BaseCombatStep.cs`、`ThreatCombatStep.cs`
- Modify: 全ロスター（Land/Naval/Air）+ `UnitStatOverrides.cs` + `Game/UnitStatsFile.cs`（ammoCombatHours）
- Test: `tests/CSWarfront.Core.Tests/AmmoRulesTests.cs`

**Interfaces (Produces):**
- `UnitType.AmmoCombatHours`（0=弾薬無限）。値: Infantry/MechInfantry 12、Tank/Apc 8、AntiAir 6、Artillery 4、DroneInfantry 8、Destroyer 8、Carrier 0、AirSuperiority 3、TacticalBomber 3、SuicideDrone 0
- `AmmoRules.HasAmmo(UnitInstance u, UnitType t)`: t.AmmoCombatHours<=0 or u.FactionId==Invader → 常にtrue
- `AmmoRules.ConsumeFire(UnitInstance u, UnitType t, float dt)`: 対象ユニットのみ `Ammo -= dt/AmmoCombatHours`（下限0）
- 射撃3ステップ+対空: ダメージ適用の直前に`if (!AmmoRules.HasAmmo(...)) continue;`、適用後に`ConsumeFire`。弾切れはターゲット解除（State=Idleへ、通常のtarget==null経路と同じ）

**Steps:**
- [ ] AmmoRulesテスト（消費、枯渇で射撃停止、Invader無限、AmmoCombatHours=0無限）→実装→緑
- [ ] CombatStep/BaseCombatStep/ThreatCombatStep/対空へ統合、統合テスト（弾切れユニットがダメージを与えない）
- [ ] 航空機: 弾切れ（Ammo<=0）の航空ユニットはAssignAdvance/air目標選定で「目標なし」扱い→既存ReturnHomeで帰還（MovementStepReturnHome/AiTargetingの航空分岐に`HasAmmo`条件を追加）
- [ ] unit-stats.xml: ammoCombatHours属性の読み書き＋テンプレ生成
- [ ] 全緑→コミット

### Task B2: 補給物資ストックと基地圏内補給

**Files:**
- Modify: `src/CSWarfront/Core/Faction.cs`（SupplyStock）
- Create: `src/CSWarfront/Core/ResupplyStep.cs`
- Modify: `src/CSWarfront/Game/MilitaryManagerSimTick.cs`（経済tickで物資生産、毎tickでResupplyStep）
- Test: `tests/CSWarfront.Core.Tests/ResupplyStepTests.cs`

**Interfaces (Produces):**
- `Faction.SupplyStock`（get/AddSupply/TrySpendSupply、上限`SupplyStockCap = 1000f`）
- `ResupplyStep.ProduceSupplies(Faction f)`: 経済tickごとに最大`SupplyPerEconomyTick = 50`生産。1物資=1生産力（不足分は×FundsPerProductionの資金で代替）
- `ResupplyStep.Advance(WarState state, float dt)`: 自基地200m以内（`ResupplyRadius = 200f`）の味方（弾薬対象ユニット）へ`RefillPerHour = 0.25`回復。消費=`SupplyPerFullReload = 10`×回復量。空母は航空機に対してのみ補給点（同半径・同レート）。SupplyStock切れは回復停止。Invaderは対象外（常に満タン扱いなので来ない）
- 航空機は満タン+Idle→次のAssignAdvanceで自然に再出撃（既存挙動）

**Steps:**
- [ ] テスト（生産と上限、基地圏内回復、圏外は回復しない、ストック切れ停止、空母→航空機のみ）→実装→緑
- [ ] SimTick配線（経済tick内でProduceSupplies、通常tickでResupplyStep.Advance）→コミット

### Task B3: 補給トラック

**Files:**
- Modify: `src/CSWarfront/Core/UnitCategory.cs`（末尾にSupplyTruck）
- Modify: `src/CSWarfront/Core/LandUnitRoster.cs`（SupplyTruck_T1登録、Tier1のみ）
- Modify: `src/CSWarfront/Core/UnitInstance.cs`（`float SupplyLoad`）
- Create: `src/CSWarfront/Core/SupplyTruckStep.cs`
- Modify: `src/CSWarfront/Core/AiTargeting.cs`（AssignAdvanceからSupplyTruck除外）
- Modify: `src/CSWarfront/Core/CombatStep.cs`（Attack<=0はターゲット選定スキップ）
- Modify: `src/CSWarfront/Game/MilitaryManagerSimTick.cs`（SupplyTruckStep配線）
- Test: `tests/CSWarfront.Core.Tests/SupplyTruckStepTests.cs`

**Interfaces (Produces):**
- SupplyTruck_T1: Domain.Land、HP40、Attack0、速度45km/h、Cost30、CanTargetDomains=None、AmmoCombatHours=0、AssetPrefabName既定
- 上限: `SupplyTruckStep.MaxTrucksPerFaction = 30`（戦闘150体と別枠——ProductionPlanning.MaxUnitsPerFactionのカウントからSupplyTruckを除外する）、`TrucksPerArmyBase = 2`
- 維持: 経済tickごと、陸軍基地1つにつき保有トラックが枠未満なら1台スポーン（UnitCosts.TryPayで支払い。Invader除外）
- 配車ロジック（毎tick、状態はSupplyLoad/位置から導出）:
  - SupplyLoad<=0: 最寄り自軍陸軍基地へ（200m以内で`TruckCapacity=1f`まで積載、SupplyStockから`SupplyPerTruckLoad = 30`消費）
  - 積載あり: 弾薬が最も少ない味方陸上ユニット（Ammo<0.5、トラック除く）へ道路経路で移動、`TransferRadius = 60f`以内の味方陸上ユニットへ`RefillPerHour=0.5`で転送（SupplyLoad消費: 回復量×0.2）
  - 補給対象がいなければ基地付近で待機（Idle）
- 撃破時は積荷ごと消失（特別処理不要）

**Steps:**
- [ ] enum/ロスター/除外（AssignAdvance・CombatStep・ProductionPlanningの150カウント）→既存テスト緑
- [ ] SupplyTruckStepテスト（維持スポーン・上限30・積載・配送・転送・対象なし待機）→実装→緑
- [ ] SimTick配線→コミット

### Task B4: セーブv9

**Files:**
- Modify: `src/CSWarfront/Core/WarStateSerializer.cs`（Version=9）
- Test: `tests/CSWarfront.Core.Tests/WarStateSerializerTests.cs`

**Interfaces:** v9追記ブロック＝各Faction{Manpower, Production, SupplyStock}＋各Unit{Ammo, SupplyLoad}。v8以前の既定: Manpower=200/Production=200/SupplyStock=200（初期付与相当）、Ammo=1/SupplyLoad=0。

**Steps:**
- [ ] v9ラウンドトリップテスト＋v8読み込み既定値テスト→実装→緑→コミット

### Task B5: UI表示

**Files:**
- Modify: `src/CSWarfront/Game/BaseUiSnapshot.cs`＋基地/勢力パネル（3資源+物資表示）
- Modify: ユニット情報パネル（弾薬%表示）

**Steps:**
- [ ] スナップショットへManpower/Production/SupplyStock/Ammoを追加し、既存パネルの表示行を拡張（新規パネルは作らない）
- [ ] net35ビルド緑→コミット

### Task B6: 統合仕上げ

- [ ] SimTickの実行順確認: 経済（3資源+物資+トラック維持）→AI→移動→Resupply→戦闘
- [ ] 全テスト緑、build.ps1で実機配置
- [ ] docs/TODO.md更新（補給実装済み、次期候補）
- [ ] コミット

## Self-Review
- 仕様§1-6すべてタスクに対応（§5 UIはB5、§4.2空母はB2、Invader例外はB1/B3）。
- 型名・定数はタスク間で一貫（UnitCosts/AmmoRules/ResupplyStep/SupplyTruckStep）。
- 実装中に判明する詳細（MovementStepReturnHomeの帰還トリガ、CombatMatchupの未知カテゴリ既定値）は該当タスク内で既存コードを読んで合わせる。
