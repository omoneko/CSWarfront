namespace CSWarfront.Core
{
    /// <summary>MovementStep continued (Task79: the suicide drones'
    /// (UnitCategoryFlags.IsKamikaze) dive movement). Only the dive-target resolution and the dive
    /// movement itself were split into this file to stay under the 500-line/file cap (the same
    /// partial-class pattern as MovementStepSea.cs).</summary>
    public static partial class MovementStep
    {
        /// <summary>Task79: resolves the dive destination — "the target's current position" — from the
        /// lock KamikazeStep wrote (TargetId/TargetThreatId/TargetBaseId). Returns null when the
        /// locked unit was destroyed/removed, the threat was defeated (IsDefeated), or the base is no
        /// longer hostile-owned (bases in capture grace are never locked by KamikazeStep in the first
        /// place, so nulling by grace re-entry cannot happen) — that tick it does not dive, and the
        /// caller falls through to the regular ResolveDomainObjective. Also returns null when
        /// TargetId/TargetThreatId/TargetBaseId are all null (nothing locked yet).</summary>
        private static WorldPos? ResolveKamikazeTarget(WarState state, UnitInstance u)
        {
            if (u.TargetId.HasValue)
            {
                UnitInstance target = state.FindUnit(u.TargetId.Value);
                if (target != null && target.IsAlive) return target.Position;
                return null;
            }

            if (u.TargetThreatId.HasValue)
            {
                for (int i = 0; i < state.Threats.Count; i++)
                {
                    ExternalThreat threat = state.Threats[i];
                    if (threat.Id == u.TargetThreatId.Value)
                        return threat.IsDefeated ? (WorldPos?)null : threat.Position;
                }
                return null;
            }

            if (u.TargetBaseId.HasValue)
            {
                for (int i = 0; i < state.Bases.Count; i++)
                {
                    MilitaryBase b = state.Bases[i];
                    if (b.BaseId != u.TargetBaseId.Value) continue;
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value == u.FactionId) return null;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) return null;
                    return b.Position;
                }
                return null;
            }

            return null;
        }

        /// <summary>Task79: the suicide drone's dive movement. Charges straight at the target's
        /// current position (target) in 3D (X/Y/Z all at once). Touches none of CruiseAltitude
        /// (cruise-altitude keeping), IHeightSampler (ground snapping) or IWaterSampler (water
        /// checks) — per the "ignoring cover/paths" spec, it heads for the target by the shortest
        /// line, ignoring terrain and obstacles. Speed is the effective value of type.Speed (the
        /// caller's stepLen = Speed × dt) times DiveSpeedMultiplier.</summary>
        private static void AdvanceKamikaze(UnitInstance u, float stepLen, WorldPos target)
        {
            float diveStepLen = stepLen * DiveSpeedMultiplier;
            float dist = u.Position.DistanceTo(target);
            if (dist <= diveStepLen || dist <= 0.01f)
            {
                u.Position = target;
                return;
            }

            float t = diveStepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
