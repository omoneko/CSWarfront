using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 車両撃破時の小さな爆発エフェクト（Task65、ユーザー要望「車両撃破時の爆発エフェクト
    /// （小さな爆発でいいです）」）。State.RecentKillsのうち、CombatFx.IsVehicleDestructionCategory
    /// がtrueを返すカテゴリ（歩兵・ドローン兵を除く車両系撃破）だけを対象にする＝撃破音
    /// (CombatFx.SpawnKillSounds)と全く同じ判定基準を1箇所（CombatFxSound.cs）から共有する
    /// （Task51の「生身の歩兵が爆発するのは演出として不自然」というルールを、音とエフェクトの
    /// 両方で一貫させるため、カテゴリ一覧を複製しない）。
    ///
    /// 見た目（Task84、ユーザー要望「撃破時の爆発をもう少しリアルに。CS内のエフェクトも使っていい。
    /// 規模は今くらい」）: CS標準の爆発エフェクト DisasterProperties.m_mediumExplosion を
    /// EffectManager.DispatchEffect で再生する（AlienInvasionのEffects.PlayImpactBurstで実績のある
    /// 経路。パーティクルの火球・煙・光を含む本物の爆発になる）。magnitudeは従来の火球サイズ
    /// （ピーク約5.5m）と釣り合う控えめな値に較正する（Alienのレーザー着弾0.7より小さい0.5）。
    /// EffectInfoが解決できない環境では、従来のprimitive球ベースの簡易爆発（下のフォールバック実装、
    /// Task65の実装そのまま）へ自動フォールバックする。
    ///
    /// 旧来の方針（CS由来のリソースを借りない）はマテリアル借用の不可視バグ（cs-mesh-material-
    /// rendering）に対するものであり、EffectManagerへのdispatchはCS自身が描画まで面倒を見るため
    /// この問題とは無関係（Alien/Godzilla MODで実機実績あり）。
    ///
    /// CombatFx.cs（577行、Task65時点で500行近く）を肥大化させないよう別ファイル・別クラスとして
    /// 新設した（CombatFxSoundのような partial 分割ではなく独立クラス。公開APIはSpawn/Update/
    /// DestroyAllの3つのみで、MilitaryManager.OnMainVisualUpdate/Resetから呼ばれる形も
    /// CombatFxのSpawn/Update/DestroyAllと完全に同じパターン）。
    ///
    /// スレッド境界: このクラスの public メソッドは全てメインスレッド専用（CombatFxと同じ規約、
    /// sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと）。
    /// </summary>
    internal static class KillFx
    {
        /// <summary>同時に生きていられるエフェクトの上限。撃破はCombatFxが扱う発砲より低頻度な
        /// イベントなので、CombatFx.MaxLiveEffects(200)より控えめな値にする（防御的上限、
        /// 大量殲滅が起きてもGameObjectが際限なく増えないようにするため）。</summary>
        private const int MaxLiveEffects = 48;

        // カメラから遠すぎる撃破は生成自体をスキップする（CombatFx.SpawnOneと同じ軽量な距離チェック）。
        private const float MaxSpawnDistanceFromCamera = 2000f;

        // 火球: 複数の小さな球をわずかにオフセットして「塊」に見せる。素早く膨らんで縮む。
        private const int FireballChunkCount = 3;
        private const float FireballDuration = 0.45f;
        private const float FireballPeakSize = 5.5f;
        private const float FireballChunkOffset = 1.2f;
        private const float FireballGrowFraction = 0.4f; // 前半40%で膨張、残りで消滅

        // 黒煙puff: 火球より少し遅れて始まり、ゆっくり膨らんで消える（合計寿命を~1.5sへ引き延ばす）。
        private const float SmokeStartDelay = 0.15f;
        private const float SmokeDuration = 1.35f; // 0.15 + 1.35 = 1.5s トータル寿命
        private const float SmokePeakSize = 7f;
        private const float SmokeGrowFraction = 0.3f;

        private static readonly Color FireballColor = new Color(1f, 0.75f, 0.25f, 1f);
        private static readonly Color SmokeColor = new Color(0.18f, 0.16f, 0.14f, 1f);

        private class Effect
        {
            public GameObject Root;
            public Transform[] FireballChunks;
            public Transform Smoke;
            public float Elapsed;
        }

        private static readonly List<Effect> _effects = new List<Effect>();

        /// <summary>Task84: CS標準爆発の再生強度。EffectManager.DispatchEffectのmagnitude引数。
        /// Alienのレーザー着弾(0.7)より控えめにして、従来の小爆発と同程度の規模に合わせる。</summary>
        private const float CsExplosionMagnitude = 0.5f;

        /// <summary>Task84: 1回のSpawn呼び出し（=1フレーム）でdispatchするCS爆発の上限。
        /// 大量殲滅時のパーティクル過剰を防ぐ（MaxLiveEffectsと同じ防御的上限の考え方）。</summary>
        private const int MaxCsDispatchPerFrame = 16;

        private static EffectInfo _csEffect;
        private static bool _csEffectResolveAttempted;

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _fireballMaterial;
        private static Material _smokeMaterial;

        /// <summary>1tick分のKillEventから爆発エフェクトを生成する（メインスレッド専用）。
        /// 歩兵・ドローン兵の撃破、カメラから遠すぎる撃破は生成しない。MaxLiveEffectsに達していれば
        /// それ以降は静かに無視する（例外にしない、CombatFx.Spawnと同じ方針）。</summary>
        public static void Spawn(List<KillEvent> kills)
        {
            if (kills == null || kills.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                int dispatched = 0;
                for (int i = 0; i < kills.Count; i++)
                {
                    KillEvent k = kills[i];
                    // Task51/65共通ルール: 歩兵・ドローン兵の撃破では爆発を出さない（撃破音と同じ判定）。
                    if (!CombatFx.IsVehicleDestructionCategory(k.Category)) continue;

                    Vector3 pos = new Vector3(k.Position.X, k.Position.Y, k.Position.Z);
                    if (cameraPos.HasValue)
                    {
                        float distSqr = (pos - cameraPos.Value).sqrMagnitude;
                        if (distSqr > MaxSpawnDistanceFromCamera * MaxSpawnDistanceFromCamera) continue;
                    }

                    // Task84: CS標準爆発が使えるならそちらを再生（リアルなパーティクル爆発）。
                    // 解決できない環境でのみ従来のprimitive球フォールバックを使う。
                    if (TryDispatchCsExplosion(pos))
                    {
                        if (++dispatched >= MaxCsDispatchPerFrame) break;
                        continue;
                    }

                    if (_effects.Count >= MaxLiveEffects) break;
                    SpawnOne(pos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.Spawn error: " + e);
            }
        }

        /// <summary>生存中の全エフェクトを実時間(realDeltaTime)で進める（メインスレッド専用）。
        /// 火球・黒煙の合計寿命(SmokeStartDelay+SmokeDuration)を過ぎたエフェクトを破棄する。</summary>
        public static void Update(float realDeltaTime)
        {
            if (_effects.Count == 0) return;

            try
            {
                for (int i = _effects.Count - 1; i >= 0; i--)
                {
                    Effect fx = _effects[i];
                    if (fx.Root == null) { _effects.RemoveAt(i); continue; }

                    fx.Elapsed += realDeltaTime;
                    StepFireball(fx);
                    StepSmoke(fx);

                    if (fx.Elapsed >= SmokeStartDelay + SmokeDuration)
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _effects.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.Update error: " + e);
            }
        }

        /// <summary>生存中の全エフェクトを破棄する（レベルアンロード時、メインスレッド専用、
        /// MilitaryManager.Resetから呼ばれる）。キャッシュ済みマテリアルはGameObjectではないため
        /// 破棄しない（CombatFx.DestroyAllと同じ扱い、次セッションでも使い回せる）。</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _effects.Count; i++)
                {
                    if (_effects[i] != null && _effects[i].Root != null)
                        UnityEngine.Object.Destroy(_effects[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.DestroyAll error: " + e);
            }
            finally
            {
                _effects.Clear();
                // Task84: EffectInfoはプレハブ参照であり、レベル再読込後は破棄済みUnityオブジェクトに
                // なりうる（==nullのfake-null自己修復も静的キャッシュには効かない、
                // cs-static-unity-object-cacheの教訓）。次レベルで必ず再解決させる。
                _csEffect = null;
                _csEffectResolveAttempted = false;
            }
        }

        /// <summary>Task84: CS標準の爆発エフェクトを撃破位置で再生する。解決できなければfalseを返し、
        /// 呼び出し元が従来のフォールバック爆発を使う。dispatch自体が例外を投げた場合はこのセッション中
        /// CS爆発を諦めてフォールバックに切り替える（毎フレームのエラーログ連発を防ぐ）。</summary>
        private static bool TryDispatchCsExplosion(Vector3 pos)
        {
            EffectInfo effect = ResolveCsEffect();
            if (effect == null) return false;

            try
            {
                var spawnArea = new EffectInfo.SpawnArea(pos, Vector3.up, 0f);
                Singleton<EffectManager>.instance.DispatchEffect(
                    effect, default(InstanceID), spawnArea, Vector3.zero, 0f, CsExplosionMagnitude,
                    Singleton<VehicleManager>.instance.m_audioGroup);
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.TryDispatchCsExplosion error (falling back to simple effect): " + e);
                _csEffect = null; // 以後このセッションはフォールバック（_csEffectResolveAttemptedは立ったまま）
                return false;
            }
        }

        /// <summary>CS標準爆発のEffectInfoを解決する（AlienInvasion Effects.ResolveImpactEffectと同じ
        /// 解決順: DisasterProperties.m_mediumExplosion → 隕石着弾エフェクト）。プロセス中1回だけ試み、
        /// 結果（失敗含む）をキャッシュする。EffectInfoはプレハブ参照でレベル再読込で無効になりうるため、
        /// DestroyAll（レベルアンロード）でキャッシュを破棄して次レベルで再解決する
        /// （[[cs-static-unity-object-cache]]のfake-null問題を避けるため、静的キャッシュを跨がせない）。</summary>
        private static EffectInfo ResolveCsEffect()
        {
            if (_csEffectResolveAttempted) return _csEffect;
            _csEffectResolveAttempted = true;

            try
            {
                DisasterProperties dp = Singleton<DisasterManager>.instance.m_properties;
                if (dp != null && dp.m_mediumExplosion != null)
                {
                    _csEffect = dp.m_mediumExplosion;
                    ModConfig.Log("KillFx: using DisasterProperties.m_mediumExplosion for kill explosions.");
                    return _csEffect;
                }
            }
            catch (Exception)
            {
                // フォールバックへ
            }

            try
            {
                int count = PrefabCollection<VehicleInfo>.LoadedCount();
                for (int i = 0; i < count; i++)
                {
                    VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (info == null) continue;
                    MeteorAI ai = info.m_vehicleAI as MeteorAI;
                    if (ai != null && ai.m_impactEffect != null)
                    {
                        _csEffect = ai.m_impactEffect;
                        ModConfig.Log("KillFx: using meteor impact effect for kill explosions.");
                        return _csEffect;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.ResolveCsEffect error: " + e);
            }

            ModConfig.Log("KillFx: no CS explosion effect available; using simple fallback explosions.");
            _csEffect = null;
            return null;
        }

        private static void SpawnOne(Vector3 pos)
        {
            try
            {
                var go = new GameObject("CSWarfrontKillFx");
                go.transform.position = pos;

                var chunks = new Transform[FireballChunkCount];
                for (int c = 0; c < FireballChunkCount; c++)
                {
                    // 見た目専用のわずかな散らし（決定性はCore側の話であり、Game層の演出には不要）。
                    Vector3 offset = UnityEngine.Random.insideUnitSphere * FireballChunkOffset;
                    chunks[c] = CreateSmallSphere(go.transform, pos + offset, 0f, GetFireballMaterial());
                }

                Transform smoke = CreateSmallSphere(go.transform, pos, 0f, GetSmokeMaterial());

                _effects.Add(new Effect
                {
                    Root = go,
                    FireballChunks = chunks,
                    Smoke = smoke,
                    Elapsed = 0f
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.SpawnOne error: " + e);
            }
        }

        /// <summary>クリック選択のraycastを邪魔しないよう、Colliderを無効化した小さな球を作る
        /// （CombatFx.CreateSmallSphereと同じ役割・同じ実装。partialでの共有ではなく独立クラスの
        /// ためのローカルコピーだが、中身は意図的に同一にしてある）。</summary>
        private static Transform CreateSmallSphere(Transform parent, Vector3 worldPos, float size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;
            go.transform.localScale = new Vector3(size, size, size);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            return go.transform;
        }

        private static void StepFireball(Effect fx)
        {
            if (fx.FireballChunks == null) return;
            float t = FireballDuration > 0f ? Mathf.Clamp01(fx.Elapsed / FireballDuration) : 1f;
            // 前半で急速に膨らみ(0->peak)、後半で0へ縮む（簡易な膨張->消滅カーブ）。
            float size = t < FireballGrowFraction
                ? Mathf.Lerp(0f, FireballPeakSize, t / FireballGrowFraction)
                : Mathf.Lerp(FireballPeakSize, 0f, (t - FireballGrowFraction) / (1f - FireballGrowFraction));

            for (int i = 0; i < fx.FireballChunks.Length; i++)
            {
                if (fx.FireballChunks[i] != null)
                    fx.FireballChunks[i].localScale = new Vector3(size, size, size);
            }
        }

        private static void StepSmoke(Effect fx)
        {
            if (fx.Smoke == null) return;
            float local = fx.Elapsed - SmokeStartDelay;
            if (local < 0f) { fx.Smoke.localScale = Vector3.zero; return; }

            float t = SmokeDuration > 0f ? Mathf.Clamp01(local / SmokeDuration) : 1f;
            // ゆっくり膨らんでから縮む（黒煙が薄れて消えるように見せる、CombatFx.StepImpactPuffと同じ簡易表現）。
            float size = t < SmokeGrowFraction
                ? Mathf.Lerp(0f, SmokePeakSize, t / SmokeGrowFraction)
                : Mathf.Lerp(SmokePeakSize, 0f, (t - SmokeGrowFraction) / (1f - SmokeGrowFraction));
            fx.Smoke.localScale = new Vector3(size, size, size);
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
                    ModConfig.LogError("KillFx: failed to resolve shader (Standard/Legacy Shaders/Diffuse all failed), kill explosion effects will not render");
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.ResolveShader error: " + e);
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
                ModConfig.LogError("KillFx.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetFireballMaterial()
        {
            if (_fireballMaterial == null) _fireballMaterial = BuildMaterial(FireballColor);
            return _fireballMaterial;
        }

        private static Material GetSmokeMaterial()
        {
            if (_smokeMaterial == null) _smokeMaterial = BuildMaterial(SmokeColor);
            return _smokeMaterial;
        }
    }
}
