using System;
using System.Collections.Generic;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// メインスレッド専用スナップショット。ユニット1体分の「見た目」を決めるのに必要な最小限の情報のみ。
    /// CSの実体（Vehicle等）は一切含まない値型。
    /// </summary>
    public struct UnitVisualState
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public Vector3 Position;
        /// <summary>Core側 UnitType.AssetPrefabName（Workshopアセット等）。空文字なら既定フォールバックを使う。</summary>
        public string AssetPrefabName;
    }

    /// <summary>
    /// ユニットの見た目を「本物のCS車両」ではなく、素のUnity GameObjectとして自前描画する。
    /// 借用するのはメッシュ＋マテリアルのみ（VehicleInfo.m_mesh / m_material）で、AIやTransferManager
    /// 連携は一切引き継がない。これにより FireTruckAI 等サービス車両AI由来のクラッシュ
    /// （TransferManager.RemoveIncomingOffer の配列範囲外アクセス）を根本的に回避する。
    /// 借用元はプレハブ名で解決するため、将来 Workshop カスタムアセットへ差し替えても
    /// （そのアセットがどんなAIを積んでいても）安全に動作する。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （new GameObject / AddComponent / Destroy / transform書込みはUnityのメインスレッド制約）。
    /// sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと。
    /// </summary>
    public static class UnitVisuals
    {
        private class VisualEntry
        {
            public GameObject GameObject;
            public Vector3 LastPosition;
        }

        private const float MinMoveDeltaForRotation = 0.01f;

        private static readonly Dictionary<uint, VisualEntry> _visuals = new Dictionary<uint, VisualEntry>();

        // メッシュ解決不能などで生成に失敗した instance id。毎フレームの再試行とログ連発を防ぐため
        // 一度失敗したidはここに記録し、以後 Sync() でスキップする。スナップショットから消えたら
        // （死亡・削除等）id再利用に備えて解放する（下の stale 処理で _visuals と同じパスで実施）。
        private static readonly HashSet<uint> _failedInstances = new HashSet<uint>();

        // Sync() 実行毎に使い回すワーク領域（GC回避）。
        private static readonly HashSet<uint> _seenIds = new HashSet<uint>();
        private static readonly List<uint> _staleIds = new List<uint>();
        private static readonly List<uint> _staleFailedIds = new List<uint>();

        public static int Count { get { return _visuals.Count; } }

        /// <summary>
        /// スナップショットに基づき、生成/移動/破棄を宣言的に反映する（メインスレッド専用）。
        /// スナップショットに存在しないid（死亡・削除・未ロード含む）はここで破棄される。
        /// </summary>
        public static void Sync(List<UnitVisualState> snapshot)
        {
            if (snapshot == null) return;

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                UnitVisualState s = snapshot[i];
                _seenIds.Add(s.InstanceId);

                try
                {
                    if (_failedInstances.Contains(s.InstanceId))
                    {
                        continue; // 生成不能と判明済み。ログ連発・再試行を避けて次のユニットへ。
                    }

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(s.InstanceId, out entry) || entry.GameObject == null)
                    {
                        entry = CreateVisual(s);
                        if (entry == null)
                        {
                            // CreateVisual内でログ済み（1回のみ）。以後このidはSyncの先頭でスキップされる。
                            _failedInstances.Add(s.InstanceId);
                            continue;
                        }
                        _visuals[s.InstanceId] = entry;
                    }
                    else
                    {
                        MoveVisual(entry, s.Position);
                    }
                }
                catch (Exception e)
                {
                    ModConfig.LogError("UnitVisuals.Sync: instance " + s.InstanceId + " の更新に失敗: " + e);
                }
            }

            // スナップショットに無いidを列挙して破棄（ループ中の Dictionary 変更を避けるため2段階）。
            _staleIds.Clear();
            foreach (var kv in _visuals)
            {
                if (!_seenIds.Contains(kv.Key)) _staleIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleIds.Count; i++)
            {
                DestroyVisual(_staleIds[i]);
            }

            // スナップショットに無い失敗済みidも解放する（id再利用時に永久ブロックされないように）。
            _staleFailedIds.Clear();
            foreach (var failedId in _failedInstances)
            {
                if (!_seenIds.Contains(failedId)) _staleFailedIds.Add(failedId);
            }
            for (int i = 0; i < _staleFailedIds.Count; i++)
            {
                _failedInstances.Remove(_staleFailedIds[i]);
            }
        }

        /// <summary>追跡中の全ビジュアルを破棄する（レベルアンロード時、メインスレッド専用）。</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _visuals)
                {
                    if (kv.Value != null && kv.Value.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(kv.Value.GameObject);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
                _failedInstances.Clear();
            }
        }

        private static VisualEntry CreateVisual(UnitVisualState s)
        {
            try
            {
                Mesh mesh;
                Material material;
                if (!UnitMeshSource.TryResolve(s.AssetPrefabName, out mesh, out material))
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " のメッシュ解決に失敗、表現をスキップ");
                    return null;
                }

                var go = new GameObject("CSWarfrontUnit_" + s.InstanceId);
                MeshFilter filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                go.transform.position = s.Position;

                ModConfig.Log("UnitVisuals: created visual for instance " + s.InstanceId + " type=" + s.TypeKey);

                return new VisualEntry { GameObject = go, LastPosition = s.Position };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " error: " + e);
                return null;
            }
        }

        private static void MoveVisual(VisualEntry entry, Vector3 newPosition)
        {
            if (entry == null || entry.GameObject == null) return;
            Vector3 delta = newPosition - entry.LastPosition;
            entry.GameObject.transform.position = newPosition;
            if (delta.sqrMagnitude > MinMoveDeltaForRotation * MinMoveDeltaForRotation)
            {
                entry.GameObject.transform.rotation = Quaternion.LookRotation(delta);
            }
            entry.LastPosition = newPosition;
        }

        private static void DestroyVisual(uint instanceId)
        {
            try
            {
                VisualEntry entry;
                if (_visuals.TryGetValue(instanceId, out entry))
                {
                    if (entry != null && entry.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(entry.GameObject);
                    }
                    _visuals.Remove(instanceId);
                    ModConfig.Log("UnitVisuals: destroyed visual for instance " + instanceId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.DestroyVisual: instance " + instanceId + " error: " + e);
            }
        }
    }
}
