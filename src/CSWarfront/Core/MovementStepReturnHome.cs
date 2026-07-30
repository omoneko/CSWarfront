namespace CSWarfront.Core
{
    /// <summary>MovementStepの続き（Task87: 航空/海上ユニットの帰還移動）。
    ///
    /// ユーザー要望「航空機がターゲットを失った際に静止してしまう→付近の航空基地または空母に帰還。
    /// 海上戦力は付近の海軍基地にもどる」。従来はIdle（交戦終了後・命令なし）の航空/海上ユニットは
    /// その場でホバリング/漂泊し続けていた。この拡張は、Idleの航空/海上ユニットに「最寄りの帰還先」
    /// を暗黙の目的地として与える:
    ///   - 航空(Domain.Air): 自軍の航空基地(BaseType.AirForce)と自軍の空母(UnitCategory.Carrier、
    ///     生存中)のうち最寄りのもの。
    ///   - 海上(Domain.Sea): 自軍の海軍基地(BaseType.Navy)のうち最寄りのもの。
    /// HomeArrivalDistance以内に着いたら以後は動かない（基地上空/沖合で待機）。AI/プレイヤーが
    /// 新しい命令（State=Moving+OrderTargetPos）を与えれば従来どおりそちらへ向かう。
    /// 状態は一切持たない（毎tick最寄りを再解決する決定的な純関数。空母が移動すれば追従する）。
    /// 地上ユニットは対象外（Idleで静止する従来挙動を維持）。</summary>
    public static partial class MovementStep
    {
        /// <summary>帰還先にこの距離まで近づいたら停止する（基地の真上に折り重ならないための余白。
        /// CoverArrivalDistanceより大きいのは、複数機が同じ基地へ帰るため
        /// ある程度の「たまり場」の広さが要るから）。</summary>
        public const float HomeArrivalDistance = 60f;

        /// <summary>Idleの航空/海上ユニットの帰還先を解決する。対象外・帰還先なし・到着済みならnull。</summary>
        private static WorldPos? ResolveHomeObjective(WarState state, UnitInstance u, UnitType type)
        {
            if (u.State != UnitState.Idle) return null; // Moving/Engagingは通常の目的地/パス移動が扱う

            BaseType homeBaseType;
            if (type.Domain == Domain.Air) homeBaseType = BaseType.AirForce;
            else if (type.Domain == Domain.Sea) homeBaseType = BaseType.Navy;
            else return null; // 地上は帰還しない

            WorldPos? best = null;
            float bestDist = float.MaxValue;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.OwnerFactionId == null || b.OwnerFactionId.Value != u.FactionId) continue;
                if (b.Type != homeBaseType) continue;
                float d = u.Position.HorizontalDistanceTo(b.Position);
                if (d < bestDist) { bestDist = d; best = b.Position; }
            }

            // 航空は自軍の空母も帰還先になる（発着艦プラットフォーム、Task85）。
            if (type.Domain == Domain.Air)
            {
                for (int j = 0; j < state.Units.Count; j++)
                {
                    UnitInstance other = state.Units[j];
                    if (!other.IsAlive || other.FactionId != u.FactionId || other.InstanceId == u.InstanceId) continue;
                    UnitType otherType = state.Types.Get(other.TypeKey);
                    if (otherType == null || otherType.Category != UnitCategory.Carrier) continue;
                    float d = u.Position.HorizontalDistanceTo(other.Position);
                    if (d < bestDist) { bestDist = d; best = other.Position; }
                }
            }

            if (!best.HasValue) return null;
            if (bestDist <= HomeArrivalDistance) return null; // 到着済み: その場で待機
            return best;
        }
    }
}
