using System.Collections.Generic;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// プレイヤーの部隊コマンド（範囲選択→自由進撃/停止/集結待機/AI委任、Task48）向けの
    /// MilitaryManager 追加メンバー。MilitaryManager.cs の500行制限のため分離した partial class
    /// （Task34のMilitaryManagerManualProductionと同じ方針）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    ///
    /// 呼び出し元（Game/UI/UnitCommandInput）はメインスレッドから呼ぶ。各メソッドは _stateLock を
    /// 短時間だけ保持してCore.UnitCommandsへ委譲するだけの薄いラッパーで、Unity API には一切触れない
    /// （ロック保持中にUnity APIを呼ばないという既定の規約に従う）。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>自由進撃（Task48）。Core.UnitCommands.ApplyFreeAdvance へ委譲し、影響を受けた
        /// ユニット数を1行ログする。State未初期化なら0を返す。</summary>
        public static int CommandFreeAdvance(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyFreeAdvance(State, instanceIds);
                ModConfig.Log("MilitaryManager: FreeAdvance applied to " + n + " unit(s)");
                return n;
            }
        }

        /// <summary>停止（Task48）。Core.UnitCommands.ApplyHold へ委譲し、影響を受けたユニット数を
        /// 1行ログする。State未初期化なら0を返す。</summary>
        public static int CommandHold(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyHold(State, instanceIds);
                ModConfig.Log("MilitaryManager: Hold applied to " + n + " unit(s)");
                return n;
            }
        }

        /// <summary>集結待機（Task48）。Core.UnitCommands.ApplyRally へ委譲し、影響を受けたユニット数を
        /// 1行ログする。State未初期化なら0を返す。</summary>
        public static int CommandRally(IList<uint> instanceIds, WorldPos rallyPoint)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyRally(State, instanceIds, rallyPoint);
                ModConfig.Log("MilitaryManager: Rally applied to " + n + " unit(s) at " +
                    rallyPoint.X.ToString("0") + "," + rallyPoint.Z.ToString("0"));
                return n;
            }
        }

        /// <summary>AI委任へ戻す（Task48）。Core.UnitCommands.ClearOrders へ委譲し、影響を受けた
        /// ユニット数を1行ログする。State未初期化なら0を返す。</summary>
        public static int CommandClear(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ClearOrders(State, instanceIds);
                ModConfig.Log("MilitaryManager: orders cleared (AI-controlled) for " + n + " unit(s)");
                return n;
            }
        }
    }
}
