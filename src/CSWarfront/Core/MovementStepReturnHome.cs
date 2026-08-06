namespace CSWarfront.Core
{
    /// <summary>Continuation of MovementStep (Task87: return-home movement for air/sea units).
    ///
    /// User request: "aircraft freeze when they lose their target → return to a nearby air base or
    /// carrier; naval forces return to a nearby navy base". Previously, Idle air/sea units (engagement
    /// over, no orders) hovered/drifted in place forever. This extension gives Idle air/sea units the
    /// nearest home as an implicit objective:
    ///   - Air (Domain.Air): the nearest of the faction's air bases (BaseType.AirForce) and living
    ///     friendly carriers (UnitCategory.Carrier).
    ///   - Sea (Domain.Sea): the nearest of the faction's navy bases (BaseType.Navy).
    /// Once within HomeArrivalDistance the unit stops moving (waiting over the base / offshore). New
    /// orders from the AI/player (State=Moving + OrderTargetPos) send it there as usual.
    /// Completely stateless (a deterministic pure function re-resolving the nearest home every tick; if a
    /// carrier moves, its aircraft follow). Land units are exempt (the traditional behavior of standing
    /// still while Idle is kept).</summary>
    public static partial class MovementStep
    {
        /// <summary>Stop once within this distance of the home (a margin so aircraft do not pile up
        /// directly over the base; larger than CoverArrivalDistance because multiple aircraft return to
        /// the same base and need a wider "apron").</summary>
        public const float HomeArrivalDistance = 60f;

        /// <summary>Task107: remaining horizontal distance at which the return approach starts
        /// descending. Between here and HomeArrivalDistance the altitude drops linearly from cruise to
        /// parked (= a smooth descent short of the base).</summary>
        public const float DescentStartDistance = 500f;

        /// <summary>Task107: height above the surface when parked (landing complete). A margin so the
        /// model does not look sunk into the ground at 0.</summary>
        public const float ParkedAltitude = 2f;

        /// <summary>Task107: deck landing height on a carrier (relative to the carrier unit's base Y =
        /// deck height). Over open water the terrain sampler returns the seabed/water level, so carrier
        /// returns use this value instead.</summary>
        public const float CarrierDeckAltitude = 12f;

        /// <summary>Task107: descent per tick while landing = stepLen × this (a factor for settling down
        /// gently rather than dropping straight down).</summary>
        public const float LandingDescentRate = 0.35f;

        /// <summary>Resolves an Idle air/sea unit's home. Null when exempt, no home exists, or already
        /// arrived.</summary>
        private static WorldPos? ResolveHomeObjective(WarState state, UnitInstance u, UnitType type)
        {
            if (u.State != UnitState.Idle) return null; // Moving/Engaging are handled by normal objective/path movement

            // Task101: transport helicopters and military trains are fully managed by their dedicated
            // steps (TransportHeliStep/TrainStep) — they must not head home to an air base on their own
            // (a transport helicopter's home is an army base).
            if (type.Category == UnitCategory.TransportHelicopter
                || type.Category == UnitCategory.MilitaryTrain) return null;

            BaseType homeBaseType;
            if (type.Domain == Domain.Air) homeBaseType = BaseType.AirForce;
            else if (type.Domain == Domain.Sea) homeBaseType = BaseType.Navy;
            else return null; // land units do not return home

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

            // For aircraft, friendly carriers are homes too (flight platforms, Task85).
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
            if (bestDist <= HomeArrivalDistance) return null; // arrived: land in place (AdvanceAirLanding)
            return best;
        }

        /// <summary>Task107: target altitude during the return approach. At or beyond
        /// DescentStartDistance stays at cruise; between there and HomeArrivalDistance it drops linearly
        /// to the parked altitude (= flying lower the closer to the base).</summary>
        private static float ApproachAltitude(float distanceToHome, float cruiseAltitude)
        {
            if (distanceToHome >= DescentStartDistance) return cruiseAltitude;
            float t = (distanceToHome - HomeArrivalDistance) / (DescentStartDistance - HomeArrivalDistance);
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return ParkedAltitude + (cruiseAltitude - ParkedAltitude) * t;
        }

        /// <summary>Task107 (user report "aircraft that lost their target hover in place forever"):
        /// lands an air unit that has neither a mission nor a home to fly to (= it has arrived inside the
        /// home area, or a transport helicopter waiting at its base). The horizontal position is
        /// untouched; only Y sinks slowly toward the parking altitude.
        /// The landing height:
        ///   - Within HomeArrivalDistance of a friendly carrier: the carrier's base Y + CarrierDeckAltitude (deck landing)
        ///   - Over land: surface + ParkedAltitude
        ///   - Over open water (no carrier): cannot land, so do nothing (keep hovering as before)
        /// When new orders arrive (State=Moving + OrderTargetPos), AdvanceAir climbs it back to cruise
        /// (takeoff is rate-limited to stepLen per tick = no teleporting up from the ground).</summary>
        private static void AdvanceAirLanding(WarState state, UnitInstance u, float stepLen, IHeightSampler height)
        {
            float parkY;
            if (!TryResolveParkAltitude(state, u, height, out parkY)) return;

            float dy = parkY - u.Position.Y;
            if (dy > -0.01f && dy < 0.01f) return; // already on the ground
            float maxStep = stepLen * LandingDescentRate;
            if (dy < -maxStep) dy = -maxStep;
            else if (dy > maxStep) dy = maxStep;
            u.Position = new WorldPos(u.Position.X, u.Position.Y + dy, u.Position.Z);
        }

        /// <summary>Resolves the Y to adopt when landing/parking (false = cannot resolve, do not land).</summary>
        private static bool TryResolveParkAltitude(WarState state, UnitInstance u, IHeightSampler height, out float parkY)
        {
            // A friendly carrier directly below (within HomeArrivalDistance) means a deck landing.
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

            // Over open water (no carrier): never ditch = keep hovering.
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
            return false; // surface unknown: leave the unit alone as before (safe fallback)
        }
    }
}
