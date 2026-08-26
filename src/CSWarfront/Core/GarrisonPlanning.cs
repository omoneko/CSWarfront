using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task149 (asked during playtest: "does the AI make use of Hold?"): it did not, and more than that,
    /// it had no idea of defence at all. Every unit a faction owned was sent at the nearest enemy base,
    /// so a faction's own bases sat empty behind the offensive - which is why an invasion wave that
    /// arrives while the army is away walks straight in.
    ///
    /// This assigns part of each faction's strength to stay home. The rules are deliberately blunt,
    /// because an AI that garrisons unpredictably is worse than one that never does:
    ///
    ///  - Only real bases are garrisoned. Emplacements defend themselves and there are far too many of
    ///    them; a faction with nine pillboxes would otherwise post its entire army as sentries.
    ///  - <see cref="PerBase"/> units per base, and never more than <see cref="MaxShareOfForce"/> of the
    ///    faction's fighting strength, so a small army does not turtle itself out of the war.
    ///  - The nearest units to each base are the ones kept, because those are the ones already in a
    ///    position to defend it and the least useful at the front.
    ///  - Selection is deterministic (distance, then instance id) and the chosen units stand still once
    ///    home, so the assignment does not flap from tick to tick.
    ///
    /// A garrison unit that has reached its post is stood down - Idle with no objective - which is the
    /// same posture Task148 credits as dug in, so the AI's defenders earn the armour bonus the player's
    /// Hold units do. They still shoot: CombatStep does not care what a unit was told to do.
    ///
    /// Pure logic: deterministic, no RNG, UnityEngine-free.
    /// </summary>
    public static class GarrisonPlanning
    {
        /// <summary>Units posted to each real base.</summary>
        public const int PerBase = 2;

        /// <summary>Ceiling on the whole garrison as a share of the faction's fighting strength. Two per
        /// base would otherwise consume a small army entirely: at four bases and six units, everything
        /// would be a sentry and the faction would never attack again.</summary>
        public const float MaxShareOfForce = 0.34f;

        /// <summary>How close counts as "at the post". Wide enough that a unit does not shuffle to hit an
        /// exact spot, tight enough that it is plainly defending this base.</summary>
        public const float PostRadius = 120f;

        /// <summary>Decides which of a faction's units stay home, as instance id -> the base they hold.
        /// Empty when the faction has no real bases or nothing to spare.</summary>
        public static Dictionary<uint, ushort> Assign(WarState state, byte factionId)
        {
            var posts = new Dictionary<uint, ushort>();
            if (state == null) return posts;

            var bases = new List<MilitaryBase>();
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != factionId) continue;
                if (FortificationRules.IsFortification(mb.Type)) continue; // emplacements are not posts
                bases.Add(mb);
            }
            if (bases.Count == 0) return posts;

            var available = new List<UnitInstance>();
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.FactionId != factionId || !u.IsAlive || u.IsCarried) continue;
                if (u.Order != UnitOrder.AiControlled) continue; // the player's orders are never overridden
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || !IsGarrisonMaterial(type)) continue;
                available.Add(u);
            }
            if (available.Count == 0) return posts;

            int budget = (int)(available.Count * MaxShareOfForce);
            if (budget < 1) return posts; // too small a force to spare anyone
            int perBase = PerBase;

            // Lowest base id first: deterministic, and it means the same bases keep their garrison as the
            // force grows rather than the assignment reshuffling.
            bases.Sort(delegate(MilitaryBase a, MilitaryBase b) { return a.BaseId.CompareTo(b.BaseId); });

            for (int b = 0; b < bases.Count && posts.Count < budget; b++)
            {
                MilitaryBase post = bases[b];
                available.Sort(delegate(UnitInstance x, UnitInstance y)
                {
                    float dx = x.Position.HorizontalDistanceTo(post.Position);
                    float dy = y.Position.HorizontalDistanceTo(post.Position);
                    if (dx < dy) return -1;
                    if (dx > dy) return 1;
                    return x.InstanceId.CompareTo(y.InstanceId);
                });

                int taken = 0;
                for (int i = 0; i < available.Count && taken < perBase && posts.Count < budget; i++)
                {
                    UnitInstance u = available[i];
                    if (posts.ContainsKey(u.InstanceId)) continue;
                    posts[u.InstanceId] = post.BaseId;
                    taken++;
                }
            }
            return posts;
        }

        /// <summary>Fighting units only. Logistics run themselves and an unarmed truck posted as a sentry
        /// is a truck lost; aircraft and ships defend an area rather than stand on it.</summary>
        public static bool IsGarrisonMaterial(UnitType type)
        {
            if (type.Domain != Domain.Land) return false;
            switch (type.Category)
            {
                case UnitCategory.SupplyTruck:
                case UnitCategory.TransportHelicopter:
                case UnitCategory.MilitaryTrain:
                    return false;
                default:
                    return true;
            }
        }
    }
}
