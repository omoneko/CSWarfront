using System;

namespace CSWarfront.Core
{
    /// <summary>The single activity domain a unit/base belongs to. The single value behind
    /// UnitType.Domain / MilitaryBase.SpawnableDomains (never represents multiple domains at
    /// once).</summary>
    public enum Domain { Land, Sea, Air }

    /// <summary>
    /// Bit flags representing multiple Domains at once (Task61: target-domain checks accompanying the
    /// naval/air force additions). Used by both UnitType.CanTargetDomains (the domains this unit may
    /// attack) and MilitaryBase.SpawnableDomains (the domains of units this base can produce).
    ///
    /// The existing Domain (single-valued, Land=0/Sea=1/Air=2) has values unusable for bitwise math,
    /// so this was deliberately added as a separate flags-only enum (avoiding the breaking change of
    /// moving Domain's values to 1/2/4. WarStateSerializer never serializes Domain directly — it is
    /// resolved only via the TypeKey string — so changing it should do no real harm, but leaving the
    /// existing enum completely untouched is the safe choice).
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
        /// <summary>Converts a single Domain to its corresponding DomainMask bit.</summary>
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

        /// <summary>Whether mask includes domain's bit.</summary>
        public static bool Contains(DomainMask mask, Domain domain)
        {
            DomainMask bit = Of(domain);
            return (mask & bit) == bit;
        }
    }
}
