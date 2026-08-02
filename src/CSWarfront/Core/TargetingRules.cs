namespace CSWarfront.Core
{
    /// <summary>
    /// 兵科ごとの「何を攻撃できるか」の集中規則（Task85、ユーザー要望）:
    ///  - 敵拠点を占領（HP0まで削る）できるのは地上戦力のみ。航空・海上戦力は拠点HPを1までしか
    ///    減らせない（最後の1は必ず陸上部隊が削る＝地上部隊なしでは占領が成立しない）。
    ///  - 戦闘機(AirSuperiority)は戦闘機・爆撃機（＝航空ユニット）とKAIJU（外部脅威）のみ攻撃可能。
    ///    拠点は攻撃不可。
    ///  - 爆撃機(TacticalBomber)は地上目標（地上ユニット・拠点）とKAIJUのみ攻撃可能。
    ///  - ミサイル駆逐艦(Destroyer)は地上目標・KAIJU・海上戦力を攻撃可能だが拠点占領は不可（上記の
    ///    HP下限1の対象）。
    ///  - 空母(Carrier)は戦闘機・爆撃機の発着艦プラットフォーム専任（CarrierAirWing）で、
    ///    一切攻撃しない。
    ///
    /// ユニット間の標的可否はUnitType.CanTargetDomains（ロスター定義、TargetSearchが適用）が担い、
    /// このクラスは「拠点」「外部脅威」という非ユニット標的への可否と、拠点HPの下限を担う。
    /// 適用箇所: BaseCombatStep（CanAttackBase/BaseHpFloor）、KamikazeStep（BaseHpFloor）、
    /// ThreatCombatStep（CanAttackThreat）。
    ///
    /// 注記: 「KAIJUのみ」のKAIJUは外部脅威全般（ゴジラ=Kaiju/トライポッド=Alien）を指すものとして
    /// 実装する（ThreatKindでの区別はしない——戦闘機がゴジラは撃てるのにトライポッドは撃てない、
    /// という非対称にする理由が無いため）。
    /// </summary>
    public static class TargetingRules
    {
        /// <summary>この兵科は拠点（MilitaryBase）を攻撃できるか。戦闘機（対空専任）・空母
        /// （プラットフォーム専任）・補給トラック（Task99: 非武装。Attack0でもCombatMath.DamagePerHitの
        /// 最低保証1が通ってしまうため、ここで明示的に除外する）のみfalse。</summary>
        public static bool CanAttackBase(UnitCategory category)
        {
            return category != UnitCategory.AirSuperiority && category != UnitCategory.Carrier
                && category != UnitCategory.SupplyTruck;
        }

        /// <summary>攻撃側ドメインに応じた拠点HPの下限。地上のみ0まで削れる（＝占領できる）。
        /// 航空・海上は1で頭打ち。</summary>
        public static float BaseHpFloor(Domain attackerDomain)
        {
            return attackerDomain == Domain.Land ? 0f : 1f;
        }

        /// <summary>この兵科は外部脅威（KAIJU/Alien）を攻撃できるか。空母と補給トラック
        /// （Task99: 非武装、CanAttackBaseと同じ理由）のみfalse。</summary>
        public static bool CanAttackThreat(UnitCategory category)
        {
            return category != UnitCategory.Carrier && category != UnitCategory.SupplyTruck;
        }
    }
}
