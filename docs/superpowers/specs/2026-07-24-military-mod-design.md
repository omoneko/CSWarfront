# Cities: Skylines 大型軍事MOD 設計書

- **対象**: Cities: Skylines（2015 / 無印, Unity 5.6）
- **モッディング基盤**: ICities API + Harmony、既存災害系MOD（Missile / Godzilla / Alien）と同じ `Core` / `Game` / `Models` 構成
- **作業名（暫定）**: CS Warfront（アセンブリ/MOD名。最終名は実装開始時に確定）
- **作成日**: 2026-07-24

---

## 1. 目的とビジョン

CS無印の3D都市の上に、HoI（Hearts of Iron）風の**Tier制軍事シミュレーション層**を自作する。最大5勢力が敵対/中立/同盟の関係を持ち、基地からユニットを生産・出撃させ、勢力圏の都市発展度から得た軍資金で戦争を継続し、敵基地を陥落させて占領・拡大する。さらに**弾道ミサイル発射場**から弾道弾を発射して敵基地・敵勢力圏を攻撃し、**迎撃ユニット**でそれを防ぐ、戦略兵器と防空の駆け引きを持つ（既存 MissileDisaster MOD の飛翔・迎撃・弾頭資産を流用）。

### プレイヤーの立場（A+B 両対応）
- **A. 観戦モード**: プレイヤー勢力を置かず、全勢力をAIが操作。配置と関係を設定して戦争を眺める・介入して遊ぶ。
- **B. 指揮モード**: プレイヤーが1勢力を操作、残りをAIが担当。生産・侵攻を指示して勝利を目指す。
- 両モードは **同一の SimulationCore** の上で成立する。コアに流れ込むのは「命令（生産する / あの基地へ侵攻）」だけで、それを出すのがAIかプレイヤーUIかの違いにすぎない。モード切替は Controller の差し替えのみで、コアは不変。

---

## 2. スコープと段階的ビルド計画

一度に全機能は作らない。**縦に薄く貫くMVP**を先に動かし、動作確認後に横（兵種・Tier・海空・モード・Workshop割当）へ拡張する。

### ビルド順

| 順 | 塊 | 中身 | 位置づけ |
|----|----|------|----------|
| 1 | **Factionコア** | 5勢力データ・関係マトリクス・軍資金・拠点参照 | 全ての土台 |
| 2 | **Unit＋戦闘コア** ⚠️ | データ駆動ユニット定義＋アセット紐付け、2体が撃ち合いHP0で撃破 | **最大リスク。最優先で潰す** |
| 3 | **基地＋スポーン** | 基地建物、そこから部隊を出撃 | |
| 4 | **勢力圏＋経済** | 基地中心の半径エリア→圏内発展度→軍資金→生産キュー | ループを自走させる心臓 |
| 5 | **侵攻＋占領AI** | 敵対勢力が相手基地を攻撃、基地HP0で占領・資産移管 | |
| 6 | **弾道ミサイル＋迎撃** | 発射場・弾頭別備蓄・弾道飛翔・迎撃ユニット・基地/都市への着弾（MissileDisaster流用） | コア安定後に載せる自己完結サブシステム |
| ─ | **横断：プレイヤー指揮層 / アセット割当UI** | 観戦⇔指揮の切替、Workshopアセット差し替え | コア安定後に被せる |

塊6（弾道ミサイル＋迎撃）はMVP範囲外だが、MissileDisasterの成熟資産を流用するため比較的自己完結で追加できる。塊1〜5のコアが安定した後に載せる。

### MVP（最初のゴール）
> 2勢力・各1基地・地上ユニット1種・敵対関係。両者が軍資金を貯めて部隊を生産・出撃させ、撃ち合い、勝った側が相手基地を（HPを削りきって）占領し、基地・圏・生産キューを奪取する——が実機で回る。

これが動けば「MODとして成立する」ことが証明される。以降は兵種30種・Tier5段階・海空・A+Bモード・Workshop割当を安全に積み増す。

---

## 3. アーキテクチャ

### 3.1 表現方式：ハイブリッド（案3）
- **地上ユニット** = 本物のCS Vehicle（`VehicleManager`でスポーン）。道路パスファインディングを流用し「街を戦車が進む」絵と軽さを取る。→ 移動は経路ベース。
- **海上・空中ユニット** = 自作の軽量エンティティ。自前のステアリングで自由移動。
- **戦闘・HP・所属・Tier数値・占領** は、表現に依存しない単一の `MilitaryManager` / `SimulationCore` に集約。地上/海/空は「移動アダプタ」だけ差し替え、戦闘ロジックは共通。
- アセット紐付けは「プレハブ名→メッシュ/マテリアル」で両表現に供給。

**選定理由**: 移動C（地上=経路、海空=自由）＋規模A（数十体）＋データ駆動アセットに最も素直に噛み合う。地上は既存の強みを流用し、海空だけ自作、戦闘は一本化。コストは移動コードが2系統になる点のみ。

### 3.2 レイヤー構成

```
┌─ UI層（HoI風パネル：勢力/関係/生産キュー/アセット割当）
├─ Controller層
│   ├─ AIController（非プレイヤー勢力：生産判断＋侵攻命令）
│   └─ PlayerCommandController（プレイヤー勢力：同じ「命令」を発行）  ← A+Bモード切替はここ
├─ SimulationCore（★モード非依存・テスト可能な純ロジック）
│   ├─ FactionManager（5勢力・関係マトリクス・軍資金）
│   ├─ UnitRegistry（データ駆動UnitType定義＋UnitInstance群）
│   ├─ CombatResolver（射程内探索→数値ダメージ→撃破。基地も攻撃対象）
│   ├─ TerritoryManager（基地中心の半径エリア→発展度→収入）
│   └─ OccupationManager（基地HP0で基地・圏・生産を移管）
├─ MovementAdapter（Land=車両流用 / Sea・Air=自作）
├─ AssetBinding（UnitType→プレハブ名 の解決。Workshop差し替え）
└─ Data（XML/JSON：UnitType定義・アセットマッピング・シナリオ）
```

**設計原則**: SimulationCore はモードにも表現にも依存しない純ロジックとし、単体テスト可能に保つ。各ユニット（Manager）は単一責務・明確なインターフェースで、独立して理解・テストできる。ファイルは500行以内・多数の小ファイル構成（ユーザー規約準拠）。

---

## 4. データモデル

### 4.1 Faction（勢力） — 最大5
```
FactionId        byte        // 0-4
Name             string
Color            Color32     // 圏・アイコン・ユニット色に反映
Treasury         float       // 軍資金ポイント
HomeBaseId       ushort?     // 拠点として設定した基地（占領で変動）
IsPlayer         bool        // B:プレイヤー操作勢力か（A観戦なら全false）
ProductionQueue  List<ProductionOrder>
```

### 4.2 関係マトリクス — 5×5・対称
```
Relation[a,b] ∈ { Hostile, Neutral, Allied }   // 敵対 / 中立 / 同盟
```
- 対称管理（A→B と B→A は同値）。UIで1セル変更すると鏡側も更新。
- **同盟** = 相手基地に侵攻しない＆圏を共有可。**中立** = 不干渉。**敵対** = 侵攻・攻撃対象。

### 4.3 UnitType（ユニット定義：データ駆動 XML/JSON） — 種別×Tierごとに1エントリ
```
TypeKey          string      // "Tank_T3" など一意
Domain           enum        // Land / Sea / Air
Category         enum        // 下記ラインナップ参照
Tier             byte        // 1-5
── 戦闘数値 ──
MaxHP            float
Attack           float       // 火力
Range            float       // 射程(m)
Armor            float       // 装甲（被ダメ軽減）
Speed            float
── 特殊 ──
CanTargetDomains flags       // 対空=空のみ / 潜水艦=水中 等
SplashRadius     float       // 砲兵・爆撃機
IsPlatform       bool        // 対地攻撃機=巡航ミサイル発射母機 等
── 経済 ──
Cost             float       // 軍資金
BuildTime        float
── 表現 ──
AssetPrefabName  string      // ★Workshop含む任意プレハブ名。空なら既定流用へフォールバック
IconPath         string
TintByFaction    bool        // 勢力色で染めるか
```

**Tierの表現方針**: Tier差は基本的に**数値（HP/火力/射程/装甲/速度/コスト）＋アイコン/色**で表現。加えて、Tierごとに別アセット（`AssetPrefabName`）を割り当てることも許容（各Tierが独立エントリなので自然に対応）。

**ユニット・ラインナップ（Category）**
- **陸上**: 戦車 / 装甲車 / 機械化歩兵 / 砲兵 / ドローン兵 / 歩兵 / 対空＝**迎撃ユニット**（THAAD・PAC3流用。航空機に加え弾道ミサイルを撃墜。§4.7/§5.5参照）
- **海上**: 空母 / 巡洋艦 / 駆逐艦 / フリゲート艦 / 機雷敷設艦 / 掃海艦 / 潜水艦 / 高速艇 / 自爆ボート / 海上ドローン
- **航空**: 制空戦闘機 / 対地攻撃機（巡航ミサイル発射プラットフォーム兼務）/ 戦術爆撃機 / 戦略爆撃機 / 電子戦機 / 早期警戒機
- 各Categoryに Tier 1〜5 を設定可能（全定義は約30種×5Tier ≈ 150エントリ。MVPでは数エントリのみ定義し順次拡張）。

### 4.4 UnitInstance（実行時の1体）
```
InstanceId       uint
TypeKey          string      // → UnitType参照
FactionId        byte
CurrentHP        float
RepresentationRef            // Land:vehicleID / Sea・Air:カスタムエンティティ参照（※非永続）
Position         Vector3
State            enum        // Idle / Moving / Engaging / Dead
TargetId         uint?       // 交戦相手（ユニット or 基地）
OrderTargetPos   Vector3?    // 侵攻先（基地座標など）
```

### 4.5 Base（軍事施設）
```
BaseId           ushort      // CS建物ID（BuildingManager）にひも付け
BaseType         enum        // Army / Navy / AirForce / MissileBase   ← 発射場を追加
OwnerFactionId   byte?       // 未占領はnull
SpawnableDomains flags       // 陸→Land, 海→Sea, 空→Air のみ生産可
InfluenceRadius  float       // 勢力圏の半径
IsHeadquarters   bool        // その勢力の拠点(HQ)か
MaxHP            float       // 基地体力
CurrentHP        float       // 0で陥落→占領
LocalQueue       List<ProductionOrder>  // 占領時に相手へ移管
MissileStockpile List<StockpiledMissile>  // MissileBaseのみ。占領時に相手へ移管（弾ごと奪取）
```

- **MissileBase（弾道ミサイル発射場）** は陸/海/空と並ぶ第4の基地種別。ユニットではなく**弾道ミサイル（弾頭別）を製造・備蓄**し、任意の敵目標へ発射する。占領されると備蓄弾ごと奪われる。

### 4.6 ProductionOrder
```
TypeKey          string
Progress         float       // 0-1
Cost             float
```

### 4.7 弾道ミサイル・迎撃の追加データ（塊6）

MissileDisaster の `WarheadType` / `WarheadSpec` / `InterceptorTier` を流用する。

**WarheadType（弾頭種別）** — MissileDisaster から流用
```
Conventional / Cluster / WhitePhosphorus / Thermobaric / Nuclear
```
- 各弾頭は `WarheadSpec`（実寸較正の破壊/延焼/汚染半径、地上/空中爆発）を持つ。核のみ汚染あり。

**MissileOrder（発射場の製造キュー要素）** — ProductionOrder の弾道弾版
```
Warhead          WarheadType
YieldMultiplier  float       // 威力係数（>1で高威力）
Cost             float       // = 弾頭ベースコスト × YieldMultiplier（核・高威力ほど高額＝抑止力）
BuildTime        float       // 同様に威力で増加（核は長時間）
Progress         float       // 0-1
```

**StockpiledMissile（完成・備蓄済みの1発）**
```
Warhead          WarheadType
YieldMultiplier  float       // 発射時に MissileManager.Launch へ渡す
```

**InterceptorProfile（迎撃ユニット＝対空UnitTypeの追加属性）** — `InterceptorTier` 相当
```
AltitudeMin      float       // 交戦可能な高度帯（下限）
AltitudeMax      float       // 高度帯（上限）
HorizontalRange  float       // 水平交戦距離
InterceptChance  float       // 単発命中確率(Pk)
Cooldown         float       // 1交戦=1発、撃つとクールダウン
```
- 対空Categoryの UnitType はこの `InterceptorProfile` を持ち、Tierごとに高度帯・射程・命中率が向上（例：PAC3=低〜中高度、THAAD=高高度）。MissileDisaster の `InterceptDecision`（純ロジック）で判定するので、建物ではなく**ユニット位置ベース**に接続するだけで流用できる。

### 4.8 設計上のポイント
- UnitType は完全にデータ（XML/JSON）で外出し。兵種/Tier/数値/アセットをコード変更なしで調整でき、Workshopアセット割当UIもこのデータを書き換えるだけ。
- 占領は Base 単位で完結：`OwnerFactionId` とキュー・圏が Base に乗るので、所有者を書き換えるだけで基地・圏・生産中ユニットがまとめて移管される（要件⑤）。
- CS建物ID/車両IDは `ushort` で本体の管理配列に整合。

---

## 5. シミュレーションの動作（各tick）

`MilitaryManager` が `ThreadingExtension.OnUpdate`（または専用MonoBehaviour）で駆動。**役割ごとに更新頻度を分け**、全処理を毎フレーム行わない（戦闘=高頻度、経済・AI=低頻度）。

### 5.1 戦闘tick（高頻度） — CombatResolver
```
各 UnitInstance について:
  State==Moving なら OrderTargetPos へ前進（Land=車両パス, Sea/Air=自作ステアリング）
  射程(Range)内に「敵対関係の敵ユニット or 敵対基地」がいるか探索（空間グリッドで近傍のみ）
    → いれば State=Engaging、最も近い/脅威度の高い対象を Target に
  Engaging中は cooldownごとに:
     damage = max(1, Attack - Target.Armor)（SplashRadiusあれば範囲対象にも適用）
     CanTargetDomains を満たす時のみ命中（対空=空のみ、潜水艦=水中 等）
     Target.CurrentHP -= damage
  CurrentHP<=0 → State=Dead：撃破エフェクト付きで表現除去（既存爆発流用）、レジストリから削除
```
- 近傍探索は**空間ハッシュ/グリッド**で O(n²) を回避（数十体でも最初から導入）。
- **基地も攻撃対象**：攻撃側ユニットが敵対基地の射程内に入れば基地HPを削る。守備ユニットが射程内にいれば先にそちらと交戦し「壁」になる。

### 5.2 経済tick（低頻度：例 ゲーム内“週”ごと） — TerritoryManager
```
各 Base（OwnerFactionあり）について:
  圏 = 中心から InfluenceRadius 内
  圏内の都市発展度を集計（CS建物のレベル/人口密度を合算）
      ※ 発展度ソースは既存の建物データ取得経路（District wellbeing系フィールド）と同系を使用
  income = 発展度 × レート
  Faction.Treasury += income
圏の重なり:
  敵対勢力の圏が重複 → 係争地として双方減額（または近い基地が取得）
  同盟勢力の圏は共有可
```

### 5.3 生産tick — FactionManager
```
ProductionQueue先頭の BuildTime を進める
  AI勢力: Treasury と戦況から「何を作るか」を AIController が投入
  Player勢力: UI から同じ ProductionOrder を積む
完了 → 所有基地（SpawnableDomains一致）からスポーン、State=Idle
```

### 5.4 侵攻・占領AI（低頻度） — AIController ＋ OccupationManager
```
AIController（非プレイヤー勢力ごと）:
  敵対関係の基地を列挙 → 距離/戦力比で目標を選定
  手持ちユニットに OrderTargetPos=目標基地 を与えて進軍
  生産判断: 守備が薄い→防御ユニット, 攻勢可→攻撃ユニット

OccupationManager:
  攻撃側ユニットが敵対基地を攻撃し、基地 CurrentHP <= 0 に到達 → 占領成立
  → Base.OwnerFactionId = 攻撃側
  → CurrentHP を回復（満タン or 一定%）して再稼働
  → LocalQueue・圏収入がそのまま攻撃側へ移管（要件⑤）
  → 拠点(HQ)を失った勢力は敗退・脱落
```

### 5.5 弾道ミサイル・迎撃tick（塊6） — MissileWarfareManager
MissileDisaster の実証済みスレッド境界を踏襲：**メインスレッド＝飛翔・迎撃**、**simスレッド＝着弾ダメージ**を、ロック保護した小さな値キュー（座標＋WarheadSpec）で受け渡す。飛翔中リストをスレッド跨ぎで共有しない。

**製造・備蓄（生産tickの一部）**
```
MissileBase の LocalQueue に MissileOrder（弾頭・威力）を積む
  Cost = 弾頭ベースコスト × YieldMultiplier、BuildTime も威力で増加（核＝高コスト・長時間＝抑止力）
完了 → base.MissileStockpile に StockpiledMissile を1発追加
```

**発射（命令）**
```
発射条件: MissileBase に該当弾の在庫あり
目標選定:
  Player勢力(B): UIで着弾点を指定（敵基地 or 敵勢力圏の都市エリア）
  AI勢力(A):   AIController が敵基地/敵勢力圏から目標を選定
発射: MissileManager.Launch(target, warhead, yieldMultiplier, burst) を呼ぶ（在庫を1減）
```

**飛翔・迎撃（メインスレッド・高頻度）**
```
飛翔中の各 Missile について（MissileManager が apex→target を降下補間）:
  射程・高度帯内の「敵対勢力の迎撃ユニット」を探索
    InterceptDecision.ShouldIntercept(missileAltitude, horizontalDistance, InterceptorProfile, roll)
    命中確定 → missile.MarkDoomed()（以後ダメージ無効）、迎撃FX、迎撃ユニットはCooldown消費
  ※ 迎撃網が薄い/飽和攻撃で撃たれると貫通 → 高威力弾ほど「撃つ隙」を突く駆け引き
```

**着弾（simスレッド）**
```
Doomed でない弾が着弾 → ImpactQueue から WarheadSpec で解決:
  A. 敵基地に命中圏 → Base.CurrentHP を弾頭威力に応じて減算（0で占領＝§5.4）
  B. 敵勢力圏の都市に命中 → 圏内建物を破壊（DestructionRadius/BurnRadius）
       → 発展度が下がり、その勢力の経済tick収入が減少（経済戦）
     核弾頭は汚染（ContaminationRadius）も発生し、圏の回復を長期に阻害
```

### 5.6 コントローラとモード
- **プレイヤー指揮層(B)** は AIController と**同じ命令**（生産投入・進軍先指定）をUI経由で発行するだけ。
- **観戦モード(A)** はプレイヤー勢力を置かず全勢力をAIが担当。
- 切替はコントローラの差し替えのみで、SimulationCoreは不変。

---

## 6. アセット・バインディング（Workshop対応）

- CS無印はWorkshopアセットも起動時に全てプレハブとしてロードされる。ユニット定義は `AssetPrefabName`（文字列）でプレハブを参照するデータ駆動方式。
- 既定は本体/DLC車両を流用（戦車風・ヘリ・船 等）、色替え・スケール変更で兵種を表現。目玉ユニット（空母・戦略爆撃機・THAAD等）は自作Blenderモデルに差し替え可。
- **アセット割当UI**：ロード済みの任意プレハブ（本体/DLC/Workshop）を各UnitTypeに割り当て。設定は UnitType データを書き換えるだけ。
- **解決失敗時**（Workshop未購読等）→ 既定プレハブへフォールバック＋ログ出力。
- **弾道ミサイル・迎撃の見た目（塊6）**：飛翔体モデル（弾頭obj）・迎撃トレイル・爆発/キノコ雲FX・発射/着弾音は MissileDisaster の `RenderAssets` / `MissileModelProvider` / `*Fx` / `SoundLibrary` を流用（コード再実装不要）。読込不可時は球フォールバックも既存挙動を踏襲。

---

## 7. 永続化（セーブ/ロード）

`ISerializableDataExtension` で軍事状態をセーブデータに保存。**最初から対応する**。

- 保存対象：Faction（軍資金・関係・拠点・playerフラグ）、Base（所有者・HP・キュー・圏半径・**MissileStockpile**）、UnitInstance（種別・所属・HP・位置・状態・命令）。
- **飛翔中の弾道弾/迎撃体は非永続**（ロード時に消える）＝一過性の演出。備蓄弾（`MissileStockpile`）は論理データなので保存し、占領時の弾ごと奪取も再現される。汚染ゾーンは MissileDisaster の `ContaminationDataExtension` 系を流用して保存。
- **重要**：CSの `vehicleID` / エンティティ参照はロードで不変ではないため、**保存するのは論理状態（UnitInstance）のみ**。ロード時に論理状態から**表現（車両/エンティティ）を再生成・再リンク**する。`RepresentationRef` は非永続フィールド。
- アセット参照は `AssetPrefabName`（文字列）で保存 → ロード時に解決、未購読なら既定へフォールバック。
- 医療データ規約に倣い、AI判断・戦闘は**乱数シード固定**で再現可能に。

---

## 8. エラー処理・堅牢性

- 表現（車両/エンティティ）が外部要因で消滅したら `UnitInstance` も安全に破棄（null参照防止）。
- 数値は全て外部データ由来のため、ロード時にバリデーション（`Range≥0`、`MaxHP>0` 等）。不正値はログ＋既定値で継続。
- アセット解決失敗 → フォールバック＋ログ（§6）。
- セーブ/ロードの表現再生成失敗時は該当ユニットをスキップしつつ処理継続（1体の失敗で全体を落とさない）。

---

## 9. テスト戦略

SimulationCore はモード非依存の純ロジックなので単体テスト可能（既存プロジェクトの `tests` 枠組みを踏襲）。

### 単体テスト
- **CombatResolver**：`max(1, Attack−Armor)` のダメージ計算、`CanTargetDomains` 判定、SplashRadius範囲適用、HP0での撃破。
- **TerritoryManager**：圏内発展度の集計、収入計算、圏重複時の係争処理。
- **OccupationManager**：基地HP0 → 所有権/キュー/圏の移管、HQ喪失での勢力脱落。
- **関係マトリクス**：対称性（片側変更で鏡側も更新）。
- **シリアライズ往復**：保存→ロードで論理状態が一致すること（備蓄弾を含む）。
- **弾道ミサイル・迎撃（塊6）**：MissileOrderのコスト式（弾頭×威力係数）、迎撃判定（`InterceptDecision`：高度帯・水平距離・確率——MissileDisaster側の既存テストを流用/踏襲）、着弾解決（敵基地HP減 / 敵勢力圏の発展度低下→収入減）、占領時の備蓄弾移管。

### 統合テスト（実機）
- 2勢力MVPシナリオを実機で回し、生産→出撃→交戦→基地陥落→占領→資産移管が通ることを確認（既存MODと同様、実機レコーディングで検証）。
- 金融/認証相当の重要計算はないが、**戦闘・経済・占領の数値ロジックは高カバレッジ**を目標（ユーザー規約：一般コード80%以上）。

---

## 10. 未確定・将来拡張（MVP範囲外）

- 兵種の全30種×Tier5段階の数値バランス（MVP後にデータ拡張で対応）。
- 海空ユニットの自作ステアリングの詳細（衝突回避・高度/深度管理）。
- 特殊兵種の固有挙動（電子戦機のジャミング、早期警戒機の索敵範囲拡張、機雷敷設/掃海、自爆ボート、巡航ミサイル発射プラットフォーム）。
- UIの最終デザイン（HoI風パネルの具体レイアウト）。
- 同盟勢力間の連携AI（共同侵攻など）。
- 弾道ミサイルの数値バランス（弾頭別コスト/威力係数/製造時間、迎撃Tierの高度帯・命中率）と、AIの発射・防空判断の詰め（塊6実装時に調整）。

これらは各サブプロジェクトの spec で個別に詰める。本設計書は**コア構造とMVP**を確定させるもの。
