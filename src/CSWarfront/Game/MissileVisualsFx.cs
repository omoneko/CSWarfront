using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// MissileVisuals のうち、着弾/迎撃の演出（フラッシュ/爆発+音、Task63）だけを分離した partial class
    /// （MissileVisuals.cs 側の見通しのため、UnitVisuals/UnitVisualsFactionIconと同じ分割方針）。
    /// WarState.RecentImpacts（Core.MissileImpactEvent）のスナップショットを受け取り、
    /// Intercepted=falseなら着弾（大きめの爆発＋既存の砲撃/撃破に近い爆発音）、
    /// Intercepted=trueなら迎撃（小さな閃光のみ、ダメージ演出無し）を出す。
    /// 全メソッドはメインスレッド専用（Unity API呼び出しのため）。
    /// </summary>
    internal static partial class MissileVisuals
    {
        private class FxEntry
        {
            public GameObject Root;
            public float Elapsed;
            public float Duration;
            public float InitialSize;
        }

        private const float ImpactFlashDuration = 0.6f;
        private const float ImpactFlashSize = 14f;
        private const float InterceptFlashDuration = 0.3f;
        private const float InterceptFlashSize = 5f;

        private static readonly Color ImpactColor = new Color(1f, 0.55f, 0.1f);
        private static readonly Color InterceptColor = new Color(1f, 0.95f, 0.75f);
        private static Material _impactMaterial;
        private static Material _interceptMaterial;

        private static readonly List<FxEntry> _fx = new List<FxEntry>();

        /// <summary>1tick分のMissileImpactEventから着弾/迎撃の演出を生成する（メインスレッド専用）。
        /// CombatFx.Spawnと同じ「カメラ位置を1回だけ取得してから全件処理する」パターン。</summary>
        public static void HandleImpacts(List<MissileImpactEvent> events)
        {
            if (events == null || events.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = 0; i < events.Count; i++)
                {
                    MissileImpactEvent e = events[i];
                    Vector3 pos = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);

                    if (e.Intercepted)
                    {
                        SpawnFlash(pos, InterceptFlashSize, InterceptFlashDuration, GetInterceptMaterial());
                        WarfrontSoundPlayer.PlayShot(WarfrontSounds.AaMissile, pos, cameraPos);
                    }
                    else
                    {
                        SpawnFlash(pos, ImpactFlashSize, ImpactFlashDuration, GetImpactMaterial());
                        WarfrontSoundPlayer.PlayKill(pos, cameraPos); // 既存の「爆発」相当音(vehicle_destroyed)を再利用
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.HandleImpacts error: " + e);
            }
        }

        /// <summary>生存中の演出（フラッシュ）を実時間で進め、寿命が尽きたものを破棄する
        /// （メインスレッド専用）。MilitaryManager.OnMainVisualUpdateから毎フレーム呼ぶこと。</summary>
        public static void UpdateFx(float realDeltaTime)
        {
            if (_fx.Count == 0) return;

            try
            {
                for (int i = _fx.Count - 1; i >= 0; i--)
                {
                    FxEntry fx = _fx[i];
                    if (fx.Root == null) { _fx.RemoveAt(i); continue; }

                    fx.Elapsed += realDeltaTime;
                    float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
                    float size = Mathf.Lerp(fx.InitialSize, 0f, t);
                    fx.Root.transform.localScale = new Vector3(size, size, size);

                    if (fx.Elapsed >= fx.Duration)
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _fx.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.UpdateFx error: " + e);
            }
        }

        /// <summary>生存中の演出を破棄する（レベルアンロード時、メインスレッド専用）。</summary>
        public static void DestroyAllFx()
        {
            try
            {
                for (int i = 0; i < _fx.Count; i++)
                {
                    if (_fx[i] != null && _fx[i].Root != null) UnityEngine.Object.Destroy(_fx[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.DestroyAllFx error: " + e);
            }
            finally
            {
                _fx.Clear();
            }
        }

        private static void SpawnFlash(Vector3 position, float size, float duration, Material material)
        {
            if (material == null) return;
            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                go.name = "CSWarfrontMissileFx";
                go.transform.position = position;
                go.transform.localScale = new Vector3(size, size, size);

                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;

                _fx.Add(new FxEntry { Root = go, Elapsed = 0f, Duration = duration, InitialSize = size });
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.SpawnFlash error: " + e);
            }
        }

        private static Material GetImpactMaterial()
        {
            if (_impactMaterial == null) _impactMaterial = BuildMaterial(ImpactColor);
            return _impactMaterial;
        }

        private static Material GetInterceptMaterial()
        {
            if (_interceptMaterial == null) _interceptMaterial = BuildMaterial(InterceptColor);
            return _interceptMaterial;
        }
    }
}
