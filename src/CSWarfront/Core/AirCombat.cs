namespace CSWarfront.Core
{
    /// <summary>
    /// 航空ユニットの交戦パス移動（Task86、ユーザー要望「爆撃機は爆弾を落としてヒットアンドアウェイ、
    /// 戦闘機は停止せずすれ違いながらドッグファイト」）の定数とダメージ補正。
    ///
    /// 旧仕様では航空ユニットは目的地に到着するとホバリングしたまま射程内へ撃ち続ける「浮かぶ砲台」
    /// だった。新仕様では交戦アンカー（ロック中の敵ユニット→射程内の敵対脅威→射程内の敵対拠点の
    /// 優先順、MovementStepAirPass.ResolveAirCombatAnchor）がある間、
    ///   接近 → 至近(PassTriggerDistance)で進行方向へ抜ける離脱点(PassEgressDistance)を設定
    ///   → 離脱点まで飛び切る → 反転して再進入
    /// のレーストラック航過を繰り返す。ダメージ判定自体は従来どおり「射程内にいる間だけ」
    /// （CombatStep/BaseCombatStep/ThreatCombatStep）なので、通過の瞬間だけ爆弾/機銃が当たる＝
    /// ヒットアンドアウェイ/すれ違いドッグファイトになる。
    /// </summary>
    public static class AirCombat
    {
        /// <summary>接近レグでアンカーへこの距離まで近づいたら離脱点を武装する（＝「上空を通過した」
        /// とみなす至近距離）。射程（戦闘機90/爆撃機70）より十分小さく、必ず射程内を貫通してから
        /// 離脱に移る。</summary>
        public const float PassTriggerDistance = 40f;

        /// <summary>離脱点までの距離（アンカーから進行方向へこの距離だけ抜ける）。射程より十分大きく
        /// 取ることで、離脱レグの大半で射程外＝撃てない時間を作る（ヒットアンドアウェイの
        /// 「アウェイ」）。</summary>
        public const float PassEgressDistance = 350f;

        /// <summary>離脱点への到達判定距離。到達したら離脱レグを終え、次tickから再進入する。</summary>
        public const float PassArrivalDistance = 20f;

        /// <summary>パス移動により射程内滞在時間が概ね1/4程度に減る（在圏窓≈2×射程 vs 周回長≈
        /// 2×PassEgressDistance）ため、航空ユニット（自爆ドローン除く）のdtスケールダメージに
        /// この倍率を掛けて補正する。実効DPSは従来の約60〜75%となり、「航空は強力だが単独では
        /// 決定打にならない」バランスを保つ（実機プレイで要調整の較正値）。</summary>
        public const float PassDamageCompensation = 3f;

        /// <summary>この兵科のdtスケールダメージに掛ける補正倍率。パス移動を行う航空ユニット
        /// （Domain=Air、自爆ドローン除く＝体当たり1回フルダメージのKamikazeStepは対象外）のみ
        /// PassDamageCompensation、それ以外は1。</summary>
        public static float DamageMultiplier(UnitType type)
        {
            if (type == null) return 1f;
            return (type.Domain == Domain.Air && !type.Category.IsKamikaze()) ? PassDamageCompensation : 1f;
        }
    }
}
