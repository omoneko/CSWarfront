namespace CSWarfront.Core
{
    /// <summary>プレイヤーが基地の生産キューへ手動で発注/取消した際の結果（Task34）。
    /// TryEnqueue と TryCancelLast で同じ列挙を共用する（両者とも「発注/取消できるか」を表す判定という
    /// 点で意味が近いため）。各メンバーの意味はメソッド側のXMLコメントに定義する。</summary>
    public enum QueueResult
    {
        Ok,
        BaseNotFound,
        UnknownType,
        QueueFull,
        NotAffordable,
        NoOwner
    }

    /// <summary>
    /// プレイヤー操作による手動生産（発注・取消）を扱う（Task34）。AIの自動生産（ProductionPlanning）とは
    /// 別クラスに分離し、両者ともUnityEngine非依存・決定的・RNG不使用を保つ。
    /// </summary>
    public static class ManualProduction
    {
        /// <summary>
        /// 基地の生産キューへ1件発注する。判定順序（先に失敗した方を返す）:
        ///  1. baseId の基地が存在するか -> BaseNotFound
        ///  2. その基地に所有勢力がいるか -> NoOwner
        ///  3. typeKey が state.Types に登録されているか -> UnknownType
        ///  4. Queue.Count が MilitaryBase.ManualQueueCap 未満か -> QueueFull
        ///  5. 所有勢力が type.Cost を払えるか（Faction.TrySpend。成功した場合のみ実際に控除する） -> NotAffordable
        /// 全て通れば ProductionOrder(typeKey, type.Cost, type.BuildTime) をQueue末尾に追加し Ok を返す。
        /// 決定的・RNG不使用。
        /// </summary>
        public static QueueResult TryEnqueue(WarState state, ushort baseId, string typeKey)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return QueueResult.BaseNotFound;
            if (b.OwnerFactionId == null) return QueueResult.NoOwner;

            UnitType type = state.Types.Get(typeKey);
            if (type == null) return QueueResult.UnknownType;

            if (b.Queue.Count >= MilitaryBase.ManualQueueCap) return QueueResult.QueueFull;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // 整合性が崩れている場合の防御（通常は起きない）

            if (!owner.TrySpend(type.Cost)) return QueueResult.NotAffordable;

            b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
            return QueueResult.Ok;
        }

        /// <summary>
        /// キューの末尾（最後に積まれた注文）を取り消し、その Cost を所有勢力へ AddTreasury で全額払い戻す。
        ///
        /// 取消可否の正確なルール:
        ///  - Queue.Count == 0: 取り消せる注文が無い -> 失敗（QueueFull を「取消不能」の意味で流用して返す。
        ///    TryEnqueueでの「満杯で入らない」とは逆方向の状況だが、「キューの現在状態がこの操作を妨げている」
        ///    という共通点でこの値を再利用する。専用のenum値を増やさない設計判断）。
        ///  - Queue.Count == 1: 唯一の注文はindex0＝生産中スロットだが、その Progress == 0f
        ///    （＝実際にはまだ1ミリも進捗していない）場合に限り取消可能。Progress > 0f なら「進行中の注文は
        ///    取り消せない」ため失敗（QueueFull）を返す。
        ///  - Queue.Count >= 2: 常に最後のインデックス（Queue.Count - 1）を取り消す。このインデックスは
        ///    index0（進行中）と一致し得ないため、常に安全に取消できる。
        ///
        /// 判定順序: 基地存在(BaseNotFound) -> 所有者あり(NoOwner) -> 上記の取消可否(QueueFull) -> Ok。
        /// 決定的・RNG不使用。
        /// </summary>
        public static QueueResult TryCancelLast(WarState state, ushort baseId)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return QueueResult.BaseNotFound;
            if (b.OwnerFactionId == null) return QueueResult.NoOwner;

            if (b.Queue.Count == 0) return QueueResult.QueueFull;
            if (b.Queue.Count == 1 && b.Queue[0].Progress > 0f) return QueueResult.QueueFull;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // 整合性が崩れている場合の防御（通常は起きない）

            int idx = b.Queue.Count - 1;
            ProductionOrder order = b.Queue[idx];
            b.Queue.RemoveAt(idx);
            owner.AddTreasury(order.Cost);
            return QueueResult.Ok;
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
