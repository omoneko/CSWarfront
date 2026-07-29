using System;

namespace CSWarfront.Core
{
    /// <summary>ユニット/基地が属する単一の活動領域。UnitType.Domain / MilitaryBase.SpawnableDomains の
    /// 元になる単一値（複数を同時に表すことはない）。</summary>
    public enum Domain { Land, Sea, Air }

    /// <summary>
    /// 複数のDomainを同時に表すビットフラグ（Task61: 海上/航空戦力の追加に伴う対象ドメイン判定用）。
    /// UnitType.CanTargetDomains（このユニットが攻撃対象にできる領域）と
    /// MilitaryBase.SpawnableDomains（この基地が生産できるユニットの領域）の両方で使う。
    ///
    /// 既存の Domain（単一値、Land=0/Sea=1/Air=2）はビット演算に使えない値のため、
    /// あえて別のフラグ専用enumとして新設した（Domainの値を1/2/4へ変更する破壊的変更を避けるため。
    /// WarStateSerializerはDomainを直接シリアライズしない＝TypeKey文字列経由でのみ解決されるため
    /// 変更しても実害は無いはずだが、念のため既存enumには一切手を加えない安全側の選択）。
    /// </summary>
    [Flags]
    public enum DomainMask
    {
        None = 0,
        Land = 1,
        Sea = 2,
        Air = 4,
        All = Land | Sea | Air
    }

    public static class DomainMaskUtil
    {
        /// <summary>単一のDomainを対応するDomainMaskビットへ変換する。</summary>
        public static DomainMask Of(Domain domain)
        {
            switch (domain)
            {
                case Domain.Land: return DomainMask.Land;
                case Domain.Sea: return DomainMask.Sea;
                case Domain.Air: return DomainMask.Air;
                default: return DomainMask.None;
            }
        }

        /// <summary>maskがdomainのビットを含むか。</summary>
        public static bool Contains(DomainMask mask, Domain domain)
        {
            DomainMask bit = Of(domain);
            return (mask & bit) == bit;
        }
    }
}
