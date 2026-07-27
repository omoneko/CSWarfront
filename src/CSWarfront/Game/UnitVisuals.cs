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
    /// 借用するのはメッシュのみ（VehicleInfo.m_mesh、なければ m_lodMesh）で、AIやTransferManager
    /// 連携は一切引き継がない。これにより FireTruckAI 等サービス車両AI由来のクラッシュ
    /// （TransferManager.RemoveIncomingOffer の配列範囲外アクセス）を根本的に回避する。
    /// 借用元はプレハブ名で解決するため、将来 Workshop カスタムアセットへ差し替えても
    /// （そのアセットがどんなAIを積んでいても）安全に動作する。
    /// マテリアルはCS車両のものを一切借用しない（<see cref="UnitMaterialFactory"/> 参照）。
    /// CS車両マテリアルは専用シェーダーがCS自身のレンダラー由来のper-instanceデータを要求するため、
    /// 素の MeshRenderer に割り当てると不可視/黒になる（実際に発生していた不可視バグの原因）。
    /// 代わりに自前の標準シェーダーマテリアルを勢力ごとに1つ生成・共有し、勢力を色で判別できるようにする。
    ///
    /// Task37: 上記の可視性マーカー立方体・勢力色は「割り当て済みプロップが無いユニット」専用の見た目に
    /// 縮小した。TypeKeyにプロップが割り当てられている場合（UnitMeshSource.TryResolveのfromAssignedProp）は
    /// マーカーを出さず、マテリアルもプロップ自身の見た目（<see cref="UnitMaterialFactory.TryGetPropMaterial"/>）
    /// を使う。クリック選択の当たり判定はマーカーのBoxColliderに代わってルートGameObject自身のBoxColliderで
    /// 提供する（CreateVisual/AttachPropCollider参照）。
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

        // 可視性マーカー（プリミティブ立方体）の大きさと、地面へ埋まらないための持ち上げ量。
        // Task37: 割り当て済みプロップがある場合はもう使わない（AttachVisibilityMarkerのfromAssignedProp分岐参照）。
        private const float MarkerSize = 8f;
        private const float MarkerHeight = 5f;

        // Task37: 割り当て済みプロップ（マーカー無し）のクリック当たり判定用BoxColliderの最小サイズ。
        // 極小プロップでもクリックできるようにするための下限。
        private const float MinPropColliderSize = 4f;

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
        /// raycastヒット先GameObject（子の可視性マーカーである場合を含む）から、それが属する論理ユニットの
        /// InstanceIdを解決する（Task31: Game/UI/UnitSelectionから使用）。本MODのユニット表現に
        /// 属さないヒット（バニラの建物・地形・道路等）はfalseを返す — 呼び出し側はその場合、選択状態を
        /// 変えずバニラのクリック挙動へそのまま委ねること。
        /// </summary>
        public static bool TryGetInstanceId(GameObject go, out uint instanceId)
        {
            instanceId = 0;
            if (go == null) return false;

            UnitVisualTag tag = go.GetComponentInParent<UnitVisualTag>();
            if (tag == null) return false;

            instanceId = tag.InstanceId;
            return true;
        }

        /// <summary>
        /// 指定idの可視表現の「現在の」ワールド座標を返す（メインスレッド専用）。
        /// Task32: UnitInfoPanelがユニットへ追従する際、スナップショット由来の座標ではなく
        /// 実際に描画されているGameObjectのtransform.positionを権威とするために使う
        /// （パネルは「描画されているものそのもの」を追いかけるべきで、スナップショットの
        /// コピー元とはタイミングがずれ得るため）。見た目が未生成/破棄済みならfalseを返す。
        /// </summary>
        public static bool TryGetPosition(uint instanceId, out Vector3 position)
        {
            position = default(Vector3);

            VisualEntry entry;
            if (!_visuals.TryGetValue(instanceId, out entry)) return false;
            if (entry == null || entry.GameObject == null) return false;

            position = entry.GameObject.transform.position;
            return true;
        }

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
                bool fromAssignedProp;
                string resolvedPropName;
                if (!UnitMeshSource.TryResolve(s.FactionId, s.TypeKey, s.AssetPrefabName, out mesh, out fromAssignedProp, out resolvedPropName))
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " のメッシュ解決に失敗、表現をスキップ");
                    return null;
                }

                // Task37: 割り当て済みプロップがある場合はプロップ自身の見た目（テクスチャ）を維持し、
                // 勢力色で塗らない。割り当てが無い場合のみ、従来通り勢力色マテリアルを使う。
                Material material;
                bool materialOk = fromAssignedProp
                    ? UnitMaterialFactory.TryGetPropMaterial(resolvedPropName, out material)
                    : UnitMaterialFactory.TryGetFactionMaterial(s.FactionId, out material);
                if (!materialOk)
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " のマテリアル生成に失敗、表現をスキップ");
                    return null;
                }

                var go = new GameObject("CSWarfrontUnit_" + s.InstanceId);

                // Task31: クリック選択(UnitSelection)がraycastヒット先から論理ユニットを逆引きできるよう、
                // ルートGameObjectに識別タグを付ける（GameObject→InstanceIdの別辞書は持たない）。
                UnitVisualTag tag = go.AddComponent<UnitVisualTag>();
                tag.InstanceId = s.InstanceId;

                // Task37: メッシュのピボットが底面にない場合、モデルが路面に半分埋まって見えることがある。
                // ルートのtransform.position自体はユニットの論理座標そのもの（垂直オフセットを一切加えない）
                // に保つため、メッシュ描画専用の子("Model")にだけこのオフセットを載せる。
                float pivotOffsetY = -mesh.bounds.min.y;

                GameObject model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.transform.localPosition = new Vector3(0f, pivotOffsetY, 0f);
                MeshFilter filter = model.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                go.transform.position = s.Position;

                if (fromAssignedProp)
                {
                    // 要件1: プロップ割り当てがある場合は可視性マーカー立方体を出さない。
                    // クリック選択の当たり判定は代わりにルートへ直接付ける（マーカーが無いため）。
                    AttachPropCollider(go, mesh, pivotOffsetY);
                }
                else
                {
                    // 可視性の保険＆切り分け: CS由来の借用メッシュが環境によって描画されない可能性があるため、
                    // 確実に描画されるプリミティブ（MissileDisasterのフォールバック球と同じ手法）を子に付ける。
                    // これが見えて借用メッシュが見えない場合、原因はメッシュ側だと確定できる。
                    AttachVisibilityMarker(go, material);
                }

                ModConfig.Log("UnitVisuals: created visual for instance " + s.InstanceId + " type=" + s.TypeKey);

                return new VisualEntry { GameObject = go, LastPosition = s.Position };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " error: " + e);
                return null;
            }
        }

        /// <summary>
        /// Task37: 割り当て済みプロップ（マーカー無し）用に、ルートGameObjectへ直接BoxColliderを付ける。
        /// マーカー立方体が無くなったため、クリック選択の当たり判定はこれが唯一の手段になる。
        /// メッシュのbounds（"Model"子への pivotOffsetY 適用後のルート相対座標に変換したもの）を元に
        /// サイズ・中心を決め、極小プロップでもクリックできるよう各軸最小 <see cref="MinPropColliderSize"/>
        /// を保証する。isTriggerはfalseのまま、GameObjectのlayerは変更しない（AttachVisibilityMarkerと同じ理由）。
        /// </summary>
        private static void AttachPropCollider(GameObject root, Mesh mesh, float pivotOffsetY)
        {
            try
            {
                BoxCollider col = root.AddComponent<BoxCollider>();
                col.isTrigger = false;

                Vector3 size = mesh.bounds.size;
                size.x = Mathf.Max(size.x, MinPropColliderSize);
                size.y = Mathf.Max(size.y, MinPropColliderSize);
                size.z = Mathf.Max(size.z, MinPropColliderSize);
                col.size = size;

                Vector3 center = mesh.bounds.center;
                center.y += pivotOffsetY; // "Model"子と同じオフセットをルート相対座標に反映
                col.center = center;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachPropCollider error: " + e);
            }
        }

        /// <summary>
        /// ユニットGameObjectに、確実に描画されるプリミティブ立方体を子として付ける（メインスレッド専用）。
        /// 借用メッシュの描画可否に依存せずユニット位置を視認できるようにするための保険。
        /// Task37: 割り当て済みプロップがある場合（fromAssignedProp）はもう呼ばれない
        /// （AttachPropColliderで当たり判定のみ用意する）。既定/未割り当てユニットの見た目保険として残す。
        /// Task31: このマーカーが生成時に持つBoxColliderは破棄せず、そのままクリック選択の当たり判定
        /// として流用する（isTriggerはfalseのまま＝Physics.Raycastで検出可能）。GameObjectのlayerは
        /// 変更しない（layerを変えるとCS側カメラのカリング/レイヤーマスクに影響し、既に解決済みの
        /// 不可視バグを再発させるリスクがあるため）。
        /// </summary>
        private static void AttachVisibilityMarker(GameObject parent, Material material)
        {
            try
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                BoxCollider col = marker.GetComponent<BoxCollider>();
                if (col != null) col.isTrigger = false;

                marker.transform.SetParent(parent.transform, false);
                marker.transform.localPosition = new Vector3(0f, MarkerHeight, 0f);
                marker.transform.localScale = new Vector3(MarkerSize, MarkerSize, MarkerSize);

                MeshRenderer markerRenderer = marker.GetComponent<MeshRenderer>();
                if (markerRenderer != null && material != null) markerRenderer.sharedMaterial = material;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachVisibilityMarker error: " + e);
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
