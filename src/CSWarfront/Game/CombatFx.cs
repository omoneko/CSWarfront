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
    internal static partial class CombatFx
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

        // Task108（ユーザー指摘「砲兵陣地の発射高さがモデルとズレている」）: 築城施設（掩蔽壕/
        // 砲兵陣地）は論理ユニットではないため銃口オフセットを引けず（ShotEvent.AttackerId==0）、
        // 上のDefaultMuzzleHeight(3m)にフォールバックしていた。同梱モデルから実測した高さに置き換える:
        //   Models/Fort_ArtilleryPost.obj … 全高3.6m。中央の榴弾砲の砲身まわりが約2.2〜2.9m
        //   Models/Fort_Bunker.obj        … 全高5.3m（アンテナ込み）。本体の銃眼は約1.1〜1.6m
        // 施設からの射撃は兵科で施設種別が一意に決まる（砲兵陣地=Artillery / 掩蔽壕=Infantry、
        // Core/FortCombatStep.cs の attackerCategory 参照）。別サイズのアセットを指定した場合は
        // この2つの値を調整する。
        private const float ArtilleryPostMuzzleHeight = 2.4f;
        private const float BunkerMuzzleHeight = 1.3f;

        // Gunfire（Infantry/MechInfantry/Apc/DroneInfantry/AntiAir）: 細く短いトレーサー＋小さなマズルフラッシュ。
        // Task43: 1発→3点バースト化に合わせて、1発ごとの表示時間を0.08s→0.06sへわずかに短縮した
        // （バースト間隔0.07sより短く保ち、次弾が出る前に前弾が消え切るようにするため）。
        // Task108（ユーザー指摘「砲撃の光跡が太すぎる。よりリアルに」）: トレーサーの太さとマズル
        // フラッシュの大きさを実物寄りに絞った（銃撃0.15→0.08 / 直射0.35→0.15 / 曲射0.4→0.15、
        // フラッシュも同様に縮小）。遠景で完全に消えない下限としてこの辺りが実用的な落としどころ。
        private const float GunfireTracerDuration = 0.06f;
        private const float GunfireTracerWidth = 0.08f;
        private const float GunfireFlashSize = 0.7f;

        // Task43: 銃撃1回＝3点バースト。1発目は即座に、2/3発目はGunfireBurstRoundGap間隔で
        // 実時間ベースに遅延させて発射する（_pendingBursts、Update内でブロッキングなしに進める）。
        private const int GunfireBurstRounds = 3;
        private const float GunfireBurstRoundGap = 0.07f;

        // DirectFire（Tank、直射）: 同じトレーサーだが太く・明るく・やや長持ち＋一回り大きいフラッシュ。
        private const float DirectFireTracerDuration = 0.15f;
        private const float DirectFireTracerWidth = 0.15f; // Task108: 0.35→0.15
        private const float DirectFireFlashSize = 1.4f;    // Task108: 2.2→1.4

        // IndirectFire（Artillery、曲射）: Fromから放物線を飛ぶ光跡（トレーサー、Task43でモデル球から変更）
        // ＋着弾時の短い噴煙。
        private const float ArcTravelDuration = 1.2f;
        private const float ArcApexRatio = 0.18f;   // 頂点高さ = 水平距離 × この比率（Task108: 0.25→0.18）
        private const float ArcApexMin = 4f;
        private const float ArcApexMax = 120f;

        // Task108（ユーザー報告「砲兵陣地の光跡がものすごくずれて見える／砲口から着弾までの曲線だけで
        // いい」）: 従来は光跡を頂点2個のLineRenderer（頭＝弾、尾＝TrailLagTぶん遅れた点）で描いていた
        // ため、実際に描かれるのは放物線上の2点を結ぶ"弦"であり、弾道の曲線からは大きく外れて見えていた
        // （距離が短いほど弦と曲線のズレが大きい）。ArcSegmentsぶんの折れ線で放物線そのものをなぞり、
        // 発射点から現在の弾位置までを伸ばしていく「曲線」に置き換える。
        private const int ArcSegments = 24;
        /// <summary>Task108: 光跡の太さ。0.4は実物に対して太すぎる（ユーザー指摘）ため細くする。</summary>
        private const float ArcTrailWidth = 0.15f;
        private const float ImpactPuffDuration = 0.3f;
        private const float ImpactPuffSize = 3.5f;

        // Task108（ユーザー要望「曲射にも砲口フラッシュを足す」）: 曲射は従来、光跡が飛び始めるだけで
        // 発射側に何も出ていなかった（直射/銃撃にはSpawnTracerのマズルフラッシュがある）。砲らしい
        // 重さを出すため、直射より一回り大きく・わずかに長く残るフラッシュを発射点に出す。
        private const float ArcMuzzleFlashSize = 1.8f; // Task108: 3.2→1.8（フラッシュの縮小に合わせる）
        private const float ArcMuzzleFlashDuration = 0.18f;

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
            from.y += ResolveAttackerMuzzleHeight(e.AttackerId, e.Category);
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
                    // Task87: 爆撃機はトレーサーではなく爆弾投下モーション（BombFxが落下・着弾爆発まで扱う）。
                    if (e.Category == UnitCategory.TacticalBomber)
                    {
                        BombFx.SpawnDrop(from, to);
                        break;
                    }
                    SpawnTracer(from, to, DirectFireTracerDuration, DirectFireTracerWidth, DirectFireFlashSize,
                        GetDirectFireMaterial());
                    break;
                case ShotKind.IndirectFire:
                    SpawnMuzzleFlash(from, ArcMuzzleFlashSize, ArcMuzzleFlashDuration); // Task108
                    SpawnArc(from, to);
                    break;
                case ShotKind.SamMissile:
                    // Task90: 対空ミサイル。追尾弾体・フレア・回避機動はAaMissileFxが完結して扱う。
                    AaMissileFx.Spawn(from, to, e.TargetId, e.Missed);
                    break;
            }

            // Task51: 兵科別の発砲音再生（実装はCombatFxSound.cs、同じpartial class）。
            PlayShotSound(e, from, cameraPos);
        }

        /// <summary>攻撃側(From)の見た目の高さ（Task43）。見た目がまだ無い（生成前/破棄済み）場合は
        /// DefaultMuzzleHeightにフォールバックする。
        /// Task108: attackerIdが0＝築城施設からの射撃（FortCombatStep）なので、施設のモデルに
        /// 合わせた高さを兵科から引く（上の定数コメント参照）。</summary>
        private static float ResolveAttackerMuzzleHeight(uint attackerId, UnitCategory category)
        {
            if (attackerId != 0)
            {
                float offset;
                if (UnitVisuals.TryGetMuzzleOffset(attackerId, out offset)) return offset;
                return DefaultMuzzleHeight;
            }

            if (category == UnitCategory.Artillery) return ArtilleryPostMuzzleHeight; // 砲兵陣地
            if (category == UnitCategory.Infantry) return BunkerMuzzleHeight;         // 掩蔽壕
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

        /// <summary>Task108: 発射点だけの短命なフラッシュ（光跡を伴わない）。曲射（砲口フラッシュ）用。
        /// Tracerフェーズを流用するが、Lineを持たないためStepTracerは球の縮小だけを進める。
        /// エフェクト総数の上限を超えていれば黙って省略する（見た目の飾りより本体の弾道を優先する）。</summary>
        private static void SpawnMuzzleFlash(Vector3 at, float size, float duration)
        {
            if (_effects.Count >= MaxLiveEffects) return;

            Material flashMaterial = GetFlashMaterial();
            if (flashMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontMuzzleFlash");
                Transform flash = CreateSmallSphere(go.transform, at, size, flashMaterial);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.Tracer,
                    Elapsed = 0f,
                    Duration = duration,
                    Line = null,
                    FlashOrShell = flash,
                    InitialFlashSize = size
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnMuzzleFlash error: " + e);
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
                line.SetVertexCount(ArcSegments + 1);
                line.SetWidth(ArcTrailWidth, ArcTrailWidth);
                // 初回StepArcTravelまでの1フレーム、伸び切った光跡が一瞬見えないよう全頂点を発射点に畳んでおく。
                for (int i = 0; i <= ArcSegments; i++) line.SetPosition(i, from);

                float horizontalDist = Mathf.Sqrt((to.x - from.x) * (to.x - from.x) + (to.z - from.z) * (to.z - from.z));
                float apex = Mathf.Clamp(horizontalDist * ArcApexRatio, ArcApexMin, ArcApexMax);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.ArcTravel,
                    Elapsed = 0f,
                    Duration = ArcTravelDuration,
                    Line = line,
                    From = from,
                    To = to,
                    ApexHeight = apex
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

        /// <summary>Task108: 発射点(t=0)から現在の弾位置(t)までの放物線を、ArcSegmentsぶんの折れ線で
        /// なぞる（＝弾道そのものの曲線が伸びていく）。従来は頂点2個で「弧上の2点を結ぶ弦」を描いており、
        /// 弾道の曲線から大きく外れて見えていた（ユーザー報告「光跡がものすごくずれて見える」）。</summary>
        private static void StepArcTravel(Effect fx)
        {
            if (fx.Line == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            for (int i = 0; i <= ArcSegments; i++)
                fx.Line.SetPosition(i, ArcPositionAt(fx, t * i / ArcSegments));
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
                    ModConfig.LogError("CombatFx: failed to resolve shader (Standard/Legacy Shaders/Diffuse all failed), muzzle flash effects will not render");
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
