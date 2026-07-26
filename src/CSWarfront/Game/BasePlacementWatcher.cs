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
            if (!WarfrontBasePrefab.IsRegistered) // マッチ対象のプレハブが無ければ何もできない
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: WarfrontBasePrefab.IsRegistered=False; skipping " + ids.Count + " id(s)");
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
                string expected = WarfrontBasePrefab.PrefabName;

                // 参照一致 OR 名前一致: ゲームがプレハブを再インスタンス化した場合、参照比較だけでは
                // 一致しなくなる可能性があるため両方を見る。
                bool refMatch = b.Info != null && ReferenceEquals(b.Info, WarfrontBasePrefab.Prefab);
                bool nameMatch = b.Info != null && b.Info.name == WarfrontBasePrefab.PrefabName;
                bool match = refMatch || nameMatch;
                string matchedBy = refMatch ? "reference" : (nameMatch ? "name" : "none");

                if (!flagsCreated)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=False info='" + (infoName ?? "null") +
                        "' expected='" + expected + "' match=" + match + " -> skip (not created)");
                    continue;
                }
                if (!match)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + (infoName ?? "null") +
                        "' expected='" + expected + "' match=False -> skip (not our prefab)");
                    continue;
                }

                bool existing = FindBase(state, id) != null; // 冪等: セーブロード直後や重複イベント対策
                if (existing)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                        "' expected='" + expected + "' match=True(" + matchedBy + ") existing=True -> skip (already registered)");
                    continue;
                }

                ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                    "' expected='" + expected + "' match=True(" + matchedBy + ") existing=False -> REGISTERING");

                Vector3 pos = b.m_position;
                var mb = new MilitaryBase(id, BaseType.Army, new WorldPos(pos.x, pos.y, pos.z));
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
            if (!WarfrontBasePrefab.IsRegistered) return; // マッチ対象のプレハブが無ければ比較できない
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
                    bool match = flagsCreated && b.Info != null && ReferenceEquals(b.Info, WarfrontBasePrefab.Prefab);
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
        /// 呼び出し元が _stateLock を保持していること。
        /// </summary>
        internal static void ReassignHqIfCleared(WarState state, byte factionId, ushort baseId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null || !f.HomeBaseId.HasValue || f.HomeBaseId.Value != baseId) return;

            f.HomeBaseId = null;
            foreach (var other in state.Bases)
            {
                if (other.BaseId != baseId && other.OwnerFactionId == factionId)
                {
                    other.IsHeadquarters = true;
                    f.HomeBaseId = other.BaseId;
                    break;
                }
            }
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
