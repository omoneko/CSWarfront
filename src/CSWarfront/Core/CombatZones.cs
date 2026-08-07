using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// One "combat zone". A UnityEngine-free value type created/updated by
    /// CombatZoneTracker.ReportCombat (Task54: civilian traffic detours around fighting).
    /// </summary>
    public struct CombatZone
    {
        public readonly WorldPos Center;
        public readonly float Radius;
        public readonly float RemainingHours;

        public CombatZone(WorldPos center, float radius, float remainingHours)
        {
            Center = center;
            Radius = radius;
            RemainingHours = remainingHours;
        }
    }

    /// <summary>
    /// Tracks "combat zones" from reports of firing/impact locations (Task54). CombatStep and
    /// BaseCombatStep pass the target position to ReportCombat at the moment damage actually applies;
    /// nearby reports merge into and extend one zone, distant reports open new zones. The Game layer's
    /// CombatRoadBlocker reads this zone set and temporarily closes road segments inside them.
    /// UnityEngine-free, deterministic, O(n) (n = zone count, capped by MaxZones, so effectively O(1)).
    /// </summary>
    public class CombatZoneTracker
    {
        /// <summary>The radius around a combat point (map units ≈ meters) treated as the "combat
        /// zone". ReportCombat's proximity-merge check uses the same radius (the distance threshold
        /// for counting as "the same fight").</summary>
        public const float ZoneRadius = 120f;

        /// <summary>The zone lifts this long (in-game hours) after the last firing/impact report.</summary>
        public const float ZoneLingerHours = 2f;

        /// <summary>The cap on concurrently tracked zones. A defensive ceiling so zone-management cost
        /// never grows without bound (O(n²)-ish) in huge melees. At the cap, the zone closest to
        /// expiry is dropped to make room (deterministic: ties prefer the smaller index).</summary>
        public const int MaxZones = 16;

        private readonly List<CombatZone> _zones = new List<CombatZone>();

        public IList<CombatZone> Zones => _zones;

        /// <summary>
        /// Reports one firing/impact location. If an existing zone lies within ZoneRadius, it merges
        /// into the nearest one (centers simply averaged; the remaining time extended = reset to
        /// ZoneLingerHours). Otherwise a new zone is opened. When a new zone is needed at MaxZones,
        /// the zone with the least remaining time (= closest to expiry) is deleted first.
        /// </summary>
        public void ReportCombat(WorldPos position)
        {
            int nearestIndex = -1;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < _zones.Count; i++)
            {
                float d = _zones[i].Center.HorizontalDistanceTo(position);
                if (d <= ZoneRadius && d < nearestDist)
                {
                    nearestDist = d;
                    nearestIndex = i;
                }
            }

            if (nearestIndex >= 0)
            {
                CombatZone existing = _zones[nearestIndex];
                WorldPos mergedCenter = new WorldPos(
                    (existing.Center.X + position.X) * 0.5f,
                    (existing.Center.Y + position.Y) * 0.5f,
                    (existing.Center.Z + position.Z) * 0.5f);
                _zones[nearestIndex] = new CombatZone(mergedCenter, ZoneRadius, ZoneLingerHours);
                return;
            }

            if (_zones.Count >= MaxZones)
            {
                int soonestIndex = 0;
                float soonestRemaining = _zones[0].RemainingHours;
                for (int i = 1; i < _zones.Count; i++)
                {
                    if (_zones[i].RemainingHours < soonestRemaining)
                    {
                        soonestRemaining = _zones[i].RemainingHours;
                        soonestIndex = i;
                    }
                }
                _zones.RemoveAt(soonestIndex);
            }

            _zones.Add(new CombatZone(position, ZoneRadius, ZoneLingerHours));
        }

        /// <summary>Subtracts dt from every zone's remaining time and removes those at or below zero.
        /// Expected to be called every tick from MilitaryManager.OnSimTick with the same dt as
        /// CombatStep/BaseCombatStep.</summary>
        public void Advance(float dt)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                CombatZone z = _zones[i];
                float remaining = z.RemainingHours - dt;
                if (remaining <= 0f)
                {
                    _zones.RemoveAt(i);
                    continue;
                }
                _zones[i] = new CombatZone(z.Center, z.Radius, remaining);
            }
        }
    }
}
