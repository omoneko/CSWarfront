using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// メインスレッド専用スナップショット。ユニット1体分の「見た目」を決めるのに必要な最小限の情報のみ。
    /// CSの実体（Vehicle等）は一切含まない値型。
    /// </summary>
    public struct UnitVisualState
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public Vector3 Position;
        /// <summary>Core側 UnitType.AssetPrefabName（Workshopアセット等）。空文字なら既定フォールバックを使う。</summary>
        public string AssetPrefabName;
    }

    /// <summary>
    /// ユニットの見た目を「本物のCS車両」ではなく、素のUnity GameObjectとして自前描画する。
    /// 借用するのはメッシュのみ（VehicleInfo.m_mesh、なければ m_lodMesh）で、AIやTransferManager
    /// 連携は一切引き継がない。これにより FireTruckAI 等サービス車両AI由来のクラッシュ
    /// （TransferManager.RemoveIncomingOffer の配列範囲外アクセス）を根本的に回避する。
    /// 借用元はプレハブ名で解決するため、将来 Workshop カスタムアセットへ差し替えても
    /// （そのアセットがどんなAIを積んでいても）安全に動作する。
    /// マテリアルはCS車両のものを一切借用しない（<see cref="UnitMaterialFactory"/> 参照）。
    /// CS車両マテリアルは専用シェーダーがCS自身のレンダラー由来のper-instanceデータを要求するため、
    /// 素の MeshRenderer に割り当てると不可視/黒になる（実際に発生していた不可視バグの原因）。
    /// 代わりに自前の標準シェーダーマテリアルを勢力ごとに1つ生成・共有し、勢力を色で判別できるようにする。
    ///
    /// Task37: 上記の可視性マーカー立方体・勢力色は「割り当て済みアセットが無いユニット」専用の見た目に
    /// 縮小した。TypeKeyにアセットが割り当てられている場合（UnitMeshSource.TryResolveのfromAssignedProp）は
    /// マーカーを出さず、マテリアルもアセット自身の見た目（<see cref="UnitMaterialFactory.TryGetAssetMaterial"/>）
    /// を使う。クリック選択の当たり判定はマーカーのBoxColliderに代わってルートGameObject自身のBoxColliderで
    /// 提供する（CreateVisual/AttachPropCollider参照）。Task41でプロップ以外（建物/車両/樹木）にも対応した。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （new GameObject / AddComponent / Destroy / transform書込みはUnityのメインスレッド制約）。
    /// sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと。
    /// </summary>
    public static partial class UnitVisuals
    {
        private class VisualEntry
        {
            public GameObject GameObject;
            public Vector3 LastPosition;

            /// <summary>Task43: このユニットのモデルの「中央高さ」（ルートGameObjectのposition、
            /// すなわちユニットの論理座標からの相対Y）。CreateVisual時に1回だけ計算してキャッシュする
            /// （メッシュはビジュアルの生存中変わらないため）。CombatFxが発砲エフェクトの発射/着弾高さを
            /// 地面レベルからこの高さへ持ち上げるために使う（TryGetMuzzleOffset参照）。</summary>
            public float MuzzleOffsetY;

            /// <summary>Task49: 勢力アイコン（小さな球）の子GameObject。WarfrontSettings.ShowFactionIconsが
            /// OFFの間、または生成にまだ成功していない間はnull（毎フレームのUpdateFactionIconが遅延生成/破棄
            /// を担当する）。fromAssignedProp（割り当て済みアセット）ユニットも含め、全ユニットに付く。</summary>
            public GameObject Icon;

            /// <summary>アイコンをルートGameObjectのローカル座標系で置く高さ（Y）。CreateVisual時に
            /// mesh.bounds.max.y + ギャップから1回だけ計算してキャッシュする（MuzzleOffsetYと同じ方針）。</summary>
            public float IconLocalHeightY;

            /// <summary>Task83（ユーザー要望「攻撃するときは攻撃方向を向く」）: 直近の発砲の射撃方向
            /// （水平、正規化済み）。NotifyShotsが発砲イベントから設定し、FacingHoldUntilまでの間
            /// MoveVisualが移動方向ではなくこちらを向きに採用する。</summary>
            public Vector3 FacingDirection;

            /// <summary>射撃方向を向き続ける期限（Time.time基準の実時間）。発砲のたびに更新されるため、
            /// 交戦が続く限り目標の方を向き続け、交戦が終われば数秒で移動方向の向きに戻る。</summary>
            public float FacingHoldUntil;

            /// <summary>Task108（ユーザー報告「ヘリが着陸するとき機体が下を向くのが不自然」）:
            /// 向きを水平成分だけから決めるか。着陸/離陸の垂直移動でも機首は水平のまま保たれる。
            /// 航空ユニット（ヘリ・戦闘機・爆撃機）で true。自爆ドローンは目標へ突っ込む姿勢が
            /// 見た目上重要なので false（従来どおり移動方向そのままを向く）。陸上/海上も false
            /// （坂道でのわずかな前後傾きは地形に沿って見えるので残す）。</summary>
            public bool LevelFlight;

            /// <summary>Task108: 連接表示（軍用貨物列車）の車両GameObject（前から後ろの順）。
            /// null＝従来どおり一体の剛体として描画する。詳細はUnitVisualsTrain.cs。</summary>
            public GameObject[] Cars;

            /// <summary>各車両が先頭から何m後ろを走るか（Carsと同じ並び）。</summary>
            public float[] CarBehindHead;

            /// <summary>先頭が通った軌跡（古い→新しい）。各車両はこの上に配置される。</summary>
            public List<Vector3> Trail;

            /// <summary>Task109: 移動音のループAudioSource（この種別に移動音が無ければnull）。</summary>
            public AudioSource Engine;

            /// <summary>Task109: 直近のフレームで実際に動いたか（移動音の再生判定に使う）。</summary>
            public bool MovedThisFrame;

            /// <summary>Task90: 対空ミサイル接近時の回避機動（視覚上のジンク）の終了時刻
            /// （Time.time基準）。AaMissileFxがNotifyEvadeで設定する。論理位置（Core）は変えず、
            /// 表示位置にだけ減衰する横揺れオフセットを加える。</summary>
            public float EvadeUntil;

            /// <summary>回避機動の横方向（水平・正規化済み）。NotifyEvadeが進行方向と直交する向きに設定する。</summary>
            public Vector3 EvadeDir;
        }

        /// <summary>回避機動の長さ（実秒）と最大振れ幅（m）。</summary>
        private const float EvadeDurationSeconds = 1.2f;
        private const float EvadeAmplitude = 10f;

        /// <summary>発砲後に射撃方向を向き続ける実時間（秒）。交戦中の発砲間隔より長めにして
        /// 「戦闘中はずっと相手を向いている」ように見せる。</summary>
        private const float FacingHoldSeconds = 4f;

        // 可視性マーカー（プリミティブ立方体）の大きさと、地面へ埋まらないための持ち上げ量。
        // Task37: 割り当て済みプロップがある場合はもう使わない（AttachVisibilityMarkerのfromAssignedProp分岐参照）。
        private const float MarkerSize = 8f;
        private const float MarkerHeight = 5f;

        // Task37: 割り当て済みプロップ（マーカー無し）のクリック当たり判定用BoxColliderの最小サイズ。
        // 極小プロップでもクリックできるようにするための下限。
        private const float MinPropColliderSize = 4f;

        private const float MinMoveDeltaForRotation = 0.01f;

        // Task43: TryGetMuzzleOffsetが返す値のクランプ範囲。借用メッシュ（アセットによって大きさが
        // まちまち）が極端に平ら/巨大でも発砲エフェクトの高さが不自然にならないための安全域。
        private const float MinMuzzleOffsetY = 1f;
        private const float MaxMuzzleOffsetY = 20f;

        // Task49: 勢力アイコン（小さな球）をモデル上端からどれだけ浮かせるか、その安全域クランプ。
        // ここで使うのはCreateVisual（下）のみ。スケール関連定数・生成/更新ロジックは
        // UnitVisualsFactionIcon.cs 側の partial class 定義に分離した（500行制限のため、
        // MilitaryManagerUnitCommands.csと同じ方針。privateメンバーでもpartial class間で共有できる）。
        private const float IconGapAboveMesh = 1.5f;
        private const float MinIconLocalHeightY = 2f;
        private const float MaxIconLocalHeightY = 25f;

        private static readonly Dictionary<uint, VisualEntry> _visuals = new Dictionary<uint, VisualEntry>();

        // メッシュ解決不能などで生成に失敗した instance id。毎フレームの再試行とログ連発を防ぐため
        // 一度失敗したidはここに記録し、以後 Sync() でスキップする。スナップショットから消えたら
        // （死亡・削除等）id再利用に備えて解放する（下の stale 処理で _visuals と同じパスで実施）。
        private static readonly HashSet<uint> _failedInstances = new HashSet<uint>();

        // Sync() 実行毎に使い回すワーク領域（GC回避）。
        private static readonly HashSet<uint> _seenIds = new HashSet<uint>();
        private static readonly List<uint> _staleIds = new List<uint>();
        private static readonly List<uint> _staleFailedIds = new List<uint>();

        public static int Count { get { return _visuals.Count; } }

        /// <summary>
        /// raycastヒット先GameObject（子の可視性マーカーである場合を含む）から、それが属する論理ユニットの
        /// InstanceIdを解決する（Task31: Game/UI/UnitSelectionから使用）。本MODのユニット表現に
        /// 属さないヒット（バニラの建物・地形・道路等）はfalseを返す — 呼び出し側はその場合、選択状態を
        /// 変えずバニラのクリック挙動へそのまま委ねること。
        /// </summary>
        public static bool TryGetInstanceId(GameObject go, out uint instanceId)
        {
            instanceId = 0;
            if (go == null) return false;

            UnitVisualTag tag = go.GetComponentInParent<UnitVisualTag>();
            if (tag == null) return false;

            instanceId = tag.InstanceId;
            return true;
        }

        /// <summary>
        /// 指定idの可視表現の「現在の」ワールド座標を返す（メインスレッド専用）。
        /// Task32: UnitInfoPanelがユニットへ追従する際、スナップショット由来の座標ではなく
        /// 実際に描画されているGameObjectのtransform.positionを権威とするために使う
        /// （パネルは「描画されているものそのもの」を追いかけるべきで、スナップショットの
        /// コピー元とはタイミングがずれ得るため）。見た目が未生成/破棄済みならfalseを返す。
        /// </summary>
        public static bool TryGetPosition(uint instanceId, out Vector3 position)
        {
            position = default(Vector3);

            VisualEntry entry;
            if (!_visuals.TryGetValue(instanceId, out entry)) return false;
            if (entry == null || entry.GameObject == null) return false;

            position = entry.GameObject.transform.position;
            return true;
        }

        /// <summary>
        /// 指定idの可視表現の「モデル中央の高さ」を、ユニットの論理座標（position.y）からの相対値で
        /// 返す（メインスレッド専用、Task43）。CombatFxが発砲エフェクトの発射/着弾位置を地面レベルから
        /// 持ち上げるために使う。CreateVisual時に mesh.bounds から1回だけ計算してキャッシュ済みの値
        /// （<see cref="MinMuzzleOffsetY"/>〜<see cref="MaxMuzzleOffsetY"/> にクランプ済み）を返すのみで、
        /// 呼び出しのたびにメッシュへ再アクセスすることはない。見た目が未生成/破棄済みならfalseを返す
        /// （呼び出し側はTask43既定値、例: DefaultMuzzleHeight/BaseTargetHeightへフォールバックすること）。
        /// </summary>
        public static bool TryGetMuzzleOffset(uint instanceId, out float yOffset)
        {
            yOffset = 0f;

            VisualEntry entry;
            if (!_visuals.TryGetValue(instanceId, out entry)) return false;
            if (entry == null || entry.GameObject == null) return false;

            yOffset = entry.MuzzleOffsetY;
            return true;
        }

        /// <summary>
        /// スナップショットに基づき、生成/移動/破棄を宣言的に反映する（メインスレッド専用）。
        /// スナップショットに存在しないid（死亡・削除・未ロード含む）はここで破棄される。
        /// </summary>
        public static void Sync(List<UnitVisualState> snapshot)
        {
            if (snapshot == null) return;

            // Task49: 勢力アイコンの距離スケーリング用にカメラを1回だけ取得する（Camera.mainはタグ検索を
            // 伴いうるため、ユニット数分ではなくフレーム当たり1回に抑える）。見つからない場合はnullのまま
            // 渡し、UpdateFactionIcon側でスケール計算をスキップする（アイコン自体の生成/破棄は継続する）。
            Camera mainCamera = Camera.main;
            Vector3? cameraPos = mainCamera != null ? (Vector3?)mainCamera.transform.position : null;
            UnitEngineAudio.BeginFrame(); // Task109: 移動音の同時再生数カウンタをリセット

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                UnitVisualState s = snapshot[i];
                _seenIds.Add(s.InstanceId);

                try
                {
                    if (_failedInstances.Contains(s.InstanceId))
                    {
                        continue; // 生成不能と判明済み。ログ連発・再試行を避けて次のユニットへ。
                    }

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(s.InstanceId, out entry) || entry.GameObject == null)
                    {
                        entry = CreateVisual(s);
                        if (entry == null)
                        {
                            // CreateVisual内でログ済み（1回のみ）。以後このidはSyncの先頭でスキップされる。
                            _failedInstances.Add(s.InstanceId);
                            continue;
                        }
                        _visuals[s.InstanceId] = entry;
                    }
                    else
                    {
                        MoveVisual(entry, s.Position);
                    }

                    // Task49: 生成/移動どちらの経路でも、この後で毎フレーム呼ぶ（トグルON/OFF・距離変化への
                    // 追従を両方の経路で一元化する）。fromAssignedProp（割り当て済みアセット）ユニットも
                    // 除外しない＝両方で動作する（要件）。
                    UpdateFactionIcon(entry, s.FactionId, mainCamera);

                    // Task109: 移動音（ループ）。移動している間だけ、可聴距離内で鳴らす。
                    UnitEngineAudio.Update(entry.Engine, entry.MovedThisFrame, s.Position, cameraPos);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("UnitVisuals.Sync: failed to update instance " + s.InstanceId + ": " + e);
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

            // スナップショットに無い失敗済みidも解放する（id再利用時に永久ブロックされないように）。
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

        /// <summary>
        /// 現在生成済みの可視表現の InstanceId と実際の描画位置を列挙する（メインスレッド専用、Task48）。
        /// Game/UI/UnitBoxSelection が範囲選択（画面矩形とワールド座標のスクリーン投影の当たり判定）に使う。
        /// 呼び出し側が渡したバッファを Clear() してから詰め直す（GC回避、UnitVisuals.Sync と同じ規約）。
        /// </summary>
        public static void CollectVisible(List<uint> ids, List<Vector3> positions)
        {
            if (ids == null || positions == null) return;
            ids.Clear();
            positions.Clear();
            foreach (var kv in _visuals)
            {
                if (kv.Value == null || kv.Value.GameObject == null) continue;
                ids.Add(kv.Key);
                positions.Add(kv.Value.GameObject.transform.position);
            }
        }

        /// <summary>追跡中の全ビジュアルを破棄する（レベルアンロード時、メインスレッド専用）。</summary>
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
                ModConfig.LogError("UnitVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
                _failedInstances.Clear();
            }
        }

        private static VisualEntry CreateVisual(UnitVisualState s)
        {
            try
            {
                Mesh mesh;
                Material[] builtInMaterials;
                bool fromAssignedProp;
                bool fromBuiltInModel;
                AssetKind resolvedKind;
                string resolvedAssetName;
                if (!UnitMeshSource.TryResolve(s.FactionId, s.TypeKey, s.AssetPrefabName, out mesh, out builtInMaterials, out fromAssignedProp, out fromBuiltInModel, out resolvedKind, out resolvedAssetName))
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " failed to resolve mesh, skipping visual");
                    return null;
                }

                // Task37: 割り当て済みアセット（プロップ/建物/車両/樹木、Task41で拡張）がある場合は
                // アセット自身の見た目（テクスチャ）を維持し、勢力色で塗らない。
                // Task69: 既定(built-in)モデルは、割り当て済みモデルと同様に自分自身の色
                // （builtInMaterials、tools/export_builtin_obj.py 由来モデルの実際のMTL色）で描画し、
                // 勢力色ティントはしない（勢力の識別は既存の勢力アイコンに一本化）。
                // どちらでもない場合のみ、従来通り勢力色の単一マテリアルを使う。
                bool useBuiltInMaterials = fromBuiltInModel && builtInMaterials != null && builtInMaterials.Length > 0;

                Material material = null;
                bool materialOk;
                if (useBuiltInMaterials)
                {
                    materialOk = true; // マテリアルは WarfrontModelProvider.TryGetModel が既に用意済み
                }
                else if (fromAssignedProp)
                {
                    materialOk = UnitMaterialFactory.TryGetAssetMaterial(resolvedKind, resolvedAssetName, out material);
                }
                else
                {
                    materialOk = UnitMaterialFactory.TryGetFactionMaterial(s.FactionId, out material);
                }
                if (!materialOk)
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " failed to create material, skipping visual");
                    return null;
                }

                var go = new GameObject("CSWarfrontUnit_" + s.InstanceId);

                // Task31: クリック選択(UnitSelection)がraycastヒット先から論理ユニットを逆引きできるよう、
                // ルートGameObjectに識別タグを付ける（GameObject→InstanceIdの別辞書は持たない）。
                UnitVisualTag tag = go.AddComponent<UnitVisualTag>();
                tag.InstanceId = s.InstanceId;

                // Task37: メッシュのピボットが底面にない場合、モデルが路面に半分埋まって見えることがある。
                // ルートのtransform.position自体はユニットの論理座標そのもの（垂直オフセットを一切加えない）
                // に保つため、メッシュ描画専用の子("Model")にだけこのオフセットを載せる。
                float pivotOffsetY = -mesh.bounds.min.y;

                // Task43: 「モデル中央の高さ」＝ pivotOffsetY（ルート相対でメッシュの底面をY=0に
                // 合わせる補正）＋ mesh.bounds.center.y（メッシュ自身のローカル空間での中心Y）。
                // pivotOffsetYのおかげでメッシュは常にルートのY=0を底面として描画されるため、この和は
                // 常に「メッシュ高さの半分」＝ルート位置から見たモデル中央の高さになる（メッシュの
                // ピボットが底面/中心/どこにあっても関係なく成立する）。極端なメッシュ（平ら/巨大）に
                // 備えて安全域へクランプする。
                float muzzleOffsetY = Mathf.Clamp(pivotOffsetY + mesh.bounds.center.y, MinMuzzleOffsetY, MaxMuzzleOffsetY);

                // Task49: 勢力アイコンをモデル上端の少し上に置くための高さ。pivotOffsetYのおかげで
                // メッシュは常にルートのY=0を底面として描画されるため、pivotOffsetY + mesh.bounds.max.y が
                // 「モデル上端」のルート相対高さになる。そこへギャップを足し、安全域へクランプする
                // （muzzleOffsetYと同じ考え方）。
                float iconLocalHeightY = Mathf.Clamp(pivotOffsetY + mesh.bounds.max.y + IconGapAboveMesh, MinIconLocalHeightY, MaxIconLocalHeightY);

                GameObject model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.transform.localPosition = new Vector3(0f, pivotOffsetY, 0f);
                MeshFilter filter = model.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                if (useBuiltInMaterials)
                {
                    renderer.sharedMaterials = builtInMaterials;
                }
                else
                {
                    renderer.sharedMaterial = material;
                }

                go.transform.position = s.Position;

                // Task108: 軍用貨物列車は「先頭車（このmesh）＋後続車両」の連接編成として描画する
                // （1両ずつ軌跡上に並べるのでカーブで編成が折れ曲がる。UnitVisualsTrain.cs）。
                GameObject[] cars = null;
                float[] carBehindHead = null;
                if (IsArticulatedType(s.TypeKey))
                    TryBuildTrainCars(go, mesh, out cars, out carBehindHead);

                // Task109: 移動音（この種別に音があれば停止状態のループAudioSourceを付ける）。
                AudioSource engine = UnitEngineAudio.TryAttach(go, s.TypeKey);

                if (fromAssignedProp || fromBuiltInModel)
                {
                    // 要件1: プロップ割り当てがある場合は可視性マーカー立方体を出さない。
                    // Task57: 既定(built-in)モデルも同様（本物のシルエットを持つため、借用メッシュ
                    // 用の保険マーカーはもう不要）。クリック選択の当たり判定は代わりにルートへ
                    // 直接付ける（マーカーが無いため）。
                    AttachPropCollider(go, mesh, pivotOffsetY);
                }
                else
                {
                    // 可視性の保険＆切り分け: CS由来の借用メッシュが環境によって描画されない可能性があるため、
                    // 確実に描画されるプリミティブ（MissileDisasterのフォールバック球と同じ手法）を子に付ける。
                    // これが見えて借用メッシュが見えない場合、原因はメッシュ側だと確定できる。
                    AttachVisibilityMarker(go, material);
                }

                ModConfig.Log("UnitVisuals: created visual for instance " + s.InstanceId + " type=" + s.TypeKey);

                return new VisualEntry
                {
                    GameObject = go,
                    LastPosition = s.Position,
                    MuzzleOffsetY = muzzleOffsetY,
                    IconLocalHeightY = iconLocalHeightY,
                    LevelFlight = IsLevelFlightType(s.TypeKey), // Task108
                    Cars = cars,
                    CarBehindHead = carBehindHead,
                    Engine = engine // Task109
                };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " error: " + e);
                return null;
            }
        }

        /// <summary>
        /// Task37: 割り当て済みプロップ（マーカー無し）用に、ルートGameObjectへ直接BoxColliderを付ける。
        /// マーカー立方体が無くなったため、クリック選択の当たり判定はこれが唯一の手段になる。
        /// メッシュのbounds（"Model"子への pivotOffsetY 適用後のルート相対座標に変換したもの）を元に
        /// サイズ・中心を決め、極小プロップでもクリックできるよう各軸最小 <see cref="MinPropColliderSize"/>
        /// を保証する。isTriggerはfalseのまま、GameObjectのlayerは変更しない（AttachVisibilityMarkerと同じ理由）。
        /// </summary>
        private static void AttachPropCollider(GameObject root, Mesh mesh, float pivotOffsetY)
        {
            try
            {
                BoxCollider col = root.AddComponent<BoxCollider>();
                col.isTrigger = false;

                Vector3 size = mesh.bounds.size;
                size.x = Mathf.Max(size.x, MinPropColliderSize);
                size.y = Mathf.Max(size.y, MinPropColliderSize);
                size.z = Mathf.Max(size.z, MinPropColliderSize);
                col.size = size;

                Vector3 center = mesh.bounds.center;
                center.y += pivotOffsetY; // "Model"子と同じオフセットをルート相対座標に反映
                col.center = center;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachPropCollider error: " + e);
            }
        }

        /// <summary>
        /// ユニットGameObjectに、確実に描画されるプリミティブ立方体を子として付ける（メインスレッド専用）。
        /// 借用メッシュの描画可否に依存せずユニット位置を視認できるようにするための保険。
        /// Task37: 割り当て済みプロップがある場合（fromAssignedProp）はもう呼ばれない
        /// （AttachPropColliderで当たり判定のみ用意する）。既定/未割り当てユニットの見た目保険として残す。
        /// Task31: このマーカーが生成時に持つBoxColliderは破棄せず、そのままクリック選択の当たり判定
        /// として流用する（isTriggerはfalseのまま＝Physics.Raycastで検出可能）。GameObjectのlayerは
        /// 変更しない（layerを変えるとCS側カメラのカリング/レイヤーマスクに影響し、既に解決済みの
        /// 不可視バグを再発させるリスクがあるため）。
        /// </summary>
        private static void AttachVisibilityMarker(GameObject parent, Material material)
        {
            try
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                BoxCollider col = marker.GetComponent<BoxCollider>();
                if (col != null) col.isTrigger = false;

                marker.transform.SetParent(parent.transform, false);
                marker.transform.localPosition = new Vector3(0f, MarkerHeight, 0f);
                marker.transform.localScale = new Vector3(MarkerSize, MarkerSize, MarkerSize);

                MeshRenderer markerRenderer = marker.GetComponent<MeshRenderer>();
                if (markerRenderer != null && material != null) markerRenderer.sharedMaterial = material;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachVisibilityMarker error: " + e);
            }
        }

        /// <summary>Task108: このTypeKeyのユニットは「常に機体を水平に保つ」対象か
        /// （＝向きを移動方向の水平成分だけから決める）。着陸/離陸の垂直移動で機首が真下/真上を
        /// 向く不自然さを避けるためのもので、航空機・ヘリが対象。自爆ドローンは突入姿勢が
        /// 見た目上重要なので対象外。解析できないTypeKeyは従来どおり（false）。</summary>
        private static bool IsLevelFlightType(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            if (category.IsKamikaze()) return false;
            return category == UnitCategory.AirSuperiority
                || category == UnitCategory.TacticalBomber
                || category == UnitCategory.AttackHelicopter
                || category == UnitCategory.TransportHelicopter;
        }

        private static void MoveVisual(VisualEntry entry, Vector3 newPosition)
        {
            if (entry == null || entry.GameObject == null) return;
            Vector3 delta = newPosition - entry.LastPosition;
            // Task109: 移動音の判定は「向き」用の加工（下のLevelFlightによるY成分の除去）より前の
            // 生の移動量で行う——垂直に降下しているだけのヘリも「移動中」として音を鳴らすため。
            float moveSqr = delta.sqrMagnitude;

            // Task90: 対空ミサイル接近中の回避機動。論理位置はCoreのまま、表示位置にだけ
            // 減衰する横揺れ（バンクを切って逃げるジンク）を加える。
            Vector3 displayPosition = newPosition;
            if (Time.time < entry.EvadeUntil)
            {
                float remaining = (entry.EvadeUntil - Time.time) / EvadeDurationSeconds; // 1→0
                float progress = 1f - remaining;
                float sway = Mathf.Sin(progress * Mathf.PI * 3f) * EvadeAmplitude * remaining;
                displayPosition += entry.EvadeDir * sway;
            }
            entry.GameObject.transform.position = displayPosition;

            // Task108: 航空ユニットは向きを水平成分だけから決める（着陸/離陸の垂直移動で機首が
            // 真下/真上を向くのを防ぐ）。水平成分がほぼ無い＝真下へ降りているだけなら向きは
            // 現状維持（最後に飛んでいた方向を向いたまま降りる）。
            if (entry.LevelFlight)
            {
                delta.y = 0f;
            }

            // Task83: 直近に発砲したユニットは移動方向ではなく射撃方向を向く（静止中の交戦でも
            // 相手の方を向くよう、移動デルタの有無に関わらず毎フレーム適用する）。
            if (Time.time < entry.FacingHoldUntil && entry.FacingDirection.sqrMagnitude > 1e-6f)
            {
                entry.GameObject.transform.rotation = Quaternion.LookRotation(entry.FacingDirection);
            }
            else if (delta.sqrMagnitude > MinMoveDeltaForRotation * MinMoveDeltaForRotation)
            {
                entry.GameObject.transform.rotation = Quaternion.LookRotation(delta);
            }

            // Task108: 連接車両（軍用貨物列車）を先頭の軌跡上へ並べ直す。
            if (entry.Cars != null)
                UpdateTrainCars(entry, displayPosition, entry.GameObject.transform.rotation);

            // Task109: 移動音の再生判定に使う（実際に位置が変わったフレームだけ「移動中」とみなす）。
            entry.MovedThisFrame = moveSqr > MinMoveDeltaForRotation * MinMoveDeltaForRotation;

            entry.LastPosition = newPosition;
        }

        /// <summary>Task90: 対空ミサイルが接近した標的機に回避機動（視覚ジンク）を開始させる
        /// （AaMissileFxから、フレア放出と同時に呼ばれる。メインスレッド専用）。
        /// 横方向は現在の機首方向と直交する水平ベクトル。</summary>
        public static void NotifyEvade(uint instanceId)
        {
            try
            {
                VisualEntry entry;
                if (!_visuals.TryGetValue(instanceId, out entry) || entry.GameObject == null) return;

                Vector3 forward = entry.GameObject.transform.forward;
                forward.y = 0f;
                Vector3 side = forward.sqrMagnitude > 1e-4f
                    ? Vector3.Cross(forward.normalized, Vector3.up)
                    : Vector3.right;
                entry.EvadeDir = side;
                entry.EvadeUntil = Time.time + EvadeDurationSeconds;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.NotifyEvade error: " + e);
            }
        }

        /// <summary>Task83: 発砲イベントから射撃方向を拾い、該当ユニットのビジュアルに
        /// 「FacingHoldSecondsの間、射撃方向を向く」指示を与える（メインスレッド専用。
        /// MilitaryManagerVisualsがロック解放後、CombatFx.Spawnと同じスナップショットで呼ぶ）。</summary>
        public static void NotifyShots(System.Collections.Generic.List<ShotEvent> shots)
        {
            try
            {
                for (int i = 0; i < shots.Count; i++)
                {
                    ShotEvent shot = shots[i];
                    if (shot.AttackerId == 0) continue;
                    // Task86: 航空機は常に進行方向を向く（射撃方向を向くと飛行方向と別方向を向いた
                    // まま飛ぶ「横滑り」の見た目になるため。機動はパス航過のすれ違いで表現する）。
                    if (shot.Category.IsAircraft()) continue;

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(shot.AttackerId, out entry)) continue;

                    Vector3 dir = new Vector3(shot.To.X - shot.From.X, 0f, shot.To.Z - shot.From.Z);
                    if (dir.sqrMagnitude < 1e-6f) continue; // 真上/同一地点への射撃は向きを変えない

                    entry.FacingDirection = dir.normalized;
                    entry.FacingHoldUntil = Time.time + FacingHoldSeconds;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.NotifyShots error: " + e);
            }
        }

        // Task49: UpdateFactionIcon/CreateFactionIcon は UnitVisualsFactionIcon.cs（同じ partial class）参照。

        private static void DestroyVisual(uint instanceId)
        {
            try
            {
                VisualEntry entry;
                if (_visuals.TryGetValue(instanceId, out entry))
                {
                    if (entry != null && entry.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(entry.GameObject);
                    }
                    _visuals.Remove(instanceId);
                    ModConfig.Log("UnitVisuals: destroyed visual for instance " + instanceId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.DestroyVisual: instance " + instanceId + " error: " + e);
            }
        }
    }
}
