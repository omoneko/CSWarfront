namespace CSWarfront.Core
{
    /// <summary>
    /// Eliminated判定とHQ整合性（Faction.HomeBaseId）を勢力単位で導出し直す（Task46）。
    ///
    /// 従来はOccupation.ResolveCapturesがHQ喪失の瞬間にFaction.Eliminated=trueを直接立てるだけで、
    /// 一度trueになったフラグを消す経路が存在しなかった。そのため、プレイヤーが脱落済み勢力へ
    /// 新しい基地を与えても、その勢力は二度と戦闘・生産をしないままだった（ユーザー報告バグ）。
    ///
    /// Refreshは「所有基地が1つも無い」という条件からEliminatedを毎tick導出し直すため、基地を
    /// 取り戻せば自動的に復活する。MilitaryManager.OnSimTickがOccupation.ResolveCaptures直後、
    /// 同じ_stateLock内で呼ぶ想定。
    /// </summary>
    public static class FactionStatus
    {
        public static void Refresh(WarState state)
        {
            for (int i = 0; i < state.Factions.Count; i++)
            {
                Faction f = state.Factions[i];

                // Task95: Invader勢力（外部襲来専用）は基地を1つも持たないのが正常状態。
                // ここでEliminated化するとAI進軍（AssignAdvance）の対象から外れ、侵攻部隊が
                // スポーン地点で永久に固まる（実機バグの根本原因）ため、常に現役として扱う。
                if (f.Id == Faction.InvaderFactionId)
                {
                    f.Eliminated = false;
                    continue;
                }

                bool ownsAnyBase = false;
                bool homeStillOwned = false;
                for (int j = 0; j < state.Bases.Count; j++)
                {
                    MilitaryBase b = state.Bases[j];
                    if (!b.OwnerFactionId.HasValue || b.OwnerFactionId.Value != f.Id) continue;
                    ownsAnyBase = true;
                    if (f.HomeBaseId.HasValue && b.BaseId == f.HomeBaseId.Value) homeStillOwned = true;
                }

                f.Eliminated = !ownsAnyBase;

                // 所有基地はあるのにHomeBaseIdが無効（null、または既に所有していない基地を指している）
                // 場合、所有基地の先頭をHQへ昇格する。
                if (ownsAnyBase && !homeStillOwned)
                    PromoteFirstOwnedBaseToHq(state, f.Id);
            }
        }

        /// <summary>
        /// factionIdが現在所有する基地のうち先頭（state.Bases順）をHQへ昇格する
        /// （対象基地のIsHeadquarters=true、faction.HomeBaseId=対象基地のBaseId）。所有基地が
        /// 無ければ何もしない。
        ///
        /// Game層のGame/BasePlacementWatcher.ReassignHqIfCleared（基地解体・所属変更でHQを失った
        /// 際の昇格）と同じ「所有基地の先頭を新HQにする」ルールを共有する唯一の実装（Task46：
        /// ロジックの重複を避けるためCoreへ集約し、Game側からはこれを呼ぶ）。
        /// </summary>
        public static void PromoteFirstOwnedBaseToHq(WarState state, byte factionId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null) return;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (!b.OwnerFactionId.HasValue || b.OwnerFactionId.Value != factionId) continue;
                b.IsHeadquarters = true;
                f.HomeBaseId = b.BaseId;
                return;
            }
        }
    }
}
