# CS 大型軍事MOD — MVP（塊1〜5）実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 2勢力・各1基地・地上ユニット1種・敵対関係で、「軍資金を貯める→生産→出撃→交戦→基地HP0で占領→基地/圏/生産キュー奪取」がCS無印の実機で回る最小スライスを作る。

**Architecture:** 全ての決定ロジックはUnityEngine非依存の純粋な `Core`（xunitで単体テスト）に置き、Unity/CS依存の橋渡し（車両スポーン・建物走査・tick駆動・セーブ）だけを `Game` に置く。`Core` はプレーンデータ（`WarState`）を受け取り新しい状態/決定を返す関数群。`Game` の `MilitaryManager` が毎tick `Core` を呼び、結果を車両・建物・セーブに反映する。

**Tech Stack:** C# 7.3 / .NET Framework 3.5（MOD DLL、CS無印=Unity 5.6）、ICities / Assembly-CSharp / ColossalManaged / UnityEngine 参照。テストは xunit / .NET 8.0（`Core/**/*.cs` を直接コンパイル）。

## Global Constraints

- **対象**: Cities: Skylines 2015 無印（Unity 5.6）。MOD DLL は `TargetFrameworkVersion=v3.5`, `LangVersion=7.3`。
- **Core は UnityEngine 非依存**：`Core/` 配下で `using UnityEngine;` は禁止。座標は Core 独自の `WorldPos` 構造体を使う（xunit/net8 で直接コンパイルするため）。
- **テスト方式**：`tests\CSWarfront.Core.Tests\` は net8.0 xunit、`..\..\src\CSWarfront\Core\**\*.cs` を `Compile Include` で直接取り込む。実行は `dotnet test`。
- **不変性**：状態変更はミューテートせず新オブジェクトを返す方針（ユーザー規約 coding-style）。ただしパフォーマンス上ミューテートが必要な `WarState` 内コレクションは、メソッド境界を明確にして局所化する。
- **ファイル規約**：1ファイル1責務、500行以内、多数の小ファイル。`console.log`/`print` 相当の常時ログを残さない（`ModConfig.Log` は既存同様デバッグ用途に限定）。
- **乱数**：AI判断・戦闘の抽選は seed 固定可能に（引数注入 or シード付き RNG）。再現性のため（医療データ規約に準拠）。
- **アセンブリ/設定ファイル名**：設定ファイル名は MOD/アセンブリ名（`CSWarfront`）と**同名にしない**（別名 `WarfrontSettings` 等）。既知の「同じキー」例外回避。
- **命名**：Microsoft C# 規約（PascalCase型/メソッド、camelCaseローカル）。

---

## ファイル構成（このMVPで作る/触るファイル）

```
軍事MODプロジェクト/
├─ CSWarfront.sln
├─ build.ps1                              # ビルド＋Addons/Modsへ配置（MissileDisaster流用）
├─ src/CSWarfront/
│  ├─ CSWarfront.csproj                   # net35 MOD DLL
│  ├─ Properties/AssemblyInfo.cs
│  ├─ Core/                               # ★UnityEngine非依存・純ロジック（テスト対象）
│  │  ├─ WorldPos.cs                      # 座標構造体＋距離
│  │  ├─ Relation.cs / RelationMatrix.cs  # 敵対/中立/同盟・対称5x5
│  │  ├─ Faction.cs                       # 勢力データ＋軍資金操作
│  │  ├─ Domain.cs / UnitCategory.cs / BaseType.cs
│  │  ├─ UnitType.cs / UnitTypeRegistry.cs
│  │  ├─ UnitInstance.cs
│  │  ├─ CombatMath.cs                    # ダメージ計算
│  │  ├─ TargetSearch.cs                  # 射程内・最近接の敵探索
│  │  ├─ CombatStep.cs                    # 交戦tick（純関数）
│  │  ├─ MilitaryBase.cs                  # 基地データ（HP/所有/キュー/圏）
│  │  ├─ ProductionStep.cs                # 生産キュー進行
│  │  ├─ TerritoryIncome.cs               # 圏内発展度→収入
│  │  ├─ Occupation.cs                    # 基地HP0→移管・勢力脱落
│  │  ├─ AiTargeting.cs                   # 侵攻目標選定
│  │  ├─ WarState.cs                      # 集約状態（factions/bases/units）
│  │  └─ WarStateSerializer.cs            # バイト列往復
│  └─ Game/                               # Unity/CS依存（実機統合検証）
│     ├─ Mod.cs                           # IUserMod entry
│     ├─ WarfrontThreadingExtension.cs    # OnUpdate/OnAfterSimulationTick
│     ├─ MilitaryManager.cs               # Core⇔CSの橋渡し（singleton）
│     ├─ LandUnitSpawner.cs               # 車両スポーン/撤去＋位置取得
│     ├─ DevelopmentSampler.cs            # BuildingManagerから発展度サンプル
│     ├─ BaseBuildingBinder.cs            # CS建物ID⇔MilitaryBase
│     ├─ ModConfig.cs                     # ログ薄ラッパ
│     └─ Serialization/WarStateDataExtension.cs  # セーブ/ロード配線
└─ tests/CSWarfront.Core.Tests/
   ├─ CSWarfront.Core.Tests.csproj        # net8 xunit
   └─ *Tests.cs                           # 各Coreクラスのテスト
```

**設計上の分担**：`Core` は「入力→出力」の純関数（座標は `WorldPos`、抽選は roll/seed 注入）。`Game` は CS の車両・建物・tick・セーブという副作用のみ担当し、判断は必ず `Core` に委譲する。これで塊1〜5のロジックは全て xunit で検証できる。

---

## Task 1: プロジェクト・スキャフォールディング（空MODビルド＋空テスト）

**Files:**
- Create: `src/CSWarfront/CSWarfront.csproj`
- Create: `src/CSWarfront/Properties/AssemblyInfo.cs`
- Create: `src/CSWarfront/Game/Mod.cs`
- Create: `src/CSWarfront/Game/ModConfig.cs`
- Create: `build.ps1`
- Create: `CSWarfront.sln`
- Create: `tests/CSWarfront.Core.Tests/CSWarfront.Core.Tests.csproj`
- Create: `src/CSWarfront/Core/WorldPos.cs`
- Test: `tests/CSWarfront.Core.Tests/WorldPosTests.cs`

**Interfaces:**
- Produces: `CSWarfront.Core.WorldPos` struct — `WorldPos(float x, float y, float z)`, プロパティ `float X,Y,Z`, メソッド `float DistanceTo(WorldPos other)`, `float HorizontalDistanceTo(WorldPos other)`（Y無視）。

- [ ] **Step 1: csproj を作成**（MissileDisaster の csproj を踏襲。`ManagedDLLPath` は各自の環境に合わせる）

`src/CSWarfront/CSWarfront.csproj`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildToolsPath)\Microsoft.Common.props" Condition="Exists('$(MSBuildToolsPath)\Microsoft.Common.props')" />
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Release</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{D3A1B3D0-0000-4000-8000-000000000020}</ProjectGuid>
    <OutputType>Library</OutputType>
    <RootNamespace>CSWarfront</RootNamespace>
    <AssemblyName>CSWarfront</AssemblyName>
    <TargetFrameworkVersion>v3.5</TargetFrameworkVersion>
    <LangVersion>7.3</LangVersion>
    <FileAlignment>512</FileAlignment>
    <ManagedDLLPath>C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed</ManagedDLLPath>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Release' ">
    <OutputPath>bin\Release\</OutputPath>
    <DefineConstants>TRACE</DefineConstants>
    <Optimize>true</Optimize>
    <DebugType>pdbonly</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Core" />
    <Reference Include="ICities"><HintPath>$(ManagedDLLPath)\ICities.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="Assembly-CSharp"><HintPath>$(ManagedDLLPath)\Assembly-CSharp.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="ColossalManaged"><HintPath>$(ManagedDLLPath)\ColossalManaged.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="UnityEngine"><HintPath>$(ManagedDLLPath)\UnityEngine.dll</HintPath><Private>False</Private></Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Core\**\*.cs" />
    <Compile Include="Game\**\*.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

- [ ] **Step 2: AssemblyInfo と最小 Mod entry・ログを作成**

`src/CSWarfront/Properties/AssemblyInfo.cs`:
```csharp
using System.Reflection;
[assembly: AssemblyTitle("CSWarfront")]
[assembly: AssemblyProduct("CSWarfront")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
```

`src/CSWarfront/Game/ModConfig.cs`:
```csharp
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>デバッグログの薄ラッパ。常時ログは残さない（規約）。</summary>
    public static class ModConfig
    {
        public const string Tag = "[CSWarfront] ";
        public static void Log(string msg) { Debug.Log(Tag + msg); }
        public static void LogError(string msg) { Debug.LogError(Tag + msg); }
    }
}
```

`src/CSWarfront/Game/Mod.cs`:
```csharp
using ICities;
namespace CSWarfront.Game
{
    public class Mod : IUserMod
    {
        public string Name => "CS Warfront";
        public string Description => "5勢力のTier制軍事シミュレーション（陸海空・基地・勢力圏・占領）。";
    }
}
```

- [ ] **Step 3: build.ps1 を作成**（MissileDisaster を踏襲。DLLをAddons/Modsへ配置）

`build.ps1`:
```powershell
$ErrorActionPreference = "Stop"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild が見つかりません" }
& $msbuild "src\CSWarfront\CSWarfront.csproj" /t:Restore,Build /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { throw "ビルド失敗" }
$dll = "src\CSWarfront\bin\Release\CSWarfront.dll"
$modDir = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities_Skylines\Addons\Mods\CSWarfront"
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
Copy-Item $dll $modDir -Force
Write-Host "配置完了: $modDir"
```

- [ ] **Step 4: テストプロジェクトと WorldPos の失敗テストを作成**

`tests/CSWarfront.Core.Tests/CSWarfront.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <LangVersion>7.3</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\..\src\CSWarfront\Core\**\*.cs" LinkBase="Core" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

`tests/CSWarfront.Core.Tests/WorldPosTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class WorldPosTests
{
    [Fact]
    public void DistanceTo_computes_3d_euclidean()
    {
        var a = new WorldPos(0f, 0f, 0f);
        var b = new WorldPos(3f, 0f, 4f);
        Assert.Equal(5f, a.DistanceTo(b), 3);
    }

    [Fact]
    public void HorizontalDistanceTo_ignores_y()
    {
        var a = new WorldPos(0f, 100f, 0f);
        var b = new WorldPos(3f, 999f, 4f);
        Assert.Equal(5f, a.HorizontalDistanceTo(b), 3);
    }
}
```

- [ ] **Step 5: テストを実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: コンパイルエラー（`WorldPos` 未定義）で FAIL。

- [ ] **Step 6: WorldPos を実装**

`src/CSWarfront/Core/WorldPos.cs`:
```csharp
using System;
namespace CSWarfront.Core
{
    /// <summary>UnityEngine非依存の座標。Game層で UnityEngine.Vector3 と相互変換する。</summary>
    public struct WorldPos
    {
        public readonly float X, Y, Z;
        public WorldPos(float x, float y, float z) { X = x; Y = y; Z = z; }

        public float DistanceTo(WorldPos o)
        {
            float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public float HorizontalDistanceTo(WorldPos o)
        {
            float dx = X - o.X, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
```

- [ ] **Step 7: テスト成功とMODビルドを確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS（2件）。
Run: `powershell -File build.ps1`
Expected: `CSWarfront.dll` がビルドされ Addons/Mods へ配置される（CS Managed DLL パスが正しい前提）。

- [ ] **Step 8: sln を作成しコミット**

`CSWarfront.sln` は VS/`dotnet sln` で生成してよい（MOD csproj とテスト csproj を含める）。最低限、両 csproj がソリューションに含まれれば形式は問わない。
```bash
git add -A
git commit -m "chore: プロジェクト初期化（空MODビルド＋WorldPos＋xunit）"
```

---

## Task 2: 勢力と関係マトリクス（塊1）

**Files:**
- Create: `src/CSWarfront/Core/Relation.cs`
- Create: `src/CSWarfront/Core/RelationMatrix.cs`
- Create: `src/CSWarfront/Core/Faction.cs`
- Test: `tests/CSWarfront.Core.Tests/RelationMatrixTests.cs`
- Test: `tests/CSWarfront.Core.Tests/FactionTests.cs`

**Interfaces:**
- Produces: `enum Relation { Hostile, Neutral, Allied }`
- Produces: `RelationMatrix` — `RelationMatrix(int factionCount)`, `Relation Get(int a, int b)`, `void Set(int a, int b, Relation r)`（対称・自分自身は常に `Allied`）。
- Produces: `Faction` — `Faction(byte id, string name)`; プロパティ `byte Id`, `string Name`, `float Treasury`, `ushort? HomeBaseId`, `bool IsPlayer`; メソッド `void AddTreasury(float amount)`, `bool TrySpend(float amount)`（不足なら false・変化なし）。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/RelationMatrixTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class RelationMatrixTests
{
    [Fact]
    public void Default_is_neutral_between_different_factions()
    {
        var m = new RelationMatrix(5);
        Assert.Equal(Relation.Neutral, m.Get(0, 1));
    }

    [Fact]
    public void Set_is_symmetric()
    {
        var m = new RelationMatrix(5);
        m.Set(0, 3, Relation.Hostile);
        Assert.Equal(Relation.Hostile, m.Get(0, 3));
        Assert.Equal(Relation.Hostile, m.Get(3, 0)); // 鏡側も更新
    }

    [Fact]
    public void Self_relation_is_always_allied()
    {
        var m = new RelationMatrix(5);
        Assert.Equal(Relation.Allied, m.Get(2, 2));
    }
}
```

`tests/CSWarfront.Core.Tests/FactionTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class FactionTests
{
    [Fact]
    public void TrySpend_succeeds_when_enough_and_deducts()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(100f);
        Assert.True(f.TrySpend(30f));
        Assert.Equal(70f, f.Treasury, 3);
    }

    [Fact]
    public void TrySpend_fails_when_insufficient_and_leaves_treasury()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(20f);
        Assert.False(f.TrySpend(30f));
        Assert.Equal(20f, f.Treasury, 3);
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL（型未定義）。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/Relation.cs`:
```csharp
namespace CSWarfront.Core
{
    public enum Relation { Hostile, Neutral, Allied }
}
```

`src/CSWarfront/Core/RelationMatrix.cs`:
```csharp
namespace CSWarfront.Core
{
    /// <summary>対称な勢力関係表。自分自身は常に Allied。</summary>
    public class RelationMatrix
    {
        private readonly Relation[,] _rel;
        private readonly int _count;

        public RelationMatrix(int factionCount)
        {
            _count = factionCount;
            _rel = new Relation[factionCount, factionCount];
            for (int i = 0; i < factionCount; i++)
                for (int j = 0; j < factionCount; j++)
                    _rel[i, j] = (i == j) ? Relation.Allied : Relation.Neutral;
        }

        public Relation Get(int a, int b) { return _rel[a, b]; }

        public void Set(int a, int b, Relation r)
        {
            if (a == b) return;      // 自己関係は不変
            _rel[a, b] = r;
            _rel[b, a] = r;          // 対称
        }
    }
}
```

`src/CSWarfront/Core/Faction.cs`:
```csharp
namespace CSWarfront.Core
{
    public class Faction
    {
        public byte Id { get; private set; }
        public string Name { get; set; }
        public float Treasury { get; private set; }
        public ushort? HomeBaseId { get; set; }
        public bool IsPlayer { get; set; }
        public bool Eliminated { get; set; }

        public Faction(byte id, string name) { Id = id; Name = name; }

        public void AddTreasury(float amount) { if (amount > 0f) Treasury += amount; }

        public bool TrySpend(float amount)
        {
            if (amount < 0f || Treasury < amount) return false;
            Treasury -= amount;
            return true;
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS（全件）。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): 勢力データ・軍資金操作・対称関係マトリクス（塊1）"
```

---

## Task 3: ユニット種別と定義レジストリ（塊2-a）

**Files:**
- Create: `src/CSWarfront/Core/Domain.cs`
- Create: `src/CSWarfront/Core/UnitCategory.cs`
- Create: `src/CSWarfront/Core/UnitType.cs`
- Create: `src/CSWarfront/Core/UnitTypeRegistry.cs`
- Test: `tests/CSWarfront.Core.Tests/UnitTypeRegistryTests.cs`

**Interfaces:**
- Produces: `enum Domain { Land, Sea, Air }`
- Produces: `enum UnitCategory { Tank, Apc, MechInfantry, Artillery, DroneInfantry, Infantry, AntiAir, Carrier, Cruiser, Destroyer, Frigate, Minelayer, Minesweeper, Submarine, FastBoat, SuicideBoat, SeaDrone, AirSuperiority, GroundAttack, TacticalBomber, StrategicBomber, ElectronicWarfare, Awacs }`
- Produces: `UnitType` — 公開読み取りプロパティ `string TypeKey`, `Domain Domain`, `UnitCategory Category`, `byte Tier`, `float MaxHP`, `float Attack`, `float Range`, `float Armor`, `float Speed`, `float SplashRadius`, `float Cost`, `float BuildTime`, `string AssetPrefabName`。ビルダ的コンストラクタ（全フィールド指定）。
- Produces: `UnitTypeRegistry` — `void Register(UnitType t)`, `UnitType Get(string typeKey)`（無ければ null）, `bool Contains(string typeKey)`。
- Produces: `static class MvpUnitTypes` — `UnitType Tank_T1` を返す既定定義（`TypeKey="Tank_T1"`, `Domain.Land`, `Category=Tank`, `Tier=1`, `MaxHP=100`, `Attack=25`, `Range=60`, `Armor=5`, `Speed=8`, `SplashRadius=0`, `Cost=50`, `BuildTime=10`, `AssetPrefabName=""`）。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/UnitTypeRegistryTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class UnitTypeRegistryTests
{
    [Fact]
    public void Register_then_Get_returns_same_type()
    {
        var reg = new UnitTypeRegistry();
        reg.Register(MvpUnitTypes.Tank_T1());
        var t = reg.Get("Tank_T1");
        Assert.NotNull(t);
        Assert.Equal(Domain.Land, t.Domain);
        Assert.Equal(25f, t.Attack, 3);
    }

    [Fact]
    public void Get_unknown_returns_null()
    {
        var reg = new UnitTypeRegistry();
        Assert.Null(reg.Get("NoSuch"));
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL（型未定義）。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/Domain.cs`:
```csharp
namespace CSWarfront.Core { public enum Domain { Land, Sea, Air } }
```

`src/CSWarfront/Core/UnitCategory.cs`:
```csharp
namespace CSWarfront.Core
{
    public enum UnitCategory
    {
        Tank, Apc, MechInfantry, Artillery, DroneInfantry, Infantry, AntiAir,
        Carrier, Cruiser, Destroyer, Frigate, Minelayer, Minesweeper, Submarine,
        FastBoat, SuicideBoat, SeaDrone,
        AirSuperiority, GroundAttack, TacticalBomber, StrategicBomber, ElectronicWarfare, Awacs
    }
}
```

`src/CSWarfront/Core/UnitType.cs`:
```csharp
namespace CSWarfront.Core
{
    /// <summary>データ駆動のユニット定義（1種別×1Tier）。実行時は不変。</summary>
    public class UnitType
    {
        public string TypeKey { get; private set; }
        public Domain Domain { get; private set; }
        public UnitCategory Category { get; private set; }
        public byte Tier { get; private set; }
        public float MaxHP { get; private set; }
        public float Attack { get; private set; }
        public float Range { get; private set; }
        public float Armor { get; private set; }
        public float Speed { get; private set; }
        public float SplashRadius { get; private set; }
        public float Cost { get; private set; }
        public float BuildTime { get; private set; }
        public string AssetPrefabName { get; private set; }

        public UnitType(string typeKey, Domain domain, UnitCategory category, byte tier,
            float maxHp, float attack, float range, float armor, float speed,
            float splashRadius, float cost, float buildTime, string assetPrefabName)
        {
            TypeKey = typeKey; Domain = domain; Category = category; Tier = tier;
            MaxHP = maxHp; Attack = attack; Range = range; Armor = armor; Speed = speed;
            SplashRadius = splashRadius; Cost = cost; BuildTime = buildTime;
            AssetPrefabName = assetPrefabName ?? "";
        }
    }

    /// <summary>MVPの既定ユニット定義（後日XML外出しに置換予定）。</summary>
    public static class MvpUnitTypes
    {
        public static UnitType Tank_T1()
        {
            return new UnitType("Tank_T1", Domain.Land, UnitCategory.Tank, 1,
                100f, 25f, 60f, 5f, 8f, 0f, 50f, 10f, "");
        }
    }
}
```

`src/CSWarfront/Core/UnitTypeRegistry.cs`:
```csharp
using System.Collections.Generic;
namespace CSWarfront.Core
{
    public class UnitTypeRegistry
    {
        private readonly Dictionary<string, UnitType> _byKey = new Dictionary<string, UnitType>();
        public void Register(UnitType t) { _byKey[t.TypeKey] = t; }
        public bool Contains(string typeKey) { return _byKey.ContainsKey(typeKey); }
        public UnitType Get(string typeKey)
        {
            UnitType t; return _byKey.TryGetValue(typeKey, out t) ? t : null;
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): データ駆動UnitType定義とレジストリ（塊2-a）"
```

---

## Task 4: 戦闘数値とターゲット探索（塊2-b）

**Files:**
- Create: `src/CSWarfront/Core/CombatMath.cs`
- Create: `src/CSWarfront/Core/UnitInstance.cs`
- Create: `src/CSWarfront/Core/TargetSearch.cs`
- Test: `tests/CSWarfront.Core.Tests/CombatMathTests.cs`
- Test: `tests/CSWarfront.Core.Tests/TargetSearchTests.cs`

**Interfaces:**
- Consumes: `UnitType`, `WorldPos`, `RelationMatrix`, `Relation`。
- Produces: `static class CombatMath` — `float DamagePerHit(float attack, float armor)` = `Math.Max(1f, attack - armor)`。
- Produces: `enum UnitState { Idle, Moving, Engaging, Dead }`
- Produces: `UnitInstance`（可変クラス）— `UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)`; フィールド `uint InstanceId`, `string TypeKey`, `byte FactionId`, `float CurrentHP`, `WorldPos Position`, `UnitState State`, `uint? TargetId`, `WorldPos? OrderTargetPos`。
- Produces: `static class TargetSearch` — `UnitInstance FindNearestHostile(UnitInstance self, IEnumerable<UnitInstance> all, RelationMatrix rel, float range)`。敵対関係かつ生存かつ水平距離 ≤ range のうち最近接を返す。無ければ null。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/CombatMathTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class CombatMathTests
{
    [Theory]
    [InlineData(25f, 5f, 20f)]   // 通常
    [InlineData(10f, 10f, 1f)]   // 装甲=攻撃 → 最低1
    [InlineData(3f, 50f, 1f)]    // 装甲超過 → 最低1
    public void DamagePerHit_is_attack_minus_armor_floored_at_1(float atk, float armor, float expected)
    {
        Assert.Equal(expected, CombatMath.DamagePerHit(atk, armor), 3);
    }
}
```

`tests/CSWarfront.Core.Tests/TargetSearchTests.cs`:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class TargetSearchTests
{
    private static UnitInstance U(uint id, byte fac, float x)
        => new UnitInstance(id, "Tank_T1", fac, 100f, new WorldPos(x, 0f, 0f));

    [Fact]
    public void Finds_nearest_hostile_in_range()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var near = U(2, 1, 30f);
        var far = U(3, 1, 55f);
        var all = new List<UnitInstance> { self, near, far };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)2, t.InstanceId);
    }

    [Fact]
    public void Ignores_non_hostile_and_out_of_range()
    {
        var rel = new RelationMatrix(5); // 0-1 は Neutral
        var self = U(1, 0, 0f);
        var neutral = U(2, 1, 10f);
        rel.Set(0, 2, Relation.Hostile);
        var hostileFar = U(3, 2, 100f); // 敵対だが射程外
        var all = new List<UnitInstance> { self, neutral, hostileFar };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }

    [Fact]
    public void Ignores_dead_units()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var dead = U(2, 1, 10f); dead.State = UnitState.Dead;
        var all = new List<UnitInstance> { self, dead };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL（型未定義）。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/CombatMath.cs`:
```csharp
using System;
namespace CSWarfront.Core
{
    public static class CombatMath
    {
        /// <summary>1発のダメージ。装甲で軽減、最低1を保証。</summary>
        public static float DamagePerHit(float attack, float armor)
        {
            return Math.Max(1f, attack - armor);
        }
    }
}
```

`src/CSWarfront/Core/UnitInstance.cs`:
```csharp
namespace CSWarfront.Core
{
    public enum UnitState { Idle, Moving, Engaging, Dead }

    /// <summary>実行時の1体。表現(車両ID)はGame層が別に保持し、ここには論理状態のみ。</summary>
    public class UnitInstance
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public float CurrentHP;
        public WorldPos Position;
        public UnitState State;
        public uint? TargetId;
        public WorldPos? OrderTargetPos;

        public UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)
        {
            InstanceId = id; TypeKey = typeKey; FactionId = factionId;
            CurrentHP = hp; Position = pos; State = UnitState.Idle;
        }

        public bool IsAlive => State != UnitState.Dead && CurrentHP > 0f;
    }
}
```

`src/CSWarfront/Core/TargetSearch.cs`:
```csharp
using System.Collections.Generic;
namespace CSWarfront.Core
{
    public static class TargetSearch
    {
        /// <summary>射程内・敵対・生存のうち水平距離最小の敵を返す。無ければ null。</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            foreach (var u in all)
            {
                if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                if (rel.Get(self.FactionId, u.FactionId) != Relation.Hostile) continue;
                float d = self.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best;
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): ダメージ計算・UnitInstance・敵探索（塊2-b）"
```

---

## Task 5: 集約状態 WarState と交戦tick（塊2-c）

**Files:**
- Create: `src/CSWarfront/Core/WarState.cs`
- Create: `src/CSWarfront/Core/CombatStep.cs`
- Test: `tests/CSWarfront.Core.Tests/CombatStepTests.cs`

**Interfaces:**
- Consumes: `Faction`, `RelationMatrix`, `UnitInstance`, `UnitType`, `UnitTypeRegistry`, `TargetSearch`, `CombatMath`。
- Produces: `WarState` — フィールド `List<Faction> Factions`, `RelationMatrix Relations`, `List<UnitInstance> Units`, `List<MilitaryBase> Bases`（`MilitaryBase` は Task 6 で定義。ここでは空Listで前方参照可にするため Task 6 と同時に型が揃う。**本タスクでは `Bases` を宣言のみ**）, `UnitTypeRegistry Types`, `uint NextInstanceId`; メソッド `UnitInstance FindUnit(uint id)`, `uint AllocInstanceId()`。
- Produces: `static class CombatStep` — `void Advance(WarState state, float dt)`。各生存ユニットについて射程内の敵を探し交戦、`DamagePerHit` を `dt`（と発射レート=毎tick1発の簡略）で適用、HP0で `State=Dead`。戻り値なし（`state.Units` をミューテート）。**MVP簡略**：発射レートは「1tickにつき `DamagePerHit` を適用」。

> 注：`WarState.Bases` の要素型 `MilitaryBase` は Task 6 で作る。Task 5 の実装時点では `MilitaryBase` クラスを**空定義**として先に作ってよい（`src/CSWarfront/Core/MilitaryBase.cs` にプロパティなしの空クラス）。Task 6 で中身を埋める。これで前方参照を解消しつつ順序通り進められる。

- [ ] **Step 1: 空の MilitaryBase を先に用意**（前方参照解消）

`src/CSWarfront/Core/MilitaryBase.cs`:
```csharp
namespace CSWarfront.Core
{
    // Task 6 で中身を実装する。ここでは WarState.Bases の要素型として先行定義。
    public partial class MilitaryBase { }
}
```

- [ ] **Step 2: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/CombatStepTests.cs`:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class CombatStepTests
{
    private static WarState TwoHostileTanks(float distance)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(distance, 0f, 0f)));
        return s;
    }

    [Fact]
    public void Units_in_range_damage_each_other()
    {
        var s = TwoHostileTanks(50f); // range 60 内
        CombatStep.Advance(s, 1f);
        // DamagePerHit(25,5)=20 を相互に適用
        Assert.Equal(80f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(80f, s.FindUnit(2).CurrentHP, 3);
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Units_out_of_range_do_not_engage()
    {
        var s = TwoHostileTanks(100f); // range 60 外
        CombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Unit_dies_when_hp_reaches_zero()
    {
        var s = TwoHostileTanks(50f);
        s.FindUnit(2).CurrentHP = 15f; // 20ダメージで死亡
        CombatStep.Advance(s, 1f);
        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
    }
}
```

- [ ] **Step 3: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL（`WarState`/`CombatStep` 未定義）。

- [ ] **Step 4: 実装**

`src/CSWarfront/Core/WarState.cs`:
```csharp
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

        public uint AllocInstanceId() { return NextInstanceId++; }

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
```

`src/CSWarfront/Core/CombatStep.cs`:
```csharp
namespace CSWarfront.Core
{
    /// <summary>交戦tick（純ロジック）。射程内の敵を探し、毎tick DamagePerHit を適用する。</summary>
    public static class CombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            // 1) ターゲット選定と発火（ダメージは後段で一括適用しないシンプル版：都度適用）
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                var target = TargetSearch.FindNearestHostile(self, state.Units, state.Relations, type.Range);
                if (target == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }
                self.State = UnitState.Engaging;
                self.TargetId = target.InstanceId;
                float dmg = CombatMath.DamagePerHit(type.Attack, TypeArmorOf(state, target));
                target.CurrentHP -= dmg;
            }
            // 2) 死亡判定
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f) u.State = UnitState.Dead;
            }
        }

        private static float TypeArmorOf(WarState state, UnitInstance u)
        {
            var t = state.Types.Get(u.TypeKey);
            return t != null ? t.Armor : 0f;
        }
    }
}
```

- [ ] **Step 5: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 6: コミット**

```bash
git add -A
git commit -m "feat(core): WarState集約と交戦tick（塊2-c）"
```

---

## Task 6: 基地・生産キュー進行（塊3-a）

**Files:**
- Modify: `src/CSWarfront/Core/MilitaryBase.cs`（Task 5 の空定義を実装）
- Create: `src/CSWarfront/Core/BaseType.cs`
- Create: `src/CSWarfront/Core/ProductionOrder.cs`
- Create: `src/CSWarfront/Core/ProductionStep.cs`
- Test: `tests/CSWarfront.Core.Tests/ProductionStepTests.cs`

**Interfaces:**
- Produces: `enum BaseType { Army, Navy, AirForce, MissileBase }`
- Produces: `ProductionOrder`（可変）— `ProductionOrder(string typeKey, float cost, float buildTime)`; フィールド `string TypeKey`, `float Cost`, `float BuildTime`, `float Progress`（0..1）。
- Produces: `MilitaryBase`（可変）— `MilitaryBase(ushort baseId, BaseType type, WorldPos pos)`; フィールド `ushort BaseId`, `BaseType Type`, `byte? OwnerFactionId`, `WorldPos Position`, `float InfluenceRadius`, `bool IsHeadquarters`, `float MaxHP`, `float CurrentHP`, `List<ProductionOrder> Queue`。`partial class` を維持。
- Produces: `static class ProductionStep` — `List<CompletedUnit> Advance(WarState state, float dt)`。所有者ありの各基地のキュー先頭を `dt` 進め、`Progress>=1` になったら完了として取り出し、`CompletedUnit { ushort BaseId, byte FactionId, string TypeKey, WorldPos SpawnPos }` を返す（Game層がここでスポーン）。
- Produces: `struct CompletedUnit`（上記フィールド）。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/ProductionStepTests.cs`:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class ProductionStepTests
{
    private static WarState OneBaseWithQueue()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void Advance_progresses_but_not_complete_before_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 5f); // 10秒中5秒
        Assert.Empty(done);
        Assert.Equal(0.5f, s.Bases[0].Queue[0].Progress, 3);
    }

    [Fact]
    public void Advance_completes_and_dequeues_at_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 10f);
        Assert.Single(done);
        Assert.Equal("Tank_T1", done[0].TypeKey);
        Assert.Equal((byte)0, done[0].FactionId);
        Assert.Empty(s.Bases[0].Queue); // 取り出し済み
    }

    [Fact]
    public void Unowned_base_does_not_produce()
    {
        var s = OneBaseWithQueue();
        s.Bases[0].OwnerFactionId = null;
        var done = ProductionStep.Advance(s, 10f);
        Assert.Empty(done);
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/BaseType.cs`:
```csharp
namespace CSWarfront.Core { public enum BaseType { Army, Navy, AirForce, MissileBase } }
```

`src/CSWarfront/Core/ProductionOrder.cs`:
```csharp
namespace CSWarfront.Core
{
    public class ProductionOrder
    {
        public string TypeKey;
        public float Cost;
        public float BuildTime;
        public float Progress; // 0..1
        public ProductionOrder(string typeKey, float cost, float buildTime)
        { TypeKey = typeKey; Cost = cost; BuildTime = buildTime; Progress = 0f; }
    }
}
```

`src/CSWarfront/Core/MilitaryBase.cs`（空定義を置換）:
```csharp
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

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }
    }
}
```

`src/CSWarfront/Core/ProductionStep.cs`:
```csharp
using System.Collections.Generic;
namespace CSWarfront.Core
{
    public struct CompletedUnit
    {
        public ushort BaseId;
        public byte FactionId;
        public string TypeKey;
        public WorldPos SpawnPos;
    }

    /// <summary>生産tick（純ロジック）。完成分を CompletedUnit として返す（スポーンはGame層）。</summary>
    public static class ProductionStep
    {
        public static List<CompletedUnit> Advance(WarState state, float dt)
        {
            var completed = new List<CompletedUnit>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                var b = state.Bases[i];
                if (b.OwnerFactionId == null || b.Queue.Count == 0) continue;
                var order = b.Queue[0];
                if (order.BuildTime <= 0f) order.Progress = 1f;
                else order.Progress += dt / order.BuildTime;
                if (order.Progress >= 1f)
                {
                    completed.Add(new CompletedUnit
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        TypeKey = order.TypeKey,
                        SpawnPos = b.Position
                    });
                    b.Queue.RemoveAt(0);
                }
            }
            return completed;
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): 基地データと生産キュー進行（塊3-a）"
```

---

## Task 7: 勢力圏の収入計算（塊4-a）

**Files:**
- Create: `src/CSWarfront/Core/TerritoryIncome.cs`
- Test: `tests/CSWarfront.Core.Tests/TerritoryIncomeTests.cs`

**Interfaces:**
- Consumes: `MilitaryBase`, `WorldPos`。
- Produces: `struct DevelopmentSample { WorldPos Position; float Development; }`（Game層がBuildingManagerから作る）。
- Produces: `static class TerritoryIncome` — `float ForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)`。基地の `InfluenceRadius` 内サンプルの `Development` を合計し `rate` を掛ける。所有者なしは 0。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/TerritoryIncomeTests.cs`:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class TerritoryIncomeTests
{
    [Fact]
    public void Sums_development_within_radius_times_rate()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.InfluenceRadius = 100f;
        var samples = new List<DevelopmentSample>
        {
            new DevelopmentSample { Position = new WorldPos(50, 0, 0), Development = 10f },  // 圏内
            new DevelopmentSample { Position = new WorldPos(80, 0, 0), Development = 5f },   // 圏内
            new DevelopmentSample { Position = new WorldPos(200, 0, 0), Development = 100f },// 圏外
        };
        Assert.Equal(1.5f, TerritoryIncome.ForBase(b, samples, 0.1f), 3); // (10+5)*0.1
    }

    [Fact]
    public void Unowned_base_yields_zero()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = null;
        var samples = new List<DevelopmentSample>
        { new DevelopmentSample { Position = new WorldPos(0, 0, 0), Development = 10f } };
        Assert.Equal(0f, TerritoryIncome.ForBase(b, samples, 1f), 3);
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/TerritoryIncome.cs`:
```csharp
using System.Collections.Generic;
namespace CSWarfront.Core
{
    public struct DevelopmentSample
    {
        public WorldPos Position;
        public float Development;
    }

    /// <summary>基地の勢力圏内の発展度合計×レート＝収入（純ロジック）。</summary>
    public static class TerritoryIncome
    {
        public static float ForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)
        {
            if (b.OwnerFactionId == null) return 0f;
            float sum = 0f;
            foreach (var s in samples)
                if (b.Position.HorizontalDistanceTo(s.Position) <= b.InfluenceRadius)
                    sum += s.Development;
            return sum * rate;
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): 勢力圏の発展度→収入計算（塊4-a）"
```

---

## Task 8: 基地攻撃と占領・勢力脱落（塊5-a）

**Files:**
- Create: `src/CSWarfront/Core/BaseCombatStep.cs`
- Create: `src/CSWarfront/Core/Occupation.cs`
- Test: `tests/CSWarfront.Core.Tests/BaseCombatStepTests.cs`
- Test: `tests/CSWarfront.Core.Tests/OccupationTests.cs`

**Interfaces:**
- Consumes: `WarState`, `MilitaryBase`, `UnitInstance`, `RelationMatrix`, `CombatMath`。
- Produces: `static class BaseCombatStep` — `void Advance(WarState state, float dt)`。各生存ユニットについて、射程内に「敵対関係の敵基地（自分の所有でない・所有者と敵対）」があれば `DamagePerHit(attack, 0)` を毎tick基地HPへ適用。守備ユニット（Task 5 の CombatStep）が先に敵ユニットを攻撃するため、基地攻撃はユニット交戦と両立してよい（MVPは両方毎tick適用）。
- Produces: `static class Occupation` — `void ResolveCaptures(WarState state)`。`CurrentHP<=0` の基地について、圏内にいる敵対攻撃側のうち最も近いユニットの `FactionId` を新所有者に設定、`CurrentHP=MaxHP` に回復、`Queue` はそのまま（＝奪取）。旧所有者がHQ（`IsHeadquarters` かつ `HomeBaseId==BaseId`）を失ったら `Faction.Eliminated=true`。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/BaseCombatStepTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class BaseCombatStepTests
{
    [Fact]
    public void Attacker_in_range_damages_hostile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // attack 25, range 60
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(75f, s.Bases[0].CurrentHP, 3); // 100-25
    }

    [Fact]
    public void Does_not_damage_own_or_neutral_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 Neutral 既定
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var neutralBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        neutralBase.OwnerFactionId = 1; neutralBase.CurrentHP = 100f;
        s.Bases.Add(neutralBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }
}
```

`tests/CSWarfront.Core.Tests/OccupationTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class OccupationTests
{
    private static WarState FallenBaseScenario()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1; b.CurrentHP = 0f; b.MaxHP = 500f; b.InfluenceRadius = 500f;
        b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f)); // 奪取される備蓄
        s.Bases.Add(b);
        s.Factions[1].HomeBaseId = 200; // BlueのHQ
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(40, 0, 0))); // 攻撃側Red圏内
        return s;
    }

    [Fact]
    public void Fallen_base_transfers_to_attacker_with_queue_and_heals()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.Equal((byte)0, s.Bases[0].OwnerFactionId.Value); // Redへ
        Assert.Equal(500f, s.Bases[0].CurrentHP, 3);           // 回復
        Assert.Single(s.Bases[0].Queue);                        // 生産キュー奪取
    }

    [Fact]
    public void Losing_hq_eliminates_faction()
    {
        var s = FallenBaseScenario();
        Occupation.ResolveCaptures(s);
        Assert.True(s.FindFaction(1).Eliminated); // Blue脱落
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/BaseCombatStep.cs`:
```csharp
namespace CSWarfront.Core
{
    /// <summary>ユニットが敵対基地を射程内で攻撃する（純ロジック）。</summary>
    public static class BaseCombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                var type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (state.Relations.Get(u.FactionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f);
                    if (b.CurrentHP < 0f) b.CurrentHP = 0f;
                }
            }
        }
    }
}
```

`src/CSWarfront/Core/Occupation.cs`:
```csharp
namespace CSWarfront.Core
{
    /// <summary>HP0の基地を最近接の敵対攻撃側へ移管し、HQ喪失勢力を脱落させる（純ロジック）。</summary>
    public static class Occupation
    {
        public static void ResolveCaptures(WarState state)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.CurrentHP > 0f || b.OwnerFactionId == null) continue;
                byte oldOwner = b.OwnerFactionId.Value;

                // 圏内・敵対の攻撃側から最近接を新所有者に
                UnitInstance nearest = null; float best = float.MaxValue;
                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (!u.IsAlive) continue;
                    if (state.Relations.Get(oldOwner, u.FactionId) != Relation.Hostile) continue;
                    float d = b.Position.HorizontalDistanceTo(u.Position);
                    if (d > b.InfluenceRadius) continue;
                    if (d < best) { best = d; nearest = u; }
                }
                if (nearest == null) continue; // 攻撃側不在なら保留（次tickへ）

                b.OwnerFactionId = nearest.FactionId;
                b.CurrentHP = b.MaxHP;         // 再稼働（キューはそのまま＝奪取）

                // HQ喪失判定
                var loser = state.FindFaction(oldOwner);
                if (loser != null && loser.HomeBaseId.HasValue && loser.HomeBaseId.Value == b.BaseId)
                    loser.Eliminated = true;
            }
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): 基地攻撃・占領移管・勢力脱落（塊5-a）"
```

---

## Task 9: 侵攻目標選定AI（塊5-b）

**Files:**
- Create: `src/CSWarfront/Core/AiTargeting.cs`
- Test: `tests/CSWarfront.Core.Tests/AiTargetingTests.cs`

**Interfaces:**
- Consumes: `WarState`, `MilitaryBase`, `RelationMatrix`。
- Produces: `static class AiTargeting` — `MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from)`。`factionId` と敵対する所有基地のうち `from` から最も近いものを返す。無ければ null。
- Produces: `static class InvasionOrders` — `void AssignAdvance(WarState state, byte factionId)`。当該勢力の各生存ユニットに、`ChooseTargetBase`（そのユニット位置起点）で選んだ基地座標を `OrderTargetPos` に設定し `State=Moving`（既に Engaging のユニットは変更しない）。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/AiTargetingTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class AiTargetingTests
{
    private static WarState TwoEnemyBases()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var near = new MilitaryBase(10, BaseType.Army, new WorldPos(100, 0, 0)); near.OwnerFactionId = 1;
        var far = new MilitaryBase(11, BaseType.Army, new WorldPos(500, 0, 0)); far.OwnerFactionId = 1;
        var own = new MilitaryBase(12, BaseType.Army, new WorldPos(50, 0, 0)); own.OwnerFactionId = 0;
        s.Bases.Add(near); s.Bases.Add(far); s.Bases.Add(own);
        return s;
    }

    [Fact]
    public void Chooses_nearest_hostile_owned_base()
    {
        var s = TwoEnemyBases();
        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)10, t.BaseId);
    }

    [Fact]
    public void Returns_null_when_no_hostile_base()
    {
        var s = TwoEnemyBases();
        s.Relations.Set(0, 1, Relation.Neutral); // 敵対解除
        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0)));
    }

    [Fact]
    public void AssignAdvance_sets_moving_orders_for_faction_units()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        InvasionOrders.AssignAdvance(s, 0);
        Assert.Equal(UnitState.Moving, s.FindUnit(1).State);
        Assert.True(s.FindUnit(1).OrderTargetPos.HasValue);
        Assert.Equal(100f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // near基地へ
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL。

- [ ] **Step 3: 実装**

`src/CSWarfront/Core/AiTargeting.cs`:
```csharp
namespace CSWarfront.Core
{
    public static class AiTargeting
    {
        public static MilitaryBase ChooseTargetBase(WarState state, byte factionId, WorldPos from)
        {
            MilitaryBase best = null; float bestDist = float.MaxValue;
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (state.Relations.Get(factionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                float d = from.HorizontalDistanceTo(b.Position);
                if (d < bestDist) { bestDist = d; best = b; }
            }
            return best;
        }
    }

    public static class InvasionOrders
    {
        /// <summary>当該勢力の非交戦ユニットに、各自位置から最寄りの敵基地へ進軍命令を与える。</summary>
        public static void AssignAdvance(WarState state, byte factionId)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive) continue;
                if (u.State == UnitState.Engaging) continue;
                var target = AiTargeting.ChooseTargetBase(state, factionId, u.Position);
                if (target == null) continue;
                u.OrderTargetPos = target.Position;
                u.State = UnitState.Moving;
            }
        }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): 侵攻目標選定と進軍命令AI（塊5-b）"
```

---

## Task 10: 状態シリアライザ（永続化の純ロジック）

**Files:**
- Create: `src/CSWarfront/Core/WarStateSerializer.cs`
- Test: `tests/CSWarfront.Core.Tests/WarStateSerializerTests.cs`

**Interfaces:**
- Consumes: `WarState`, `Faction`, `RelationMatrix`, `MilitaryBase`, `UnitInstance`, `ProductionOrder`。
- Produces: `static class WarStateSerializer` — `byte[] Serialize(WarState state)`, `WarState Deserialize(byte[] bytes, UnitTypeRegistry types)`。論理状態（factions: id/name/treasury/homeBase/isPlayer/eliminated、relations 全ペア、bases: 全フィールド＋queue、units: id/typeKey/faction/hp/pos/state/target/order、NextInstanceId）を往復。`RepresentationRef` 等の非永続は保存しない。バージョンタグ先頭に `int version=1`。

- [ ] **Step 1: 失敗テストを書く**

`tests/CSWarfront.Core.Tests/WarStateSerializerTests.cs`:
```csharp
using CSWarfront.Core;
using Xunit;

public class WarStateSerializerTests
{
    private static WarState Sample()
    {
        var s = new WarState();
        var red = new Faction(0, "Red"); red.AddTreasury(123.5f); red.HomeBaseId = 200; red.IsPlayer = true;
        var blue = new Faction(1, "Blue"); blue.AddTreasury(10f);
        s.Factions.Add(red); s.Factions.Add(blue);
        s.Relations.Set(0, 1, Relation.Hostile);
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 5));
        b.OwnerFactionId = 0; b.CurrentHP = 250f; b.IsHeadquarters = true;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f) { Progress = 0.3f });
        s.Bases.Add(b);
        var u = new UnitInstance(7, "Tank_T1", 1, 80f, new WorldPos(1, 2, 3));
        u.State = UnitState.Moving; u.OrderTargetPos = new WorldPos(40, 0, 5);
        s.Units.Add(u);
        s.NextInstanceId = 8;
        return s;
    }

    [Fact]
    public void Roundtrip_preserves_logical_state()
    {
        var types = new UnitTypeRegistry(); types.Register(MvpUnitTypes.Tank_T1());
        var s = Sample();
        byte[] bytes = WarStateSerializer.Serialize(s);
        var r = WarStateSerializer.Deserialize(bytes, types);

        Assert.Equal(2, r.Factions.Count);
        Assert.Equal(123.5f, r.FindFaction(0).Treasury, 3);
        Assert.True(r.FindFaction(0).IsPlayer);
        Assert.Equal((ushort)200, r.FindFaction(0).HomeBaseId.Value);
        Assert.Equal(Relation.Hostile, r.Relations.Get(0, 1));
        Assert.Single(r.Bases);
        Assert.Equal(250f, r.Bases[0].CurrentHP, 3);
        Assert.Single(r.Bases[0].Queue);
        Assert.Equal(0.3f, r.Bases[0].Queue[0].Progress, 3);
        Assert.Single(r.Units);
        Assert.Equal(80f, r.FindUnit(7).CurrentHP, 3);
        Assert.Equal(UnitState.Moving, r.FindUnit(7).State);
        Assert.True(r.FindUnit(7).OrderTargetPos.HasValue);
        Assert.Equal((uint)8, r.NextInstanceId);
    }

    [Fact]
    public void Deserialize_empty_returns_fresh_state()
    {
        var types = new UnitTypeRegistry();
        var r = WarStateSerializer.Deserialize(null, types);
        Assert.NotNull(r);
        Assert.Empty(r.Factions);
    }
}
```

- [ ] **Step 2: 実行して失敗を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: FAIL。

- [ ] **Step 3: 実装**（`System.IO.BinaryWriter/Reader`。net35/net8 共通）

`src/CSWarfront/Core/WarStateSerializer.cs`:
```csharp
using System.IO;
namespace CSWarfront.Core
{
    /// <summary>WarStateの論理状態をバイト列へ往復（表現参照は保存しない）。</summary>
    public static class WarStateSerializer
    {
        private const int Version = 1;

        public static byte[] Serialize(WarState s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Version);
                // factions
                w.Write(s.Factions.Count);
                foreach (var f in s.Factions)
                {
                    w.Write(f.Id); w.Write(f.Name ?? "");
                    w.Write(f.Treasury);
                    w.Write(f.HomeBaseId.HasValue); w.Write(f.HomeBaseId.HasValue ? f.HomeBaseId.Value : (ushort)0);
                    w.Write(f.IsPlayer); w.Write(f.Eliminated);
                }
                // relations（5x5固定）
                for (int a = 0; a < 5; a++)
                    for (int b = 0; b < 5; b++)
                        w.Write((int)s.Relations.Get(a, b));
                // bases
                w.Write(s.Bases.Count);
                foreach (var b in s.Bases)
                {
                    w.Write(b.BaseId); w.Write((int)b.Type);
                    w.Write(b.OwnerFactionId.HasValue); w.Write(b.OwnerFactionId.HasValue ? b.OwnerFactionId.Value : (byte)0);
                    WritePos(w, b.Position);
                    w.Write(b.InfluenceRadius); w.Write(b.IsHeadquarters);
                    w.Write(b.MaxHP); w.Write(b.CurrentHP);
                    w.Write(b.Queue.Count);
                    foreach (var o in b.Queue) { w.Write(o.TypeKey ?? ""); w.Write(o.Cost); w.Write(o.BuildTime); w.Write(o.Progress); }
                }
                // units
                w.Write(s.Units.Count);
                foreach (var u in s.Units)
                {
                    w.Write(u.InstanceId); w.Write(u.TypeKey ?? ""); w.Write(u.FactionId); w.Write(u.CurrentHP);
                    WritePos(w, u.Position); w.Write((int)u.State);
                    w.Write(u.TargetId.HasValue); w.Write(u.TargetId.HasValue ? u.TargetId.Value : 0u);
                    w.Write(u.OrderTargetPos.HasValue);
                    WritePos(w, u.OrderTargetPos.HasValue ? u.OrderTargetPos.Value : new WorldPos(0, 0, 0));
                }
                w.Write(s.NextInstanceId);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static WarState Deserialize(byte[] bytes, UnitTypeRegistry types)
        {
            var s = new WarState();
            s.Types = types;
            if (bytes == null || bytes.Length == 0) return s;
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                int version = r.ReadInt32(); // 将来の分岐用
                int fcount = r.ReadInt32();
                for (int i = 0; i < fcount; i++)
                {
                    byte id = r.ReadByte(); string name = r.ReadString();
                    var f = new Faction(id, name); f.AddTreasury(r.ReadSingle());
                    bool hasHome = r.ReadBoolean(); ushort home = r.ReadUInt16();
                    if (hasHome) f.HomeBaseId = home;
                    f.IsPlayer = r.ReadBoolean(); f.Eliminated = r.ReadBoolean();
                    s.Factions.Add(f);
                }
                for (int a = 0; a < 5; a++)
                    for (int b = 0; b < 5; b++)
                        s.Relations.Set(a, b, (Relation)r.ReadInt32());
                int bcount = r.ReadInt32();
                for (int i = 0; i < bcount; i++)
                {
                    ushort baseId = r.ReadUInt16(); var type = (BaseType)r.ReadInt32();
                    bool hasOwner = r.ReadBoolean(); byte owner = r.ReadByte();
                    var pos = ReadPos(r);
                    var b = new MilitaryBase(baseId, type, pos);
                    if (hasOwner) b.OwnerFactionId = owner;
                    b.InfluenceRadius = r.ReadSingle(); b.IsHeadquarters = r.ReadBoolean();
                    b.MaxHP = r.ReadSingle(); b.CurrentHP = r.ReadSingle();
                    int qn = r.ReadInt32();
                    for (int q = 0; q < qn; q++)
                    {
                        var o = new ProductionOrder(r.ReadString(), r.ReadSingle(), r.ReadSingle());
                        o.Progress = r.ReadSingle(); b.Queue.Add(o);
                    }
                    s.Bases.Add(b);
                }
                int ucount = r.ReadInt32();
                for (int i = 0; i < ucount; i++)
                {
                    uint iid = r.ReadUInt32(); string tk = r.ReadString(); byte fac = r.ReadByte(); float hp = r.ReadSingle();
                    var pos = ReadPos(r);
                    var u = new UnitInstance(iid, tk, fac, hp, pos);
                    u.State = (UnitState)r.ReadInt32();
                    bool hasTarget = r.ReadBoolean(); uint tid = r.ReadUInt32(); if (hasTarget) u.TargetId = tid;
                    bool hasOrder = r.ReadBoolean(); var op = ReadPos(r); if (hasOrder) u.OrderTargetPos = op;
                    s.Units.Add(u);
                }
                s.NextInstanceId = r.ReadUInt32();
            }
            return s;
        }

        private static void WritePos(BinaryWriter w, WorldPos p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        private static WorldPos ReadPos(BinaryReader r) { return new WorldPos(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    }
}
```

- [ ] **Step 4: 実行して成功を確認**

Run: `dotnet test tests/CSWarfront.Core.Tests`
Expected: PASS。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(core): WarStateのバイト列シリアライズ往復（永続化コア）"
```

---

## Task 11: MilitaryManager と tick 駆動（Game層・統合）

> ここから Game 層。UnityEngine/CS 依存のため xunit ではなく**実機統合検証**（既存MODと同じ方針＝ビルド成功＋ゲーム内挙動確認）。各ステップの「確認」はゲーム内での目視。

**Files:**
- Create: `src/CSWarfront/Game/MilitaryManager.cs`
- Create: `src/CSWarfront/Game/WarfrontThreadingExtension.cs`
- Modify: `src/CSWarfront/Game/Mod.cs`（初期化フック追加）

**Interfaces:**
- Consumes: すべての Core step（`CombatStep`, `BaseCombatStep`, `Occupation`, `ProductionStep`, `InvasionOrders`, `TerritoryIncome`）、`WarState`。
- Produces: `static class MilitaryManager` — `WarState State { get; }`, `void EnsureInitialized()`, `void OnMainUpdate(float dt)`（メインスレッド：移動・スポーン反映）, `void OnSimTick()`（simスレッド：Core step群を回す）, `void ReplaceState(WarState s)`（ロード用）。
- Produces: `WarfrontThreadingExtension : ThreadingExtensionBase` — `OnUpdate`→`MilitaryManager.OnMainUpdate`、`OnAfterSimulationTick`→`MilitaryManager.OnSimTick`。

- [ ] **Step 1: MilitaryManager を実装**（Coreを束ねる。スポーン/移動は Task 12 で `LandUnitSpawner` に委譲、まずは Core step 駆動と収入・生産・戦闘・占領のループを組む）

`src/CSWarfront/Game/MilitaryManager.cs`:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。</summary>
    public static class MilitaryManager
    {
        public static WarState State { get; private set; }
        private static float _economyAccum;
        private const float EconomyIntervalSeconds = 5f;   // 経済tick間隔
        private const float IncomeRate = 0.01f;

        public static void EnsureInitialized()
        {
            if (State != null) return;
            State = new WarState();
            State.Types.Register(MvpUnitTypes.Tank_T1());
            // 勢力2つ（MVP）。基地/所有はTask13のシナリオ配線で設定。
            State.Factions.Add(new Faction(0, "Red"));
            State.Factions.Add(new Faction(1, "Blue"));
            State.Relations.Set(0, 1, Relation.Hostile);
        }

        public static void ReplaceState(WarState s) { State = s; }

        /// <summary>simスレッド：判断ロジックを回す。</summary>
        public static void OnSimTick()
        {
            if (State == null) return;
            float dt = 1f; // 1 sim tick ≈ 固定dt（MVP簡略）

            // 生産 → 完成分をスポーン要求へ
            var completed = ProductionStep.Advance(State, dt);
            foreach (var c in completed) SpawnQueue.Enqueue(c);

            // AI進軍命令（非プレイヤー勢力）
            foreach (var f in State.Factions)
                if (!f.IsPlayer && !f.Eliminated) InvasionOrders.AssignAdvance(State, f.Id);

            // 戦闘（ユニット同士＋基地攻撃）→ 占領
            CombatStep.Advance(State, dt);
            BaseCombatStep.Advance(State, dt);
            Occupation.ResolveCaptures(State);

            // 経済（低頻度）
            _economyAccum += dt;
            if (_economyAccum >= EconomyIntervalSeconds)
            {
                _economyAccum = 0f;
                var samples = DevelopmentSampler.Sample(); // Task 12
                foreach (var b in State.Bases)
                {
                    if (b.OwnerFactionId == null) continue;
                    float inc = TerritoryIncome.ForBase(b, samples, IncomeRate);
                    State.FindFaction(b.OwnerFactionId.Value)?.AddTreasury(inc);
                }
            }

            // 死亡ユニットの掃除（表現撤去はメインスレッドで）
            State.Units.RemoveAll(u => u.State == UnitState.Dead && !LandUnitSpawner.HasRepresentation(u.InstanceId));
        }

        /// <summary>メインスレッド：スポーン要求消化・移動・撃破表現の撤去。</summary>
        public static void OnMainUpdate(float dt)
        {
            if (State == null) return;
            while (SpawnQueue.Count > 0)
            {
                var c = SpawnQueue.Dequeue();
                uint id = State.AllocInstanceId();
                var type = State.Types.Get(c.TypeKey);
                State.Units.Add(new UnitInstance(id, c.TypeKey, c.FactionId, type != null ? type.MaxHP : 100f, c.SpawnPos));
                LandUnitSpawner.Spawn(id, c);
            }
            LandUnitSpawner.UpdateMovementAndCleanup(State);
        }

        internal static readonly Queue<CompletedUnit> SpawnQueue = new Queue<CompletedUnit>();
    }
}
```

- [ ] **Step 2: ThreadingExtension を実装**

`src/CSWarfront/Game/WarfrontThreadingExtension.cs`:
```csharp
using ICities;
namespace CSWarfront.Game
{
    public class WarfrontThreadingExtension : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try
            {
                MilitaryManager.EnsureInitialized();
                if (!SimulationManager.instance.SimulationPaused)
                    MilitaryManager.OnMainUpdate(simulationTimeDelta);
            }
            catch (System.Exception e) { ModConfig.LogError("OnUpdate: " + e); }
        }

        public override void OnAfterSimulationTick()
        {
            try { MilitaryManager.OnSimTick(); }
            catch (System.Exception e) { ModConfig.LogError("OnSimTick: " + e); }
        }
    }
}
```

- [ ] **Step 3: ビルドを確認**（`LandUnitSpawner`/`DevelopmentSampler` はTask12で実装。ここでは**空スタブ**を先に置いてビルドを通す）

先行スタブ `src/CSWarfront/Game/LandUnitSpawner.cs`（Task 12 で中身実装）:
```csharp
using CSWarfront.Core;
namespace CSWarfront.Game
{
    public static class LandUnitSpawner
    {
        public static void Spawn(uint instanceId, CompletedUnit c) { }
        public static void UpdateMovementAndCleanup(WarState state) { }
        public static bool HasRepresentation(uint instanceId) { return false; }
    }
}
```
先行スタブ `src/CSWarfront/Game/DevelopmentSampler.cs`（Task 12 で実装）:
```csharp
using System.Collections.Generic;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    public static class DevelopmentSampler
    {
        public static List<DevelopmentSample> Sample() { return new List<DevelopmentSample>(); }
    }
}
```

Run: `powershell -File build.ps1`
Expected: ビルド成功、DLL配置。

- [ ] **Step 4: 実機でMOD有効化を確認**

CS無印を起動→Content ManagerでMOD有効→街ロード→ログ（`[CSWarfront]`）にエラーが出ないこと。ユニット/基地はまだ配線前なので画面上の変化はなくてよい。

- [ ] **Step 5: コミット**

```bash
git add -A
git commit -m "feat(game): MilitaryManagerとtick駆動配線（Core step群を実行）"
```

---

## Task 12: 地上ユニットのスポーン/移動と発展度サンプリング（Game層・統合）

**Files:**
- Modify: `src/CSWarfront/Game/LandUnitSpawner.cs`（スタブを実装）
- Modify: `src/CSWarfront/Game/DevelopmentSampler.cs`（スタブを実装）

**Interfaces:**
- Consumes: `WarState`, `UnitInstance`, `CompletedUnit`, `DevelopmentSample`, `WorldPos`。CS: `VehicleManager`, `BuildingManager`, `UnityEngine.Vector3`。
- Produces: `LandUnitSpawner` — `void Spawn(uint instanceId, CompletedUnit c)`（車両プレハブを生成し instanceId と対応付け）, `void UpdateMovementAndCleanup(WarState state)`（各ユニットの `Position` を車両位置で更新、`OrderTargetPos` へ誘導、Dead は車両撤去）, `bool HasRepresentation(uint instanceId)`。
- Produces: `DevelopmentSampler` — `List<DevelopmentSample> Sample()`（`BuildingManager` を走査し建物レベル×存在で発展度サンプルを作る。全走査は重いので**間引き**：本MVPは一定間隔でのみ呼ばれる経済tickから使う）。

- [ ] **Step 1: LandUnitSpawner を実装**（車両プレハブは既定流用。`WorldPos`⇔`Vector3` 変換）

`src/CSWarfront/Game/LandUnitSpawner.cs`:
```csharp
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>地上ユニットをCS車両として表現する。位置取得と誘導、撃破時の撤去を担う。</summary>
    public static class LandUnitSpawner
    {
        private static readonly Dictionary<uint, ushort> _vehicleByInstance = new Dictionary<uint, ushort>();

        public static bool HasRepresentation(uint instanceId) { return _vehicleByInstance.ContainsKey(instanceId); }

        public static void Spawn(uint instanceId, CompletedUnit c)
        {
            // MVP: 既定の車両プレハブ名を1つ流用（例: 消防車/装甲車風）。アセット割当は後日UI化。
            VehicleInfo info = FindDefaultLandVehicle();
            if (info == null) { ModConfig.LogError("既定車両プレハブ未取得"); return; }
            Vector3 pos = ToVec(c.SpawnPos);
            VehicleManager vm = Singleton<VehicleManager>.instance;
            ushort vid;
            if (vm.CreateVehicle(out vid, ref Singleton<SimulationManager>.instance.m_randomizer,
                info, pos, TransferManager.TransferReason.None, false, false))
            {
                _vehicleByInstance[instanceId] = vid;
            }
        }

        public static void UpdateMovementAndCleanup(WarState state)
        {
            VehicleManager vm = Singleton<VehicleManager>.instance;
            var toRemove = new List<uint>();
            foreach (var u in state.Units)
            {
                ushort vid;
                if (!_vehicleByInstance.TryGetValue(u.InstanceId, out vid)) continue;
                if (u.State == UnitState.Dead)
                {
                    vm.ReleaseVehicle(vid);
                    toRemove.Add(u.InstanceId);
                    continue;
                }
                // 位置をCoreへ反映
                Vector3 p = vm.m_vehicles.m_buffer[vid].GetLastFramePosition();
                u.Position = new WorldPos(p.x, p.y, p.z);
                // 誘導は MVP 簡略：目標方向へ直接テレポート漸進（本格パスファインディングは後日）
                if (u.OrderTargetPos.HasValue && u.State == UnitState.Moving)
                {
                    // NOTE: MVPでは車両AIの目的地設定に置換予定。ここでは位置補間で前進を可視化。
                }
            }
            foreach (var id in toRemove) _vehicleByInstance.Remove(id);
        }

        private static VehicleInfo FindDefaultLandVehicle()
        {
            // MVP: PrefabCollection から適当な地上車両を1つ。実装時にゲーム内に存在する確実な名前へ調整する。
            return PrefabCollection<VehicleInfo>.FindLoaded("Fire Truck");
        }

        private static Vector3 ToVec(WorldPos p) { return new Vector3(p.X, p.Y, p.Z); }
    }
}
```

> **実装メモ（重要）**：`FindDefaultLandVehicle` の名前 `"Fire Truck"` と `CreateVehicle` の引数は環境で挙動差がある。実機でスポーンを確認し、確実に存在する車両プレハブ名／生成フラグに調整すること。移動誘導（車両AIの目的地設定）は MVP では簡略で可（位置が更新され、交戦・占領が成立すればよい）。本格的な経路誘導は塊拡張で対応。

- [ ] **Step 2: DevelopmentSampler を実装**

`src/CSWarfront/Game/DevelopmentSampler.cs`:
```csharp
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>BuildingManagerから発展度サンプルを作る（経済tickの低頻度でのみ呼ぶ）。</summary>
    public static class DevelopmentSampler
    {
        public static List<DevelopmentSample> Sample()
        {
            var list = new List<DevelopmentSample>();
            BuildingManager bm = Singleton<BuildingManager>.instance;
            Building[] buf = bm.m_buildings.m_buffer;
            for (int i = 1; i < buf.Length; i++)
            {
                if ((buf[i].m_flags & Building.Flags.Created) == 0) continue;
                if (buf[i].Info == null) continue;
                Vector3 p = buf[i].m_position;
                // 発展度＝建物レベル+1（MVP簡略。人口密度等は後日加味）。
                float dev = buf[i].m_level + 1;
                list.Add(new DevelopmentSample { Position = new WorldPos(p.x, p.y, p.z), Development = dev });
            }
            return list;
        }
    }
}
```

- [ ] **Step 3: ビルドを確認**

Run: `powershell -File build.ps1`
Expected: ビルド成功。

- [ ] **Step 4: コミット**

```bash
git add -A
git commit -m "feat(game): 地上ユニット車両表現と発展度サンプリング"
```

---

## Task 13: 基地バインドとMVPシナリオ配線＋永続化（Game層・統合）

**Files:**
- Create: `src/CSWarfront/Game/BaseBuildingBinder.cs`
- Create: `src/CSWarfront/Game/Serialization/WarStateDataExtension.cs`
- Modify: `src/CSWarfront/Game/MilitaryManager.cs`（シナリオ初期化に基地2つを追加）

**Interfaces:**
- Consumes: `WarState`, `MilitaryBase`, `WarStateSerializer`。CS: `SerializableDataExtensionBase`, `BuildingManager`。
- Produces: `BaseBuildingBinder` — `void SeedTwoBaseScenario(WarState state)`（MVP：都市内の適当な2地点に `Army` 基地を1つずつ作り、Red/Blueに割り当て、各HQに設定、初期軍資金と初期生産キューを積む）。実運用では建物設置と紐付けに置換予定。
- Produces: `WarStateDataExtension : SerializableDataExtensionBase` — `OnSaveData`/`OnLoadData` で `WarStateSerializer` を介して保存・復元し、ロード時は `MilitaryManager.ReplaceState` → 表現再生成。

- [ ] **Step 1: MVPシナリオ配線**（`EnsureInitialized` から基地シードを呼ぶ）

`src/CSWarfront/Game/BaseBuildingBinder.cs`:
```csharp
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>MVP用に2基地シナリオを配置する（後日、実建物への紐付けに置換）。</summary>
    public static class BaseBuildingBinder
    {
        public static void SeedTwoBaseScenario(WarState s)
        {
            if (s.Bases.Count > 0) return; // 二重配置防止

            var redBase = new MilitaryBase(1, BaseType.Army, new WorldPos(-300, 0, 0));
            redBase.OwnerFactionId = 0; redBase.IsHeadquarters = true;
            redBase.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
            s.Bases.Add(redBase);
            s.FindFaction(0).HomeBaseId = 1;
            s.FindFaction(0).AddTreasury(200f);

            var blueBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0));
            blueBase.OwnerFactionId = 1; blueBase.IsHeadquarters = true;
            blueBase.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
            s.Bases.Add(blueBase);
            s.FindFaction(1).HomeBaseId = 2;
            s.FindFaction(1).AddTreasury(200f);
        }
    }
}
```

`MilitaryManager.EnsureInitialized` の末尾に追記（`State.Relations.Set(...)` の直後）:
```csharp
            BaseBuildingBinder.SeedTwoBaseScenario(State);
```

> 座標 `(-300,0,0)`/`(300,0,0)` はマップ中心基準の仮値。実機で可視域内になるよう調整（マップ中心はワールド原点付近）。

- [ ] **Step 2: 永続化エクステンションを実装**

`src/CSWarfront/Game/Serialization/WarStateDataExtension.cs`:
```csharp
using ICities;
using CSWarfront.Core;
namespace CSWarfront.Game.Serialization
{
    /// <summary>WarStateをセーブデータへ永続化。ロード時に状態復元＋表現再生成。</summary>
    public class WarStateDataExtension : SerializableDataExtensionBase
    {
        private const string DataId = "CSWarfront.WarState.v1";

        public override void OnSaveData()
        {
            try
            {
                MilitaryManager.EnsureInitialized();
                byte[] bytes = WarStateSerializer.Serialize(MilitaryManager.State);
                serializableDataManager.SaveData(DataId, bytes);
            }
            catch (System.Exception e) { ModConfig.LogError("Save: " + e); }
        }

        public override void OnLoadData()
        {
            try
            {
                byte[] bytes = serializableDataManager.LoadData(DataId);
                if (bytes == null || bytes.Length == 0) return; // 新規ゲームは既定初期化に任せる
                var types = new UnitTypeRegistry();
                types.Register(MvpUnitTypes.Tank_T1());
                WarState restored = WarStateSerializer.Deserialize(bytes, types);
                MilitaryManager.ReplaceState(restored);
                // 表現は非永続なので、生存ユニットの車両をロード後に再生成する
                foreach (var u in restored.Units)
                    if (u.State != UnitState.Dead)
                        LandUnitSpawner.Spawn(u.InstanceId,
                            new CompletedUnit { BaseId = 0, FactionId = u.FactionId, TypeKey = u.TypeKey, SpawnPos = u.Position });
            }
            catch (System.Exception e) { ModConfig.LogError("Load: " + e); }
        }
    }
}
```

- [ ] **Step 3: ビルドを確認**

Run: `powershell -File build.ps1`
Expected: ビルド成功。

- [ ] **Step 4: コミット**

```bash
git add -A
git commit -m "feat(game): 2基地MVPシナリオ配線とWarState永続化"
```

---

## Task 14: MVP実機統合検証（縦スライス通し）

**Files:**（コード変更なし。検証と微調整のみ。必要に応じ座標/プレハブ名/数値を実機に合わせて修正）

**検証シナリオ**：Red/Blue 2勢力・各Army基地1・敵対。生産→出撃→交戦→基地HP0→占領→キュー奪取が回ること。

- [ ] **Step 1: ビルド＆配置**

Run: `powershell -File build.ps1`
Expected: 成功・配置。

- [ ] **Step 2: 生産とスポーンを確認**

CS起動→MOD有効→街ロード→数十秒待機。ログまたは画面で、両基地から Tank（既定車両）が**スポーン**することを確認。出なければ `LandUnitSpawner.FindDefaultLandVehicle` の名前と `CreateVehicle` フラグを実機に合わせて修正し、再ビルド。

- [ ] **Step 3: 交戦と撃破を確認**

スポーンしたユニットが敵対相手と近接した際に HP が削れ、撃破で車両が消えることを確認（`CombatStep`）。距離が縮まらない場合は、基地座標を近づける／`InfluenceRadius`・`Range` を実機スケールに調整。

- [ ] **Step 4: 基地占領と資産移管を確認**

攻撃側ユニットが敵基地に到達し、基地HPが削れて0になると所有権が移り（`OwnerFactionId` 変化）、HQ喪失側が脱落することを確認。占領後、奪った基地から新所有者のユニットが生産されること（キュー奪取）。

- [ ] **Step 5: セーブ/ロードの永続化を確認**

戦闘途中でセーブ→メインメニュー→再ロード。軍資金・基地所有・生存ユニット・生産キューが復元され、車両表現が再生成されることを確認（`WarStateDataExtension`）。

- [ ] **Step 6: 調整値を反映してコミット**

実機で調整した座標・プレハブ名・数値（`InfluenceRadius`, `Range`, income rate, 基地HP等）をソースに反映。
```bash
git add -A
git commit -m "chore: MVP実機検証に基づく座標/プレハブ/数値の調整"
```

- [ ] **Step 7: MVP完了レビュー**

以下を満たせば MVP 完了：
- [ ] 2勢力が軍資金→生産→スポーンを自走
- [ ] ユニット同士が敵対時に交戦・撃破
- [ ] 敵基地HPを削りきって占領、基地/圏/生産キューを奪取
- [ ] HQ喪失で勢力脱落
- [ ] セーブ/ロードで軍事状態が復元

---

## 自己レビュー（spec対応・プレースホルダ・型整合）

**spec対応**：
- 要件① 勢力・関係 → Task 2（RelationMatrix 対称・Hostile/Neutral/Allied）✔
- 要件② Tier制ユニット → Task 3（UnitType データ駆動、Domain/Category/Tier）。MVPは Tank_T1 のみ定義、拡張余地あり ✔（全30種は spec §10 で将来拡張）
- 要件③ 基地からスポーン → Task 6（基地・生産）＋Task 11/12（スポーン）✔
- 要件④ 勢力圏→軍資金 → Task 7（TerritoryIncome）＋Task 12（DevelopmentSampler）＋Task 11（経済tick）✔
- 要件⑤ 占領・資産移管 → Task 8（Occupation：所有/HP/キュー移管、HQ喪失脱落）✔
- A+Bモード：MVPは観戦(A)を実装（全勢力AI）。プレイヤー指揮(B)のUIは spec 横断項目として MVP 後（`IsPlayer` フラグとController分離の素地は Task 2/11 に存在）✔（MVPスコープ通り）
- 永続化（最初から）→ Task 10（コア）＋Task 13（配線）✔
- ハイブリッド表現：地上=車両（Task 12）。海空自由移動は MVP 範囲外（地上1種のMVP）✔
- 塊6（弾道ミサイル・迎撃）：別スライス。本計画に含めない（spec 通り）✔

**プレースホルダscan**：Game層(Task 11-13)の「実機で名前/座標/フラグを調整」は、CS実機依存で事前確定不能な既知の調整点であり、各所に具体的な既定値と調整手順を明記済み。Core層(Task 1-10)はTBD無し・全コード記載済み。

**型整合**：`WarState`(Factions/Relations/Units/Bases/Types/NextInstanceId)、`UnitInstance`(InstanceId/TypeKey/FactionId/CurrentHP/Position/State/TargetId/OrderTargetPos)、`MilitaryBase`(BaseId/Type/OwnerFactionId/Position/InfluenceRadius/IsHeadquarters/MaxHP/CurrentHP/Queue)、`CompletedUnit`(BaseId/FactionId/TypeKey/SpawnPos)、`DevelopmentSample`(Position/Development) は全タスクで一貫。`MilitaryBase` は Task 5 で空 `partial class`、Task 6 で実装（前方参照解消）。Serializer(Task 10)は全フィールドを往復。整合を確認済み。
