namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: 歩兵の陣地志向AI（設計§1.4、ユーザー要望「歩兵は敵付近の塹壕や掩蔽壕に積極的に
    /// 向かう」）。敵対ユニットがEnemyRadius以内にいる歩兵系（AiControlled/FreeAdvance）へ、
    /// SeekRadius以内の塹壕/掩蔽壕のうち「敵に最も近い」ものを立ち位置として与える。
    ///
    /// 実装はCoverSeekStepの遮蔽移動と同じCoverDestination/CoverHoldフィールドを使い、
    /// **CoverSeekStepの後**に走って上書きする（陣地は建物の陰より常に優先。毎tick決定的に
    /// 再導出するステートレス設計で、CoverHoldTimerを0に保つことでMovementStepのホールド上限にも
    /// かからない＝敵がいる限り陣地に留まる）。敵がいなくなれば何もしない＝通常の遮蔽/進軍へ
    /// 自然に復帰する。
    ///
    /// 対象の陣地: 自軍所有のBunker（機能停止=Owner無しも地形として可）と、Trench（所有不問。
    /// ただし敵歩兵が既に乗っている塹壕へは向かわない）。
    /// </summary>
    public static class FortSeekStep
    {
        /// <summary>この距離以内に敵対ユニットがいるとき陣地を探す。</summary>
        public const float EnemyRadius = 600f;

        /// <summary>陣地の探索半径。</summary>
        public const float SeekRadius = 300f;

        public static void Advance(WarState state, float dt)
        {
            state.UnitGrid.Build(state.Units);

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;
                if (type.Category != UnitCategory.Infantry && type.Category != UnitCategory.MechInfantry) continue;

                UnitInstance enemy = TargetSearch.FindNearestHostile(u, state.UnitGrid, state.Relations,
                    EnemyRadius, DomainMask.All, state.Types);
                if (enemy == null) continue; // 敵が近くにいない: 通常の遮蔽/進軍のまま

                MilitaryBase fort = FindBestFort(state, u, enemy.Position);
                if (fort == null) continue;

                // 陣地へ向かう/陣地に留まる（CoverSeekStepの決定を上書き。コメント参照）。
                u.CoverDestination = fort.Position;
                u.CoverHold = true;
                u.CoverHoldTimer = 0f; // ホールド上限（MovementStep.MaxCoverHoldHours）を無効化し続ける
            }
        }

        /// <summary>SeekRadius以内の使える陣地のうち、敵位置に最も近いもの。無ければnull。</summary>
        private static MilitaryBase FindBestFort(WarState state, UnitInstance u, WorldPos enemyPos)
        {
            MilitaryBase best = null;
            float bestEnemyDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                float radius;
                if (mb.Type == BaseType.Trench) radius = FortDefenseBonus.TrenchRadius;
                else if (mb.Type == BaseType.Bunker) radius = FortDefenseBonus.BunkerRadius;
                else continue;

                // Bunkerは自軍所有（または機能停止=中立）のみ。敵所有の稼働Bunkerへ突っ込ませない。
                if (mb.Type == BaseType.Bunker && mb.OwnerFactionId != null &&
                    mb.OwnerFactionId.Value != u.FactionId) continue;

                if (u.Position.HorizontalDistanceTo(mb.Position) > SeekRadius) continue;
                if (mb.Type == BaseType.Trench && IsHeldByEnemyInfantry(state, mb, u.FactionId, radius)) continue;

                float d = enemyPos.HorizontalDistanceTo(mb.Position);
                if (d < bestEnemyDist) { bestEnemyDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>塹壕の上に敵対勢力の歩兵系が既に乗っているか（取られた塹壕へは向かわない）。</summary>
        private static bool IsHeldByEnemyInfantry(WarState state, MilitaryBase trench, byte factionId, float radius)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried) continue;
                if (!state.Relations.Get(factionId, o.FactionId).IsHostile()) continue;
                UnitType t = state.Types.Get(o.TypeKey);
                if (t == null || (t.Category != UnitCategory.Infantry && t.Category != UnitCategory.MechInfantry)) continue;
                if (trench.Position.HorizontalDistanceTo(o.Position) <= radius) return true;
            }
            return false;
        }
    }
}
