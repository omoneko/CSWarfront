using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 爆撃機の爆弾投下モーション（Task87、ユーザー要望「爆撃機は爆弾を落とすモーションを入れてほしい」）。
    ///
    /// CombatFx.SpawnOneが、爆撃機(TacticalBomber)のShotEventについて直射トレーサーの代わりに
    /// このクラスのSpawnDropを呼ぶ。爆弾は投下位置（爆撃機のモデル中央高さ）から着弾点まで、
    /// 水平は等速・垂直は加速（自由落下風の二次カーブ）で落ち、機首（+Z）を速度方向へ向けて回転する。
    /// 着弾時はKillFx.TryDispatchCsExplosion（CS標準爆発、Task84と同じ経路）を撃破爆発より小さめの
    /// 倍率で再生する。
    ///
    /// モデルはModels/Prop_Bomb.obj（WarfrontModelProvider経由）。2026-07-31時点ではmodels.blendに
    /// 爆弾オブジェクトがまだ無いため暫定生成モデルを同梱している——ユーザーが「17_Bomb」等を
    /// models.blendへ追加してtools/export_builtin_obj.pyを再実行すれば差し替わる。モデルが解決
    /// できない環境ではプリミティブ球（暗色）で代用する。
    ///
    /// スレッド境界: 全publicメソッドはメインスレッド専用（CombatFx/KillFxと同じ規約）。
    /// </summary>
    internal static class BombFx
    {
        private const int MaxLiveBombs = 40; // 防御的上限（KillFx.MaxLiveEffectsと同じ考え方）

        /// <summary>落下時間（実秒）。高度120の巡航から地表まで、視認できる速さに較正した値。</summary>
        private const float FallDuration = 1.1f;

        /// <summary>着弾爆発の強度。撃破爆発(KillFx.CsExplosionMagnitude=0.5)より控えめ
        /// （爆弾1発ごとに出るため、連続爆撃で過剰にならないように）。</summary>
        private const float ImpactExplosionMagnitude = 0.4f;

        private const string ModelName = "Prop_Bomb";

        private class Bomb
        {
            public GameObject Root;
            public Vector3 From;
            public Vector3 To;
            public float Elapsed;
        }

        private static readonly List<Bomb> _bombs = new List<Bomb>();

        private static Mesh _mesh;
        private static Material[] _materials;
        private static bool _modelResolveAttempted;
        private static Material _fallbackMaterial;

        /// <summary>爆弾を1発投下する（メインスレッド専用、CombatFx.SpawnOneの爆撃機分岐から呼ばれる）。
        /// from=投下位置（爆撃機のモデル中央高さ込み）、to=着弾点。</summary>
        public static void SpawnDrop(Vector3 from, Vector3 to)
        {
            try
            {
                if (_bombs.Count >= MaxLiveBombs) return;

                GameObject go = CreateBombObject();
                if (go == null) return;
                go.transform.position = from;

                _bombs.Add(new Bomb { Root = go, From = from, To = to, Elapsed = 0f });
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.SpawnDrop error: " + e);
            }
        }

        /// <summary>落下中の全爆弾を実時間で進める（メインスレッド専用）。着弾したら爆発を再生して破棄。</summary>
        public static void Update(float realDeltaTime)
        {
            if (_bombs.Count == 0) return;

            try
            {
                for (int i = _bombs.Count - 1; i >= 0; i--)
                {
                    Bomb b = _bombs[i];
                    if (b.Root == null) { _bombs.RemoveAt(i); continue; }

                    b.Elapsed += realDeltaTime;
                    float t = Mathf.Clamp01(b.Elapsed / FallDuration);

                    // 水平は等速、垂直は加速（t^2）＝投下直後は前へ流れ、落ちるほど機首が下がる。
                    float x = Mathf.Lerp(b.From.x, b.To.x, t);
                    float z = Mathf.Lerp(b.From.z, b.To.z, t);
                    float y = Mathf.Lerp(b.From.y, b.To.y, t * t);
                    Vector3 pos = new Vector3(x, y, z);

                    // 速度方向（解析微分: 水平=定数、垂直=2t比例）へ機首(+Z)を向ける。
                    Vector3 vel = new Vector3(
                        (b.To.x - b.From.x) / FallDuration,
                        (b.To.y - b.From.y) * 2f * t / FallDuration,
                        (b.To.z - b.From.z) / FallDuration);
                    b.Root.transform.position = pos;
                    if (vel.sqrMagnitude > 1e-4f)
                        b.Root.transform.rotation = Quaternion.LookRotation(vel);

                    if (t >= 1f)
                    {
                        KillFx.TryDispatchCsExplosion(b.To, ImpactExplosionMagnitude);
                        UnityEngine.Object.Destroy(b.Root);
                        _bombs.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.Update error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset、メインスレッド専用）。
        /// モデルキャッシュも破棄する（Mesh/MaterialはWarfrontModelProviderがレベルを跨いで
        /// キャッシュするが、こちらの参照は次レベルで取り直す方が安全側）。</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _bombs.Count; i++)
                {
                    if (_bombs[i] != null && _bombs[i].Root != null)
                        UnityEngine.Object.Destroy(_bombs[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.DestroyAll error: " + e);
            }
            finally
            {
                _bombs.Clear();
                _mesh = null;
                _materials = null;
                _modelResolveAttempted = false;
            }
        }

        private static GameObject CreateBombObject()
        {
            ResolveModel();

            if (_mesh != null)
            {
                var go = new GameObject("CSWarfrontBomb");
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                if (_materials != null) mr.sharedMaterials = _materials;
                return go;
            }

            // フォールバック: 小さな暗色の球（モデル未解決の環境でも投下は見える）。
            GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Collider col = fallback.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col); // クリック判定を邪魔しない
            fallback.name = "CSWarfrontBombFallback";
            fallback.transform.localScale = new Vector3(1.6f, 1.6f, 2.4f);
            Renderer r = fallback.GetComponent<Renderer>();
            if (r != null && GetFallbackMaterial() != null) r.sharedMaterial = GetFallbackMaterial();
            return fallback;
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
                ModConfig.Log("BombFx: bomb model resolved (" + ModelName + ").");
            }
            else
            {
                ModConfig.Log("BombFx: bomb model not found (" + ModelName + "); using fallback sphere.");
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
                _fallbackMaterial.color = new Color(0.2f, 0.21f, 0.18f, 1f);
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.GetFallbackMaterial error: " + e);
            }
            return _fallbackMaterial;
        }
    }
}
