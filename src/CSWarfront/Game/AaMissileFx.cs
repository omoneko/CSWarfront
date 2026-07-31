using System;
using System.Collections.Generic;
using CSWarfront.Game.Effects;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 対空ミサイルの飛翔演出（Task90、ユーザー要望「対空兵器は戦闘機や爆撃機に対して対空ミサイルを
    /// 発射」「迎撃アニメーション周りはMissileDisaster MODを参考に」）。
    /// MissileDisaster.Game.InterceptorProjectileの追尾弾体パターンの移植:
    ///  - 発射位置から標的機の現在位置（UnitVisuals経由、毎フレーム追尾）へモデル弾体
    ///    （Models/Prop_Interceptor.obj、+Z=機首）が飛ぶ。SamTrail（ノズル火炎＋噴煙）付き。
    ///  - 命中弾（Missed=false）: 到達時にSamBurstFx.PlayFlash（閃光）。ダメージはCore側で確定済み。
    ///  - 外れ弾（Missed=true）: 標的の脇へ逸れた点を狙い、到達時にPlayFizzle（不発煙）。
    ///  - どちらも接近時（FlareTriggerDistance以内）に一度だけ、標的機がフレアを放出し
    ///    （SamBurstFx.PlayFlares）回避機動を取る（UnitVisuals.NotifyEvade、視覚上のジンク）。
    ///    命中/外れの結果自体はCoreの命中ロールで確定済み——フレアと回避は「外れた理由」の演出。
    ///
    /// スレッド境界: 全publicメソッドはメインスレッド専用（CombatFx/BombFxと同じ規約）。
    /// </summary>
    internal static class AaMissileFx
    {
        private const int MaxLive = 32;
        private const float Speed = 260f;               // m/秒（実時間）。射程120-190mを0.5〜0.8秒で駆ける
        private const float CatchRadius = 10f;          // 到達判定距離
        private const float MaxFlightSeconds = 4f;      // 追尾不能時の保険
        private const float MissOffsetDistance = 30f;   // 外れ弾が標的の脇へ逸れる距離
        private const float FlareTriggerDistance = 70f; // フレア放出・回避機動を始める接近距離
        private const string ModelName = "Prop_Interceptor";

        private class Sam
        {
            public GameObject Root;
            public uint TargetId;
            public Vector3 AimPos;      // 標的消失時の最終既知点（または外れ弾の逸れ先）
            public Vector3 MissOffset;  // 外れ弾のみ: 標的位置に足すオフセット
            public bool Missed;
            public bool FlareDone;
            public float Elapsed;
        }

        private static readonly List<Sam> _live = new List<Sam>();

        private static Mesh _mesh;
        private static Material[] _materials;
        private static bool _modelResolveAttempted;
        private static Material _fallbackMaterial;

        /// <summary>1発発射する（CombatFx.SpawnOneのSamMissile分岐から。from=発射位置（銃口高さ込み）、
        /// to=発射時点の標的位置、targetId=標的機、missed=Core側で確定済みの外れフラグ）。</summary>
        public static void Spawn(Vector3 from, Vector3 to, uint targetId, bool missed)
        {
            try
            {
                if (_live.Count >= MaxLive) return;

                GameObject go = CreateProjectile();
                if (go == null) return;
                go.transform.position = from;

                var sam = new Sam
                {
                    Root = go,
                    TargetId = targetId,
                    AimPos = to,
                    Missed = missed,
                    FlareDone = false,
                    Elapsed = 0f
                };
                if (missed)
                {
                    // 進行方向に対して横へ逸れるオフセット（決定的: targetIdの偶奇で左右を決める）。
                    Vector3 dir = (to - from);
                    dir.y = 0f;
                    Vector3 side = dir.sqrMagnitude > 1e-4f
                        ? Vector3.Cross(dir.normalized, Vector3.up)
                        : Vector3.right;
                    sam.MissOffset = side * ((targetId & 1) == 0 ? MissOffsetDistance : -MissOffsetDistance)
                        + Vector3.up * (MissOffsetDistance * 0.4f);
                }

                SamTrail.Attach(go);
                AimAt(go, to);
                _live.Add(sam);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.Spawn error: " + e);
            }
        }

        /// <summary>飛翔中の全弾を実時間で進める（メインスレッド専用）。</summary>
        public static void Update(float realDeltaTime)
        {
            if (_live.Count == 0) return;

            try
            {
                for (int i = _live.Count - 1; i >= 0; i--)
                {
                    Sam sam = _live[i];
                    if (sam.Root == null) { _live.RemoveAt(i); continue; }

                    sam.Elapsed += realDeltaTime;

                    // 標的の現在位置を追尾（見た目が消えていれば最終既知点へ）。外れ弾はオフセット付き。
                    Vector3 targetPos;
                    if (UnitVisuals.TryGetPosition(sam.TargetId, out targetPos))
                        sam.AimPos = sam.Missed ? targetPos + sam.MissOffset : targetPos;

                    Vector3 pos = sam.Root.transform.position;
                    Vector3 delta = sam.AimPos - pos;
                    float dist = delta.magnitude;
                    float step = Speed * realDeltaTime;

                    // 接近したら標的機が一度だけフレアを撒いて回避機動に入る（結果はCore確定済み）。
                    if (!sam.FlareDone && dist <= FlareTriggerDistance)
                    {
                        sam.FlareDone = true;
                        Vector3 planePos;
                        if (UnitVisuals.TryGetPosition(sam.TargetId, out planePos))
                        {
                            SamBurstFx.PlayFlares(planePos);
                            UnitVisuals.NotifyEvade(sam.TargetId);
                        }
                    }

                    bool reached = dist <= Mathf.Max(step, CatchRadius);
                    bool timedOut = sam.Elapsed >= MaxFlightSeconds;
                    if (reached || timedOut)
                    {
                        Vector3 point = reached ? sam.AimPos : pos;
                        if (sam.Missed || timedOut) SamBurstFx.PlayFizzle(point);
                        else SamBurstFx.PlayFlash(point);

                        SamTrail.DetachAndLinger(sam.Root);
                        UnityEngine.Object.Destroy(sam.Root);
                        _live.RemoveAt(i);
                        continue;
                    }

                    Vector3 next = pos + delta / dist * step;
                    sam.Root.transform.position = next;
                    AimAt(sam.Root, sam.AimPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.Update error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset、メインスレッド専用）。</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _live.Count; i++)
                {
                    if (_live[i] != null && _live[i].Root != null)
                        UnityEngine.Object.Destroy(_live[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.DestroyAll error: " + e);
            }
            finally
            {
                _live.Clear();
                _mesh = null;
                _materials = null;
                _modelResolveAttempted = false;
            }
        }

        private static void AimAt(GameObject go, Vector3 aim)
        {
            Vector3 dir = aim - go.transform.position;
            if (dir.sqrMagnitude > 1e-6f) go.transform.rotation = Quaternion.LookRotation(dir);
        }

        private static GameObject CreateProjectile()
        {
            ResolveModel();

            if (_mesh != null)
            {
                var go = new GameObject("CSWarfrontSam");
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                if (_materials != null) mr.sharedMaterials = _materials;
                return go;
            }

            // フォールバック: 細長い白い箱（InterceptorProjectileのフォールバック球と同じ役割）。
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Collider col = box.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            box.name = "CSWarfrontSamFallback";
            box.transform.localScale = new Vector3(0.7f, 0.7f, 3.5f);
            Renderer r = box.GetComponent<Renderer>();
            if (r != null && GetFallbackMaterial() != null) r.sharedMaterial = GetFallbackMaterial();
            return box;
        }

        private static void ResolveModel()
        {
            if (_modelResolveAttempted) return;
            _modelResolveAttempted = true;

            Mesh mesh;
            Material[] materials;
            if (Models.WarfrontModelProvider.TryGetModel(ModelName, out mesh, out materials))
            {
                _mesh = mesh;
                _materials = materials;
                ModConfig.Log("AaMissileFx: interceptor model resolved (" + ModelName + ").");
            }
            else
            {
                ModConfig.Log("AaMissileFx: interceptor model not found (" + ModelName + "); using fallback box.");
            }
        }

        private static Material GetFallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                if (shader == null) return null;
                _fallbackMaterial = new Material(shader);
                _fallbackMaterial.color = new Color(0.9f, 0.9f, 0.88f, 1f);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.GetFallbackMaterial error: " + e);
            }
            return _fallbackMaterial;
        }
    }
}
