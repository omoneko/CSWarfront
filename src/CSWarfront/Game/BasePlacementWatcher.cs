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

            if (created != null) ProcessCreated(state, created);
            if (released != null) ProcessReleased(state, released);
        }

        private static void ProcessCreated(WarState state, List<ushort> ids)
        {
            if (!WarfrontBasePrefab.IsRegistered) return; // マッチ対象のプレハブが無ければ何もできない

            if (!Singleton<BuildingManager>.exists) return;
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            foreach (ushort id in ids)
            {
                if (id >= buf.Length) continue;
                Building b = buf[id];
                if ((b.m_flags & Building.Flags.Created) == 0) continue;
                if (b.Info == null) continue;
                if (!ReferenceEquals(b.Info, WarfrontBasePrefab.Prefab)) continue; // 自MODの基地建物以外は無視

                if (FindBase(state, id) != null) continue; // 冪等: セーブロード直後や重複イベント対策

                Vector3 pos = b.m_position;
                var mb = new MilitaryBase(id, BaseType.Army, new WorldPos(pos.x, pos.y, pos.z));
                mb.OwnerFactionId = WarfrontSettings.BuildFactionId;
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
                    " pos=(" + pos.x + "," + pos.y + "," + pos.z + ")");
            }
        }

        private static void ProcessReleased(WarState state, List<ushort> ids)
        {
            foreach (ushort id in ids)
            {
                MilitaryBase mb = FindBase(state, id);
                if (mb == null) continue;

                state.Bases.Remove(mb);

                // 破壊されたのが所属勢力のHQなら、HomeBaseIdをクリアし、残る所有基地があれば先頭を昇格する。
                // Eliminatedはここでは設定しない（勢力消滅はCoreのOccupationが決める戦闘結果のため）。
                if (mb.OwnerFactionId != null)
                {
                    Faction f = state.FindFaction(mb.OwnerFactionId.Value);
                    if (f != null && f.HomeBaseId.HasValue && f.HomeBaseId.Value == id)
                    {
                        f.HomeBaseId = null;
                        foreach (var other in state.Bases)
                        {
                            if (other.OwnerFactionId == mb.OwnerFactionId.Value)
                            {
                                other.IsHeadquarters = true;
                                f.HomeBaseId = other.BaseId;
                                break;
                            }
                        }
                    }
                }

                ModConfig.Log("BasePlacementWatcher: base removed id=" + id +
                    " (was HQ=" + mb.IsHeadquarters + ", faction=" + mb.OwnerFactionId + ")");
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
