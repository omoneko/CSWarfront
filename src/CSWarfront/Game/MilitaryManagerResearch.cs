using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーによる研究投資・Tier解禁（Task35）向けの MilitaryManager 追加メンバー。
    /// MilitaryManagerManualProduction.cs と同じ方針で、MilitaryManager.cs の500行制限のため分離した
    /// partial class。_stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによる資金→研究点の投資（Task35）。
        /// Core.Research.TryInvest へ _stateLock 内で委譲するだけの薄いラッパー。
        /// </summary>
        /// <returns>State未初期化、factionId不明、または資金不足なら false。</returns>
        public static bool TryInvestResearch(byte factionId, float amount)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                Faction f = State.FindFaction(factionId);
                if (f == null) return false;
                return Research.TryInvest(f, amount);
            }
        }

        /// <summary>
        /// 基地情報パネルから呼ばれる、プレイヤーによる次Tierの解禁（Task35）。
        /// Core.Research.TryUnlockNext へ _stateLock 内で委譲するだけの薄いラッパー。
        /// </summary>
        /// <returns>State未初期化、factionId不明、研究点不足、または既に最大Tierなら false。</returns>
        public static bool TryUnlockNextTier(byte factionId)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                Faction f = State.FindFaction(factionId);
                if (f == null) return false;
                return Research.TryUnlockNext(f);
            }
        }
    }
}
