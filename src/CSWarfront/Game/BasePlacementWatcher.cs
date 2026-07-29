using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーが電力タブから配置/解体した軍事基地建物（WarfrontBasePrefab.Prefab）を検知し、
    /// 対応する論理 MilitaryBase を作成/削除する（Task18）。
    /// スレッド注記:
    ///  - CS のイベント（EventBuildingCreated/EventBuildingReleased）はどのスレッドから発火するか
    ///    保証されないため、ハンドラは最小限（idの記録のみ）に留め、CS API呼び出しやWarState操作は
    ///    一切行わない（ここで例外が漏れるとゲーム側が未捕捉ポップアップを出すため try/catch で必ず握る）。
    ///  - 実際の反映（BuildingManagerバッファ読み取り＋WarState更新）は ProcessPending 経由で
    ///    MilitaryManager.OnSimTick（simスレッド、_stateLock保持済み）からのみ行う。
    /// </summary>
    public static class BasePlacementWatcher
    {
        private static bool _subscribed;
        private static readonly object _pendingLock = new object();
        private static readonly List<ushort> _pendingCreated = new List<ushort>();
        private static readonly List<ushort> _pendingReleased = new List<ushort>();

        /// <summary>
        /// Task60: 基地建物の向き（ラジアン、CS Building.m_angle）のキャッシュ。Game/BaseVisuals.cs が
        /// 勢力別モデルのオーバーレイを正しい向きで表示するために使う。書き込みは常にこのクラスの
        /// simスレッド専用メソッド（ProcessCreated、呼び出し元 MilitaryManager.OnSimTick が既に
        /// _stateLock を保持している）から行う——ここで既にCS建物バッファ（Building構造体）を
        /// 読んでいるため、便乗して1回の読み取りで済ませ、メインスレッドから BuildingManager へ
        /// 触れる経路を増やさない（既定の安全な選択：CS実体はsimスレッド専用というルールを維持する）。
        /// 読み取り（<see cref="TryGetAngle"/>）は MilitaryManager.OnMainVisualUpdate が同じ
        /// _stateLock を保持したままスナップショット構築中に呼ぶ想定（Dictionary自体はスレッドセーフで
        /// はないため、呼び出し側の規約でスレッド安全性を担保する）。
        /// </summary>
        private static readonly Dictionary<ushort, float> _baseAngles = new Dictionary<ushort, float>();

        /// <summary>冪等。OnLevelLoaded から呼ばれる想定。</summary>
        public static void Subscribe()
        {
            if (_subscribed) return;
            try
            {
                if (!Singleton<BuildingManager>.exists)
                {
                    ModConfig.LogError("BasePlacementWatcher.Subscribe: BuildingManager not ready; skip");
                    return;
                }
                BuildingManager bm = Singleton<BuildingManager>.instance;
                bm.EventBuildingCreated += OnBuildingCreated;
                bm.EventBuildingReleased += OnBuildingReleased;
                _subscribed = true;
                ModConfig.Log("BasePlacementWatcher: subscribed to EventBuildingCreated/EventBuildingReleased");
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Subscribe exception: " + e); }
        }

        /// <summary>冪等。OnLevelUnloading から呼ばれる想定。</summary>
        public static void Unsubscribe()
        {
            if (!_subscribed) return;
            try
            {
                if (Singleton<BuildingManager>.exists)
                {
                    BuildingManager bm = Singleton<BuildingManager>.instance;
                    bm.EventBuildingCreated -= OnBuildingCreated;
                    bm.EventBuildingReleased -= OnBuildingReleased;
                }
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Unsubscribe exception: " + e); }
            finally { _subscribed = false; }
        }

        /// <summary>セッション終了時（MilitaryManager.Reset経由）に持ち越しを防ぐ。</summary>
        public static void ClearPending()
        {
            lock (_pendingLock)
            {
                _pendingCreated.Clear();
                _pendingReleased.Clear();
            }
            // Task60: 呼び出し元（MilitaryManager.Reset）は既に_stateLockを保持しているため、
            // ここで追加のロックは不要（_baseAnglesの書き込みは常にそのロック内、ProcessCreated経由）。
            _baseAngles.Clear();
        }

        /// <summary>Task60: 指定基地の向き（ラジアン）を返す。呼び出し元は_stateLockを保持していること
        /// （クラス冒頭の<see cref="_baseAngles"/>コメント参照）。まだ一度もProcessCreatedで観測されて
        /// いない基地（理論上は無いはずだが、防御的に）はfalseを返し、呼び出し側は既定角度(0)へ
        /// フォールバックすること。</summary>
        internal static bool TryGetAngle(ushort baseId, out float angleRadians)
        {
            return _baseAngles.TryGetValue(baseId, out angleRadians);
        }

        // CSのイベントハンドラ本体。呼び出しスレッド不明のため、idの記録のみ（例外は必ず握る）。
        private static void OnBuildingCreated(ushort id)
        {
            try { lock (_pendingLock) { _pendingCreated.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingCreated exception: " + e); }
        }

        private static void OnBuildingReleased(ushort id)
        {
            try { lock (_pendingLock) { _pendingReleased.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingReleased exception: " + e); }
        }

        /// <summary>
        /// simスレッド（MilitaryManager.OnSimTick、呼び出し元が既に _stateLock 保持済み）から呼ぶ。
        /// pending リストを排出し、CS建物バッファを読んで WarState.Bases を更新する。
        /// </summary>
        public static void ProcessPending(WarState state)
        {
            if (state == null) return;

            List<ushort> created = null;
            List<ushort> released = null;
            lock (_pendingLock)
            {
                if (_pendingCreated.Count > 0) { created = new List<ushort>(_pendingCreated); _pendingCreated.Clear(); }
                if (_pendingReleased.Count > 0) { released = new List<ushort>(_pendingReleased); _pendingReleased.Clear(); }
            }

            // デバッグ計装（一時的）: 基地登録が一度も走らない不具合の原因切り分けのため、
            // drain 結果があれば件数をログする。恒久ログにはしない想定。
            if (created != null || released != null)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessPending: drained created=" +
                    (created != null ? created.Count : 0) + " released=" + (released != null ? released.Count : 0));
            }

            // 解放を先に処理する（重要）: CSは建物IDを再利用するため、プレイヤーが解体→同じIDで
            // 再建築した場合、両イベントが同一バッチに入りうる。作成を先に処理すると、まだ残っている
            // 古い基地との重複判定で新しい基地の登録がスキップされ、その直後に古い基地が削除されて
            // 「建物はあるが論理基地が無い」状態（以後どのイベントでも復旧しない）になる。
            if (released != null) ProcessReleased(state, released);
            if (created != null) ProcessCreated(state, created);
        }

        private static void ProcessCreated(WarState state, List<ushort> ids)
        {
            // デバッグ計装（一時的）: 早期returnも含め、なぜ基地が登録されないかを追えるようにする。
            if (!WarfrontBasePrefab.IsAnyRegistered) // マッチ対象のプレハブが1つも無ければ何もできない
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: WarfrontBasePrefab.IsAnyRegistered=False; skipping " + ids.Count + " id(s)");
                return;
            }

            if (!Singleton<BuildingManager>.exists)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: BuildingManager does not exist; skipping " + ids.Count + " id(s)");
                return;
            }
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            foreach (ushort id in ids)
            {
                if (id >= buf.Length)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " out of building buffer range (len=" + buf.Length + ") -> skip");
                    continue;
                }

                Building b = buf[id];
                bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;
                string infoName = b.Info != null ? b.Info.name : null;

                // Task61: どの基地種別（Army/Navy/AirForce）のプレハブに一致するかをWarfrontBasePrefabへ
                // 委譲する（参照一致優先、ダメなら名前一致。ゲームがプレハブを再インスタンス化した場合の保険）。
                BaseType matchedType = default(BaseType);
                bool match = b.Info != null && WarfrontBasePrefab.TryMatch(b.Info, out matchedType);

                if (!flagsCreated)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=False info='" + (infoName ?? "null") +
                        "' match=" + match + " -> skip (not created)");
                    continue;
                }
                if (!match)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + (infoName ?? "null") +
                        "' match=False -> skip (not our prefab)");
                    continue;
                }

                // Task60: この時点でCS建物バッファ（Building構造体、既にbとして読み込み済み）から
                // 基地の向きを取得できる。新規登録・復元済み（existing、下のチェック）のどちらでも
                // 常に更新する——復元時（セーブロード後にEventBuildingCreatedが再発火するケース）は
                // これを existing チェックより前で行わないと、プロセス起動直後は BaseVisuals が
                // 向きを一切知らないままになってしまうため。
                _baseAngles[id] = b.m_angle;

                bool existing = FindBase(state, id) != null; // 冪等: セーブロード直後や重複イベント対策
                if (existing)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                        "' match=True(" + matchedType + ") existing=True -> skip (already registered)");
                    continue;
                }

                ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                    "' match=True(" + matchedType + ") existing=False -> REGISTERING");

                Vector3 pos = b.m_position;
                // Task61: 陸軍/海軍/航空、TryMatchが特定した種別でMilitaryBaseを作る（従来は常にArmy固定だった）。
                var mb = new MilitaryBase(id, matchedType, new WorldPos(pos.x, pos.y, pos.z));
                mb.OwnerFactionId = WarfrontSettings.BuildFactionId;
                // 新設基地は一定期間占領されない（Task24）：プレイヤーが両陣営の基地を配置し終える前に
                // 一方的に占領されてしまう不具合の対策。
                mb.CaptureGraceHours = MilitaryBase.NewBaseGraceHours;
                state.Bases.Add(mb);

                Faction f = state.FindFaction(WarfrontSettings.BuildFactionId);
                bool isHq = false;
                if (f != null)
                {
                    if (f.HomeBaseId == null)
                    {
                        f.HomeBaseId = id;
                        mb.IsHeadquarters = true;
                        isHq = true;
                    }
                }
                else
                {
                    ModConfig.LogError("BasePlacementWatcher: no Faction found for id=" + WarfrontSettings.BuildFactionId +
                        " while registering base id=" + id + "; base added without a valid owning faction record");
                }

                ModConfig.Log("BasePlacementWatcher: base registered id=" + id +
                    " faction=" + WarfrontSettings.BuildFactionId +
                    (isHq ? " (HQ)" : "") +
                    " grace=" + mb.CaptureGraceHours.ToString("0") + "h" +
                    " pos=(" + pos.x + "," + pos.y + "," + pos.z + ")");
            }
        }

        private static void ProcessReleased(WarState state, List<ushort> ids)
        {
            foreach (ushort id in ids)
            {
                MilitaryBase mb = FindBase(state, id);
                if (mb == null) continue;

                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(id); // Task60: 解体済みidの向きキャッシュを持ち越さない

                ModConfig.Log("BasePlacementWatcher: base removed id=" + id +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// ロジック基地の建物（BuildingManagerが管理するCS建物実体）が既に存在しない「幽霊基地」を
        /// 掃除する（Task24）。建物が過去に検知されないまま解体された場合や、古いセーブ由来のケースで、
        /// 論理基地だけがWarState.Basesに残り続け、生産/攻撃対象であり続けてしまう不具合の対策。
        /// simスレッド（MilitaryManager.OnSimTick、呼び出し元が既に_stateLock保持済み）から
        /// スロットル付きで呼ばれる想定。
        /// </summary>
        public static void ReconcileBases(WarState state)
        {
            if (state == null) return;
            if (!WarfrontBasePrefab.IsAnyRegistered) return; // マッチ対象のプレハブが1つも無ければ比較できない
            if (!Singleton<BuildingManager>.exists) return;

            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            // 削除対象を先に集めてから削除する（列挙中にstate.Basesを変更しないため）。
            List<MilitaryBase> ghosts = null;
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase mb = state.Bases[i];
                bool isGhost;
                if (mb.BaseId >= buf.Length)
                {
                    isGhost = true;
                }
                else
                {
                    Building b = buf[mb.BaseId];
                    bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;
                    // Task61: mb.Typeに対応する具体的なプレハブ(Army/Navy/AirForce)と一致するかを見る
                    // （以前は常にArmyのプレハブとしか比較しておらず、海軍/航空基地が常にゴースト扱いに
                    // なってしまう回帰があったため、必ずmb.Type別に比較する）。
                    BuildingInfo expected;
                    bool match = flagsCreated && b.Info != null &&
                        WarfrontBasePrefab.TryGetPrefab(mb.Type, out expected) && ReferenceEquals(b.Info, expected);
                    isGhost = !match;
                }
                if (isGhost)
                {
                    if (ghosts == null) ghosts = new List<MilitaryBase>();
                    ghosts.Add(mb);
                }
            }

            if (ghosts == null) return;

            foreach (MilitaryBase mb in ghosts)
            {
                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(mb.BaseId); // Task60: 幽霊基地の向きキャッシュを持ち越さない
                ModConfig.Log("BasePlacementWatcher: ReconcileBases: removed ghost base id=" + mb.BaseId +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// 基地を論理状態から取り除き、それが所属勢力のHQだった場合はHomeBaseIdをクリアして
        /// 残る所有基地があれば先頭を昇格する（解体経路・幽霊基地掃除経路の共通処理）。
        /// Eliminatedはここでは設定しない（勢力消滅はCoreのOccupationが決める戦闘結果のため）。
        /// </summary>
        private static void RemoveBaseAndReassignHq(WarState state, MilitaryBase mb)
        {
            state.Bases.Remove(mb);

            if (mb.OwnerFactionId == null) return;
            ReassignHqIfCleared(state, mb.OwnerFactionId.Value, mb.BaseId);
        }

        /// <summary>
        /// factionId の HomeBaseId が baseId を指している場合にそれをクリアし、その勢力がまだ
        /// 所有する他の基地があれば先頭を新HQへ昇格する。指していなければ何もしない（no-op）。
        /// 基地の削除（RemoveBaseAndReassignHq）・所属変更（MilitaryManager.TrySetBaseOwner）の
        /// 両方から共有される、HQ整合性維持の唯一の実装（Task25：ロジック重複を避けるため internal 公開）。
        /// 昇格そのものは Core.FactionStatus.PromoteFirstOwnedBaseToHq を呼ぶ（Task46：
        /// 「所有基地の先頭を新HQにする」ルールを FactionStatus.Refresh と重複させないため）。
        /// 呼び出し元が _stateLock を保持していること。
        /// </summary>
        internal static void ReassignHqIfCleared(WarState state, byte factionId, ushort baseId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null || !f.HomeBaseId.HasValue || f.HomeBaseId.Value != baseId) return;

            f.HomeBaseId = null;
            // この時点で baseId の基地は既に削除済み（RemoveBaseAndReassignHq経由）か、既に
            // factionId以外の所有へ切り替わっている（TrySetBaseOwner経由）ため、
            // PromoteFirstOwnedBaseToHq の所有権チェックが自然にbaseIdを除外する。
            FactionStatus.PromoteFirstOwnedBaseToHq(state, factionId);
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
