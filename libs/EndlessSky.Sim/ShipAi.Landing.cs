using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The landing half of the pilot: choosing somewhere in this system to put down,
    /// and flying there. Ports of upstream's LAND key handling (<c>AI.cpp:4590-4726</c>),
    /// <c>AI::MoveToPlanet</c> (<c>AI.cpp:2592</c>), <c>AI::MoveTo</c>
    /// (<c>AI.cpp:2604</c>) and <c>AI::StoppingPoint</c> (<c>AI.cpp:3669</c>).
    /// </summary>
    /// <remarks>
    /// Kept apart from the engagement core in <c>ShipAi.cs</c> because it is a separate
    /// job with separate rules; the two only share the ship.
    ///
    /// INCOMPLETE, tracked rather than dropped: upstream ranks by
    /// <c>Port::CanRecharge(Fuel)</c> and consults <c>Planet::CanLand</c> for landing
    /// permission, warning the pilot when the authorities refuse. <see cref="Planet"/>
    /// models neither licences nor government access yet — the same gap
    /// <see cref="Ship.CanLandOn"/> documents — so every world with a landing site
    /// accepts anyone, and "can it refuel me" stands in for the ranking.
    /// </remarks>
    public static partial class ShipAi
    {
        /// <summary>
        /// How far upstream pushes a world that cannot refuel you when ranking landing
        /// targets (<c>AI.cpp:4677</c>). Large enough that the nearest bare rock loses
        /// to a port most of the way across a system, which is the intent: a pilot who
        /// asks to land almost always means somewhere with a fuel pump.
        /// </summary>
        public const double UnservicedLandingPenalty = 10000.0;

        /// <summary>
        /// Below this speed, a ship sitting over a world counts as trying to land on
        /// it. Upstream's <c>MIN_LANDING_VELOCITY / 60</c> (<c>AI.h:255</c>).
        /// </summary>
        /// <remarks>
        /// Deliberately looser than <see cref="Ship.LandingSpeed"/>: a pilot drifting
        /// over a world is CONSIDERING it slightly before they are legally able to put
        /// down, and the selection should agree with them rather than send them
        /// somewhere else at the last moment.
        /// </remarks>
        public const double HoveringSpeed = 80.0 / 60.0;

        /// <summary>
        /// Port of the LAND key's target selection (<c>AI.cpp:4590-4726</c>): pick
        /// somewhere in this system to put down, or step to the next candidate.
        /// </summary>
        /// <param name="cycle">
        /// False for a fresh ask — take the best world. True to step to the NEXT one,
        /// which is what a second press means. Upstream tells the two apart with a
        /// key-repeat cooldown in its input layer (<c>Engine.cpp:2241</c>); leaving it a
        /// parameter keeps that an input decision and this a rule.
        /// </param>
        /// <remarks>
        /// The ranking is the part worth reading twice. Plain "nearest object" is the
        /// obvious rule and the wrong one: systems are full of bare rocks and moons a
        /// ship CAN land on and a pilot almost never means, and one of them is usually
        /// closer than the port. Upstream ranks by distance with everything that cannot
        /// refuel you pushed <see cref="UnservicedLandingPenalty"/> down the list, so
        /// the rocks stay reachable by cycling but are never chosen for you.
        /// </remarks>
        public static LandingChoice SelectLandingTarget(Ship? ship, bool cycle = false)
        {
            if (ship?.CurrentSystem is null)
                return LandingChoice.None("There is nowhere to land.");

            // The selection must be one of the CURRENT system's objects (AI.cpp:4616).
            // Without that check a target survives a jump, and the autopilot then flies
            // at coordinates that mean nothing in the system the ship is now in.
            List<StellarObject> landables = ship.CurrentSystem.AllObjects()
                .Where(ship.CanEverLandOn)
                .ToList();

            if (landables.Count == 0)
            {
                ship.TargetStellar = null;
                return LandingChoice.None("There is nowhere to land in this system.");
            }

            int index = ship.TargetStellar is null
                ? -1
                : landables.FindIndex(o => ReferenceEquals(o, ship.TargetStellar));
            StellarObject? target = index >= 0 ? landables[index] : null;

            if (target is not null && cycle)
            {
                // Upstream's special case (AI.cpp:4637): with a target selected and the
                // ship already inside its radius, pressing land again must NOT toggle
                // away — otherwise the last press before touchdown throws the approach
                // away and sends the ship back across the system.
                if ((target.Position - ship.Position).Length < target.LandingRadius)
                    return new LandingChoice(target, $"Landing on {NameOf(target)}.");

                target = landables[(index + 1) % landables.Count];
                ship.TargetStellar = target;
                return new LandingChoice(target,
                    $"Switching landing targets. Now landing on {NameOf(target)}.");
            }

            if (target is not null)
                return new LandingChoice(target, $"Landing on {NameOf(target)}.");

            // A pilot drifting over a world means THAT world (AI.cpp:4604). Without
            // this, flying yourself onto a moon and pressing land selects the nearest
            // port instead and turns the ship around — the selection overriding the
            // player at the one moment they have been explicit about what they want.
            if (ship.Velocity.Length < HoveringSpeed)
            {
                foreach (StellarObject candidate in landables)
                {
                    if ((candidate.Position - ship.Position).Length < candidate.LandingRadius)
                    {
                        ship.TargetStellar = candidate;
                        return new LandingChoice(candidate, $"Landing on {NameOf(candidate)}.");
                    }
                }
            }

            StellarObject best = landables[0];
            double closest = double.PositiveInfinity;
            foreach (StellarObject candidate in landables)
            {
                double distance = (candidate.Position - ship.Position).Length;
                if (!Ship.WouldRefuelAt(candidate))
                    distance += UnservicedLandingPenalty;

                if (distance < closest)
                {
                    closest = distance;
                    best = candidate;
                }
            }

            ship.TargetStellar = best;
            return new LandingChoice(best, landables.Count > 1
                ? $"You can land on more than one world here. Landing on {NameOf(best)}."
                : $"Landing on {NameOf(best)}.");
        }

        private static string NameOf(StellarObject obj) => obj.PlanetName ?? "this world";

        /// <summary>
        /// Port of <c>AI::MoveToPlanet</c> (<c>AI.cpp:2592</c>): one frame of flying to
        /// the ship's <see cref="Ship.TargetStellar"/>. <paramref name="arrived"/> goes
        /// true once the ship is inside the landing radius and slow enough to put down
        /// — the same two conditions <see cref="Ship.CanLandOn"/> gates on.
        /// </summary>
        public static Command MoveToPlanet(Ship? ship, out bool arrived, double cruiseSpeed = 0.0)
        {
            arrived = false;
            if (ship?.TargetStellar is null)
                return Command.None;

            return MoveTo(ship, ship.TargetStellar.Position, Point.Zero,
                          ship.TargetStellar.LandingRadius, Ship.LandingSpeed,
                          out arrived, cruiseSpeed);
        }

        /// <summary>
        /// Port of <c>AI::MoveTo</c> (<c>AI.cpp:2604</c>): fly to a point, and be slow
        /// when you get there.
        /// </summary>
        /// <remarks>
        /// The steering is aimed at where the ship would COME TO REST if it turned
        /// around and braked now (<see cref="StoppingPoint"/>), not at the target
        /// itself. That one substitution is what makes an approach converge: steering
        /// straight at a destination means arriving at full speed and sailing past it,
        /// then turning round and doing it again.
        /// </remarks>
        public static Command MoveTo(Ship? ship, Point targetPosition, Point targetVelocity,
                                     double radius, double slow, out bool arrived,
                                     double cruiseSpeed = 0.0)
        {
            arrived = false;
            var command = new Command();
            if (ship is null)
                return command;

            Point position = ship.Position;
            Point velocity = ship.Velocity;
            Angle angle = ship.Facing;
            Point dp = targetPosition - position;
            double speed = (targetVelocity - velocity).Length;

            bool isClose = dp.Length < radius;
            if (isClose && speed < slow)
            {
                arrived = true;
                return command;
            }

            dp = targetPosition - StoppingPoint(ship, targetVelocity, out bool shouldReverse);

            Point tv = dp;
            bool hasCruiseSpeed = cruiseSpeed > 0.0;
            if (hasCruiseSpeed)
            {
                tv = dp.Unit() * cruiseSpeed - velocity;
                if (tv.LengthSquared < 0.01)
                    tv = dp;
            }

            bool isFacing = tv.Unit().Dot(angle.Unit()) > 0.95;
            if (!isClose || (!isFacing && !shouldReverse))
                command.Turn = FlightControls.TurnToward(ship, tv);

            // Drag acts only while thrusting, so a ship already at its top speed stops
            // burning fuel to stay there.
            double maxVelocity = ship.MaxVelocity * 0.99;
            if (isFacing && (velocity.LengthSquared <= maxVelocity * maxVelocity ||
                             dp.Unit().Dot(velocity.Unit()) < 0.95))
            {
                bool movingTowardsTarget = velocity.Unit().Dot(dp.Unit()) > 0.95;
                if (!hasCruiseSpeed || !movingTowardsTarget || velocity.Length < cruiseSpeed)
                    command.Forward = true;
            }
            else if (shouldReverse)
            {
                command.Turn = FlightControls.TurnToward(ship, velocity);
                command.Back = true;
            }

            return command;
        }

        /// <summary>
        /// Port of <c>AI::StoppingPoint</c> (<c>AI.cpp:3669</c>): where this ship would
        /// end up if it turned around and braked to <paramref name="targetVelocity"/>
        /// starting now, and whether reverse thrust would get there sooner.
        /// </summary>
        public static Point StoppingPoint(Ship? ship, Point targetVelocity, out bool shouldReverse)
        {
            shouldReverse = false;
            if (ship is null)
                return Point.Zero;

            Point position = ship.Position;
            Point velocity = ship.Velocity - targetVelocity;
            double v = velocity.Length;
            if (v == 0.0)
                return position;

            double acceleration = ship.Acceleration;
            double turnRate = ship.TurnRate;
            if (acceleration <= 0.0 || turnRate <= 0.0)
                return position;

            // Assume the ship is facing exactly the wrong way, then add the braking run:
            // v + (v − a) + (v − 2a) + ... + 0, which is v/a terms averaging v/2.
            double degreesToTurn = Degrees(-velocity.Unit().Dot(ship.Facing.Unit()));
            double stopDistance = v * (degreesToTurn / turnRate) + 0.5 * v * v / acceleration;

            double reverseAcceleration = ship.ReverseAcceleration;
            if (reverseAcceleration > 0.0)
            {
                double reverseDistance = v * (180.0 - degreesToTurn) / turnRate +
                                         0.5 * v * v / reverseAcceleration;
                if (reverseDistance < stopDistance)
                {
                    shouldReverse = true;
                    stopDistance = reverseDistance;
                }
            }

            return position + velocity.Unit() * stopDistance;
        }
    }

    /// <summary>
    /// The outcome of asking for somewhere to land: what got selected, and what to tell
    /// the pilot. Upstream posts the text to its message log; a caller without one can
    /// put it anywhere the player will read it.
    /// </summary>
    public readonly struct LandingChoice
    {
        public LandingChoice(StellarObject? target, string message)
        {
            Target = target;
            Message = message;
        }

        /// <summary>The object now selected, or null when there was nothing to pick.</summary>
        public StellarObject? Target { get; }

        /// <summary>What to show the pilot. Never empty.</summary>
        public string Message { get; }

        public bool Succeeded => Target is not null;

        internal static LandingChoice None(string message) => new LandingChoice(null, message);
    }
}
