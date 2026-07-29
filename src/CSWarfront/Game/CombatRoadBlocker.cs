using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// 戦闘域（State.CombatZones）に応じて道路セグメントを一時封鎖し、民間の車両/歩行者を迂回させる
    /// （Task54）。simスレッド専用（MilitaryManager.OnSimTickから呼ばれる想定、RoadGraphBuilderと同じ
    /// スレッド境界）。
    ///
    /// Part 0で検証した内容（Assembly-CSharp.dllをilspycmdで逆コンパイルして確認）:
    ///  - 当初案の NetSegment.Flags.Blocked は使わない。理由:
    ///     (a) PathFind.ProcessItemCosts（1087行）では Blocked は「車線がVehicle/TransportVehicleの
    ///         場合にのみ comparisonValue へ+0.1する弱いペナルティ」でしかなく、ハード除外ではない。
    ///         しかも歩行者/自転車の経路探索（ProcessItemPedBicycle）はBlockedを一切参照しないため、
    ///         歩行者はまったく迂回しない。
    ///     (b) RoadBaseAI.SimulationStep（1411〜1421行）が、そのセグメントの m_trafficBuffer が
    ///         ushort.MaxValue（＝実際の渋滞で身動きが取れない状態）でない限り、Blockedフラグを
    ///         毎回自動でクリアする。この処理はセグメントごとに約256フレーム＝約0.094ゲーム内時間に
    ///         一度走る（NetManager.SimulationStepImplの `m_currentFrameIndex & 0xFF` によるグループ
    ///         分割）。つまり外部からBlockedを立てても、ゲームが渋滞と判断しない限りすぐ消される。
    ///  - 代わりに NetSegment.Flags.PathFailed を使う。理由:
    ///     (a) PathFind.m_disableMask = Collapsed | PathFailed。ProcessItemCosts冒頭（916行）・
    ///         ProcessItemPedBicycle冒頭（1133行）の両方で「このマスクのビットが立っていたら
    ///         そのセグメントを一切候補にしない」というハード除外。車・歩行者の両方に効く。
    ///     (b) NetSegment/NetAI/RoadBaseAI/NetTool/NetManager/PathManager/PathFindの逆コンパイル結果
    ///         全体を検索したが、PathFailedへ書き込む箇所は一切見つからなかった（enum宣言と
    ///         disableMaskでの参照のみ）。Blocked/Floodedのように毎tick自動で上書きされる競合が無い
    ///         （＝ただし全アセンブリを網羅したわけではないため「見つからなかった」までの保証。
    ///         念のため後述の毎tick再アサートで防御する）。
    ///     (c) PassengerCarAI/CitizenAI（民間車両・市民）はどちらもCreatePathを
    ///         ignoreClosed: false（EventClosed考慮）・既定の各種ignore*: falseで呼ぶため、
    ///         disableMaskの除外を回避する経路（ignoreClosedはEventClosed専用でPathFailedには無関係）
    ///         は無い。
    ///  - 既知の限界（要件通り、危険とは判断しないため採用）: 既にそのセグメントを走行中/横断中の
    ///     車両・歩行者は、PathManagerに「既存の経路を再検証して未実行分を破棄する」仕組みが
    ///     見当たらないため、フラグが立った瞬間に強制で引き返すことはない（次にそのユニットが
    ///     新しい経路計算をする時点から効く）。現実の一時封鎖でも「今渡っている途中の車はそのまま
    ///     渡り切る」のと同じ挙動であり、ZoneLingerHours=2hという十分長い封鎖時間を考えれば
    ///     実用上は迂回として機能する。
    ///  - フラグ変更後は NetManager.UpdateSegment(segmentID) を呼ぶ（NetToolが同種のフラグコピー後に
    ///     採用しているのと同じ慣習。PathFindはNetManagerの生バッファを直接読むためパス探索自体には
    ///     必須ではないが、隣接ノードの更新マーカー等を一貫させるため踏襲する）。
    /// </summary>
    internal static class CombatRoadBlocker
    {
        /// <summary>このMODが道路封鎖に使うフラグ。他Mod/vanilla由来の既存Blockedを一切いじらないため、
        /// 自分が立てたPathFailedビットだけを対象にする（「既にこのビットが立っているセグメントは
        /// 触らない」という所有権チェックと組み合わせて安全側に倒す）。</summary>
        private const NetSegment.Flags BlockFlag = NetSegment.Flags.PathFailed;

        /// <summary>戦闘域の再スキャン間隔（ゲーム内時間）。全セグメントを線形走査するため、
        /// RoadGraphBuilder/CoverMapBuilderの定期再構築と同じ「間引く」パターンを踏襲する。</summary>
        public const float BlockUpdateIntervalHours = 0.25f;

        /// <summary>同時に封鎖するセグメント数の上限（安全弁）。巨大な戦闘域が広大な道路網に
        /// 重なっても、1tickあたりの走査・反映コストとログ量を一定に保つ。</summary>
        public const int MaxBlockedSegments = 400;

        // このMODが現在「自分が封鎖した」と認識しているセグメントID集合（simスレッド専用、ロック不要
        // ＝MilitaryManager.OnSimTickの_stateLock内からのみ触られる）。既にBlockedFlagが立っていた
        // セグメント（＝他Mod/vanilla由来）は絶対にここへ入れない＝絶対に触らない。
        private static readonly HashSet<ushort> _owned = new HashSet<ushort>();

        private static float _accum;

        // 失敗ログの間引き（RoadGraphBuilderと同じパターン）。
        private static bool _failureAlreadyLogged;

        /// <summary>現在このMODが封鎖しているセグメント数（診断・テスト用）。</summary>
        public static int OwnedCount => _owned.Count;

        /// <summary>
        /// MilitaryManager.OnSimTickから毎tick呼ばれる。
        ///  1) 毎tick: 現在所有している封鎖セグメントへBlockFlagを再アサートする（vanilla側に
        ///     見つけられなかった書き込み元がもし存在してもすぐ上書きされるようにするための防御。
        ///     所有セグメントはMaxBlockedSegments以下に抑えられているため毎tickでも軽い）。
        ///  2) BlockUpdateIntervalHoursごと: 全道路セグメントを線形走査して「今あるべき封鎖集合」を
        ///     求め、差分（追加/解除）だけを適用する。
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            try
            {
                if (!Singleton<NetManager>.exists)
                {
                    return; // NetManager未準備。_ownedが空ならここまでで何もしていないので安全。
                }

                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;

                ReassertOwned(nm, segments);

                _accum += dt;
                if (_accum < BlockUpdateIntervalHours) return;
                _accum -= BlockUpdateIntervalHours;
                if (_accum < 0f) _accum = 0f;

                if (state.CombatZones.Zones.Count == 0 && _owned.Count == 0) return; // 何もすることが無い

                NetNode[] nodes = nm.m_nodes.m_buffer;
                HashSet<ushort> desired = ComputeDesired(state, segments, nodes);
                ApplyDelta(nm, segments, desired);

                _failureAlreadyLogged = false;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("CombatRoadBlocker.Advance exception: " + e);
                    _failureAlreadyLogged = true;
                }
            }
        }

        /// <summary>現在の所有セグメントへBlockFlagを毎tick立て直す。既にCreatedでなくなっている
        /// （破壊/削除済みの）セグメントは所有集合から静かに落とす。</summary>
        private static void ReassertOwned(NetManager nm, NetSegment[] segments)
        {
            if (_owned.Count == 0) return;

            List<ushort> stale = null;
            foreach (ushort id in _owned)
            {
                if (id >= segments.Length || (segments[id].m_flags & NetSegment.Flags.Created) == 0)
                {
                    (stale ?? (stale = new List<ushort>())).Add(id);
                    continue;
                }
                segments[id].m_flags |= BlockFlag;
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++) _owned.Remove(stale[i]);
            }
        }

        /// <summary>道路セグメント（RoadGraphBuilderと同じ Service.Road && Created フィルタ）のうち、
        /// 中点（始点/終点ノード位置の平均）がいずれかの戦闘域の半径内に入るものの集合を求める。
        /// MaxBlockedSegmentsに達したら以降は無視する（走査自体は最後まで続けてログの整合を保つ）。</summary>
        private static HashSet<ushort> ComputeDesired(WarState state, NetSegment[] segments, NetNode[] nodes)
        {
            var desired = new HashSet<ushort>();
            IList<CombatZone> zones = state.CombatZones.Zones;
            if (zones.Count == 0) return desired;

            for (int i = 0; i < segments.Length; i++)
            {
                if (desired.Count >= MaxBlockedSegments) break;
                if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                NetInfo info = segments[i].Info;
                if (info == null || info.m_class == null) continue;
                if (info.m_class.m_service != ItemClass.Service.Road) continue;

                ushort startNode = segments[i].m_startNode;
                ushort endNode = segments[i].m_endNode;
                if (startNode >= nodes.Length || endNode >= nodes.Length) continue;
                if ((nodes[startNode].m_flags & NetNode.Flags.Created) == 0) continue;
                if ((nodes[endNode].m_flags & NetNode.Flags.Created) == 0) continue;

                UnityEngine.Vector3 sp = nodes[startNode].m_position;
                UnityEngine.Vector3 ep = nodes[endNode].m_position;
                var mid = new WorldPos((sp.x + ep.x) * 0.5f, (sp.y + ep.y) * 0.5f, (sp.z + ep.z) * 0.5f);

                for (int z = 0; z < zones.Count; z++)
                {
                    if (mid.HorizontalDistanceTo(zones[z].Center) <= zones[z].Radius)
                    {
                        desired.Add((ushort)i);
                        break;
                    }
                }
            }
            return desired;
        }

        /// <summary>所有集合(_owned)を desired へ近づける: 追加が必要なものだけ封鎖し、不要になった
        /// ものだけ解除する。他者が既に立てているPathFailedは絶対に触らない（所有していないビットへは
        /// 書き込まない＝奪わない・消さない）。変化があった時だけ1行サマリをログする。</summary>
        private static void ApplyDelta(NetManager nm, NetSegment[] segments, HashSet<ushort> desired)
        {
            int added = 0;
            int removed = 0;

            // 追加: desiredにあって_ownedに無いもの。
            foreach (ushort id in desired)
            {
                if (_owned.Contains(id)) continue;
                if (id >= segments.Length) continue;
                if ((segments[id].m_flags & BlockFlag) != NetSegment.Flags.None)
                {
                    // 既に(他Mod/vanilla/このMODの前回残骸ではない何かによって)立っている＝所有権を主張しない。
                    continue;
                }
                segments[id].m_flags |= BlockFlag;
                nm.UpdateSegment(id);
                _owned.Add(id);
                added++;
            }

            // 解除: _ownedにあってdesiredに無いもの。
            List<ushort> toRemove = null;
            foreach (ushort id in _owned)
            {
                if (desired.Contains(id)) continue;
                (toRemove ?? (toRemove = new List<ushort>())).Add(id);
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                {
                    ushort id = toRemove[i];
                    if (id < segments.Length)
                    {
                        segments[id].m_flags &= ~BlockFlag;
                        nm.UpdateSegment(id);
                    }
                    _owned.Remove(id);
                    removed++;
                }
            }

            if (added != 0 || removed != 0)
            {
                ModConfig.Log("CombatRoadBlocker: blocked +" + added + " -" + removed + " total " + _owned.Count);
            }
        }

        /// <summary>
        /// セーブ直前に呼ぶ（Task54）。このMODが立てたPathFailedビットをセーブデータへ焼き込まないよう
        /// 一時的に全部クリアする。所有集合(_owned)自体はメモリ上に保持したままにし、
        /// ReblockAfterSaveで同じ集合へ即座に立て直す。呼び出し元（MilitaryManager.SerializeLocked）が
        /// _stateLockを保持したまま呼ぶため、simスレッドとの競合は無い。
        /// </summary>
        public static void UnblockAllForSave()
        {
            try
            {
                if (_owned.Count == 0) return;
                if (!Singleton<NetManager>.exists) return;
                NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                foreach (ushort id in _owned)
                {
                    if (id < segments.Length) segments[id].m_flags &= ~BlockFlag;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.UnblockAllForSave exception: " + e);
            }
        }

        /// <summary>UnblockAllForSaveの直後、シリアライズが終わった後に呼ぶ。_owned集合をそのまま
        /// 使って封鎖を立て直す（セーブは論理状態を変えない一時的な処理として扱う）。</summary>
        public static void ReblockAfterSave()
        {
            try
            {
                if (_owned.Count == 0) return;
                if (!Singleton<NetManager>.exists) return;
                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;
                ReassertOwned(nm, segments);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.ReblockAfterSave exception: " + e);
            }
        }

        /// <summary>
        /// レベルアンロード時（MilitaryManager.Reset()から呼ばれる）：所有している封鎖を全部解除してから
        /// 内部状態をクリアする。レベルがティアダウン中でNetManagerが既に無効化されているケースもあり
        /// うるため、解除に失敗しても（ログするだけで）例外を外へ伝播しない
        /// （アンロード中の解除失敗自体は実害が無い＝これから読み込まれるレベルにこのMODのフラグは
        /// 存在しないため）。
        /// </summary>
        public static void Reset()
        {
            try
            {
                if (_owned.Count > 0 && Singleton<NetManager>.exists)
                {
                    NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                    foreach (ushort id in _owned)
                    {
                        if (id < segments.Length) segments[id].m_flags &= ~BlockFlag;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.Reset: unblock-on-unload failed (harmless, level is tearing down): " + e);
            }
            finally
            {
                _owned.Clear();
                _accum = 0f;
                _failureAlreadyLogged = false;
            }
        }
    }
}
