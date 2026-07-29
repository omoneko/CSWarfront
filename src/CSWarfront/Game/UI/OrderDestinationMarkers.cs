using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>どの種類の命令に紐づく目的地か（Task62）。見た目の色分けにのみ使う。</summary>
    public enum OrderMarkerKind { Advance, Rally }

    /// <summary>メインスレッド専用スナップショット1件＝選択中の1ユニットぶんの目的地
    /// （自由進撃/AI委任中ならOrderTargetPos、集結待機ならRallyPoint）。Hold中や目的地未設定の
    /// ユニットはそもそも生成しない（呼び出し側＝MilitaryManager.OnMainVisualUpdateが除外する）。</summary>
    public struct OrderDestinationState
    {
        public Vector3 Position;
        public OrderMarkerKind Kind;
    }

    /// <summary>
    /// Task62（Mount&amp;Blade風の指示フィードバック 1/2）: 選択中の部隊の進撃/集結の目的地に、
    /// 「〇＋短い棒」の即席マーカーを世界空間に表示する。UnitVisuals/BaseVisualsと同じ宣言的reconcile
    /// パターン（create/move/destroy）で、Sync(list)に渡したスナップショットに存在しないマーカーは
    /// 自動的に破棄される。
    ///
    /// マージ: 同じ命令種別で概ね10ユニット以内に集まる目的地は1個のマーカーへまとめる。実装は
    /// MergeRadiusを一辺とする単純な格子(floor(x/r), floor(z/r))へバケット分けするだけの近似
    /// （四捨五入境界をまたぐ僅かなケースで別マーカーになることはあるが、視覚的なヒント用途としては
    /// 十分）。この格子キー自体を辞書のキーとして使い回すことで、同じ目的地クラスタが毎フレーム
    /// 同じキーへ解決される＝UnitVisualsのInstanceIdと同じ役割を果たし、フレームをまたいで
    /// 「同じマーカーを動かす」reconcileが素直に書ける。
    ///
    /// 見た目: 地面のリング（薄い円柱で近似、UnitBoxSelectionのハイライトマーカーと同じ手法）＋
    /// 短い棒（縦長の細い円柱）。マテリアルはCS車両/建物のものを一切借用せず、Shader.Find("Standard")
    /// による自前マテリアルを命令種別ごとに1つだけ生成し使い回す（UnitBoxSelection._highlightMaterialと
    /// 同じ方針）。プリミティブ生成時に付くColliderは全て破棄する（Physics.Raycastによるユニット選択・
    /// 集結地点指定のクリック判定を邪魔しないため、Task31/Task48の既存raycast経路を汚染しない）。
    ///
    /// 表示条件: PanelChrome.IsGameReadyForUi()==false、またはPanelChrome.IsGameMenuOpen()==true の間は
    /// 全マーカーを非表示にする（破棄はしない＝再表示時に作り直すコストを避ける）。選択が0件（スナップ
    /// ショットが空）ならSyncが自然に全マーカーを破棄する（宣言的reconcileそのもの、特別扱い不要）。
    ///
    /// スレッド境界: 全メソッドがメインスレッド専用（Unity API呼び出しのため）。呼び出し元
    /// （MilitaryManager.OnMainVisualUpdate）が_stateLock内でOrderDestinationStateのリストを構築し、
    /// ロック解放後にSync()へ渡す（UnitVisuals.Syncと全く同じ規約）。
    /// </summary>
    public static class OrderDestinationMarkers
    {
        /// <summary>同じ命令種別でこの一辺（マップ単位）の格子に入る目的地を1個のマーカーへまとめる。</summary>
        private const float MergeRadius = 10f;

        private const float RingDiameter = 7f;
        private const float RingThinHeight = 0.25f;
        private const float RingYOffset = 0.25f;
        private const float PoleHeight = 9f;
        private const float PoleDiameter = 0.6f;

        private struct MarkerKey : IEquatable<MarkerKey>
        {
            public readonly OrderMarkerKind Kind;
            public readonly int CellX;
            public readonly int CellZ;

            public MarkerKey(OrderMarkerKind kind, int cellX, int cellZ)
            {
                Kind = kind; CellX = cellX; CellZ = cellZ;
            }

            public bool Equals(MarkerKey other)
            {
                return Kind == other.Kind && CellX == other.CellX && CellZ == other.CellZ;
            }

            public override bool Equals(object obj) { return obj is MarkerKey && Equals((MarkerKey)obj); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (int)Kind;
                    h = h * 486187739 + CellX;
                    h = h * 486187739 + CellZ;
                    return h;
                }
            }
        }

        private class MarkerEntry
        {
            public GameObject Root;
            public Vector3 LastPosition;
        }

        private static readonly Dictionary<MarkerKey, MarkerEntry> _markers = new Dictionary<MarkerKey, MarkerEntry>();

        // Sync() 実行毎に使い回すワーク領域（GC回避、UnitVisuals.Syncと同じ方針）。
        private static readonly Dictionary<MarkerKey, Vector3> _sums = new Dictionary<MarkerKey, Vector3>();
        private static readonly Dictionary<MarkerKey, int> _counts = new Dictionary<MarkerKey, int>();
        private static readonly List<MarkerKey> _staleKeys = new List<MarkerKey>();

        private static Material _advanceMaterial;
        private static Material _rallyMaterial;
        private static bool _hiddenLastSync; // 直前のSyncで「メニュー中/未準備につき非表示」だったか（復帰時の再表示制御に使用）

        /// <summary>スナップショットに基づき、生成/移動/破棄を宣言的に反映する（メインスレッド専用）。</summary>
        public static void Sync(List<OrderDestinationState> snapshot)
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi() || PanelChrome.IsGameMenuOpen())
                {
                    SetAllVisible(false);
                    _hiddenLastSync = true;
                    return;
                }
                if (_hiddenLastSync)
                {
                    SetAllVisible(true);
                    _hiddenLastSync = false;
                }

                if (snapshot == null) snapshot = _emptySnapshot;

                // 1st pass: 格子キーごとに座標を合算する（マージ）。
                _sums.Clear();
                _counts.Clear();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    OrderDestinationState s = snapshot[i];
                    MarkerKey key = new MarkerKey(
                        s.Kind,
                        FloorDiv(s.Position.x, MergeRadius),
                        FloorDiv(s.Position.z, MergeRadius));

                    Vector3 sum;
                    int count;
                    _sums.TryGetValue(key, out sum);
                    _counts.TryGetValue(key, out count);
                    _sums[key] = sum + s.Position;
                    _counts[key] = count + 1;
                }

                // 2nd pass: 平均座標でreconcile（create/move）。
                foreach (var kv in _sums)
                {
                    MarkerKey key = kv.Key;
                    Vector3 avg = kv.Value / _counts[key];

                    MarkerEntry entry;
                    if (!_markers.TryGetValue(key, out entry) || entry.Root == null)
                    {
                        entry = CreateMarker(key.Kind, avg);
                        if (entry == null) continue; // CreateMarker内でログ済み
                        _markers[key] = entry;
                    }
                    else if ((entry.LastPosition - avg).sqrMagnitude > 0.0001f)
                    {
                        entry.Root.transform.position = avg;
                        entry.LastPosition = avg;
                    }
                }

                // 3rd pass: スナップショットに無いキーを破棄。
                _staleKeys.Clear();
                foreach (var kv in _markers)
                {
                    if (!_sums.ContainsKey(kv.Key)) _staleKeys.Add(kv.Key);
                }
                for (int i = 0; i < _staleKeys.Count; i++)
                {
                    DestroyMarker(_staleKeys[i]);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.Sync error: " + e);
            }
        }

        private static readonly List<OrderDestinationState> _emptySnapshot = new List<OrderDestinationState>();

        /// <summary>追跡中の全マーカーを破棄する（レベルアンロード時、メインスレッド専用）。</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _markers)
                {
                    if (kv.Value != null && kv.Value.Root != null) UnityEngine.Object.Destroy(kv.Value.Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.DestroyAll error: " + e);
            }
            finally
            {
                _markers.Clear();
                _hiddenLastSync = false;
            }
        }

        private static void SetAllVisible(bool visible)
        {
            foreach (var kv in _markers)
            {
                if (kv.Value != null && kv.Value.Root != null) kv.Value.Root.SetActive(visible);
            }
        }

        private static int FloorDiv(float value, float cellSize)
        {
            return Mathf.FloorToInt(value / cellSize);
        }

        private static MarkerEntry CreateMarker(OrderMarkerKind kind, Vector3 position)
        {
            try
            {
                Material material = GetMaterial(kind);
                if (material == null) return null;

                GameObject root = new GameObject("CSWarfrontOrderMarker");
                root.transform.position = position;

                GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "Ring";
                StripCollider(ring);
                ring.transform.SetParent(root.transform, false);
                ring.transform.localPosition = new Vector3(0f, RingYOffset, 0f);
                ring.transform.localScale = new Vector3(RingDiameter, RingThinHeight, RingDiameter);
                MeshRenderer ringRenderer = ring.GetComponent<MeshRenderer>();
                if (ringRenderer != null) ringRenderer.sharedMaterial = material;

                GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pole.name = "Pole";
                StripCollider(pole);
                pole.transform.SetParent(root.transform, false);
                pole.transform.localPosition = new Vector3(0f, PoleHeight * 0.5f, 0f);
                pole.transform.localScale = new Vector3(PoleDiameter, PoleHeight * 0.5f, PoleDiameter);
                MeshRenderer poleRenderer = pole.GetComponent<MeshRenderer>();
                if (poleRenderer != null) poleRenderer.sharedMaterial = material;

                return new MarkerEntry { Root = root, LastPosition = position };
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.CreateMarker error: " + e);
                return null;
            }
        }

        private static void StripCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
        }

        private static void DestroyMarker(MarkerKey key)
        {
            MarkerEntry entry;
            if (_markers.TryGetValue(key, out entry))
            {
                if (entry != null && entry.Root != null) UnityEngine.Object.Destroy(entry.Root);
                _markers.Remove(key);
            }
        }

        private static Material GetMaterial(OrderMarkerKind kind)
        {
            if (kind == OrderMarkerKind.Rally)
            {
                if (_rallyMaterial == null) _rallyMaterial = CreateMaterial(new Color(0.25f, 0.9f, 0.95f, 1f)); // シアン寄り
                return _rallyMaterial;
            }

            if (_advanceMaterial == null) _advanceMaterial = CreateMaterial(new Color(0.95f, 0.25f, 0.2f, 1f)); // 赤寄り
            return _advanceMaterial;
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null) return null;
            Material m = new Material(shader);
            m.color = color;
            return m;
        }
    }
}
