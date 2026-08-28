using System;

namespace CSWarfront.Core
{
    /// <summary>Continuation of MovementStep (Task154: the fixed-wing flight model). Split out to stay
    /// within the 500-line-per-file limit (the same partial-class pattern as MovementStepAirPass).
    ///
    /// Playtest: "no sharp U-turns - turning back should draw a wide arc" and "never stand still, not even
    /// for an instant; only a helicopter is allowed to hover".
    ///
    /// Both came from the same primitive. Air movement was "each tick, step straight toward the
    /// objective", which has no notion of which way the aircraft is facing: reversing direction cost
    /// nothing and happened within a single tick, and arriving meant the step snapped onto the objective
    /// and every tick after that moved zero. So a bomber flipped through 180 degrees at the end of its
    /// egress leg, and any aircraft that reached its objective parked in the sky.
    ///
    /// A fixed-wing aircraft carries a heading (UnitInstance.AirHeading) and obeys two rules:
    ///   - it always moves a full step forward along that heading - there is no arriving and no stopping;
    ///   - the heading turns toward the objective by at most one step's worth of arc, so the tightest
    ///     turn it can fly is a circle of TurnRadius. A reversal becomes a half-circle.
    /// Helicopters keep the old direct model, which is what lets them hover.</summary>
    public static partial class MovementStep
    {
        /// <summary>Tightest circle a fighter can fly (map units = metres). Small enough to keep a
        /// dogfight in one place, large enough that the turn reads as a turn.</summary>
        public const float FighterTurnRadius = 170f;

        /// <summary>Tightest circle a bomber can fly. Deliberately wider than the fighter's: the half
        /// circle at the end of a bombing run is about 800 metres of arc, which at 650 km/h is roughly
        /// four and a half seconds of sweeping turn.</summary>
        public const float BomberTurnRadius = 260f;

        /// <summary>Tightest circle a suicide drone can fly - small and light, so it turns hard.</summary>
        public const float DroneTurnRadius = 120f;

        /// <summary>Tightest circle for any other fixed-wing category.</summary>
        public const float DefaultTurnRadius = 220f;

        /// <summary>Anything that flies but is not a helicopter: it must keep flying forward, and it turns
        /// on an arc. Helicopters are the only aircraft allowed to stop in the air.</summary>
        public static bool IsFixedWing(UnitType type)
        {
            return type != null && type.Domain == Domain.Air && !TargetingRules.IsHelicopter(type.Category);
        }

        /// <summary>The radius of the tightest circle this category can fly.</summary>
        public static float TurnRadius(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.AirSuperiority:
                    return FighterTurnRadius;
                case UnitCategory.SuicideDrone:
                    return DroneTurnRadius;
                case UnitCategory.TacticalBomber:
                case UnitCategory.StrategicBomber:
                case UnitCategory.GroundAttack:
                    return BomberTurnRadius;
                default:
                    return DefaultTurnRadius;
            }
        }

        /// <summary>Wraps an angle into (-pi, pi] so a turn is always taken the short way round.</summary>
        private static float NormalizeAngle(float radians)
        {
            const float TwoPi = (float)(Math.PI * 2.0);
            while (radians > (float)Math.PI) radians -= TwoPi;
            while (radians <= -(float)Math.PI) radians += TwoPi;
            return radians;
        }

        /// <summary>Heading in radians, measured the way the rest of the movement code measures bearings:
        /// 0 is +Z and it increases toward +X.</summary>
        private static float HeadingTo(UnitInstance u, WorldPos objective)
        {
            float dx = objective.X - u.Position.X;
            float dz = objective.Z - u.Position.Z;
            if (dx * dx + dz * dz < 1e-6f) return u.AirHeading.HasValue ? u.AirHeading.Value : 0f;
            return (float)Math.Atan2(dx, dz);
        }

        /// <summary>Task154: one tick of fixed-wing flight. With an objective the aircraft turns toward it
        /// as hard as its radius allows; with none it holds a circle (see AdvanceAirLoiter). Either way it
        /// moves a full step - a fixed-wing aircraft that stops falls out of the sky.</summary>
        private static void AdvanceAirFixedWing(UnitInstance u, UnitType type, float stepLen,
            WorldPos? objective, IHeightSampler height, float altitude)
        {
            // First tick after a spawn or a load: face wherever the aircraft is being sent, so it does not
            // start by flying a pointless turn.
            if (!u.AirHeading.HasValue)
                u.AirHeading = objective.HasValue ? HeadingTo(u, objective.Value) : 0f;

            float radius = TurnRadius(type.Category);
            float maxTurn = radius > 0.01f ? stepLen / radius : (float)Math.PI;

            float turn;
            if (objective.HasValue)
            {
                turn = NormalizeAngle(HeadingTo(u, objective.Value) - u.AirHeading.Value);
                if (turn > maxTurn) turn = maxTurn;
                else if (turn < -maxTurn) turn = -maxTurn;
            }
            else
            {
                turn = maxTurn; // nowhere to be: hold a circle rather than hover
            }

            float heading = NormalizeAngle(u.AirHeading.Value + turn);
            u.AirHeading = heading;

            float nx = u.Position.X + (float)Math.Sin(heading) * stepLen;
            float nz = u.Position.Z + (float)Math.Cos(heading) * stepLen;
            u.Position = ResolveAirPosition(u, nx, nz, stepLen, height, altitude);
        }

        /// <summary>Task154: a fixed-wing aircraft with nothing to do and nowhere to land holds a circle at
        /// cruise altitude. This replaces settling vertically onto whatever happened to be underneath it,
        /// which is a thing only a helicopter can do.</summary>
        private static void AdvanceAirLoiter(UnitInstance u, UnitType type, float stepLen,
            IHeightSampler height)
        {
            AdvanceAirFixedWing(u, type, stepLen, null, height, CruiseAltitude);
        }

        /// <summary>Task154: whether this aircraft is already standing on its home apron or deck - the one
        /// place a fixed-wing aircraft is allowed to come to rest. Shares its notion of "home" with
        /// ResolveHomeObjective, which returns null at exactly this distance.</summary>
        private static bool IsAtHome(WarState state, UnitInstance u, UnitType type)
        {
            WorldPos home;
            float dist;
            if (!TryFindHome(state, u, type, out home, out dist)) return false;
            return dist <= HomeArrivalDistance;
        }
    }
}
