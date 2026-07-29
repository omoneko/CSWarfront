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
        NoOwner,
        /// <summary>発注しようとしたUnitType.TierがFaction.UnlockedTierを超えている（Task35：研究未解禁）。</summary>
        TierLocked,
        /// <summary>発注しようとしたUnitType.Domainが、その基地のMilitaryBase.SpawnableDomainsに
        /// 含まれていない（Task61：陸軍基地で艦艇/航空機を発注しようとした等）。</summary>
        WrongDomain
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
        ///  4. 所有勢力の Faction が見つかるか -> NoOwner（整合性が崩れている場合の防御）
        ///  5. type.Tier が owner.UnlockedTier 以下か（Task35） -> TierLocked
        ///  6. type.Domain が b.SpawnableDomains に含まれるか（Task61） -> WrongDomain
        ///  7. Queue.Count が MilitaryBase.ManualQueueCap 未満か -> QueueFull
        ///  8. 所有勢力が type.Cost を払えるか（Faction.TrySpend。成功した場合のみ実際に控除する） -> NotAffordable
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

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // 整合性が崩れている場合の防御（通常は起きない）

            if (type.Tier > owner.UnlockedTier) return QueueResult.TierLocked; // Task35: 未解禁Tier

            // Task61: 基地のSpawnableDomains（Army->Land, Navy->Sea, AirForce->Air）に含まれない
            // 領域のユニットは発注できない（例: 陸軍基地から駆逐艦や戦闘機を発注することはできない）。
            if (!DomainMaskUtil.Contains(b.SpawnableDomains, type.Domain)) return QueueResult.WrongDomain;

            if (b.Queue.Count >= MilitaryBase.ManualQueueCap) return QueueResult.QueueFull;

            if (!owner.TrySpend(type.Cost)) return QueueResult.NotAffordable;

            b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
            return QueueResult.Ok;
        }

        /// <summary>
        /// キューの末尾（最後に積まれた注文）を取り消す（Task35で仕様変更：進行中＝index0の注文も、
        /// それが唯一の注文であっても常に取消可能にした。旧仕様「唯一の注文でProgress&gt;0なら取消不可」は
        /// プレイヤーから見て「取消ボタンが理由なく効かないバグ」だったため撤廃）。
        ///
        /// 払い戻しは全額ではなく Cost * (1f - Progress) の部分返金にする（Task35）。ほとんど進んでいない
        /// 注文はほぼ全額、完成間際の注文はほぼ0しか返ってこない。結果を [0, order.Cost] へクランプする
        /// （Progressが理論上の範囲0..1を外れていても払い戻しが負値や超過にならないための防御）。
        ///
        /// 判定順序: 基地存在(BaseNotFound) -> 所有者あり(NoOwner) -> キューが空でないか(QueueFull、
        /// 「取消可能な注文が無い」の意味でTryEnqueueの「満杯で入らない」と同じ値を再利用) -> Ok。
        /// 決定的・RNG不使用。
        /// </summary>
        public static QueueResult TryCancelLast(WarState state, ushort baseId)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return QueueResult.BaseNotFound;
            if (b.OwnerFactionId == null) return QueueResult.NoOwner;

            if (b.Queue.Count == 0) return QueueResult.QueueFull;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // 整合性が崩れている場合の防御（通常は起きない）

            int idx = b.Queue.Count - 1;
            ProductionOrder order = b.Queue[idx];
            float refund = order.Cost * (1f - order.Progress);
            if (refund < 0f) refund = 0f;
            if (refund > order.Cost) refund = order.Cost;

            b.Queue.RemoveAt(idx);
            owner.AddTreasury(refund);
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
