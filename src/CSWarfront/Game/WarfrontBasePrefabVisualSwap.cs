using System;
using CSWarfront.Game.Models;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task71: WarfrontBasePrefab.RegisterOne から呼ばれる、既定モデルへの見た目差し替えの実装本体
    /// （WarfrontBasePrefab.cs の500行制限のため分離）。
    ///
    /// 【風力タービンが残っていた根本原因】（ゲーム本体 Assembly-CSharp.dll を ilspycmd で逆コンパイル
    /// して確認済み、詳細は .superpowers/sdd/task-71-report.md）:
    /// 旧実装（Task57時点のTrySwapVisualMesh）は BuildingInfo.m_mesh/m_material/m_lodMesh/m_lodMaterial
    /// という「フィールド」だけを書き換えていた。しかしその直後に呼ばれる
    /// PrefabCollection&lt;BuildingInfo&gt;.InitializePrefabs → BuildingInfo.InitializePrefab() は、
    /// これらのフィールドを clone の GameObject 上の実コンポーネント
    /// （GetComponent&lt;MeshFilter&gt;().sharedMesh / GetComponent&lt;Renderer&gt;().sharedMaterial、
    /// LODオブジェクトがあればそちらのMeshFilter/Renderer）から無条件に再取得して「上書き」する。
    /// Instantiate で複製した直後の clone の実コンポーネントは複製元（風力タービン）のメッシュ/
    /// マテリアルのままなので、フィールドへ書いた新モデルは InitializePrefab() の数行後に即座に
    /// 元へ戻されてしまい、「表示は常にタービン」という結果になっていた。
    /// 対策: フィールドではなく実コンポーネント（MeshFilter.sharedMesh / Renderer.sharedMaterial）を
    /// 書き換える。InitializePrefab() がそこから正しく新メッシュ/マテリアルを拾うため、以後の
    /// m_mesh/m_material/m_lodMesh/m_lodMaterial は自然に新モデルへ揃う。
    /// </summary>
    internal static class WarfrontBasePrefabVisualSwap
    {
        /// <summary>
        /// PrefabCollection&lt;BuildingInfo&gt;.InitializePrefabs より前に呼ぶこと。
        /// clone の実コンポーネント（ルート+LODオブジェクトがあればそちら）のメッシュ/マテリアルを
        /// 書き換え、あわせて m_generatedInfo を clone 専用の新規インスタンスへ差し替えて
        /// CalculateGeneratedInfo()（ゲーム本体のAsset Editorが使うのと同じAPI、GetComponentsInChildren
        /// 経由でメッシュから寸法/footprint/衝突域を再計算する）を呼ぶ。
        /// これが必要な理由: Instantiate は GameObject 階層は複製するが、フィールドが参照する
        /// ScriptableObject（m_generatedInfo）はコピーせず複製元と同じインスタンスを指したままになる。
        /// 複製元（風力タービン）の m_generatedInfo.m_buildingInfo は既に複製元自身を指しているため、
        /// メッシュだけ差し替えると InitializePrefab() の
        /// 「m_generatedInfo.m_buildingInfo.m_mesh != m_mesh」チェックに引っかかり
        /// PrefabException("Same generated info but different mesh") で登録全体が失敗する。
        /// clone専用の新規インスタンスに差し替えれば m_buildingInfo は null から始まるため
        /// （BuildingInfoGen.m_buildingInfo は [NonSerialized] であり、ScriptableObject.CreateInstance
        /// は初期値のまま = null）、この例外を回避しつつ実際の footprint も新モデルに合った値になる。
        /// 失敗時は clone のコンポーネント/フィールドを可能な限り元のまま return false する
        /// （＝クローン元＝風力タービンの見た目が残る、EnsureRegistered本体のプレハブ登録自体は
        /// 継続できる、Task57時点からの既存フォールバック方針を踏襲）。
        /// </summary>
        public static bool TryApplyMesh(BuildingInfo clone, string modelName, Color fallbackColor)
        {
            try
            {
                Mesh mesh;
                if (!WarfrontModelProvider.TryGetMesh(modelName, out mesh) || mesh == null)
                {
                    ModConfig.LogError("WarfrontBasePrefabVisualSwap.TryApplyMesh: built-in model '" + modelName +
                        "' の読み込みに失敗。既定（複製元借用）の見た目を維持します");
                    return false;
                }

                // Task71: ハードコードした固定色ではなく、実際のBlenderパレット（.mtl）の平均色を
                // 使う（見つからなければ呼び出し元が渡した既定色にフォールバック）。要件3の判断:
                // 複数マテリアルモデルの完全な多色描画（パレットテクスチャ+UV焼き込み）は
                // BuildingInfo.m_material が単一フィールドである制約と、本タスクの主眼である
                // タービン残存バグの修正の複雑さを踏まえ、今回は見送った（詳細はレポート参照）。
                Color modelColor;
                if (!WarfrontModelProvider.TryGetAverageColor(modelName, out modelColor)) modelColor = fallbackColor;

                Material material;
                if (!UnitMaterialFactory.TryGetSolidColorMaterial(modelColor, out material) || material == null)
                {
                    ModConfig.LogError("WarfrontBasePrefabVisualSwap.TryApplyMesh: 建物用マテリアル生成に失敗。既定の見た目を維持します");
                    return false;
                }

                MeshFilter rootFilter = clone.GetComponent<MeshFilter>();
                Renderer rootRenderer = clone.GetComponent<Renderer>();
                if (rootFilter == null || rootRenderer == null)
                {
                    ModConfig.LogError("WarfrontBasePrefabVisualSwap.TryApplyMesh: clone に MeshFilter/Renderer が無い。既定の見た目を維持します");
                    return false;
                }
                rootFilter.sharedMesh = mesh;
                rootRenderer.sharedMaterial = material;

                // LODオブジェクトがあれば同じメッシュ/マテリアルへ揃える（低ポリな既定モデルは
                // 近距離/遠距離で別メッシュを持つ必要が無いため、専用LODメッシュは作らない）。
                if (clone.m_lodObject != null)
                {
                    MeshFilter lodFilter = clone.m_lodObject.GetComponent<MeshFilter>();
                    Renderer lodRenderer = clone.m_lodObject.GetComponent<Renderer>();
                    if (lodFilter != null) lodFilter.sharedMesh = mesh;
                    if (lodRenderer != null) lodRenderer.sharedMaterial = material;
                }

                // Task71: 複製元（PowerPlantAI系統、FindElectricitySourceがm_subBuildings非空の物は
                // 除外済みだが m_subMeshes＝同一建物内の装飾的サブメッシュ、例えば煙突・配管等は対象外
                // だった）が m_subMeshes を持っていた場合の保険。null にしておけば
                // BuildingInfo.InitializePrefab()/RenderSubMeshes 双方が該当ロジックを丸ごとスキップし
                // （どちらも `if (m_subMeshes != null)` ガード済み、ilspycmdで確認済み）、複製元由来の
                // 装飾ジオメトリが新モデルの脇に残る可能性を根絶できる。
                clone.m_subMeshes = null;

                // clone専用のgeneratedInfoに差し替える（複製元と共有したままだと上記の例外が起きる）。
                clone.m_generatedInfo = ScriptableObject.CreateInstance<BuildingInfoGen>();
                clone.CalculateGeneratedInfo();

                ModConfig.Log("WarfrontBasePrefabVisualSwap.TryApplyMesh: 見た目を built-in model '" + modelName +
                    "' へ差し替えました（実コンポーネント + generatedInfo 再計算）");
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefabVisualSwap.TryApplyMesh error（既定の見た目を維持します）: " + e);
                return false;
            }
        }

        /// <summary>
        /// PrefabCollection&lt;BuildingInfo&gt;.InitializePrefabs / BindPrefabs の後に呼ぶこと
        /// （<see cref="TryApplyMesh"/> が成功した場合のみ）。
        /// ゲーム本体の BuildingManager.InitRenderData()（遠距離LOD用の結合メッシュ
        /// m_lodMeshCombined1/4/8 + m_lodMaterialCombined を焼き込む、レベルロード時に一度だけ
        /// 実行される処理）は、その実行時点で読み込み済みのプレハブしか処理しない
        /// （PrefabCollection&lt;BuildingInfo&gt;.LoadedCount() をその場でスキャンするだけで、
        /// 以後に登録されたプレハブを拾い直す仕組みは無い）。本MODはレベルロード完了後
        /// （OnLevelLoaded）にこのプレハブを登録するため、何もしなければ m_lodMeshCombined1/4/8 は
        /// 永久に null のままとなり、遠距離での Building.RenderLod が空のメッシュを描画しようとする
        /// （＝タービンではなく「遠くから見ると消える」という別のLODバグになる）。
        /// BuildingManager.InitRenderDataImpl の「テクスチャ無しLOD」分岐
        /// （m_lodMaterial にメインテクスチャが無い場合の InitMeshData(Rect(0,0,1,1), null,null,null)
        /// 呼び出し）と全く同じ呼び出しをここで肩代わりする（本MODの単色マテリアルは常にテクスチャ
        /// 無しのため、この分岐の前提条件と一致する）。
        /// </summary>
        public static void FinalizeLod(BuildingInfo clone)
        {
            try
            {
                if (clone.m_hasLodData) return; // 既に処理済みなら何もしない（冪等）
                clone.m_hasLodData = true;
                clone.InitMeshData(new Rect(0f, 0f, 1f, 1f), null, null, null);
                ModConfig.Log("WarfrontBasePrefabVisualSwap.FinalizeLod: LOD結合メッシュ(m_lodMeshCombined1/4/8)を焼き込みました");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefabVisualSwap.FinalizeLod error: " + e);
            }
        }
    }
}
