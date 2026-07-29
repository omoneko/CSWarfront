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
    /// </summary>
    internal static class BaseHiddenSync
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<ushort, bool> _pending = new Dictionary<ushort, bool>();

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

            foreach (var kv in drained)
            {
                try
                {
                    ushort id = kv.Key;
                    bool hidden = kv.Value;
                    if (id >= buf.Length) continue;
                    if ((buf[id].m_flags & Building.Flags.Created) == 0) continue; // 既に解体済み等

                    BaseType ignored;
                    if (buf[id].Info == null || !WarfrontBasePrefab.TryMatch(buf[id].Info, out ignored)) continue;

                    if (hidden)
                    {
                        buf[id].m_flags |= Building.Flags.Hidden;
                        _hiddenIds.Add(id);
                    }
                    else
                    {
                        buf[id].m_flags &= ~Building.Flags.Hidden;
                        _hiddenIds.Remove(id);
                    }
                }
                catch (Exception e)
                {
                    ModConfig.LogError("BaseHiddenSync.ApplyPending: base " + kv.Key + " error: " + e);
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
                lock (_lock) { _pending.Clear(); }
            }
        }
    }
}
