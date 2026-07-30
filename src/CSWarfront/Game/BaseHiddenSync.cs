using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task71: 勢力別アセットのオーバーレイ（<see cref="BaseVisuals"/>）が表示されている拠点について、
    /// バニラ/既定モデルの建物メッシュを Building.Flags.Hidden で個別に隠す（要件2、
    /// オーバーレイと二重描画（スタッキング）にならないようにする）。
    ///
    /// なぜ Building.Flags.Hidden か（ゲーム本体 Assembly-CSharp.dll を ilspycmd で逆コンパイルして
    /// 確認済み、詳細は task-71-report.md）:
    ///   - Building.RenderInstance の先頭:
    ///     `if ((flags &amp; (Flags.Created | Flags.Deleted | Flags.Hidden)) != Flags.Created) return;`
    ///     によりメッシュ/LOD/props/通知アイコンの描画呼び出しが丸ごとスキップされる。
    ///   - クリック選択のヒット判定（BuildingManager.RayCast → Building.RayCast(buildingID, ray, out t)）
    ///     はレンダリングと完全に独立した、footprint（Width/Length）に対する幾何グリッドラウンド
    ///     キャストであり、Hiddenフラグを一切参照しない（呼び出し元が渡す ignoreFlags に Hidden を
    ///     含めない限り素通りする）。よって選択・BaseInfoPanel・占領（Core側はCS実体のflagsを
    ///     一切見ない）は Hidden を立てても壊れない。
    ///   - PlayAudio のみ Hidden 中は無音になる（既知の軽微な副作用として許容: 見た目を差し替えて
    ///     いる拠点で環境音が消える程度）。
    ///
    /// スレッド境界:
    ///   - <see cref="SetDesired"/> はメインスレッド専用。BaseVisuals（オーバーレイのGameObject
    ///     生成/破棄と同じ箇所）からのみ呼ぶ。CS実体（Building構造体）には一切触れず、
    ///     (baseId, hidden) のペンディングをロックで保護した辞書へ積むだけ
    ///     （BasePlacementWatcher._pendingCreated/_pendingReleasedと全く同じブリッジパターン）。
    ///   - <see cref="ApplyPending"/> はsimスレッド専用（MilitaryManager.OnSimTick、_stateLock
    ///     保持中）から呼び、ペンディングを排出してCS建物バッファへ実際に書き込む。
    ///   - <see cref="IsHiddenApplied"/> はメインスレッド専用。Hiddenが実際にCS建物バッファへ
    ///     反映済みかどうかを _lock 経由で安全に読む（Task75、BaseVisuals参照）。
    ///
    /// Task75（基地二重表示バグの根本原因と修正）:
    ///   実機ログ（output_log.txt）で確認したところ、このMODで実際に配置された基地は全て
    ///   Task74の「Optionsで指定した建物アセット」経路（<see cref="BaseBuildingDesignation"/>）
    ///   で登録されていた（例: Info.name="MilitaryBase_Army.MilitaryBase_Army_Data"、電力タブの
    ///   複製プレハブ名"CSWarfront Military Base"等ではない）。ところが旧実装の
    ///   <see cref="ApplyPending"/> は「対象は必ず WarfrontBasePrefab が登録した自MOD基地プレハブと
    ///   一致するidのみに限定する」保険チェックとして <see cref="WarfrontBasePrefab.TryMatch"/> のみを
    ///   見ており、Task74で追加されたもう一方の正規登録経路（BaseBuildingDesignation）を見ていなかった
    ///   （Task61時点のコメントのまま更新されていなかった）。
    ///   結果、BaseBuildingDesignation経由で登録された基地は BaseVisuals.Sync がオーバーレイを生成し
    ///   SetDesiredで「隠すべき」と要求しても、ApplyPendingのTryMatchが常にfalseを返すため
    ///   Building.Flags.Hidden が永久に立たない＝バニラの実体とオーバーレイが同時に描画され続ける
    ///   （プレイヤーが割り当てたオーバーレイのアセット名と配置に使ったアセット名が一致していれば、
    ///   文字通り「同じ建物」が重なって見える。占領等でそのオーバーレイが破棄されるまで消えない
    ///   ＝ユーザー報告の「一定時間」と一致）。修正: BasePlacementWatcher.ProcessCreatedと全く同じ
    ///   2経路判定（WarfrontBasePrefab.TryMatch → BaseBuildingDesignation.TryMatch）へ揃えた。
    ///
    ///   加えて、これとは独立した理論上の競合（メインスレッドでオーバーレイ生成 → 次のsimスレッド
    ///   tickでHidden反映、の1tick分のギャップ）も閉じる。<see cref="_confirmedHidden"/>
    ///   （ApplyPendingが実際にHiddenを立てた瞬間だけ追加）を新設し、BaseVisuals.Syncは
    ///   IsHiddenAppliedがtrueを返すまでオーバーレイの生成を待つ（先にSetDesired(true)だけ発行し、
    ///   確認が取れてから初めてGameObjectを作る）。これにより「バニラ実体とオーバーレイが同一フレームで
    ///   同時に見える瞬間」が理論上も発生しなくなる。
    /// </summary>
    internal static class BaseHiddenSync
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<ushort, bool> _pending = new Dictionary<ushort, bool>();

        // Task75: ApplyPendingが実際にBuilding.Flags.Hiddenを立てた（かつ、まだ外していない）baseIdの集合。
        // _lock で保護し、メインスレッド（IsHiddenApplied経由）とsimスレッド（ApplyPending経由）の
        // 両方から安全にアクセスできるようにする（_pendingと同じロックを共有、専用ロックを増やさない）。
        // UnhideAllForSave/ReapplyAfterSaveはセーブ処理向けの一時的な物理ビット操作であり、このMODの
        // 「隠したい」という論理的な意図（＝オーバーレイがその基地を代表しているという事実）は変わらない
        // ため、この2メソッドはここを一切更新しない（更新するとセーブ中にオーバーレイが一瞬消える）。
        private static readonly HashSet<ushort> _confirmedHidden = new HashSet<ushort>();

        // Task72: 現在このMODが Building.Flags.Hidden を立てていると認識している建物id集合
        // （simスレッド専用、ロック不要＝MilitaryManager.OnSimTick/SerializeLocked経由の
        // _stateLock内、またはReset()＝レベルアンロード時のメインスレッドからのみ触られる。
        // CombatRoadBlocker._ownedと全く同じ所有権追跡パターン）。ApplyPendingが実際にフラグを
        // 立てた/消した瞬間にだけ更新する。セーブ直前後のクリア/再アサート（UnhideAllForSave/
        // ReapplyAfterSave）とレベルアンロード時の一括解除（Reset）は、この集合を頼りに
        // 「今どの建物が隠れているか」を知る。
        private static readonly HashSet<ushort> _hiddenIds = new HashSet<ushort>();

        /// <summary>メインスレッド専用。次回 <see cref="ApplyPending"/> で反映される「この拠点を
        /// 隠すべきか」の最新の希望状態を記録する（同一tick内に複数回呼ばれても最後の値だけが残る
        /// ＝上書きでよい）。</summary>
        public static void SetDesired(ushort baseId, bool hidden)
        {
            lock (_lock) { _pending[baseId] = hidden; }
        }

        /// <summary>
        /// メインスレッド専用（Task75）。<see cref="SetDesired"/>(baseId, true) で要求した
        /// Hiddenが、simスレッドの <see cref="ApplyPending"/> によって実際にCS建物バッファへ
        /// 反映済みかどうかを返す。BaseVisualsはこれがtrueになるまでオーバーレイのGameObjectを
        /// 生成しない（生成前に隠れていないバニラ実体と一瞬でも同時に見える＝報告された二重表示
        /// バグを、確認が取れるまで待つことで構造的に防ぐ）。
        /// </summary>
        public static bool IsHiddenApplied(ushort baseId)
        {
            lock (_lock) { return _confirmedHidden.Contains(baseId); }
        }

        /// <summary>
        /// simスレッド専用（呼び出し元が _stateLock を保持していること）。ペンディングを排出し、
        /// CS建物バッファの Flags.Hidden ビットへ反映する。対象は必ず WarfrontBasePrefab が登録した
        /// 自MOD基地プレハブと一致するidのみに限定する（他Modや通常建物のidを誤って書き換えない
        /// ための保険。idは常に BasePlacementWatcher.ProcessCreated が TryMatch で確認済みのものだけが
        /// WarState.Bases → BaseVisuals.Sync のスナップショット経由でここに来るため理論上は不要だが、
        /// CS建物バッファへの直接ビット書き込みという性質上、多層防御として維持する）。
        /// </summary>
        public static void ApplyPending()
        {
            Dictionary<ushort, bool> drained;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                drained = new Dictionary<ushort, bool>(_pending);
                _pending.Clear();
            }

            if (!Singleton<BuildingManager>.exists) return;
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            // Task75: このtickで確定した確認状態の変化を集め、Building構造体のロックとは別に
            // 一度だけ_lockを取って反映する（ループ本体を_lock保持中に回さないため）。
            List<ushort> newlyHidden = null;
            List<ushort> newlyUnhidden = null;

            foreach (var kv in drained)
            {
                try
                {
                    ushort id = kv.Key;
                    bool hidden = kv.Value;
                    if (id >= buf.Length) continue;
                    if ((buf[id].m_flags & Building.Flags.Created) == 0) continue; // 既に解体済み等

                    // Task75: 対象は必ずこのMODが登録した自基地idのみに限定する（多層防御、
                    // クラス冒頭コメント参照）。BasePlacementWatcher.ProcessCreatedが基地登録時に
                    // 使う判定と全く同じ2経路（電力タブの複製プレハブ／Optionsで指定した建物アセット）
                    // を両方見る。旧実装はWarfrontBasePrefab.TryMatchのみを見ており、Task74で追加された
                    // BaseBuildingDesignation経由の基地でHiddenが永久に立たない不具合の原因だった。
                    BaseType ignored;
                    if (buf[id].Info == null) continue;
                    bool isOwnBase = WarfrontBasePrefab.TryMatch(buf[id].Info, out ignored) ||
                        BaseBuildingDesignation.TryMatch(buf[id].Info.name, out ignored);
                    if (!isOwnBase) continue;

                    if (hidden)
                    {
                        buf[id].m_flags |= Building.Flags.Hidden;
                        _hiddenIds.Add(id);
                        (newlyHidden ?? (newlyHidden = new List<ushort>())).Add(id);
                    }
                    else
                    {
                        buf[id].m_flags &= ~Building.Flags.Hidden;
                        _hiddenIds.Remove(id);
                        (newlyUnhidden ?? (newlyUnhidden = new List<ushort>())).Add(id);
                    }
                }
                catch (Exception e)
                {
                    ModConfig.LogError("BaseHiddenSync.ApplyPending: base " + kv.Key + " error: " + e);
                }
            }

            if (newlyHidden != null || newlyUnhidden != null)
            {
                lock (_lock)
                {
                    if (newlyHidden != null)
                        for (int i = 0; i < newlyHidden.Count; i++) _confirmedHidden.Add(newlyHidden[i]);
                    if (newlyUnhidden != null)
                        for (int i = 0; i < newlyUnhidden.Count; i++) _confirmedHidden.Remove(newlyUnhidden[i]);
                }
            }
        }

        /// <summary>
        /// セーブ直前に呼ぶ（Task72）。このMODが立てたHiddenビットをセーブデータへ焼き込まないよう
        /// 一時的に全部クリアする。<see cref="_hiddenIds"/> 自体はメモリ上に保持したままにし、
        /// <see cref="ReapplyAfterSave"/> で同じ集合へ立て直す。呼び出し元
        /// （MilitaryManager.SerializeLocked）が _stateLock を保持したまま呼ぶため、simスレッドとの
        /// 競合は無い。CombatRoadBlocker.UnblockAllForSaveと全く同じパターン。
        /// </summary>
        public static void UnhideAllForSave()
        {
            try
            {
                if (_hiddenIds.Count == 0) return;
                if (!Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                foreach (ushort id in _hiddenIds)
                {
                    if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Hidden;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.UnhideAllForSave exception: " + e);
            }
        }

        /// <summary>
        /// UnhideAllForSaveで外したHiddenビットを立て直す（Task72）。
        ///
        /// 重要: このメソッドは「WarStateSerializer.Serializeの直後」ではなく、必ず
        /// 「バニラのBuildingManager.Data.Serializeが実際にBuilding.m_flagsをストリームへ書き終えた後」
        /// に呼ばれるよう、呼び出し元（MilitaryManagerPersistence.SerializeLocked）が
        /// Singleton&lt;SimulationManager&gt;.instance.AddAction で次のアクションとして遅延実行する
        /// 契約になっている（ilspycmdでSimulationManager.Data.Serialize/LoadingManager.SaveSimulationData
        /// を逆コンパイルして確認した実際のセーブ順序に基づく。詳細はSerializeLockedのコメント参照）。
        /// 建物が解体済み（Created落ち）になっていれば、それは静かに諦めて集合から落とす
        /// （CombatRoadBlocker.ReassertOwnedのstale処理と同じ）。
        /// </summary>
        public static void ReapplyAfterSave()
        {
            try
            {
                if (_hiddenIds.Count == 0) return;
                if (!Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                List<ushort> stale = null;
                foreach (ushort id in _hiddenIds)
                {
                    if (id >= buf.Length || (buf[id].m_flags & Building.Flags.Created) == 0)
                    {
                        (stale ?? (stale = new List<ushort>())).Add(id);
                        continue;
                    }
                    buf[id].m_flags |= Building.Flags.Hidden;
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++) _hiddenIds.Remove(stale[i]);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.ReapplyAfterSave exception: " + e);
            }
        }

        /// <summary>
        /// レベルアンロード時（MilitaryManager.Reset()から呼ばれる）：このMODがHiddenにした建物を
        /// 全部素の見た目へ戻してから内部状態をクリアする（Task72）。CombatRoadBlocker.Resetと同じ
        /// 理由・同じ形: Reset()はメインスレッド（OnLevelUnloading経由）から呼ばれ、以後simスレッドの
        /// OnSimTick（延いてはApplyPendingによる_pendingの排出）はもう回らない可能性が高いため、
        /// SetDesiredで_pendingへ積むだけでは戻す保証にならない。ここでCS建物バッファへ直接書き込む。
        /// レベルがティアダウン中でBuildingManagerが既に無効化されているケースもありうるため、
        /// 解除に失敗しても（ログするだけで）例外を外へ伝播しない。
        /// </summary>
        public static void Reset()
        {
            try
            {
                if (_hiddenIds.Count > 0 && Singleton<BuildingManager>.exists)
                {
                    Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    foreach (ushort id in _hiddenIds)
                    {
                        if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Hidden;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.Reset: unhide-on-unload failed (harmless, level is tearing down): " + e);
            }
            finally
            {
                _hiddenIds.Clear();
                lock (_lock) { _pending.Clear(); _confirmedHidden.Clear(); } // Task75
            }
        }
    }
}
