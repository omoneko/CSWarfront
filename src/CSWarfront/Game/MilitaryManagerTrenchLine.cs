using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task106: 塹壕ライン敷設のsim側処理（MilitaryManager partial、500行制限のため分離）。
    ///
    /// UI（TrenchLineTargeting、メインスレッド）が2点を確定するとRequestTrenchLineがキューへ積み、
    /// OnSimTickのProcessPendingTrenchLinesがsimスレッドで消化する:
    ///   - Options指定のTrench建物アセットを、起点→終点の直線上にTrenchSegmentSpacing間隔で
    ///     BuildingManager.CreateBuildingにより直接生成する（バニラ配置ツールを使わないため
    ///     「道路に接して配置」要件を完全にバイパスする）。向きはライン方向へ自動回転。
    ///   - 1セグメントごとに建物の建設費を通常どおり市の資金から支払う（足りなくなったら中断）。
    ///   - 生成された建物はEventBuildingCreated経由でBasePlacementWatcherが通常どおり
    ///     論理塹壕（無所属）として登録する（専用の登録処理は不要）。
    ///
    /// あわせてSuppressFortificationProblemsが、登録済みの築城系建物（塹壕・掩蔽壕・砲兵陣地・
    /// 補給拠点・貨物駅）の問題アイコン（道路未接続・電気・水道等）を毎tick消す——野戦築城は
    /// 都市インフラを必要としない、という扱いにする（道路未接続警告への対策）。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>塹壕セグメントの配置間隔（m）。モデル16×32の長辺と一致させ、継ぎ目が揃うようにする。</summary>
        public const float TrenchSegmentSpacing = 32f;

        /// <summary>1本のラインで配置するセグメント数の上限（暴発防止）。</summary>
        public const int MaxTrenchSegmentsPerLine = 64;

        private struct TrenchLineRequest { public Vector3 Start, End; }

        // メインスレッド（UI）→simスレッドの受け渡しキュー。_stateLockで保護する。
        private static readonly List<TrenchLineRequest> _pendingTrenchLines = new List<TrenchLineRequest>();

        /// <summary>メインスレッド（TrenchLineTargeting）から呼ばれる。</summary>
        public static void RequestTrenchLine(Vector3 start, Vector3 end)
        {
            lock (_stateLock)
            {
                _pendingTrenchLines.Add(new TrenchLineRequest { Start = start, End = end });
            }
        }

        /// <summary>simスレッド（OnSimTick、_stateLock内）: 保留中の塹壕ラインを建物として生成する。</summary>
        private static void ProcessPendingTrenchLines()
        {
            if (_pendingTrenchLines.Count == 0) return;
            List<TrenchLineRequest> requests = new List<TrenchLineRequest>(_pendingTrenchLines);
            _pendingTrenchLines.Clear();

            string assetName;
            if (!BaseBuildingDesignation.TryGet(BaseType.Trench, out assetName))
            {
                ModConfig.Log("TrenchLine: no trench building designated in Options; ignored");
                return;
            }
            BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(assetName);
            if (info == null)
            {
                ModConfig.Log("TrenchLine: designated asset '" + assetName + "' is not loaded; ignored");
                return;
            }

            foreach (TrenchLineRequest req in requests)
                PlaceTrenchLine(info, req.Start, req.End);
        }

        private static void PlaceTrenchLine(BuildingInfo info, Vector3 start, Vector3 end)
        {
            Vector3 delta = end - start;
            delta.y = 0f;
            float length = delta.magnitude;
            Vector3 dir = length > 0.01f ? delta / length : Vector3.forward;

            // 起点も終点も含めて等間隔に置く（1点クリック相当なら1個だけ）。
            int segments = Mathf.Min(MaxTrenchSegmentsPerLine, Mathf.FloorToInt(length / TrenchSegmentSpacing) + 1);
            // 塹壕モデルの溝はローカルX方向に走っている（実機確認: ローカルZを向けると
            // ライン方向に対して90°直交して見えた）。そのため+90°回してモデルの長辺＝溝を
            // ライン方向と平行にする。
            float angle = Mathf.Atan2(dir.x, dir.z) + Mathf.PI * 0.5f;

            BuildingManager bm = Singleton<BuildingManager>.instance;
            SimulationManager sm = Singleton<SimulationManager>.instance;
            EconomyManager em = Singleton<EconomyManager>.instance;
            TerrainManager tm = Singleton<TerrainManager>.instance;

            int placed = 0;
            for (int i = 0; i < segments; i++)
            {
                Vector3 pos = start + dir * (i * TrenchSegmentSpacing);
                pos.y = tm.SampleDetailHeight(pos);

                // 建設費は通常どおり市の資金から支払う（払えなくなったら中断）。
                int cost = info.m_buildingAI.GetConstructionCost();
                if (cost > 0)
                {
                    if (em.PeekResource(EconomyManager.Resource.Construction, cost) < cost)
                    {
                        ModConfig.Log("TrenchLine: out of money after " + placed + " segment(s)");
                        break;
                    }
                    em.FetchResource(EconomyManager.Resource.Construction, cost, info.m_class);
                }

                ushort id;
                if (bm.CreateBuilding(out id, ref sm.m_randomizer, info, pos, angle, 0, sm.m_currentBuildIndex))
                {
                    sm.m_currentBuildIndex++;
                    placed++;
                    // 登録自体はEventBuildingCreated→BasePlacementWatcher（無所属の塹壕として登録）。
                }
            }
            ModConfig.Log("TrenchLine: placed " + placed + " trench segment(s) from (" +
                start.x.ToString("0") + "," + start.z.ToString("0") + ") to (" +
                end.x.ToString("0") + "," + end.z.ToString("0") + ")");
        }

        /// <summary>simスレッド（OnSimTick、_stateLock内）: 築城系建物の問題アイコン（道路未接続・
        /// 電気・水道等）を消す。野戦築城は都市インフラ不要という扱い（Task106）。
        /// 対象は登録済みの論理施設（State.Bases）のうちFortificationRules.IsFortificationのもの。</summary>
        private static void SuppressFortificationProblems()
        {
            if (State == null) return;
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 0; i < State.Bases.Count; i++)
            {
                MilitaryBase b = State.Bases[i];
                if (!FortificationRules.IsFortification(b.Type)) continue;
                if (b.BaseId >= buffer.Length) continue;
                if ((buffer[b.BaseId].m_flags & Building.Flags.Created) == 0) continue;
                // 注: このCS1バージョンではProblem型はNotification.Problem1（後期パッチで改名済み）。
                buffer[b.BaseId].m_problems = Notification.Problem1.None;
            }
        }
    }
}
