using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 発砲エフェクト（Task42）: WarState.RecentShotsから受け取ったShotEventを、短命なUnity
    /// GameObjectとして描画する。戦略ビューであることを踏まえ、シューターゲームのような派手さではなく
    /// 「何が起きているか一目でわかる」程度の軽量な表現にとどめる。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （new GameObject / AddComponent / Destroy / transform書込みはUnityのメインスレッド制約）。
    /// sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと（UnitVisualsと同じ規約）。
    ///
    /// マテリアルはCS由来のものを一切借用しない。UnitMaterialFactoryと同じ理由（CS車両シェーダーは
    /// CS自身のレンダラー由来のper-instanceデータを要求し、素のRendererに割り当てると不可視/黒になる）
    /// により、自前の標準シェーダーMaterialを勢力に依存しない固定色で生成・共有する（sharedMaterial、
    /// per-instance化しない）。勢力色でチントしない＝トレーサーは「発砲そのもの」として読めるようにし、
    /// 陣営マーカーと誤読されないようにする（要件）。
    /// </summary>
    internal static class CombatFx
    {
        /// <summary>同時に生きていられるエフェクトの上限（Task42）。大規模乱戦でGameObjectが
        /// 際限なく増えないようにする防御的上限。ShotEvent側の上限(WarState.MaxRecentShotsPerTick)とは
        /// 独立に、こちらは「現在生存中」の総数を制限する。
        /// Task43: 銃撃が1発→3点バーストになったことで、同じ発砲を発端に一時的に生きるエフェクト数が
        /// 最大3倍近くまで増え得るため、120→200へ引き上げた（大規模乱戦でも従来と同程度の余裕を保つ）。</summary>
        private const int MaxLiveEffects = 200;

        // カメラから遠すぎる発砲は生成自体をスキップする（軽量な距離チェックのみ）。
        private const float MaxSpawnDistanceFromCamera = 2000f;

        // Task43: 発射/着弾位置をモデル中央高さへ持ち上げるための既定値（UnitVisuals.TryGetMuzzleOffset
        // が見つからない場合のフォールバック）。AttackerId/TargetIdが0（基地等、論理ユニットでない対象）
        // か、見た目がまだ生成されていない場合に使う。基地は建物なので既定の発射高さより高めに設定する。
        private const float DefaultMuzzleHeight = 3f;
        private const float BaseTargetHeight = 8f;

        // Gunfire（Infantry/MechInfantry/Apc/DroneInfantry/AntiAir）: 細く短いトレーサー＋小さなマズルフラッシュ。
        // Task43: 1発→3点バースト化に合わせて、1発ごとの表示時間を0.08s→0.06sへわずかに短縮した
        // （バースト間隔0.07sより短く保ち、次弾が出る前に前弾が消え切るようにするため）。
        private const float GunfireTracerDuration = 0.06f;
        private const float GunfireTracerWidth = 0.15f;
        private const float GunfireFlashSize = 1.2f;

        // Task43: 銃撃1回＝3点バースト。1発目は即座に、2/3発目はGunfireBurstRoundGap間隔で
        // 実時間ベースに遅延させて発射する（_pendingBursts、Update内でブロッキングなしに進める）。
        private const int GunfireBurstRounds = 3;
        private const float GunfireBurstRoundGap = 0.07f;

        // DirectFire（Tank、直射）: 同じトレーサーだが太く・明るく・やや長持ち＋一回り大きいフラッシュ。
        private const float DirectFireTracerDuration = 0.15f;
        private const float DirectFireTracerWidth = 0.35f;
        private const float DirectFireFlashSize = 2.2f;

        // IndirectFire（Artillery、曲射）: Fromから放物線を飛ぶ光跡（トレーサー、Task43でモデル球から変更）
        // ＋着弾時の短い噴煙。
        private const float ArcTravelDuration = 1.2f;
        private const float ArcApexRatio = 0.25f;   // 頂点高さ = 水平距離 × この比率
        private const float ArcApexMin = 4f;
        private const float ArcApexMax = 120f;
        // Task43: 光跡（トレーサー）の見た目の太さと、狙う世界座標系での長さ（仕様の12〜16の中間）。
        // 実際の遅延(TrailLagT)は経路長からこの長さに近づくよう逆算する（SpawnArc参照）ため、
        // 短距離・長距離どちらの砲撃でもおおむね一定の長さの光跡に見える。
        private const float ArcTrailWidth = 0.4f;
        private const float ArcTrailLength = 14f;
        private const float ArcTrailMinLagT = 0.02f;
        private const float ArcTrailMaxLagT = 0.3f;
        private const float ImpactPuffDuration = 0.3f;
        private const float ImpactPuffSize = 3.5f;

        // 暖色系固定（勢力色でチントしない）。
        private static readonly Color GunfireColor = new Color(1f, 0.92f, 0.55f);     // 暖かい黄白色
        private static readonly Color DirectFireColor = new Color(1f, 0.75f, 0.25f);  // より濃いオレンジ（直射の重み）
        private static readonly Color FlashColor = new Color(1f, 0.95f, 0.8f);
        // Task43: 曲射砲弾の光跡色。DirectFireより深いオレンジにして、遠目でも銃撃/直射と見分けやすくする
        // （勢力色でチントしないのは他のトレーサーと同じ方針）。
        private static readonly Color ArcTrailColor = new Color(1f, 0.55f, 0.15f);
        private static readonly Color PuffColor = new Color(0.55f, 0.5f, 0.45f);

        private enum Phase { Tracer, ArcTravel, ImpactPuff }

        private class Effect
        {
            public GameObject Root;
            public Phase Phase;
            public float Elapsed;
            public float Duration;

            // Tracer(Gunfire/DirectFire)専用、およびArcTravel専用（Task43: 光跡トレーサーとして共用）。
            public LineRenderer Line;
            public float InitialWidth;

            // Tracerのマズルフラッシュ、またはImpactPuffの噴煙（役割はPhaseで決まる）を指す共有transform。
            // Task43: ArcTravel中はもう使わない（曲射砲弾の「弾」表現はLineに置き換わった）。
            public Transform FlashOrShell;
            public float InitialFlashSize;

            // ArcTravel専用。
            public Vector3 From;
            public Vector3 To;
            public float ApexHeight;
            /// <summary>光跡の尾が頭からどれだけ遅れるか（t=0..1の弧パラメータ上の遅延量、Task43）。
            /// SpawnArcで経路長から逆算し、ショットの距離によらずおおむね一定の世界座標長に見えるようにする。</summary>
            public float TrailLagT;
        }

        private static readonly List<Effect> _effects = new List<Effect>();

        /// <summary>Task43: 銃撃3点バーストのうち、まだ発射していない後続弾を実時間で待たせておく
        /// キュー。Update()内でブロッキングなしに実時間を消費して進める（乱数不使用・sim tickとは無関係、
        /// あくまでGame層の見た目専用の演出）。</summary>
        private class PendingBurst
        {
            public Vector3 From;
            public Vector3 To;
            public int RemainingRounds;
            public float TimeUntilNextRound;
        }

        private static readonly List<PendingBurst> _pendingBursts = new List<PendingBurst>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _gunfireMaterial;
        private static Material _directFireMaterial;
        private static Material _flashMaterial;
        private static Material _arcTrailMaterial;
        private static Material _puffMaterial;

        /// <summary>
        /// 1tick分のShotEventからエフェクトを生成する（メインスレッド専用）。
        /// MaxLiveEffectsに達していれば、それ以降のshotsは静かに無視する（例外にしない）。
        /// </summary>
        public static void Spawn(List<ShotEvent> shots)
        {
            if (shots == null || shots.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = 0; i < shots.Count; i++)
                {
                    if (_effects.Count >= MaxLiveEffects) break;
                    SpawnOne(shots[i], cameraPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.Spawn error: " + e);
            }
        }

        /// <summary>生存中の全エフェクトと、実行待ちのバースト後続弾(Task43)を実時間(realDeltaTime)で
        /// 進める（メインスレッド専用）。ArcTravelはDurationに達すると新規GameObjectを作らず、同じ弾を
        /// 着弾噴煙(ImpactPuff)へ転生させてから継続する。</summary>
        public static void Update(float realDeltaTime)
        {
            if (_effects.Count == 0 && _pendingBursts.Count == 0) return;

            try
            {
                for (int i = _effects.Count - 1; i >= 0; i--)
                {
                    Effect fx = _effects[i];
                    if (fx.Root == null) { _effects.RemoveAt(i); continue; }

                    fx.Elapsed += realDeltaTime;

                    switch (fx.Phase)
                    {
                        case Phase.Tracer: StepTracer(fx); break;
                        case Phase.ArcTravel: StepArcTravel(fx); break;
                        case Phase.ImpactPuff: StepImpactPuff(fx); break;
                    }

                    if (fx.Elapsed < fx.Duration) continue;

                    if (fx.Phase == Phase.ArcTravel)
                    {
                        TransitionToImpactPuff(fx);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _effects.RemoveAt(i);
                    }
                }

                AdvancePendingBursts(realDeltaTime);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.Update error: " + e);
            }
        }

        /// <summary>Task43: 銃撃3点バーストの2/3発目を、ブロッキングせず実時間の経過だけで進める。
        /// GunfireBurstRoundGapごとに1発ずつ、既存のSpawnTracerで新規トレーサーを生成する。
        /// MaxLiveEffectsに達している間は生成だけを静かにスキップする（キューの消化自体は止めない、
        /// 大規模乱戦で待ち行列が際限なく伸び続けないようにするため）。</summary>
        private static void AdvancePendingBursts(float realDeltaTime)
        {
            if (_pendingBursts.Count == 0) return;

            for (int i = _pendingBursts.Count - 1; i >= 0; i--)
            {
                PendingBurst b = _pendingBursts[i];
                b.TimeUntilNextRound -= realDeltaTime;
                if (b.TimeUntilNextRound > 0f) continue;

                if (_effects.Count < MaxLiveEffects)
                {
                    SpawnTracer(b.From, b.To, GunfireTracerDuration, GunfireTracerWidth, GunfireFlashSize,
                        GetGunfireMaterial());
                }

                b.RemainingRounds--;
                if (b.RemainingRounds <= 0)
                {
                    _pendingBursts.RemoveAt(i);
                }
                else
                {
                    b.TimeUntilNextRound += GunfireBurstRoundGap;
                }
            }
        }

        /// <summary>生存中の全エフェクトと待機中のバースト後続弾(Task43)を破棄する
        /// （レベルアンロード時、メインスレッド専用）。キャッシュ済みマテリアルはGameObjectではないため
        /// 破棄しない（UnitMaterialFactoryと同じ扱い、次セッションでも使い回せる）。</summary>
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
                ModConfig.LogError("CombatFx.DestroyAll error: " + e);
            }
            finally
            {
                _effects.Clear();
                _pendingBursts.Clear(); // Task43: レベルアンロード後に旧セッションの後続弾が漏れて発射されないように。
            }
        }

        private static void SpawnOne(ShotEvent e, Vector3? cameraPos)
        {
            Vector3 from = new Vector3(e.From.X, e.From.Y, e.From.Z);
            Vector3 to = new Vector3(e.To.X, e.To.Y, e.To.Z);

            // Task43: 発射/着弾位置を地面レベルからモデル中央の高さへ持ち上げる。攻撃側(From)は
            // AttackerIdの、着弾側(To)はTargetIdの見た目の高さを使う。TargetId==0は基地（または
            // 不明な対象＝論理ユニットではない）を意味し、見た目のルックアップを試みず既定値を使う。
            from.y += ResolveAttackerMuzzleHeight(e.AttackerId);
            to.y += ResolveTargetMuzzleHeight(e.TargetId);

            if (cameraPos.HasValue)
            {
                Vector3 mid = (from + to) * 0.5f;
                float distSqr = (mid - cameraPos.Value).sqrMagnitude;
                if (distSqr > MaxSpawnDistanceFromCamera * MaxSpawnDistanceFromCamera) return;
            }

            switch (e.Kind)
            {
                case ShotKind.Gunfire:
                    // Task43: 銃撃は3点バースト。1発目はここで即座に、2/3発目はGunfireBurstRoundGap
                    // 間隔で実時間ベースに遅延させる（AdvancePendingBursts、Updateからブロッキングなしで進行）。
                    SpawnTracer(from, to, GunfireTracerDuration, GunfireTracerWidth, GunfireFlashSize,
                        GetGunfireMaterial());
                    _pendingBursts.Add(new PendingBurst
                    {
                        From = from,
                        To = to,
                        RemainingRounds = GunfireBurstRounds - 1,
                        TimeUntilNextRound = GunfireBurstRoundGap
                    });
                    break;
                case ShotKind.DirectFire:
                    SpawnTracer(from, to, DirectFireTracerDuration, DirectFireTracerWidth, DirectFireFlashSize,
                        GetDirectFireMaterial());
                    break;
                case ShotKind.IndirectFire:
                    SpawnArc(from, to);
                    break;
            }
        }

        /// <summary>攻撃側(From)の見た目の高さ（Task43）。attackerIdが0（論理ユニットでない）か、
        /// 見た目がまだ無い（生成前/破棄済み）場合はDefaultMuzzleHeightにフォールバックする。</summary>
        private static float ResolveAttackerMuzzleHeight(uint attackerId)
        {
            if (attackerId != 0)
            {
                float offset;
                if (UnitVisuals.TryGetMuzzleOffset(attackerId, out offset)) return offset;
            }
            return DefaultMuzzleHeight;
        }

        /// <summary>着弾側(To)の見た目の高さ（Task43）。targetId==0は基地（または不明な対象）を意味し
        /// 見た目のルックアップを試みずBaseTargetHeightを使う。targetId!=0だが見た目が無い場合
        /// （対象ユニットが死亡・未生成等）はDefaultMuzzleHeightにフォールバックする。</summary>
        private static float ResolveTargetMuzzleHeight(uint targetId)
        {
            if (targetId == 0) return BaseTargetHeight;

            float offset;
            if (UnitVisuals.TryGetMuzzleOffset(targetId, out offset)) return offset;
            return DefaultMuzzleHeight;
        }

        private static void SpawnTracer(Vector3 from, Vector3 to, float duration, float width, float flashSize,
            Material tracerMaterial)
        {
            if (tracerMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontShotFx");
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = tracerMaterial;
                line.useWorldSpace = true;
                line.SetVertexCount(2);
                line.SetPosition(0, from);
                line.SetPosition(1, to);
                line.SetWidth(width, width * 0.4f);

                Transform flash = CreateSmallSphere(go.transform, from, flashSize, GetFlashMaterial());

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.Tracer,
                    Elapsed = 0f,
                    Duration = duration,
                    Line = line,
                    InitialWidth = width,
                    FlashOrShell = flash,
                    InitialFlashSize = flashSize
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnTracer error: " + e);
            }
        }

        /// <summary>Task43: 曲射砲弾を、球のモデルではなく銃撃のような光跡（トレーサー）として描く。
        /// LineRenderer1本の頭(head)を放物線上のtに沿って進め、尾(tail)をTrailLagTぶん遅らせて追従させる
        /// ことで、短いストリークが弧を描いて飛んでいくように見せる（StepArcTravel参照）。</summary>
        private static void SpawnArc(Vector3 from, Vector3 to)
        {
            Material trailMaterial = GetArcTrailMaterial();
            if (trailMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontShotFxArc");
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = trailMaterial;
                line.useWorldSpace = true;
                line.SetVertexCount(2);
                line.SetWidth(ArcTrailWidth, ArcTrailWidth);
                // 初回StepArcTravelまでの1フレーム、伸び切った光跡が一瞬見えないよう発射点で頭と尾を揃えておく。
                line.SetPosition(0, from);
                line.SetPosition(1, from);

                float horizontalDist = Mathf.Sqrt((to.x - from.x) * (to.x - from.x) + (to.z - from.z) * (to.z - from.z));
                float apex = Mathf.Clamp(horizontalDist * ArcApexRatio, ArcApexMin, ArcApexMax);

                // 経路長（水平距離＋弧による上乗せの粗い見積もり）から、光跡がおおむねArcTrailLength
                // （世界座標の長さ）に見えるよう尾の遅延量(t換算)を逆算する。近距離/遠距離どちらでも
                // 極端に間延び/短縮しすぎないよう安全域にクランプする。
                float approxPathLength = Mathf.Max(horizontalDist + apex, 1f);
                float trailLagT = Mathf.Clamp(ArcTrailLength / approxPathLength, ArcTrailMinLagT, ArcTrailMaxLagT);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.ArcTravel,
                    Elapsed = 0f,
                    Duration = ArcTravelDuration,
                    Line = line,
                    From = from,
                    To = to,
                    ApexHeight = apex,
                    TrailLagT = trailLagT
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnArc error: " + e);
            }
        }

        /// <summary>クリック選択のraycastを邪魔しないよう、Colliderを無効化した小さな球を作る
        /// （マズルフラッシュ／曳光弾／着弾噴煙で共用）。</summary>
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

        private static void StepTracer(Effect fx)
        {
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            if (fx.Line != null)
            {
                float w = Mathf.Lerp(fx.InitialWidth, 0f, t);
                fx.Line.SetWidth(w, w * 0.4f);
            }
            if (fx.FlashOrShell != null)
            {
                float s = Mathf.Lerp(fx.InitialFlashSize, 0f, t);
                fx.FlashOrShell.localScale = new Vector3(s, s, s);
            }
        }

        /// <summary>放物線上のパラメータt(0=発射, 1=着弾)における世界座標を返す（Task43:
        /// StepArcTravelが光跡の頭・尾の両方でこれを呼ぶための共通ヘルパー）。</summary>
        private static Vector3 ArcPositionAt(Effect fx, float t)
        {
            t = Mathf.Clamp01(t);
            Vector3 pos = Vector3.Lerp(fx.From, fx.To, t);
            // 4*apex*t*(1-t): t=0(発射)/1(着弾)で0、t=0.5でapex高さになる標準的な放物線補間。
            pos.y += 4f * fx.ApexHeight * t * (1f - t);
            return pos;
        }

        /// <summary>Task43: 光跡（LineRenderer）の頭をt、尾をt-TrailLagTの弧上の位置へ進める。
        /// 尾が頭より前に出ないようtailTを0未満にはしない（発射直後は頭と尾が同じ発射点に収束する）。</summary>
        private static void StepArcTravel(Effect fx)
        {
            if (fx.Line == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            float tailT = Mathf.Max(0f, t - fx.TrailLagT);
            fx.Line.SetPosition(1, ArcPositionAt(fx, t));
            fx.Line.SetPosition(0, ArcPositionAt(fx, tailT));
        }

        private static void StepImpactPuff(Effect fx)
        {
            if (fx.FlashOrShell == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            float s = Mathf.Lerp(fx.InitialFlashSize, 0f, t);
            fx.FlashOrShell.localScale = new Vector3(s, s, s);
        }

        /// <summary>着弾した光跡(Line)を消し、代わりに小さな着弾噴煙(puff)を1つだけ生成して継続する
        /// （Task43: 曲射砲弾の「弾」表現が球からトレーサーに変わったため、旧実装のように既存の球を
        /// 使い回すのではなく、この時点で初めて着弾フラッシュ用の球を作る。曲射砲弾自体は最後まで球にしない）。
        /// 転生に失敗した場合はエフェクトを取り残さないよう即座に破棄する。</summary>
        private static void TransitionToImpactPuff(Effect fx)
        {
            try
            {
                if (fx.Line != null)
                {
                    fx.Line.SetWidth(0f, 0f);
                    fx.Line.enabled = false;
                }

                Transform puff = CreateSmallSphere(fx.Root.transform, fx.To, ImpactPuffSize, GetPuffMaterial());
                fx.FlashOrShell = puff;
                fx.InitialFlashSize = ImpactPuffSize;

                fx.Phase = Phase.ImpactPuff;
                fx.Elapsed = 0f;
                fx.Duration = ImpactPuffDuration;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.TransitionToImpactPuff error: " + e);
                if (fx.Root != null) UnityEngine.Object.Destroy(fx.Root);
                _effects.Remove(fx);
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
                    ModConfig.LogError("CombatFx: シェーダー解決に失敗（Standard/Legacy Shaders/Diffuse/Diffuse 全滅）、発砲エフェクトは描画されません");
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.ResolveShader error: " + e);
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
                ModConfig.LogError("CombatFx.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetGunfireMaterial()
        {
            if (_gunfireMaterial == null) _gunfireMaterial = BuildMaterial(GunfireColor);
            return _gunfireMaterial;
        }

        private static Material GetDirectFireMaterial()
        {
            if (_directFireMaterial == null) _directFireMaterial = BuildMaterial(DirectFireColor);
            return _directFireMaterial;
        }

        private static Material GetFlashMaterial()
        {
            if (_flashMaterial == null) _flashMaterial = BuildMaterial(FlashColor);
            return _flashMaterial;
        }

        private static Material GetArcTrailMaterial()
        {
            if (_arcTrailMaterial == null) _arcTrailMaterial = BuildMaterial(ArcTrailColor);
            return _arcTrailMaterial;
        }

        private static Material GetPuffMaterial()
        {
            if (_puffMaterial == null) _puffMaterial = BuildMaterial(PuffColor);
            return _puffMaterial;
        }
    }
}
