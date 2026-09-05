using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A standing order given to the player's escorts. Port of the subset of
    /// upstream's <c>AI::Orders</c> that a headless simulation can carry out.
    /// </summary>
    public enum FleetOrder
    {
        /// <summary>Fly in company with the flagship. The default.</summary>
        Escort,

        /// <summary>Close on the flagship and stop there.</summary>
        Gather,

        /// <summary>Stop where you are and stay put.</summary>
        Hold,

        /// <summary>Attack whatever the flagship is attacking.</summary>
        AttackTarget,
    }

    /// <summary>
    /// Fleet commands: what the player's other ships are told to do.
    /// </summary>
    /// <remarks>
    /// The directive names "escorts" and "fleet commands" under Milestone 6. Escorts
    /// existed as a list; nothing turned that list into behaviour, so the player's
    /// other ships sat inert wherever they were left.
    ///
    /// Movement is upstream's <c>AI::MoveTo</c>, including its station-keeping
    /// constants: escorts hold formation within 40 units and settle when their
    /// relative speed drops below 0.8. Those numbers are why an Endless Sky fleet
    /// clusters loosely around its flagship rather than piling into it or trailing in
    /// a line.
    ///
    /// Steering aims at the ship's STOPPING POINT rather than its current position,
    /// which is what turns following into formation-keeping: without it an escort runs
    /// at its flagship flat out, sails past, turns and sails past again, converging on
    /// an orbit instead of a station.
    ///
    /// PlayerFleet.StepEscorts coordinates these commands with independent jumps.
    /// INCOMPLETE, tracked rather than dropped: named formation patterns, in-flight
    /// cargo and fighter transfer, per-ship landing and orders for selected groups.
    /// </remarks>
    public static class FleetOrders
    {
        /// <summary>How close an escort tries to stay to its flagship.</summary>
        public const double StationRadius = 40.0;

        /// <summary>Relative speed below which an escort considers itself settled.</summary>
        public const double SettledSpeed = 0.8;

        /// <summary>
        /// The command one escort should execute this frame.
        /// </summary>
        public static Command For(FleetOrder order, Ship? self, Ship? flagship, Ship? target = null)
        {
            if (self is null || self.IsDisabled)
                return Command.None;

            switch (order)
            {
                case FleetOrder.AttackTarget when target is not null && !target.IsDestroyed:
                    return ShipAi.Attack(self, target);

                case FleetOrder.Hold:
                    return Stop(self);

                case FleetOrder.Gather:
                case FleetOrder.Escort:
                default:
                    if (flagship is null || ReferenceEquals(flagship, self))
                        return Command.None;

                    return MoveTo(self, flagship.Position, flagship.Velocity,
                                  StationRadius, SettledSpeed);
            }
        }

        /// <summary>
        /// Issues an order to every escort and steps them. Returns how many moved.
        /// </summary>
        public static int Execute(PlayerFleet? fleet, FleetOrder order, Ship? target = null)
        {
            if (fleet is null)
                return 0;

            int moved = 0;
            foreach (Ship escort in new List<Ship>(fleet.Escorts))
            {
                Command command = For(order, escort, fleet.Flagship, target);
                escort.Step(command);
                if (command.Forward || command.Turn != 0.0 || command.Back)
                    moved++;
            }

            return moved;
        }

        /// <summary>
        /// Flies toward a moving point, port of upstream <c>AI::MoveTo</c> without the
        /// cruise-speed path.
        /// </summary>
        /// <remarks>
        /// The arrival test is on RELATIVE velocity, not on distance alone. A ship
        /// keeping station with a moving flagship is at rest with respect to it while
        /// both are travelling fast, and testing absolute speed would have escorts
        /// endlessly correcting a formation they were already holding.
        /// </remarks>
        public static Command MoveTo(Ship self, Point targetPosition, Point targetVelocity,
                                     double radius, double slow)
        {
            if (self is null)
                return Command.None;

            Point dp = targetPosition - self.Position;
            Point dv = targetVelocity - self.Velocity;

            bool isClose = dp.Length < radius;
            if (isClose && dv.Length < slow)
                return Command.None;

            // Steer for where the ship would COAST TO if it began braking now, not for
            // where it currently is. Without this an escort aims at its flagship at
            // full speed, sails past, turns around and sails past again - it converges
            // to an orbit instead of a formation.
            dp = targetPosition - StoppingPoint(self, targetVelocity, out bool shouldReverse);

            var command = new Command();
            bool isFacing = dp.Unit().Dot(self.Facing.Unit()) > 0.95;

            if (!isClose || (!isFacing && !shouldReverse))
                command.Turn = FlightControls.TurnToward(self, dp);

            // Drag only applies under thrust, so a ship at cruising speed stops burning
            // to save power - unless it is not actually heading where it wants to go.
            double maxVelocity = self.MaxVelocity * 0.99;
            if (isFacing && (self.Velocity.LengthSquared <= maxVelocity * maxVelocity ||
                             dp.Unit().Dot(self.Velocity.Unit()) < 0.95))
            {
                command.Forward = true;
            }
            else if (shouldReverse)
            {
                // Braking on the reverse thruster is quicker than turning around.
                command.Turn = FlightControls.TurnToward(self, self.Velocity);
                command.Back = true;
            }

            return command;
        }

        /// <summary>
        /// Where this ship would come to rest relative to a target if it started
        /// stopping now. Port of upstream <c>AI::StoppingPoint</c>.
        /// </summary>
        /// <remarks>
        /// The distance is the turn plus the burn: how far the ship travels while
        /// swinging around to face retrograde, plus the distance covered decelerating
        /// from there. A ship with reverse thrusters compares the two and takes
        /// whichever is shorter, which is why some hulls brake without turning at all.
        /// </remarks>
        public static Point StoppingPoint(Ship self, Point targetVelocity, out bool shouldReverse)
        {
            shouldReverse = false;
            Point velocity = self.Velocity - targetVelocity;

            double v = velocity.Length;
            if (v <= 0.0)
                return self.Position;

            double acceleration = self.Acceleration;
            double turnRate = self.TurnRate;
            if (acceleration <= 0.0 || turnRate <= 0.0)
                return self.Position;

            // Assumes the ship is currently facing exactly the wrong way.
            double degreesToTurn = Math.Acos(
                Math.Clamp(-velocity.Unit().Dot(self.Facing.Unit()), -1.0, 1.0)) * (180.0 / Math.PI);

            // Sum of v + (v - a) + (v - 2a) + ... + 0, which averages v/2 over v/a terms.
            double stopDistance = v * (degreesToTurn / turnRate) + 0.5 * v * v / acceleration;

            if (self.ReverseThrust > 0.0)
            {
                double reverseAcceleration = self.ReverseAcceleration;
                if (reverseAcceleration > 0.0)
                {
                    double reverseDistance = v * (180.0 - degreesToTurn) / turnRate
                                             + 0.5 * v * v / reverseAcceleration;

                    if (reverseDistance < stopDistance)
                    {
                        shouldReverse = true;
                        stopDistance = reverseDistance;
                    }
                }
            }

            return self.Position + velocity.Unit() * stopDistance;
        }

        /// <summary>
        /// Kills velocity. Port of upstream <c>AI::Stop</c> (<c>AI.cpp:2670-2726</c>).
        /// </summary>
        /// <remarks>
        /// Thrust waits until the ship is actually pointed the right way. Burning while
        /// still turning around means the first thing a hold order does is push the
        /// escort further from where it was told to hold, and — because thrust in the
        /// wrong direction adds speed faster than the turn can correct — it may never
        /// converge at all.
        ///
        /// The tolerance is upstream's, and it is a fudge factor rather than a
        /// principle: 0.8 when the stop will take many frames, tightening toward 1 as
        /// it gets short, so a ship nearly stopped has to be aimed nearly perfectly
        /// before it spends its last burn.
        ///
        /// A reverse thruster is preferred when turning 180 degrees would cost more
        /// time than braking from where the ship already points.
        /// </remarks>
        private static Command Stop(Ship self)
        {
            var command = new Command();
            ShipAi.Stop(self, ref command, 0.0);
            return command;
        }
    }
}
