using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーがOptions指定建物（BaseBuildingDesignation）として配置/解体した軍事基地建物を検知し、
    /// 対応する論理 MilitaryBase を作成/削除する（Task18、Task82で電力タブの複製プレハブ経路を撤去し
    /// この経路のみに一本化）。
    /// スレッド注記:
    ///  - CS のイベント（EventBuildingCreated/EventBuildingReleased）はどのスレッドから発火するか
    ///    保証されないため、ハンドラは最小限（idの記録のみ）に留め、CS API呼び出しやWarState操作は
    ///    一切行わない（ここで例外が漏れるとゲーム側が未捕捉ポップアップを出すため try/catch で必ず握る）。
    ///  - 実際の反映（BuildingManagerバッファ読み取り＋WarState更新）は ProcessPending 経由で
    ///    MilitaryManager.OnSimTick（simスレッド、_stateLock保持済み）からのみ行う。
    /// </summary>
    public static class BasePlacementWatcher
    {
        private static bool _subscribed;
        private static readonly object _pendingLock = new object();
        private static readonly List<ushort> _pendingCreated = new List<ushort>();
        private static readonly List<ushort> _pendingReleased = new List<ushort>();

        /// <summary>
        /// Task60: 基地建物の向き（ラジアン、CS Building.m_angle）のキャッシュ。Game/BaseVisuals.cs が
        /// 勢力別モデルのオーバーレイを正しい向きで表示するために使う。書き込みは常にこのクラスの
        /// simスレッド専用メソッド（ProcessCreated、呼び出し元 MilitaryManager.OnSimTick が既に
        /// _stateLock を保持している）から行う——ここで既にCS建物バッファ（Building構造体）を
        /// 読んでいるため、便乗して1回の読み取りで済ませ、メインスレッドから BuildingManager へ
        /// 触れる経路を増やさない（既定の安全な選択：CS実体はsimスレッド専用というルールを維持する）。
        /// 読み取り（<see cref="TryGetAngle"/>）は MilitaryManager.OnMainVisualUpdate が同じ
        /// _stateLock を保持したままスナップショット構築中に呼ぶ想定（Dictionary自体はスレッドセーフで
        /// はないため、呼び出し側の規約でスレッド安全性を担保する）。
        /// </summary>
        private static readonly Dictionary<ushort, float> _baseAngles = new Dictionary<ushort, float>();

        /// <summary>
        /// Task74: 基地登録時（ProcessCreated）に確定した建物の<see cref="BuildingInfo"/>.nameのキャッシュ。
        /// ReconcileBasesの「幽霊基地」判定を、建物実体の消失／id使い回しの検知だけに絞り込むために使う
        /// （以前はOptionsの現在の指定(BaseBuildingDesignation)とInfo.nameを比較しており、登録後に
        /// プレイヤーが指定を変更/解除しただけで、生きている基地がゴースト扱いされ削除される不具合が
        /// あった）。書き込みは常にこのクラスのsimスレッド専用メソッド（ProcessCreated、呼び出し元
        /// MilitaryManager.OnSimTick が既に_stateLockを保持している）から行う——_baseAnglesと同じ箇所・
        /// 同じタイミングで書く（Task82で電力タブの複製プレハブ経由の判定は撤去し、Options指定建物
        /// （BaseBuildingDesignation）経由の一本のmatch判定のみになった）。
        /// 読み取り（ReconcileBases）は同じ_stateLockを保持したまま呼ぶ想定（Dictionary自体はスレッド
        /// セーフではないため、呼び出し側の規約でスレッド安全性を担保する）。
        /// セッション限定キャッシュであり、セーブファイルには含まれない点に注意（セーブロード直後は
        /// 該当基地のエントリが無い＝「まだ一度もProcessCreatedを通っていない」状態になる。この場合の
        /// 扱いはReconcileBasesのコメントを参照）。
        /// </summary>
        private static readonly Dictionary<ushort, string> _baseInfoNames = new Dictionary<ushort, string>();

        /// <summary>冪等。OnLevelLoaded から呼ばれる想定。</summary>
        public static void Subscribe()
        {
            if (_subscribed) return;
            try
            {
                if (!Singleton<BuildingManager>.exists)
                {
                    ModConfig.LogError("BasePlacementWatcher.Subscribe: BuildingManager not ready; skip");
                    return;
                }
                BuildingManager bm = Singleton<BuildingManager>.instance;
                bm.EventBuildingCreated += OnBuildingCreated;
                bm.EventBuildingReleased += OnBuildingReleased;
                _subscribed = true;
                ModConfig.Log("BasePlacementWatcher: subscribed to EventBuildingCreated/EventBuildingReleased");
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Subscribe exception: " + e); }
        }

        /// <summary>冪等。OnLevelUnloading から呼ばれる想定。</summary>
        public static void Unsubscribe()
        {
            if (!_subscribed) return;
            try
            {
                if (Singleton<BuildingManager>.exists)
                {
                    BuildingManager bm = Singleton<BuildingManager>.instance;
                    bm.EventBuildingCreated -= OnBuildingCreated;
                    bm.EventBuildingReleased -= OnBuildingReleased;
                }
            }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.Unsubscribe exception: " + e); }
            finally { _subscribed = false; }
        }

        /// <summary>セッション終了時（MilitaryManager.Reset経由）に持ち越しを防ぐ。</summary>
        public static void ClearPending()
        {
            lock (_pendingLock)
            {
                _pendingCreated.Clear();
                _pendingReleased.Clear();
            }
            // Task60: 呼び出し元（MilitaryManager.Reset）は既に_stateLockを保持しているため、
            // ここで追加のロックは不要（_baseAnglesの書き込みは常にそのロック内、ProcessCreated経由）。
            _baseAngles.Clear();
            // Task74: _baseInfoNamesも同じ規約（_stateLock保持済み、ProcessCreated経由でのみ書き込み）。
            _baseInfoNames.Clear();
        }

        /// <summary>Task60: 指定基地の向き（ラジアン）を返す。呼び出し元は_stateLockを保持していること
        /// （クラス冒頭の<see cref="_baseAngles"/>コメント参照）。まだ一度もProcessCreatedで観測されて
        /// いない基地（理論上は無いはずだが、防御的に）はfalseを返し、呼び出し側は既定角度(0)へ
        /// フォールバックすること。</summary>
        internal static bool TryGetAngle(ushort baseId, out float angleRadians)
        {
            return _baseAngles.TryGetValue(baseId, out angleRadians);
        }

        // CSのイベントハンドラ本体。呼び出しスレッド不明のため、idの記録のみ（例外は必ず握る）。
        private static void OnBuildingCreated(ushort id)
        {
            try { lock (_pendingLock) { _pendingCreated.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingCreated exception: " + e); }
        }

        private static void OnBuildingReleased(ushort id)
        {
            try { lock (_pendingLock) { _pendingReleased.Add(id); } }
            catch (Exception e) { ModConfig.LogError("BasePlacementWatcher.OnBuildingReleased exception: " + e); }
        }

        /// <summary>
        /// simスレッド（MilitaryManager.OnSimTick、呼び出し元が既に _stateLock 保持済み）から呼ぶ。
        /// pending リストを排出し、CS建物バッファを読んで WarState.Bases を更新する。
        /// </summary>
        public static void ProcessPending(WarState state)
        {
            if (state == null) return;

            List<ushort> created = null;
            List<ushort> released = null;
            lock (_pendingLock)
            {
                if (_pendingCreated.Count > 0) { created = new List<ushort>(_pendingCreated); _pendingCreated.Clear(); }
                if (_pendingReleased.Count > 0) { released = new List<ushort>(_pendingReleased); _pendingReleased.Clear(); }
            }

            // デバッグ計装（一時的）: 基地登録が一度も走らない不具合の原因切り分けのため、
            // drain 結果があれば件数をログする。恒久ログにはしない想定。
            if (created != null || released != null)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessPending: drained created=" +
                    (created != null ? created.Count : 0) + " released=" + (released != null ? released.Count : 0));
            }

            // 解放を先に処理する（重要）: CSは建物IDを再利用するため、プレイヤーが解体→同じIDで
            // 再建築した場合、両イベントが同一バッチに入りうる。作成を先に処理すると、まだ残っている
            // 古い基地との重複判定で新しい基地の登録がスキップされ、その直後に古い基地が削除されて
            // 「建物はあるが論理基地が無い」状態（以後どのイベントでも復旧しない）になる。
            if (released != null) ProcessReleased(state, released);
            if (created != null) ProcessCreated(state, created);
        }

        private static void ProcessCreated(WarState state, List<ushort> ids)
        {
            // デバッグ計装（一時的）: 早期returnも含め、なぜ基地が登録されないかを追えるようにする。
            // Task82: 電力タブの複製プレハブ機構（WarfrontBasePrefab）を完全撤去したため、マッチ対象は
            // Optionsで指定した建物アセット(BaseBuildingDesignation)のみになった。指定が1件も無ければ
            // どの建物も基地になり得ないため早期returnする（唯一の回復策はOptionsで基地種別ごとに
            // 建物を指定すること）。
            if (!BaseBuildingDesignation.HasAny)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: no designated buildings; " +
                    "designate a building per base type in Options to place bases; skipping " + ids.Count + " id(s)");
                return;
            }

            if (!Singleton<BuildingManager>.exists)
            {
                ModConfig.Log("BasePlacementWatcher: ProcessCreated: BuildingManager does not exist; skipping " + ids.Count + " id(s)");
                return;
            }
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            foreach (ushort id in ids)
            {
                if (id >= buf.Length)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " out of building buffer range (len=" + buf.Length + ") -> skip");
                    continue;
                }

                Building b = buf[id];
                bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;
                string infoName = b.Info != null ? b.Info.name : null;

                // Task82: どの基地種別（Army/Navy/AirForce/MissileBase）に一致するかは、Optionsで指定した
                // 建物アセット名（BaseBuildingDesignation）とInfo.nameの一致のみで判定する（電力タブの
                // 複製プレハブとの参照/名前一致判定=WarfrontBasePrefab.TryMatchは撤去済み）。
                BaseType matchedType = default(BaseType);
                bool match = infoName != null && BaseBuildingDesignation.TryMatch(infoName, out matchedType);

                if (!flagsCreated)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=False info='" + (infoName ?? "null") +
                        "' match=" + match + " -> skip (not created)");
                    continue;
                }
                if (!match)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + (infoName ?? "null") +
                        "' match=False -> skip (not our prefab)");
                    continue;
                }

                // Task60: この時点でCS建物バッファ（Building構造体、既にbとして読み込み済み）から
                // 基地の向きを取得できる。新規登録・復元済み（existing、下のチェック）のどちらでも
                // 常に更新する——復元時（セーブロード後にEventBuildingCreatedが再発火するケース）は
                // これを existing チェックより前で行わないと、プロセス起動直後は BaseVisuals が
                // 向きを一切知らないままになってしまうため。
                _baseAngles[id] = b.m_angle;
                // Task74: 登録が確定した時点（クローンプレハブ経由・指定建物経由のいずれでもmatch==true）
                // でInfo.nameをキャッシュする。以後のReconcileBasesはこの名前とだけ比較し、Optionsの
                // 現在の指定がどう変わっても影響を受けない（idが同じ建物実体である限り基地は維持される）。
                _baseInfoNames[id] = infoName;

                bool existing = FindBase(state, id) != null; // 冪等: セーブロード直後や重複イベント対策
                if (existing)
                {
                    ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                        "' match=True(" + matchedType + ") existing=True -> skip (already registered)");
                    continue;
                }

                ModConfig.Log("BasePlacementWatcher: id=" + id + " flags-created=True info='" + infoName +
                    "' match=True(" + matchedType + ") existing=False -> REGISTERING");

                Vector3 pos = b.m_position;
                // Task61: 陸軍/海軍/航空、TryMatchが特定した種別でMilitaryBaseを作る（従来は常にArmy固定だった）。
                var mb = new MilitaryBase(id, matchedType, new WorldPos(pos.x, pos.y, pos.z));
                mb.OwnerFactionId = WarfrontSettings.BuildFactionId;
                // Task101（ユーザー要望「塹壕は勢力関係なく使える」）: 塹壕は完全に無所属の地形として
                // 登録する（建設先勢力ドロップダウンの選択も無視。守備ボーナス・歩兵の陣地志向は
                // 元々所有不問で、所有記録だけが残っていたのを廃止）。
                if (matchedType == BaseType.Trench) mb.OwnerFactionId = null;
                // Task101: 種別ごとの既定HP（通常基地500/築城は各種の値、FortificationRules参照）。
                mb.MaxHP = FortificationRules.DefaultMaxHP(matchedType);
                mb.CurrentHP = mb.MaxHP;
                // 新設基地は一定期間占領されない（Task24）：プレイヤーが両陣営の基地を配置し終える前に
                // 一方的に占領されてしまう不具合の対策。
                mb.CaptureGraceHours = MilitaryBase.NewBaseGraceHours;
                state.Bases.Add(mb);

                Faction f = state.FindFaction(WarfrontSettings.BuildFactionId);
                bool isHq = false;
                if (f != null)
                {
                    // Task101: 築城（塹壕・掩蔽壕等）は本拠地(HQ)にしない（HQ=勢力の存続を賭ける
                    // 軍事基地という意味付けを保つ）。
                    if (f.HomeBaseId == null && !FortificationRules.IsFortification(matchedType))
                    {
                        f.HomeBaseId = id;
                        mb.IsHeadquarters = true;
                        isHq = true;
                    }
                }
                else
                {
                    ModConfig.LogError("BasePlacementWatcher: no Faction found for id=" + WarfrontSettings.BuildFactionId +
                        " while registering base id=" + id + "; base added without a valid owning faction record");
                }

                ModConfig.Log("BasePlacementWatcher: base registered id=" + id +
                    " faction=" + WarfrontSettings.BuildFactionId +
                    (isHq ? " (HQ)" : "") +
                    " grace=" + mb.CaptureGraceHours.ToString("0") + "h" +
                    " pos=(" + pos.x + "," + pos.y + "," + pos.z + ")");
            }
        }

        private static void ProcessReleased(WarState state, List<ushort> ids)
        {
            foreach (ushort id in ids)
            {
                MilitaryBase mb = FindBase(state, id);
                if (mb == null) continue;

                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(id); // Task60: 解体済みidの向きキャッシュを持ち越さない
                _baseInfoNames.Remove(id); // Task74: 解体済みidのInfo名キャッシュも持ち越さない

                ModConfig.Log("BasePlacementWatcher: base removed id=" + id +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// ロジック基地の建物（BuildingManagerが管理するCS建物実体）が既に存在しない「幽霊基地」を
        /// 掃除する（Task24）。建物が過去に検知されないまま解体された場合や、古いセーブ由来のケースで、
        /// 論理基地だけがWarState.Basesに残り続け、生産/攻撃対象であり続けてしまう不具合の対策。
        /// simスレッド（MilitaryManager.OnSimTick、呼び出し元が既に_stateLock保持済み）から
        /// スロットル付きで呼ばれる想定。
        /// </summary>
        public static void ReconcileBases(WarState state)
        {
            if (state == null) return;
            // Task82: ProcessCreatedと同じ理由で、Options指定建物(BaseBuildingDesignation)に比較対象が
            // 1件も無ければ続ける意味が無い（電力タブの複製プレハブ経由の判定=WarfrontBasePrefab.
            // IsAnyRegisteredは撤去済み）。
            if (!BaseBuildingDesignation.HasAny) return;
            if (!Singleton<BuildingManager>.exists) return;

            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            // 削除対象を先に集めてから削除する（列挙中にstate.Basesを変更しないため）。
            List<MilitaryBase> ghosts = null;
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase mb = state.Bases[i];
                bool isGhost;
                if (mb.BaseId >= buf.Length)
                {
                    isGhost = true;
                }
                else
                {
                    Building b = buf[mb.BaseId];
                    bool flagsCreated = (b.m_flags & Building.Flags.Created) != 0;

                    if (!flagsCreated || b.Info == null)
                    {
                        // 建物実体が既に無い（解体済み／未生成）→ 幽霊基地として削除（変更なし）。
                        isGhost = true;
                    }
                    else
                    {
                        // Task74: ReconcileBasesの本来の役目は「建物が既に無い」「idが無関係な建物に
                        // 再利用された」の2つだけを捕まえること。Optionsの現在の指定
                        // （BaseBuildingDesignation）とここで比較するのは誤り——プレイヤーが登録後に
                        // 指定を変更/解除しただけで、生きている基地が幽霊扱いされ削除されてしまう
                        // （Task74で報告された不具合）。よって現在の指定は一切見ない。
                        string cachedName;
                        if (_baseInfoNames.TryGetValue(mb.BaseId, out cachedName))
                        {
                            // 登録時（ProcessCreated）に記録したInfo.nameと現在のInfo.nameを比較する
                            // ことで、真のid使い回し（このidが解体後に別の建物種別へ再割当てされた）
                            // だけを検知する。現在の指定がどうであろうと、登録時と同じ建物である限り
                            // ゴースト扱いしない。
                            isGhost = b.Info.name != cachedName;
                        }
                        else
                        {
                            // キャッシュに無い＝このセッションでProcessCreatedを一度も通っていない基地
                            // （典型的にはセーブから復元した直後：_baseInfoNamesはセッション限定キャッシュ
                            // であり、セーブデータには含まれない）。この場合は寛容に扱う：建物実体が
                            // 生きている（flagsCreated && Info!=nullを上で確認済み）こと自体を信頼して
                            // 基地を維持する——セーブされた時点でこの基地は有効だったはずであり、id使い
                            // 回しはCS自身がそのidを一度解放してから再割当てするまで起こり得ない
                            // （flagsCreatedがtrueのまま保たれている限り、このidは解体されていない）。
                            // ここで現在のInfo.nameをキャッシュに補完し、以後は通常のid使い回し検知
                            // （上のブランチ）に合流させる。
                            isGhost = false;
                            _baseInfoNames[mb.BaseId] = b.Info.name;
                        }
                    }
                }
                if (isGhost)
                {
                    if (ghosts == null) ghosts = new List<MilitaryBase>();
                    ghosts.Add(mb);
                }
            }

            if (ghosts == null) return;

            foreach (MilitaryBase mb in ghosts)
            {
                bool wasHq = mb.IsHeadquarters;
                byte? owner = mb.OwnerFactionId;
                RemoveBaseAndReassignHq(state, mb);
                _baseAngles.Remove(mb.BaseId); // Task60: 幽霊基地の向きキャッシュを持ち越さない
                _baseInfoNames.Remove(mb.BaseId); // Task74: 幽霊基地のInfo名キャッシュも持ち越さない
                ModConfig.Log("BasePlacementWatcher: ReconcileBases: removed ghost base id=" + mb.BaseId +
                    " (was HQ=" + wasHq + ", faction=" + owner + ")");
            }
        }

        /// <summary>
        /// 基地を論理状態から取り除き、それが所属勢力のHQだった場合はHomeBaseIdをクリアして
        /// 残る所有基地があれば先頭を昇格する（解体経路・幽霊基地掃除経路の共通処理）。
        /// Eliminatedはここでは設定しない（勢力消滅はCoreのOccupationが決める戦闘結果のため）。
        /// </summary>
        private static void RemoveBaseAndReassignHq(WarState state, MilitaryBase mb)
        {
            state.Bases.Remove(mb);

            if (mb.OwnerFactionId == null) return;
            ReassignHqIfCleared(state, mb.OwnerFactionId.Value, mb.BaseId);
        }

        /// <summary>
        /// factionId の HomeBaseId が baseId を指している場合にそれをクリアし、その勢力がまだ
        /// 所有する他の基地があれば先頭を新HQへ昇格する。指していなければ何もしない（no-op）。
        /// 基地の削除（RemoveBaseAndReassignHq）・所属変更（MilitaryManager.TrySetBaseOwner）の
        /// 両方から共有される、HQ整合性維持の唯一の実装（Task25：ロジック重複を避けるため internal 公開）。
        /// 昇格そのものは Core.FactionStatus.PromoteFirstOwnedBaseToHq を呼ぶ（Task46：
        /// 「所有基地の先頭を新HQにする」ルールを FactionStatus.Refresh と重複させないため）。
        /// 呼び出し元が _stateLock を保持していること。
        /// </summary>
        internal static void ReassignHqIfCleared(WarState state, byte factionId, ushort baseId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null || !f.HomeBaseId.HasValue || f.HomeBaseId.Value != baseId) return;

            f.HomeBaseId = null;
            // この時点で baseId の基地は既に削除済み（RemoveBaseAndReassignHq経由）か、既に
            // factionId以外の所有へ切り替わっている（TrySetBaseOwner経由）ため、
            // PromoteFirstOwnedBaseToHq の所有権チェックが自然にbaseIdを除外する。
            FactionStatus.PromoteFirstOwnedBaseToHq(state, factionId);
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
