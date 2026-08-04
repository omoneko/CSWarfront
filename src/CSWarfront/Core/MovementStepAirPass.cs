using System;

namespace CSWarfront.Core
{
    /// <summary>MovementStepの続き（Task86: 航空ユニットの交戦パス移動）。500行/ファイルの上限に
    /// 収めるための分離（MovementStepSea/MovementStepKamikazeと同じpartial classパターン）。
    ///
    /// 「爆撃機は爆弾を落としてヒットアンドアウェイ、戦闘機は停止せずすれ違いながらドッグファイト」
    /// （ユーザー要望）を、次のレーストラック航過で実現する:
    ///   1. 交戦アンカー（下記ResolveAirCombatAnchor）が見つかったらそこへ接近する。
    ///   2. AirCombat.PassTriggerDistanceまで近づいたら、進行方向へAirCombat.PassEgressDistance
    ///      抜けた先を離脱点(UnitInstance.AirPassEgress)として武装する。
    ///   3. 離脱点まで飛び切る（この間アンカーは再評価しない＝標的が死んでも/射程外に出ても
    ///      レグを完走する。射程境界での小刻みな反転を防ぐため）。
    ///   4. 到達したら離脱点をクリアし、次tickで再びアンカーを評価して再進入する。
    /// ダメージは従来どおり射程内でのみ入る（CombatStep等）ので、この動きだけで
    /// 「通過の瞬間に爆弾/機銃が当たり、離脱中は撃てない」が成立する。減った射程内滞在時間は
    /// AirCombat.DamageMultiplierが補正する。
    ///
    /// 双方が航空機のドッグファイトでは、互いに相手をアンカーに接近→すれ違い→離脱→反転を
    /// 繰り返すため、自然と交差機動（すれ違いざまの射撃）になる。
    /// </summary>
    public static partial class MovementStep
    {
        /// <summary>航空ユニットの交戦パス移動を1tick進める。パス移動を行った場合はtrue
        /// （呼び出し元は通常のAdvanceAirをスキップする）。交戦アンカーが無く離脱レグ中でも
        /// なければfalse（通常の目的地移動へフォールスルー）。非kamikazeのDomain.Air専用。</summary>
        private static bool AdvanceAirPass(WarState state, UnitInstance u, UnitType type, float stepLen,
            IHeightSampler height)
        {
            // Task101: ヘリはホバリング型（レーストラック航過をしない。通常のAdvanceAir巡航＝
            // 接近して射程内に留まる移動へフォールバックさせる）。
            if (TargetingRules.IsHelicopter(type.Category)) return false;

            // 離脱レグ中: アンカーの生死・射程を問わず離脱点まで飛び切る（クラスコメントの3）。
            if (u.AirPassEgress.HasValue)
            {
                WorldPos egress = u.AirPassEgress.Value;
                AdvanceAir(u, stepLen, egress, height);
                if (u.Position.HorizontalDistanceTo(egress) <= AirCombat.PassArrivalDistance)
                    u.AirPassEgress = null;
                return true;
            }

            WorldPos? anchor = ResolveAirCombatAnchor(state, u, type);
            if (!anchor.HasValue) return false;

            float dist = u.Position.HorizontalDistanceTo(anchor.Value);
            if (dist <= AirCombat.PassTriggerDistance)
            {
                // 至近＝「上空を通過中」。進行方向（自機→アンカー）へ抜けた先を離脱点にする。
                float dx = anchor.Value.X - u.Position.X;
                float dz = anchor.Value.Z - u.Position.Z;
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-3f) { dx = 1f; dz = 0f; len = 1f; } // 真上に重なった退化ケースは+Xへ（決定的）
                float ex = anchor.Value.X + dx / len * AirCombat.PassEgressDistance;
                float ez = anchor.Value.Z + dz / len * AirCombat.PassEgressDistance;
                u.AirPassEgress = new WorldPos(ex, 0f, ez); // Yは毎tickのAdvanceAirが巡航高度へ再解決する
                AdvanceAir(u, stepLen, u.AirPassEgress.Value, height);
                return true;
            }

            // 接近レグ: アンカーへ直進する。
            AdvanceAir(u, stepLen, anchor.Value, height);
            return true;
        }

        /// <summary>この航空ユニットが現在パス航過すべき交戦対象の位置。優先順:
        ///   1. CombatStepがロック中の敵ユニット（TargetId、生存中のみ）
        ///   2. 射程(+Radius)内の最近接の敵対脅威（ThreatCombatStepと同じ敵対判定・実効射程）
        ///   3. 射程内の最近接の敵対拠点（BaseCombatStepと同じ判定。Task85のCanAttackBaseを尊重
        ///      ＝戦闘機は拠点をアンカーにしない）
        /// いずれも無ければnull（通常の目的地移動）。</summary>
        private static WorldPos? ResolveAirCombatAnchor(WarState state, UnitInstance u, UnitType type)
        {
            if (u.TargetId.HasValue)
            {
                UnitInstance target = state.FindUnit(u.TargetId.Value);
                if (target != null && target.IsAlive) return target.Position;
            }

            if (TargetingRules.CanAttackThreat(type.Category))
            {
                ExternalThreat bestThreat = null;
                float bestDist = float.MaxValue;
                for (int j = 0; j < state.Threats.Count; j++)
                {
                    ExternalThreat threat = state.Threats[j];
                    if (threat.IsDefeated) continue;
                    if (!state.ThreatRelations.Get(u.FactionId, threat.Kind).IsHostile()) continue;
                    float d = u.Position.HorizontalDistanceTo(threat.Position);
                    if (d > type.Range + threat.Radius) continue; // ThreatCombatStepと同じ実効射程
                    if (d < bestDist) { bestDist = d; bestThreat = threat; }
                }
                if (bestThreat != null) return bestThreat.Position;
            }

            if (TargetingRules.CanAttackBase(type.Category))
            {
                MilitaryBase bestBase = null;
                float bestDist = float.MaxValue;
                for (int j = 0; j < state.Bases.Count; j++)
                {
                    MilitaryBase b = state.Bases[j];
                    if (b.CaptureGraceHours > 0f) continue; // 猶予中は無敵＝攻撃対象でない（BaseCombatStepと同じ）
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) continue;
                    // Task88: この攻撃側のHP床（航空=1）に達した拠点はもう航過アンカーにしない
                    // （BaseCombatStepの攻撃停止と対で、爆撃機がHP1の拠点上空を回り続けるのを防ぐ）。
                    if (b.CurrentHP <= TargetingRules.BaseHpFloor(type.Domain)) continue;
                    float d = u.Position.HorizontalDistanceTo(b.Position);
                    if (d > type.Range) continue;
                    if (d < bestDist) { bestDist = d; bestBase = b; }
                }
                if (bestBase != null) return bestBase.Position;
            }

            return null;
        }
    }
}
