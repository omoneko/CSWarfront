using System;
using System.Collections.Generic;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// メインスレッド専用スナップショット。軍事拠点1個分の「見た目」を決めるのに必要な最小限の情報のみ。
    /// CSの実体（Building等）は一切含まない値型（UnitVisuals.UnitVisualStateと同じ方針）。
    /// </summary>
    public struct BaseVisualState
    {
        public ushort BaseId;
        public byte FactionId;
        public Vector3 Position;

        /// <summary>建物の向き（ラジアン、CS Building.m_angle そのもの）。
        /// BasePlacementWatcher.TryGetAngle が解決できなかった場合は呼び出し側が0（既定の向き）を渡す。</summary>
        public float Angle;
    }

    /// <summary>
    /// Task60: 軍事拠点（MilitaryBase）に勢力ごとの見た目を持たせるためのオーバーレイ描画。
    ///
    /// 設計判断（すべてのバニラ機能を壊さないための唯一の安全な経路）:
    /// 全勢力の拠点は WarfrontBasePrefab が登録する「たった1つの共有BuildingInfo」から生成される
    /// 本物のCS建物（配置/AI/情報パネル/占領は全てバニラのBuildingManager/BuildingAIが担う）。
    /// BuildingInfo.m_mesh はプレハブ（アセット）単位のフィールドであり、CS標準APIには「建物インスタンス
    /// ごとに描画メッシュを差し替える」手段が無い。もし拠点ごとにm_meshを書き換えられたとしても、
    /// それは共有BuildingInfoそのものを書き換えることになり、既に配置済みの他勢力の拠点も含めて
    /// 全拠点の見た目が同時に変わってしまう（要件と正反対の結果）。バニラ側のメッシュを個別に隠す
    /// 公式APIも存在しない（Building.m_flagsをいじって「見えなくする」ことは可能かもしれないが、
    /// 占領/生産/情報パネル等バニラ・Core双方の判定に使われるフラグへ干渉するリスクがあり、
    /// 「Core/CS実体の判定ロジックには触れない」という制約に反する）。
    /// そのためこのクラスは UnitVisuals と全く同じ手法（CS実体には一切触れない自前GameObjectを
    /// 論理座標へ重ねて描画する）を採用する。<b>バニラの建物メッシュは隠さない</b>——割り当て済みの
    /// 拠点では、既定モデル（Building_MilitaryBase.obj、WarfrontBasePrefab.TrySwapVisualMesh参照）の
    /// 上に、割り当てられたアセットのオーバーレイが重なって見える（意図的なトレードオフ。要件の
    /// 「隠すのが危険なら重ねて表示し、その旨を明記する」に従う）。
    ///
    /// 割り当てが無い勢力の拠点にはオーバーレイを一切生成しない（要件: 「割り当てのある拠点だけ」）。
    /// UnitAssetBindings の特別キー <see cref="UnitAssetBindings.BaseTypeKey"/>（"MilitaryBase"、
    /// ユニット種別ではない）で (勢力, "MilitaryBase") → アセットの割り当てを解決する
    /// （UnitMeshSource.TryResolveは使わない——あちらはユニット向けのTierフォールバック/既定モデル
    /// フォールバック/車両プレハブ借用フォールバックまで含む重い解決チェーンだが、拠点には
    /// Tierも既定built-inモデル解決も無関係なため、UnitAssetBindings.TryGetを直接呼ぶ薄い実装で足りる）。
    ///
    /// 借用するのはメッシュ(AssetCatalog.TryGetMesh)とテクスチャ(UnitMaterialFactory.TryGetAssetMaterial
    /// 経由でmainTextureのみ)だけで、CS側のMaterial/AIは一切借用しない（UnitVisuals/UnitMeshSourceと
    /// 同じ安全性保証。AIをインスタンス化しないため副作用・クラッシュが原理的に起こらない）。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （new GameObject / AddComponent / Destroy / transform書込みはUnityのメインスレッド制約）。
    /// simスレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと。スナップショット
    /// （<see cref="BaseVisualState"/>）はMilitaryManager.OnMainVisualUpdateが_stateLock保持中に
    /// WarState.Bases（Core、位置は生成時にBasePlacementWatcherが記録した不変値）と
    /// BasePlacementWatcher._baseAngles（Game、simスレッドがCS建物バッファから読み取り済みの値）
    /// から組み立てる。CS実体（BuildingManager）への直接アクセスはこのクラス自身には一切無い。
    /// </summary>
    public static class BaseVisuals
    {
        private class VisualEntry
        {
            public GameObject GameObject;
        }

        private static readonly Dictionary<ushort, VisualEntry> _visuals = new Dictionary<ushort, VisualEntry>();

        // メッシュ/マテリアル解決不能などで生成に失敗したbaseId。毎フレームの再試行とログ連発を防ぐため
        // 一度失敗したidはここに記録し、以後 Sync() でスキップする（UnitVisuals._failedInstancesと同じ方針）。
        private static readonly HashSet<ushort> _failedInstances = new HashSet<ushort>();

        // Sync() 実行毎に使い回すワーク領域（GC回避）。
        private static readonly HashSet<ushort> _seenIds = new HashSet<ushort>();
        private static readonly List<ushort> _staleIds = new List<ushort>();
        private static readonly List<ushort> _staleFailedIds = new List<ushort>();

        public static int Count { get { return _visuals.Count; } }

        /// <summary>
        /// スナップショットに基づき、勢力別アセットが割り当てられた拠点のオーバーレイのみを
        /// 生成/移動/破棄する（メインスレッド専用）。割り当てが無い拠点はオーバーレイを持たない
        /// （既にオーバーレイを持っていれば、割り当てが外れたこのタイミングで破棄し既定の見た目へ戻す）。
        /// スナップショットに存在しないid（基地の解体・幽霊基地掃除等）はここで破棄される。
        /// </summary>
        public static void Sync(List<BaseVisualState> snapshot)
        {
            if (snapshot == null) return;

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                BaseVisualState s = snapshot[i];
                _seenIds.Add(s.BaseId);

                try
                {
                    AssetKind kind;
                    string name;
                    bool hasAssignment = UnitAssetBindings.TryGet(s.FactionId, UnitAssetBindings.BaseTypeKey, out kind, out name);

                    if (!hasAssignment)
                    {
                        // 割り当てが無い（既定モデルのまま）。以前オーバーレイを持っていればここで破棄し、
                        // バニラ/既定built-inの見た目へ戻す（勢力の所属変更・割り当て解除の両方で通る経路）。
                        if (_visuals.ContainsKey(s.BaseId)) DestroyVisual(s.BaseId);
                        continue;
                    }

                    if (_failedInstances.Contains(s.BaseId))
                    {
                        continue; // 生成不能と判明済み。ログ連発・再試行を避けて次の拠点へ。
                    }

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(s.BaseId, out entry) || entry.GameObject == null)
                    {
                        entry = CreateVisual(s, kind, name);
                        if (entry == null)
                        {
                            _failedInstances.Add(s.BaseId);
                            continue;
                        }
                        _visuals[s.BaseId] = entry;
                    }
                    else
                    {
                        // 拠点（バニラ建物）は配置後に移動しないが、念のため毎回位置だけ同期する
                        // （回転は生成時の値のまま固定＝建物が向きを変えることは無い）。
                        entry.GameObject.transform.position = s.Position;
                    }
                }
                catch (Exception e)
                {
                    ModConfig.LogError("BaseVisuals.Sync: base " + s.BaseId + " の更新に失敗: " + e);
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

            // スナップショットに無い失敗済みidも解放する（id再利用に備えて永久ブロックされないように）。
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

        /// <summary>追跡中の全オーバーレイを破棄する（レベルアンロード時、および割り当て変更の反映時、
        /// メインスレッド専用）。AssetAssignPanel/OptionsModelAssignPageが割り当て変更のたびに呼ぶ
        /// （UnitVisuals.DestroyAllと同じ呼び出し規約: 破棄後は次回SyncでCreateVisualが新しい
        /// 割り当てを解決し直す）。</summary>
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
                ModConfig.LogError("BaseVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
                _failedInstances.Clear();
            }
        }

        private static VisualEntry CreateVisual(BaseVisualState s, AssetKind kind, string name)
        {
            try
            {
                Mesh mesh;
                if (!AssetCatalog.TryGetMesh(kind, name, out mesh) || mesh == null)
                {
                    ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " のメッシュ解決に失敗（" +
                        kind + ":" + name + "）、オーバーレイをスキップします（既定の見た目のまま）");
                    return null;
                }

                // Task37のUnitVisuals.CreateVisualと同じ理由でCS側のMaterialは一切借用しない。
                // アセット自身の見た目（テクスチャ）を維持し、勢力色では塗らない。
                Material material;
                if (!UnitMaterialFactory.TryGetAssetMaterial(kind, name, out material) || material == null)
                {
                    ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " のマテリアル生成に失敗、オーバーレイをスキップします");
                    return null;
                }

                var go = new GameObject("CSWarfrontBaseOverlay_" + s.BaseId);

                // メッシュのピボットが底面にない場合、モデルが地面に半分埋まって見えることがある。
                // ルートのtransform.position自体は拠点の論理座標そのものに保つため、メッシュ描画専用の
                // 子("Model")にだけこのオフセットを載せる（UnitVisuals.CreateVisualと同じ手法）。
                float pivotOffsetY = -mesh.bounds.min.y;

                GameObject model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.transform.localPosition = new Vector3(0f, pivotOffsetY, 0f);
                MeshFilter filter = model.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                go.transform.position = s.Position;
                // CS本体の建物回転規約（BuildingAI.RenderInstance/RefreshInstance相当。Assembly-CSharp.dllを
                // ildasmで逆アセンブルし Quaternion.AngleAxis(m_angle * 57.29578f, Vector3.down) であることを
                // 確認済み、57.29578 = Mathf.Rad2Deg）に合わせ、Building.m_angle（ラジアン）と同じ変換で
                // 向きを揃える。こうすることでオーバーレイは既定モデル（バニラ建物）と同じ向きで重なる。
                go.transform.rotation = Quaternion.AngleAxis(s.Angle * Mathf.Rad2Deg, Vector3.down);

                // オーバーレイは純粋に見た目専用。当たり判定（クリック選択）は敢えて付けない——
                // 拠点の選択・情報パネル(BaseInfoPanel)はバニラ建物側のコライダー/CityServiceWorldInfoPanel
                // 経由の選択で動作しており、そちらを一切変更しない・奪わないことが要件（占領・パネルが
                // 従来通り動く）のため。

                ModConfig.Log("BaseVisuals: created overlay for base " + s.BaseId + " faction=" + s.FactionId +
                    " asset=" + kind + ":" + name);

                return new VisualEntry { GameObject = go };
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " error: " + e);
                return null;
            }
        }

        private static void DestroyVisual(ushort baseId)
        {
            try
            {
                VisualEntry entry;
                if (_visuals.TryGetValue(baseId, out entry))
                {
                    if (entry != null && entry.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(entry.GameObject);
                    }
                    _visuals.Remove(baseId);
                    ModConfig.Log("BaseVisuals: destroyed overlay for base " + baseId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseVisuals.DestroyVisual: base " + baseId + " error: " + e);
            }
        }
    }
}
