using System;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Core の各stepを CS の tick で駆動し、結果を表現へ反映する橋渡し（singleton相当の静的）。
    /// スレッド境界（重要・Task19で変更）:
    ///  - ユニットはもうCS車両（VehicleManager）を借用しない。CS車両AI（例: FireTruckAI）は
    ///    TransferManager等サービス系のディスパッチに組み込まれており、TransferReason.None等の
    ///    無効な文脈で呼ばれるとオファー簿記が配列範囲外アクセスでクラッシュする（実機ログ確認済み、
    ///    TransferManager.RemoveIncomingOffer）。Task15でCore（MovementStep）がユニット位置を
    ///    論理的に所有するようになって以降、CS車両AI（パスファインディング等）は一切使われておらず
    ///    得るものがない。そのためユニットの見た目は素のUnity GameObject（UnitVisuals）とし、
    ///    車両プレハブからはメッシュ＋マテリアルのみを借用してAIには一切触れない。
    ///  - sim スレッド（OnSimTick、ThreadingExtensionBase.OnAfterSimulationTick経由）:
    ///    Core判断ロジック＋CSバッファ読み取り（基地建物・地形サンプリング等）専用。
    ///    _stateLock を保持したまま直列に実行する。
    ///  - メインスレッド（OnMainVisualUpdate、ThreadingExtensionBase.OnUpdate経由）:
    ///    Unityオブジェクト操作（GameObject生成/移動/破棄）専用。CS実体（Vehicle/Building等）には
    ///    一切触れない。_stateLock はスナップショット構築中のみ保持し、Unity操作はロック解放後に行う
    ///    （ロック保持中にUnity APIを呼ぶと最悪ケースでsimスレッドを長時間ブロックしうるため）。
    ///  - 注意: ゲームが一時停止中は OnAfterSimulationTick が発火しないため、基地配置やスポーンは
    ///    停止解除まで待機する（MVPとして許容）。一方 OnUpdate は一時停止中も動くため、見た目の同期は
    ///    停止中も続く（意図的：一時停止中も現在の配置を表示し続けるため）。
    ///  - _stateLock は、save/loadスレッド（SerializeLocked/LoadAndRebuild）とsimスレッド(OnSimTick)
    ///    の State への同時アクセスを防ぐために引き続き必要（net35のため
    ///    System.Collections.Concurrent は使用不可）。
    ///
    /// Task67: 本ファイルは核となる状態（State/_stateLock）とライフサイクル（EnsureInitialized/
    /// ReplaceState/Reset）のみを持つ。責務ごとに以下の partial ファイルへ分割済み
    /// （MilitaryManagerRelations/MilitaryManagerUnitCommands等、既存のTask34/48の分割方針を踏襲）:
    ///  - MilitaryManagerSimTick.cs: OnSimTick とそのための蓄積カウンタ/定数、LogDiagnostics。
    ///  - MilitaryManagerVisuals.cs: OnMainVisualUpdate とそのためのスナップショットリスト。
    ///  - MilitaryManagerPersistence.cs: SerializeLocked/LoadAndRebuild（セーブ/ロード）。
    ///  - MilitaryManagerUiApi.cs: 基地/ユニットのUI向けスナップショット取得・所有権変更等の薄いラッパー。
    /// Reset() が触る蓄積カウンタ（_economyAccum等）はOnSimTick側と共有するためこちらに残す。
    /// </summary>
    public static partial class MilitaryManager
    {
        public static WarState State { get; private set; }

        // save/loadスレッドとsimスレッド間の State への同時アクセスを防ぐ粗粒度ロック。
        // MVP規模（数十ユニット）では単一ロックで十分。
        private static readonly object _stateLock = new object();

        // 経済tickの間引き用（OnSimTick、MilitaryManagerSimTick.cs）。Reset()で0に戻すためここに置く。
        private static float _economyAccum;

        // 道路グラフの再構築間隔（ゲーム内時間）。プレイヤーが道路を敷設/破壊し続けるため定期的に作り直す
        // （Task23）。simスレッドのみが触る。Reset()で0に戻すためここに置く。
        private static float _roadRebuildAccum;

        // 道路グラフ未構築時（State.Roads == null）の再試行間隔（ゲーム内時間）。NetManagerがまだ
        // 準備できていない等の失敗が続く間、毎tickフルビルドを試みてログを埋め尽くさないため
        // （Task23レビューImportant）。simスレッドのみが触る。Reset()で0に戻すためここに置く。
        private static float _roadBuildRetryAccum;
        // セッション中まだ一度も構築を試みていない場合は初回のみ即座に試行する（上の間隔待ちをしない）。
        private static bool _hasAttemptedRoadBuild;

        // 遮蔽物マップ（State.Cover）の再構築間隔（ゲーム内時間）。RoadGraphと同じ理由
        // （プレイヤーが建物を建設/解体し続けるため定期的に作り直す、Task44）。simスレッドのみが触る。
        // Reset()で0に戻すためここに置く。
        private static float _coverRebuildAccum;

        // 遮蔽物マップ未構築時（State.Cover == null）の再試行間隔（ゲーム内時間）。RoadGraphの
        // _roadBuildRetryAccumと同じ間引きパターン（Task44）。simスレッドのみが触る。
        // Reset()で0に戻すためここに置く。
        private static float _coverBuildRetryAccum;
        private static bool _hasAttemptedCoverBuild;

        // 幽霊基地（建物が既に存在しない論理基地）掃除の間引き用（ゲーム内時間）。毎tickフルスキャンは
        // 無駄なため一定間隔でのみ実行する（Task24）。simスレッドのみが触る。Reset()で0に戻すためここに置く。
        private static float _baseReconcileAccum;

        // ゲーム内時間ベースのdt計算用（Task21）。simスレッドのみが触る。Reset()で0に戻すためここに置く。
        private static DateTime _lastGameTime;
        private static bool _hasLastGameTime;

        public static void EnsureInitialized()
        {
            if (State != null) return;
            // ローカルで完全に初期化してから最後に State へ代入する。
            // こうすることで OnSimTick の `if (State == null) return;` が
            // 初期化途中の半端な状態を観測することがない。
            var state = new WarState();
            UnitStatsFile.EnsureLoaded(); // Task92: unit-stats.xmlの上書きをロスター構築より前に反映する
            LandUnitRoster.RegisterAll(state.Types); // 陸上7兵種×Tier1〜5（Task28）
            NavalUnitRoster.RegisterAll(state.Types); // 海上2種(Destroyer/Carrier)×Tier1〜5（Task61）
            AirUnitRoster.RegisterAll(state.Types);   // 航空3種(AirSuperiority/TacticalBomber/SuicideDrone)×Tier1〜5（Task61）

            // 全5勢力を生成する（Options内の「建設先勢力」ドロップダウンはどの選択も有効な値を
            // 指すようにするため）。
            // 基地はもうここではシードしない（Task18）：プレイヤーがOptions指定建物（Task74/Task82で
            // 唯一の配置経路になった）を配置した瞬間にBasePlacementWatcher が論理基地を作成する。
            // 開始時の軍資金だけは、配置直後に生産を
            // 始められるようここで与えておく。
            string[] names = WarfrontSettings.FactionNames;
            for (byte i = 0; i < WarfrontSettings.MaxFactions; i++)
            {
                var f = new Faction(i, names[i]);
                f.AddTreasury(200f);
                state.Factions.Add(f);
            }

            // Task95: 外部襲来イベント専用のInvader勢力（第6勢力、モスグリーン、常時敵対）。
            // 建設先勢力ドロップダウン（MaxFactions=5）や関係設定UIには登場しない。軍資金も不要
            // （基地・生産を一切持たず、部隊はInvasionEventsが直接スポーンする）。
            InvasionEvents.EnsureInvaderFaction(state);

            // MVPの既定関係は全勢力ペアがHostile。実装はCore.RelationPresets.ApplyAllHostileに委譲する
            // （Task49: Options画面の「全て敵対に戻す」ボタンからも同じ実装を再利用するため）。
            // これは「新規State作成時」のみ適用される既定値であり、既存セーブをロードした場合は
            // シリアライズ済みの関係（WarState.Relations、format v4で25ペア全て永続化済み）がそのまま復元される。
            RelationPresets.ApplyAllHostile(state.Relations, WarfrontSettings.MaxFactions);

            State = state;
        }

        public static void ReplaceState(WarState s) { lock (_stateLock) { State = s; } }

        /// <summary>
        /// レベルアンロード時（メインメニューへ戻る等）に全セッション状態を初期化する（Task16レビューImportant）。
        /// これが無いと、セーブから復元したセッション由来のセッション状態が同一プロセス内の
        /// 次のゲーム開始へ持ち越されてしまう。呼び出し元（WarfrontLoadingExtension.OnLevelUnloading）は
        /// OnSimTickの外側（CSのロードライフサイクル）で呼ばれるため、_stateLock の再入は発生しない。
        /// BasePlacementWatcher.Unsubscribe は呼び出し元がこのReset()より先に行う想定（イベント購読解除は
        /// レベルライフサイクルに紐づくため、ここでは pending リストのクリアのみ行う）。
        /// </summary>
        public static void Reset()
        {
            // UnitVisuals.DestroyAll はUnity GameObjectを破棄するためメインスレッド専用API。
            // Reset()自体はCSのロードライフサイクル（メインスレッド、OnLevelUnloading経由）から
            // 呼ばれるためここで直接呼んで問題ない（_stateLockはCS実体を持たないState差し替えのみ保護）。
            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: 基地の勢力別オーバーレイもレベルアンロード時に破棄する。
            CombatFx.DestroyAll(); // Task42: 発砲エフェクトもレベルアンロード時に破棄する。
            KillFx.DestroyAll(); // Task65: 撃破爆発エフェクトもレベルアンロード時に破棄する。
            BombFx.DestroyAll(); // Task87: 落下中の爆弾もレベルアンロード時に破棄する。
            AaMissileFx.DestroyAll(); // Task90: 飛翔中の対空ミサイルも破棄する。
            UnitStatsFile.Reset(); // Task92: 次のロードでunit-stats.xmlを再読込させる。
            MissileVisuals.DestroyAll(); // Task63: 飛翔中ミサイルの見た目もレベルアンロード時に破棄する。
            MissileVisuals.DestroyAllFx(); // Task63: 着弾/迎撃の演出も同様。
            UI.OrderDestinationMarkers.DestroyAll(); // Task62: 目的地マーカーもレベルアンロード時に破棄する。
            UI.CommandToast.Destroy(); // Task62: トーストラベルもレベルアンロード時に破棄する。
            // Task54: このMODが封鎖した道路(PathFailedビット)を解除する。Reset()自体はOnLevelUnloading
            // （レベル遷移中、simスレッドは既に停止している想定）から呼ばれるため他スレッドとの競合は
            // 想定していない。CombatRoadBlocker.Reset内部は例外を外へ伝播しないガード付き
            // （レベルティアダウン中でNetManagerが無効化されているケースがあり得るため、失敗しても
            // 実害なしとして許容する＝要件通り）。
            // Task56レビュー: このコメントは元々あったが実際の呼び出しが抜けており、レベルアンロードでも
            // PathFailedビットが解除されないまま次セッションへ持ち越されうる欠落だったため追加した。
            CombatRoadBlocker.Reset();
            // Task72: このMODが隠した基地建物(Hiddenビット)もレベルアンロード時に解除する。
            // 従来はBaseVisuals.DestroyAll()がBaseHiddenSync.SetDesired(false)を積むだけで、
            // 実際にCS建物バッファへ反映するApplyPendingはsimスレッドのOnSimTick経由でしか
            // 呼ばれないため、アンロード後にOnSimTickが二度と回らなければ反映されないまま
            // （次にこのbuildingIdへ乗る全く別の建物へHiddenが漏れうる）欠落だった。
            // CombatRoadBlocker.Resetと同じ「メインスレッドから直接CSバッファへ書く」形で確実に戻す。
            BaseHiddenSync.Reset();
            CombatCollateral.Reset(); // Task65: 抽選間引き用の内部状態もレベルアンロード時にクリアする。

            lock (_stateLock)
            {
                State = null;
                _economyAccum = 0f;
                _roadRebuildAccum = 0f;
                _roadBuildRetryAccum = 0f;
                _hasAttemptedRoadBuild = false;
                _seaGridRebuildAccum = 0f; // Task92: 海上航行グリッドの構築状態もセッションを跨がせない
                _hasAttemptedSeaGridBuild = false;
                _coverRebuildAccum = 0f;
                _coverBuildRetryAccum = 0f;
                _hasAttemptedCoverBuild = false;
                _baseReconcileAccum = 0f;
                _hasLastGameTime = false;
                _lastGameTime = default(DateTime);
                BasePlacementWatcher.ClearPending();
            }

            // 較正診断の積算（Task26）：MilitaryManagerとは別の専用ロックで保護されているため個別にクリアする。
            SpeedCalibrationDiagnostics.Reset();

            // *Panel.Destroy はUnity GameObjectを破棄するためメインスレッド専用API。Reset()自体が
            // CSのロードライフサイクルから呼ばれる点は上の UnitVisuals.DestroyAll と同じ前提のため、
            // ここで直接呼んで問題ない。UnitSelection.Clear は次セッションへ選択IDを持ち越さないため（Task31）。
            UI.BaseInfoPanel.Destroy();
            UI.UnitInfoPanel.Destroy();
            UI.AssetAssignPanel.Destroy(); // Task36
            UI.UnitSelection.Clear();
            UI.UnitBoxSelection.Destroy(); // Task48: 範囲選択の矩形/ハイライトGameObjectと選択状態
            UI.UnitCommandInput.Reset(); // Task48: 集結地点のターゲティング状態を持ち越さない
            UI.MissileLaunchTargeting.Reset(); // Task63: ミサイル発射地点のターゲティング状態を持ち越さない
            UI.PanelChrome.ResetCache(); // Task56: キャッシュ済みPauseMenu/UIView参照を次セッションへ持ち越さない
        }
    }
}
