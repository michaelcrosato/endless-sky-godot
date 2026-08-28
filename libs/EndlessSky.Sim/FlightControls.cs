using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The player-input → <see cref="Command"/> translation upstream performs
    /// in <c>AI::MovePlayer</c> (AI.cpp:4809-4815). The sim's <c>Ship::Step</c>
    /// faithfully ignores a reverse command on a ship with no reverse
    /// thruster; upstream's *input layer* is what converts a held BACK key on
    /// such a ship into a full-rate retrograde turn. Without this translation
    /// the reverse key is simply dead on most ships (the stock Shuttle
    /// included), and controlled deceleration is impossible.
    /// </summary>
    public static class FlightControls
    {
        /// <summary>
        /// Port of <c>AI::TurnToward</c> (AI.cpp:2559, default precision
        /// 0.9999 per AI.h:161): the turn command, in [-1, 1], that best faces
        /// the ship along <paramref name="vector"/> this frame. Preserved
        /// upstream edge case: at zero vector this returns -1 (the ship spins
        /// clockwise), because dot is not &gt; 0 and cross is not &lt; 0.
        /// </summary>
        public static double TurnToward(Ship ship, Point vector, double precision = 0.9999)
        {
            Point facing = ship.Facing.Unit();
            double cross = vector.Cross(facing);
            double dot = vector.Dot(facing);
            if (dot > 0.0)
            {
                bool close = precision < 1.0 && precision > 0.0 &&
                             dot * dot >= precision * vector.LengthSquared;
                double angle = Math.Asin(Math.Clamp(cross / vector.Length, -1.0, 1.0)) * (180.0 / Math.PI);
                if (Math.Abs(angle) < ship.TurnRate)
                {
                    return close ? 0.0 : -angle / ship.TurnRate;
                }
            }

            bool left = cross < 0.0;
            return left ? 1.0 : -1.0; // upstream `left - !left`
        }

        /// <summary>Port of <c>AI::TurnBackward</c> (AI.cpp:2549): face retrograde.</summary>
        public static double TurnBackward(Ship ship) => TurnToward(ship, -ship.Velocity);

        /// <summary>
        /// Build one frame's player command from held keys, applying the
        /// upstream BACK translation: reverse-thrust ships get a true reverse
        /// command (unless also thrusting forward); everything else turns
        /// retrograde, but never overriding a held turn key.
        /// </summary>
        public static Command BuildPlayerCommand(Ship ship, bool forward, bool back, double turnInput)
        {
            var command = new Command { Forward = forward, Turn = turnInput };
            if (back)
            {
                if (!forward && ship.ReverseThrust != 0.0)
                {
                    command.Back = true;
                }
                else if (turnInput == 0.0)
                {
                    command.Turn = TurnBackward(ship);
                }
            }

            return command;
        }
    }
}
