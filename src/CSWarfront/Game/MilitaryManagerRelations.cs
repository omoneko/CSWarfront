using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// 勢力関係（Task49、Options画面「勢力の関係」グループ）向けの MilitaryManager 追加メンバー。
    /// MilitaryManager.cs の500行制限のため分離した partial class
    /// （Task34の MilitaryManagerManualProduction / Task48の MilitaryManagerUnitCommands と同じ方針）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    ///
    /// 呼び出し元（Game/Mod.cs の Options UI コールバック）はメインスレッドから呼ぶ。各メソッドは
    /// _stateLock を短時間だけ保持して Core.RelationMatrix / Core.RelationPresets へ委譲するだけの
    /// 薄いラッパーで、Unity API には一切触れない（ロック保持中にUnity APIを呼ばないという既定の規約に従う）。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// 勢力 a と b の関係を r に設定する（Task49）。RelationMatrix.Set は対称なので鏡側も更新される。
        /// State未初期化（メインメニューから開いた場合など）なら false を返し、何もしない。
        /// </summary>
        public static bool TrySetRelation(byte a, byte b, Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                State.Relations.Set(a, b, r);
                ModConfig.Log("MilitaryManager: relation " + a + " <-> " + b + " set to " + r);
                return true;
            }
        }

        /// <summary>
        /// 現在の勢力 a-b 間の関係を取得する（Task49、Options UI の初期表示用）。
        /// State未初期化なら false を返し、out引数は既定値（Neutral）のままにする。
        /// </summary>
        public static bool TryGetRelation(byte a, byte b, out Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) { r = Relation.Neutral; return false; }

                r = State.Relations.Get(a, b);
                return true;
            }
        }

        /// <summary>
        /// 「全て敵対に戻す」ボタン（Task49）。Core.RelationPresets.ApplyAllHostile へ委譲する。
        /// State未初期化なら false を返し、何もしない。
        /// </summary>
        public static bool TryResetRelationsToAllHostile()
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                RelationPresets.ApplyAllHostile(State.Relations, WarfrontSettings.MaxFactions);
                ModConfig.Log("MilitaryManager: all relations reset to Hostile");
                return true;
            }
        }
    }
}
