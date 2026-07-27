using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// 基地情報パネル（Game/UI/BaseInfoPanel）向けの読み取り専用スナップショット（Task25）。
    /// UIが WarState / MilitaryBase へ直接触れずに済むよう、MilitaryManager.TryGetBaseSnapshot が
    /// _stateLock 内で値をコピーして渡す。500行制限のため MilitaryManager.cs から分離（Task30）。
    /// </summary>
    public struct BaseUiSnapshot
    {
        public byte? OwnerFactionId;
        public float CurrentHP;
        public float MaxHP;
        public float CaptureGraceHours;
        public int QueueCount;
        public bool IsHeadquarters;

        /// <summary>先頭の生産オーダー（キューが空なら空文字列）。何を作っているかをUIに出すため（Task30）。</summary>
        public string ProducingTypeKey;
        /// <summary>先頭オーダーの進捗（0..1）。キューが空なら0。</summary>
        public float ProducingProgress;
        /// <summary>先頭オーダーのビルド時間（ゲーム内時間）。キューが空なら0。</summary>
        public float ProducingBuildTime;
        /// <summary>所属勢力の軍資金。未所属なら0。</summary>
        public float OwnerTreasury;
        /// <summary>所属勢力が現在保有する生存ユニット数。未所属なら0。</summary>
        public int OwnerUnitCount;
        /// <summary>拠点の自衛射撃ダメージ（Task29で追加されたMilitaryBase.DefenseAttack）。</summary>
        public float DefenseAttack;
        /// <summary>拠点の自衛射撃射程（Task29で追加されたMilitaryBase.DefenseRange）。</summary>
        public float DefenseRange;

        /// <summary>trueならAIがこの基地のキューを自動補充する（Task34、MilitaryBase.AutoProduceの写し）。</summary>
        public bool AutoProduce;

        /// <summary>現在のキュー内容をTypeKeyだけの配列にした表示用コピー（Task34）。
        /// index 0 == 生産中（ProducingTypeKeyと同じ内容）。選択中の1基地分のみ構築するため
        /// 毎tickのホットパスではない（TryGetBaseSnapshot呼び出し時のみ）。</summary>
        public string[] QueuedTypeKeys;
    }

    /// <summary>
    /// BaseUiSnapshot の組み立てロジック（Task30）。MilitaryManager.TryGetBaseSnapshot の
    /// _stateLock 内から呼ばれる想定 — 呼び出し側がロックを保持していること（このクラス自体はロックしない）。
    /// MilitaryManager.cs の500行制限のため分離。
    /// </summary>
    internal static class BaseUiSnapshotBuilder
    {
        public static BaseUiSnapshot Build(MilitaryBase mb, WarState state)
        {
            string producingTypeKey = "";
            float producingProgress = 0f;
            float producingBuildTime = 0f;
            if (mb.Queue.Count > 0)
            {
                ProductionOrder head = mb.Queue[0];
                producingTypeKey = head.TypeKey;
                producingProgress = head.Progress;
                producingBuildTime = head.BuildTime;
            }

            float ownerTreasury = 0f;
            int ownerUnitCount = 0;
            if (mb.OwnerFactionId.HasValue)
            {
                byte owner = mb.OwnerFactionId.Value;
                Faction f = state.FindFaction(owner);
                if (f != null) ownerTreasury = f.Treasury;

                for (int u = 0; u < state.Units.Count; u++)
                {
                    UnitInstance unit = state.Units[u];
                    if (unit.FactionId == owner && unit.State != UnitState.Dead) ownerUnitCount++;
                }
            }

            // Task34: 選択中の1基地分のみ、キューのTypeKeyだけをUI表示用にコピーする。
            var queuedTypeKeys = new string[mb.Queue.Count];
            for (int q = 0; q < mb.Queue.Count; q++) queuedTypeKeys[q] = mb.Queue[q].TypeKey;

            return new BaseUiSnapshot
            {
                OwnerFactionId = mb.OwnerFactionId,
                CurrentHP = mb.CurrentHP,
                MaxHP = mb.MaxHP,
                CaptureGraceHours = mb.CaptureGraceHours,
                QueueCount = mb.Queue.Count,
                IsHeadquarters = mb.IsHeadquarters,
                ProducingTypeKey = producingTypeKey,
                ProducingProgress = producingProgress,
                ProducingBuildTime = producingBuildTime,
                OwnerTreasury = ownerTreasury,
                OwnerUnitCount = ownerUnitCount,
                DefenseAttack = mb.DefenseAttack,
                DefenseRange = mb.DefenseRange,
                AutoProduce = mb.AutoProduce,
                QueuedTypeKeys = queuedTypeKeys
            };
        }
    }
}
