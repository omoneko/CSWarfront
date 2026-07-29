using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>メインスレッド専用スナップショット。飛翔中ミサイル1発分の「見た目」を決めるのに
    /// 必要な最小限の情報のみ（Task63）。UnitVisualState と同じ設計思想。</summary>
    public struct MissileVisualState
    {
        public uint Id;
        public byte FactionId;
        public Vector3 From;
        public Vector3 To;
        /// <summary>飛行進捗（0..1、Core.MissileInFlight.Progressの写し）。</summary>
        public float Progress;
    }

    /// <summary>
    /// 弾道ミサイルの見た目（Task63、塊6 MVP Part2）。UnitVisuals.Syncと同じ「宣言的reconcile」パターン:
    /// From→Toの直線をProgressでたどるCore側の抽象を、見た目だけ高高度の放物線弧（apex height =
    /// min(800, distance*0.5)）へ変換して描画する。ミサイルはCS車両/建物を一切借用しない素の
    /// Unity GameObject（小さな細長い箱）＋TrailRenderer（暖色系トレーサー、CombatFxの
    /// 「Standardシェーダー・固定色・sharedMaterial」という方針を踏襲）で表現する。
    ///
    /// 着弾/迎撃の演出（フラッシュ/爆発+音）は同じpartial classの別ファイル（MissileVisualsFx.cs）。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （UnitVisualsと同じ規約）。sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと。
    /// </summary>
    internal static partial class MissileVisuals
    {
        private class Entry
        {
            public GameObject GameObject;
        }

        // 見た目の弾道弧（Core.WorldPosは直線From→Toのみ、Progressで補間する抽象）。
        private const float ApexHeightCap = 800f;
        private const float ApexHeightRatio = 0.5f;

        // 「小さな細長いGameObject」の寸法（発射地点→目標のワールド距離に対して十分小さい既定サイズ）。
        private const float BodyWidth = 2.5f;
        private const float BodyLength = 12f;

        private const float TrailTime = 1.0f;
        private const float TrailStartWidth = 1.4f;
        private const float TrailEndWidth = 0.1f;

        // 姿勢（進行方向）を求めるための微小な先読み量（弧のパラメータt上）。
        private const float VelocitySampleDeltaT = 0.01f;

        private static readonly Dictionary<uint, Entry> _visuals = new Dictionary<uint, Entry>();
        private static readonly HashSet<uint> _seenIds = new HashSet<uint>();
        private static readonly List<uint> _staleIds = new List<uint>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _bodyMaterial;
        private static Material _trailMaterial;

        // 暖色系のトレーサー色（CombatFxのGunfire/DirectFire色と同系統。勢力色でチントしない
        // ＝発射勢力を問わず「飛翔中の弾道ミサイル」そのものとして一目でわかるようにする）。
        private static readonly Color BodyColor = new Color(0.85f, 0.35f, 0.15f);
        private static readonly Color TrailColor = new Color(1f, 0.75f, 0.35f);

        public static int Count { get { return _visuals.Count; } }

        /// <summary>スナップショットに基づき、生成/移動/破棄を宣言的に反映する（メインスレッド専用）。
        /// スナップショットに存在しないid（着弾・迎撃・セーブロード等で消えたもの）はここで破棄される。</summary>
        public static void Sync(List<MissileVisualState> snapshot)
        {
            if (snapshot == null) return;

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                MissileVisualState s = snapshot[i];
                _seenIds.Add(s.Id);

                try
                {
                    Entry entry;
                    if (!_visuals.TryGetValue(s.Id, out entry) || entry.GameObject == null)
                    {
                        entry = Create(s);
                        if (entry == null) continue; // 生成失敗はログ済み。次回のSyncで再試行する。
                        _visuals[s.Id] = entry;
                    }
                    UpdatePose(entry, s);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("MissileVisuals.Sync: missile " + s.Id + " の更新に失敗: " + e);
                }
            }

            _staleIds.Clear();
            foreach (var kv in _visuals)
            {
                if (!_seenIds.Contains(kv.Key)) _staleIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleIds.Count; i++) Destroy(_staleIds[i]);
        }

        /// <summary>追跡中の全ビジュアルを破棄する（レベルアンロード時、メインスレッド専用）。</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _visuals)
                {
                    if (kv.Value != null && kv.Value.GameObject != null)
                        UnityEngine.Object.Destroy(kv.Value.GameObject);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
            }
        }

        private static Entry Create(MissileVisualState s)
        {
            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col); // 選択/raycastの邪魔をしない

                go.name = "CSWarfrontMissile_" + s.Id;
                go.transform.localScale = new Vector3(BodyWidth, BodyWidth, BodyLength);

                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = GetBodyMaterial();

                TrailRenderer trail = go.AddComponent<TrailRenderer>();
                trail.time = TrailTime;
                trail.startWidth = TrailStartWidth;
                trail.endWidth = TrailEndWidth;
                trail.material = GetTrailMaterial();
                trail.minVertexDistance = 1f;
                trail.autodestruct = false;

                return new Entry { GameObject = go };
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.Create: missile " + s.Id + " error: " + e);
                return null;
            }
        }

        /// <summary>From→ToをProgressで補間しつつ、見た目だけ放物線弧（apex height =
        /// min(ApexHeightCap, 水平距離*ApexHeightRatio)）へ持ち上げる。姿勢は微小先読みで求めた
        /// 進行方向（速度ベクトル）に向ける。</summary>
        private static void UpdatePose(Entry entry, MissileVisualState s)
        {
            if (entry.GameObject == null) return;

            float t = Mathf.Clamp01(s.Progress);
            float apex = ApexHeight(s.From, s.To);

            Vector3 pos = ArcPositionAt(s.From, s.To, apex, t);
            entry.GameObject.transform.position = pos;

            float t2 = Mathf.Clamp01(t + VelocitySampleDeltaT);
            if (t2 > t)
            {
                Vector3 ahead = ArcPositionAt(s.From, s.To, apex, t2);
                Vector3 vel = ahead - pos;
                if (vel.sqrMagnitude > 1e-6f) entry.GameObject.transform.rotation = Quaternion.LookRotation(vel);
            }
        }

        private static float ApexHeight(Vector3 from, Vector3 to)
        {
            float dx = to.x - from.x, dz = to.z - from.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.Min(ApexHeightCap, dist * ApexHeightRatio);
        }

        private static Vector3 ArcPositionAt(Vector3 from, Vector3 to, float apex, float t)
        {
            Vector3 pos = Vector3.Lerp(from, to, t);
            pos.y += 4f * apex * t * (1f - t); // CombatFx.ArcPositionAtと同じ標準的な放物線補間
            return pos;
        }

        private static void Destroy(uint id)
        {
            try
            {
                Entry entry;
                if (_visuals.TryGetValue(id, out entry))
                {
                    if (entry != null && entry.GameObject != null) UnityEngine.Object.Destroy(entry.GameObject);
                    _visuals.Remove(id);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.Destroy: missile " + id + " error: " + e);
            }
        }

        private static Shader ResolveShader()
        {
            if (_shaderResolved) return _shader;
            _shaderResolved = true;
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                _shader = shader;
                if (shader == null)
                    ModConfig.LogError("MissileVisuals: シェーダー解決に失敗、ミサイルは描画されません");
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.ResolveShader error: " + e);
                _shader = null;
            }
            return _shader;
        }

        private static Material BuildMaterial(Color color)
        {
            Shader shader = ResolveShader();
            if (shader == null) return null;
            try
            {
                var mat = new Material(shader);
                mat.color = color;
                return mat;
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetBodyMaterial()
        {
            if (_bodyMaterial == null) _bodyMaterial = BuildMaterial(BodyColor);
            return _bodyMaterial;
        }

        private static Material GetTrailMaterial()
        {
            if (_trailMaterial == null) _trailMaterial = BuildMaterial(TrailColor);
            return _trailMaterial;
        }
    }
}
