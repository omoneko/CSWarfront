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

        /// <summary>Task107: 帰還進入で降下を開始する残り水平距離。ここからHomeArrivalDistanceまでの
        /// 間で巡航高度→駐機高度へ線形に降下する（＝基地の手前で滑らかに高度を落とす）。</summary>
        public const float DescentStartDistance = 500f;

        /// <summary>Task107: 駐機（着陸完了）時の地表からの高さ。0だと地面にめり込んで見えるための余白。</summary>
        public const float ParkedAltitude = 2f;

        /// <summary>Task107: 空母への着艦高度（空母ユニットの基準Yからの相対＝甲板の高さ）。
        /// 洋上では地形サンプラーが海底/水面を返すため、空母帰還時はこの値を使う。</summary>
        public const float CarrierDeckAltitude = 12f;

        /// <summary>Task107: 着陸降下の1tickあたりの下降量＝stepLen×これ（真下へ落ちるのではなく
        /// ゆっくり接地させるための係数）。</summary>
        public const float LandingDescentRate = 0.35f;

        /// <summary>Idleの航空/海上ユニットの帰還先を解決する。対象外・帰還先なし・到着済みならnull。</summary>
        private static WorldPos? ResolveHomeObjective(WarState state, UnitInstance u, UnitType type)
        {
            if (u.State != UnitState.Idle) return null; // Moving/Engagingは通常の目的地/パス移動が扱う

            // Task101: 輸送ヘリ・軍用列車は専用step（TransportHeliStep/TrainStep）が全移動を管理する
            // ——航空基地へ勝手に帰ろうとさせない（輸送ヘリの母基地は陸軍基地）。
            if (type.Category == UnitCategory.TransportHelicopter
                || type.Category == UnitCategory.MilitaryTrain) return null;

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
            if (bestDist <= HomeArrivalDistance) return null; // 到着済み: その場で着陸（AdvanceAirLanding）
            return best;
        }

        /// <summary>Task107: 帰還進入中の目標高度。DescentStartDistance以遠は巡航高度のまま、そこから
        /// HomeArrivalDistanceまでの間で駐機高度へ線形に落とす（＝基地に近づくほど低く飛ぶ）。</summary>
        private static float ApproachAltitude(float distanceToHome, float cruiseAltitude)
        {
            if (distanceToHome >= DescentStartDistance) return cruiseAltitude;
            float t = (distanceToHome - HomeArrivalDistance) / (DescentStartDistance - HomeArrivalDistance);
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return ParkedAltitude + (cruiseAltitude - ParkedAltitude) * t;
        }

        /// <summary>Task107（ユーザー報告「目標がなくなった航空戦力が空中でホバリングしてしまう」）:
        /// 任務も帰還先も無い航空ユニット（＝帰還先の圏内に到着済み、または母基地で待機中の輸送ヘリ）を
        /// その場で着陸させる。水平位置は変えず、Yだけを駐機高度へ向けてゆっくり下ろす。
        /// 着陸先の高さは:
        ///   - 自軍空母のHomeArrivalDistance以内: 空母の基準Y+CarrierDeckAltitude（着艦）
        ///   - 陸上: 地表+ParkedAltitude
        ///   - 洋上（空母なし）: 着陸できないので何もしない（従来どおり滞空）
        /// 新しい命令（State=Moving+OrderTargetPos）が来れば、AdvanceAirが巡航高度へ上昇させる
        /// （離陸はstepLen/tickの上昇制限つき＝地上から瞬間移動しない）。</summary>
        private static void AdvanceAirLanding(WarState state, UnitInstance u, float stepLen, IHeightSampler height)
        {
            float parkY;
            if (!TryResolveParkAltitude(state, u, height, out parkY)) return;

            float dy = parkY - u.Position.Y;
            if (dy > -0.01f && dy < 0.01f) return; // 接地済み
            float maxStep = stepLen * LandingDescentRate;
            if (dy < -maxStep) dy = -maxStep;
            else if (dy > maxStep) dy = maxStep;
            u.Position = new WorldPos(u.Position.X, u.Position.Y + dy, u.Position.Z);
        }

        /// <summary>着陸/駐機時に取るべきYを解決する（解決できなければfalse＝着陸しない）。</summary>
        private static bool TryResolveParkAltitude(WarState state, UnitInstance u, IHeightSampler height, out float parkY)
        {
            // 直下（HomeArrivalDistance以内）に自軍の空母が居れば着艦する。
            for (int j = 0; j < state.Units.Count; j++)
            {
                UnitInstance other = state.Units[j];
                if (!other.IsAlive || other.FactionId != u.FactionId || other.InstanceId == u.InstanceId) continue;
                UnitType otherType = state.Types.Get(other.TypeKey);
                if (otherType == null || otherType.Category != UnitCategory.Carrier) continue;
                if (u.Position.HorizontalDistanceTo(other.Position) > HomeArrivalDistance) continue;
                parkY = other.Position.Y + CarrierDeckAltitude;
                return true;
            }

            // 洋上（空母なし）は着水させない＝そのまま滞空。
            if (state.Water != null && state.Water.IsWater(u.Position.X, u.Position.Z))
            {
                parkY = 0f;
                return false;
            }

            float groundY;
            if (height != null && height.TrySampleHeight(u.Position.X, u.Position.Z, out groundY))
            {
                parkY = groundY + ParkedAltitude;
                return true;
            }

            parkY = 0f;
            return false; // 地表が分からない: 従来どおり動かさない（安全側フォールバック）
        }
    }
}
