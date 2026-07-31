using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーによる手動生産（基地パネルからの発注・取消・自動生産切替）向けの
    /// MilitaryManager 追加メンバー（Task34）。MilitaryManager.cs の500行制限のため分離した
    /// partial class（Task30のBaseUiSnapshotBuilder分離と同じ方針）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによる手動ユニット発注（Task34）。
        /// Core.ManualProduction.TryEnqueue へ _stateLock 内で委譲するだけの薄いラッパー。
        /// State未初期化ならBaseNotFoundを返す（実運用ではEnsureInitialized済みの前提だが、
        /// 呼び出しタイミングに依存しないよう防御的にNULLガードする）。
        /// </summary>
        public static QueueResult TryQueueUnit(ushort baseId, string typeKey)
        {
            lock (_stateLock)
            {
                if (State == null) return QueueResult.BaseNotFound;
                return ManualProduction.TryEnqueue(State, baseId, typeKey);
            }
        }

        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによる手動発注の取消（Task34）。
        /// Core.ManualProduction.TryCancelLast へ _stateLock 内で委譲するだけの薄いラッパー。
        /// </summary>
        public static QueueResult TryCancelLastOrder(ushort baseId)
        {
            lock (_stateLock)
            {
                if (State == null) return QueueResult.BaseNotFound;
                return ManualProduction.TryCancelLast(State, baseId);
            }
        }

        /// <summary>
        /// 基地情報パネルから呼ばれる、基地のAI自動生産ON/OFF切替（Task34）。
        /// </summary>
        /// <returns>baseId の基地が見つからない場合は false。</returns>
        public static bool TrySetAutoProduce(ushort baseId, bool value)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId != baseId) continue;
                    State.Bases[i].AutoProduce = value;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 基地情報パネルから呼ばれる、ミサイル基地の自動発射ON/OFF切替（Task90）。
        /// OFFの間はMissileDoctrine（AIの自動発射）がこの基地を撃たず、プレイヤーの
        /// 「Set Launch Target」経由でのみ発射できる。
        /// </summary>
        /// <returns>baseId の基地が見つからない場合は false。</returns>
        public static bool TrySetMissileAutoLaunch(ushort baseId, bool value)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId != baseId) continue;
                    State.Bases[i].AutoLaunchMissiles = value;
                    return true;
                }
                return false;
            }
        }
    }
}
