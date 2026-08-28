using System;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The travel half of Ship: hyperspace jumps between linked systems.
    /// Port of upstream Ship::IsReadyToJump (Ship.cpp:2467),
    /// DoInitializeMovement's commit (Ship.cpp:4836) and DoHyperspaceLogic
    /// (Ship.cpp:4596), hyperdrive path. Jump drives, scram drives, wormholes
    /// and escort offsets are later-milestone surface; where they alter a
    /// check it is noted inline. See docs/upstream-reference.md, "The jump
    /// protocol".
    /// </summary>
    public partial class Ship
    {
        /// <summary>Frames to enter or exit hyperspace (upstream HYPER_C).</summary>
        public const int HyperspaceFrames = 100;

        /// <summary>Hyperspace accel/decel in px/frame² (upstream HYPER_A).</summary>
        public const double HyperspaceAcceleration = 2.0;

        /// <summary>Exit distance margin in px (upstream HYPER_D).</summary>
        public const double HyperspaceExitDistance = 1000.0;

        private double _hyperspaceFuelCost;

        /// <summary>The system this ship is currently in (engine-assigned).</summary>
        public StarSystem? CurrentSystem { get; set; }

        /// <summary>The jump destination the pilot has selected.</summary>
        public StarSystem? TargetSystem { get; set; }

        /// <summary>Committed jump destination; non-null during the outbound phase.</summary>
        public StarSystem? HyperspaceSystem { get; private set; }

        /// <summary>0 when not jumping; counts 1→100 outbound, back down inbound.</summary>
        public int HyperspaceCount { get; private set; }

        public bool IsEnteringHyperspace => HyperspaceSystem != null;

        public bool IsHyperspacing => HyperspaceCount != 0;

        public bool HasHyperdrive => Attributes.Get("hyperdrive") > 0.0;

        /// <summary>Max |velocity| allowed to enter hyperspace ("jump speed").</summary>
        public double JumpSpeedLimit => Attributes.Get("jump speed");

        /// <summary>
        /// Fuel for one hyperdrive jump. Upstream normalizes the legacy
        /// "jump fuel" alias into "hyperdrive fuel" (default 100) per drive
        /// OUTFIT and takes the cheapest drive; attributes here are summed,
        /// so a ship with multiple drives reads high — fine for stock ships,
        /// revisit with ShipJumpNavigation if multi-drive ships matter.
        /// </summary>
        public double JumpFuelCost
        {
            get
            {
                if (!HasHyperdrive)
                {
                    return 0.0;
                }

                double explicitCost = Attributes.Get("hyperdrive fuel");
                if (explicitCost > 0.0)
                {
                    return Math.Max(1.0, explicitCost);
                }

                double legacy = Attributes.Get("jump fuel");
                return Math.Max(1.0, legacy > 0.0 ? legacy : 100.0);
            }
        }

        /// <summary>
        /// Departure direction: between the two systems' galactic map
        /// positions, not anything in-system.
        /// </summary>
        public Point JumpDirection =>
            TargetSystem != null && CurrentSystem != null
                ? TargetSystem.MapPosition - CurrentSystem.MapPosition
                : Point.Zero;

        /// <summary>
        /// Port of Ship::IsReadyToJump for the hyperdrive path: linked target,
        /// fuel, speed at or under "jump speed", and facing within one turn
        /// step of the departure direction (crossing over or landing exactly).
        /// Vanilla has no departure-distance gate (gamerules min is 0).
        /// </summary>
        public bool IsReadyToJump()
        {
            if (IsDisabled || HyperspaceCount != 0 || TargetSystem == null || CurrentSystem == null)
            {
                return false;
            }

            if (!HasHyperdrive || !CurrentSystem.Links.Contains(TargetSystem.Name))
            {
                return false;
            }

            double fuelCost = JumpFuelCost;
            if (fuelCost <= 0.0 || Fuel < fuelCost)
            {
                return false;
            }

            if (Velocity.Length > JumpSpeedLimit)
            {
                return false;
            }

            // Within one turn step of facing the target system, exactly as
            // upstream: turn toward the direction and require the turn to
            // cross over it (or land on it, quantized).
            Point direction = JumpDirection;
            bool left = direction.Cross(Facing.Unit()) < 0.0;
            Angle turned = Facing + new Angle(TurnRate * (left ? 1.0 : -1.0));
            bool stillLeft = direction.Cross(turned.Unit()) < 0.0;
            if (left == stillLeft && turned != Angle.FromPoint(direction))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// The commit point (upstream DoInitializeMovement): latch the jump if
        /// every gate passes. The ship still moves normally on the commit
        /// frame; the sequence starts next frame.
        /// </summary>
        public bool TryCommitJump()
        {
            if (!IsReadyToJump())
            {
                return false;
            }

            HyperspaceSystem = TargetSystem;
            _hyperspaceFuelCost = JumpFuelCost;
            return true;
        }

        /// <summary>
        /// Port of DoHyperspaceLogic. Call FIRST each frame; when it returns
        /// true the frame is consumed (no turning, thrust, or drag). The
        /// caller detects arrival by watching <see cref="CurrentSystem"/>
        /// change, and owns the date advance that upstream performs in
        /// Engine::EnterSystem.
        /// </summary>
        public bool StepHyperspace()
        {
            if (HyperspaceSystem == null && HyperspaceCount == 0)
            {
                return false;
            }

            int direction = HyperspaceSystem != null ? 1 : -1;
            HyperspaceCount += direction;
            if (HyperspaceSystem != null)
            {
                // Fuel drains over the outbound frames, not as a lump sum.
                Fuel -= _hyperspaceFuelCost / HyperspaceFrames;
            }

            if (HyperspaceCount == HyperspaceFrames)
            {
                // Arrival: switch system, teleport short of the target along the
                // preserved facing, snap velocity onto it.
                CurrentSystem = HyperspaceSystem;
                HyperspaceSystem = null;
                TargetSystem = null;

                // The arrival target is the system centre by DEFAULT. Upstream aims at
                // a planet only when the system sets no extra arrival distance - a
                // system that sets one does so precisely to keep arrivals away from its
                // inhabited worlds, and aiming at a planet anyway drops ships on top of
                // what the setting exists to prevent.
                double extraArrivalDistance = CurrentSystem!.ExtraHyperArrivalDistance;

                Point target = Point.Zero;
                if (extraArrivalDistance == 0.0)
                {
                    foreach (StellarObject obj in CurrentSystem.AllObjects())
                    {
                        // Upstream requires a planet with SERVICES, not merely a named
                        // one. Uninhabited rocks and moons carry names too, and aiming
                        // at the first of those drops arrivals nowhere near the port
                        // the player is heading for.
                        if (obj.Planet is { HasServices: true })
                        {
                            target = obj.Position;
                            break;
                        }
                    }
                }

                double distance = HyperspaceFrames * HyperspaceFrames * 0.5 * HyperspaceAcceleration
                                  + HyperspaceExitDistance
                                  + extraArrivalDistance;
                Position = target - Facing.Unit() * distance;
                Velocity = Facing.Unit() * Velocity.Length;
                direction = -1;
            }

            Velocity += Facing.Unit() * (HyperspaceAcceleration * direction);
            if (direction < 0)
            {
                // Exit once slow enough to stop before the planet: the same
                // over-estimate quadratic upstream uses.
                double exitV = Math.Max(HyperspaceAcceleration, MaxVelocity);
                double a = 0.5 / Acceleration - 0.25;
                double b = 150.0 / TurnRate;
                double discriminant = b * b + 4.0 * a * HyperspaceExitDistance;
                if (discriminant > 0.0)
                {
                    double altV = (-b + Math.Sqrt(discriminant)) / (2.0 * a);
                    if (altV > 0.0 && altV < exitV)
                    {
                        exitV = altV;
                    }
                }

                if (Velocity.Dot(Facing.Unit()) <= exitV)
                {
                    Velocity = Facing.Unit() * exitV;
                    HyperspaceCount = 0;
                }
            }

            Position += Velocity;
            return true;
        }
    }
}
