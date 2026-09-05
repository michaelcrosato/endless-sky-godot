using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The travel half of Ship: hyperspace jumps between linked systems.
    /// Port of upstream Ship::IsReadyToJump (Ship.cpp:2467),
    /// DoInitializeMovement's commit (Ship.cpp:4836) and DoHyperspaceLogic
    /// (Ship.cpp:4596). Both drives are here and they are NOT the same journey: a
    /// hyperdrive follows a lane, has to line up with it, and flies a deceleration run
    /// in at the far end; a jump drive tears a hole where the ship is, points anywhere,
    /// and drops it on a random bearing close in.
    ///
    /// INCOMPLETE, tracked rather than dropped: scram drives and their deviation
    /// threshold, escort arrival offsets, and upstream's cheapest-drive-per-destination
    /// selection (attributes are summed here, so a ship with two drives reads the best
    /// of both). See docs/upstream-reference.md, "The jump protocol".
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
        internal double HyperspaceFuelCost => _hyperspaceFuelCost;

        internal void RestoreHyperspace(int count, StarSystem? destination, double fuelCost, bool jumpDrive)
        {
            if (count < 0 || count > HyperspaceFrames || !double.IsFinite(fuelCost) || fuelCost <= 0
                || (count == 0 && destination == null)
                || (count == HyperspaceFrames && destination != null)) return;
            HyperspaceCount = count;
            HyperspaceSystem = destination;
            TargetSystem = destination;
            _hyperspaceFuelCost = fuelCost;
            IsUsingJumpDrive = jumpDrive;
        }

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

        /// <summary>Ship::IsTargetable stops exposing a ship at hyperspace frame 70.</summary>
        public bool IsTargetable => !IsDestroyed && HyperspaceCount < 70;

        /// <summary>Whether the jump in progress is on the jump drive rather than a hyperdrive.</summary>
        public bool IsUsingJumpDrive { get; private set; }

        /// <summary>
        /// Which drive a jump to the current target would use. A hyperdrive takes any
        /// LINKED destination; a jump drive is what goes where the links do not.
        /// </summary>
        public bool WouldUseJumpDrive
        {
            get
            {
                if (!HasJumpDrive)
                    return false;

                bool linked = TargetSystem != null && CurrentSystem != null &&
                              CurrentSystem.Links.Contains(TargetSystem.Name);

                return !(HasHyperdrive && linked);
            }
        }

        public bool HasHyperdrive => Attributes.Get("hyperdrive") > 0.0;

        /// <summary>Whether this ship carries a jump drive of any kind.</summary>
        public bool HasJumpDrive => Attributes.Get("jump drive") > 0.0;

        /// <summary>
        /// How far this ship can jump on its jump drive, in galactic map units.
        /// </summary>
        /// <remarks>
        /// A jump drive does not follow hyperspace links at all: it reaches ANY system
        /// within range on the map, which is what makes alien ships able to cross
        /// regions the human network does not connect. Upstream takes the range from
        /// the drive outfit's "jump range" and falls back to the default neighbour
        /// distance of 100 when the outfit does not state one.
        ///
        /// INCOMPLETE, tracked rather than dropped: upstream picks the cheapest drive
        /// per distance band and applies jump mass costs. Attributes here are summed
        /// across outfits, so the range is the best the ship carries and the fuel cost
        /// is the simple one.
        /// </remarks>
        public double JumpDriveRange
        {
            get
            {
                if (!HasJumpDrive)
                    return 0.0;

                double stated = Attributes.Get("jump range");
                return stated > 0.0 ? stated : DefaultJumpRange;
            }
        }

        /// <summary>Upstream's <c>System::DEFAULT_NEIGHBOR_DISTANCE</c>.</summary>
        public const double DefaultJumpRange = 100.0;

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
                // Which drive pays depends on the destination, not on the ship: a ship
                // with both takes the cheap hyperdrive along a link and only falls back
                // to the jump drive where no link goes.
                bool linked = TargetSystem != null && CurrentSystem != null &&
                              CurrentSystem.Links.Contains(TargetSystem.Name);

                if (HasHyperdrive && (linked || !HasJumpDrive))
                {
                    double explicitCost = Attributes.Get("hyperdrive fuel");
                    if (explicitCost > 0.0)
                        return Math.Max(1.0, explicitCost);

                    double legacy = Attributes.Get("jump fuel");
                    return Math.Max(1.0, legacy > 0.0 ? legacy : DefaultHyperdriveFuel);
                }

                if (HasJumpDrive)
                {
                    double explicitCost = Attributes.Get("jump drive fuel");
                    if (explicitCost > 0.0)
                        return Math.Max(1.0, explicitCost);

                    double legacy = Attributes.Get("jump fuel");
                    return Math.Max(1.0, legacy > 0.0 ? legacy : DefaultJumpDriveFuel);
                }

                return 0.0;
            }
        }

        /// <summary>Upstream's <c>Outfit::DEFAULT_HYPERDRIVE_COST</c>.</summary>
        public const double DefaultHyperdriveFuel = 100.0;

        /// <summary>
        /// Upstream's <c>Outfit::DEFAULT_JUMP_DRIVE_COST</c>. Twice a hyperdrive jump:
        /// going where the links do not is meant to cost more.
        /// </summary>
        public const double DefaultJumpDriveFuel = 200.0;

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
        /// <summary>
        /// Whether this ship's drives can reach a system at all: a hyperdrive follows
        /// the link network, a jump drive ignores it and goes by map distance.
        /// </summary>
        public bool CanReach(StarSystem? destination) => CanReach(CurrentSystem, destination);

        /// <summary>Checks one route edge without moving the ship to that system.</summary>
        public bool CanReach(StarSystem? from, StarSystem? destination)
        {
            if (destination is null || from is null || ReferenceEquals(from, destination))
                return false;

            if (HasHyperdrive && from.Links.Contains(destination.Name))
                return true;

            if (!HasJumpDrive)
                return false;

            // A system may extend the reach of drives inside it; upstream takes the
            // larger of the ship's range and the system's own.
            double range = Math.Max(JumpDriveRange, from.JumpRange);
            return (destination.MapPosition - from.MapPosition).Length <= range;
        }

        /// <summary>Every system this ship could jump to from where it is.</summary>
        public IEnumerable<StarSystem> ReachableSystems(GameData? data)
        {
            if (data is null || CurrentSystem is null)
                yield break;

            foreach (StarSystem system in data.Systems.Values)
                if (!ReferenceEquals(system, CurrentSystem) && CanReach(system))
                    yield return system;
        }

        public bool IsReadyToJump()
        {
            if (IsDisabled || HyperspaceCount != 0 || TargetSystem == null || CurrentSystem == null)
            {
                return false;
            }

            if (!CanReach(TargetSystem))
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

            // A jump drive tears a hole where the ship is; only a HYPERDRIVE has to
            // line up with the lane. Upstream guards the whole facing test with
            // `if(!isJump)` (Ship.cpp:2505), and requiring the turn for both made every
            // jump-drive ship fly a hyperdrive approach it never needed.
            if (WouldUseJumpDrive)
            {
                return true;
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
            IsUsingJumpDrive = WouldUseJumpDrive;
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

            // Power, cooling, repair and reload keep running through a jump. Upstream
            // calls DoGeneration (Ship.cpp:1660) BEFORE DoHyperspaceLogic (:1679), and
            // it is load-bearing pacing rather than a detail: you break off a fight,
            // jump, and arrive with your shields back. This method owns the whole frame
            // — the flight loop returns as soon as it reports true — so with the call
            // missing here, a jump froze every ship in exactly the state it left in.
            StepResources();
            StepArmament();

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
                double extraArrivalDistance = IsUsingJumpDrive
                    ? CurrentSystem!.ExtraJumpArrivalDistance
                    : CurrentSystem!.ExtraHyperArrivalDistance;

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

                if (IsUsingJumpDrive)
                {
                    // A jump drive drops the ship on a random bearing, close in, and is
                    // finished — there is no deceleration run to fly
                    // (Ship.cpp:4679-4691). Sharing the hyperdrive path put every
                    // jump-drive arrival 11,000 units out and made it cross the distance
                    // under its own power, which is a different journey entirely.
                    var bearing = new Angle(RandomUnit() * 360.0);
                    double reach = 300.0 * (RandomUnit() + 1.0) + extraArrivalDistance;

                    Position = target + bearing.Unit() * reach;
                    Velocity = Point.Zero;
                    HyperspaceCount = 0;
                    IsUsingJumpDrive = false;
                    return true;
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

        /// <summary>
        /// The stellar object the pilot has selected to land on, if any. Upstream's
        /// <c>Ship::targetPlanet</c> (<c>Ship.h</c>), the landing counterpart of
        /// <see cref="TargetSystem"/>.
        /// </summary>
        /// <remarks>
        /// This is a SELECTION, not a permission: it says where the pilot intends to
        /// put down, and says nothing about whether they have arrived, slowed down or
        /// are welcome. <see cref="CanLandOn"/> remains the only gate on actually
        /// landing.
        /// </remarks>
        public StellarObject? TargetStellar { get; set; }

        /// <summary>
        /// Whether this ship could ever put down on <paramref name="where"/> — as
        /// opposed to whether it may do so this frame, which is
        /// <see cref="CanLandOn"/>.
        /// </summary>
        /// <remarks>
        /// Upstream's test is <c>object.HasValidPlanet() &amp;&amp;
        /// GetPlanet()->IsAccessible(ship)</c> (AI.cpp:4605). A star, or a body the
        /// dataset names but defines no planet for, is scenery: you can fly into it all
        /// day and never land. This is what separates the objects worth labelling and
        /// cycling through from the ones that are only in the way.
        ///
        /// INCOMPLETE, tracked rather than dropped: <c>IsAccessible</c> also gates on a
        /// world's "requires" attributes, which <see cref="Planet"/> does not model —
        /// the same gap <see cref="CanLandOn"/> already documents.
        /// </remarks>
        public bool CanEverLandOn(StellarObject? where) =>
            where?.Planet is not null && !where.IsStar;

        /// <summary>
        /// Whether landing on <paramref name="where"/> would refuel the ship. Upstream
        /// asks <c>Port::CanRecharge(Fuel)</c> when ranking landing targets
        /// (AI.cpp:4677) and pushes everything else 10,000 units down the list, because
        /// the nearest rock is rarely the world the pilot meant.
        /// </summary>
        public static bool WouldRefuelAt(StellarObject? where) =>
            where?.Planet is { HasServices: true };

        /// <summary>
        /// Whether this ship may put down on a stellar object right now. Port of
        /// upstream <c>Ship::CanLand</c> (<c>Ship.cpp:2344-2358</c>).
        /// </summary>
        /// <remarks>
        /// This is a simulation rule, and it used to live in the flight scene with
        /// constants of its own: a speed limit three times upstream's and a flat reach
        /// that ignored how big the world was. Rules written in the view layer are
        /// invisible to the sim suite AND to the architecture test, because nothing
        /// stops a rule being written on the wrong side of a boundary that only guards
        /// which direction the dependencies point.
        ///
        /// INCOMPLETE, tracked rather than dropped: upstream also asks
        /// <c>Planet::CanLand(ship)</c>, which gates on licences, government access and
        /// a world's "requires" attributes. None of that is modelled on Planet yet, so
        /// any world with a landing site accepts anyone.
        /// </remarks>
        public bool CanLandOn(StellarObject? where, Planet? planet)
        {
            if (where is null || planet is null)
                return false;

            if (IsDisabled || IsDestroyed || IsHyperspacing || IsEnteringHyperspace)
                return false;

            if (Velocity.Length >= LandingSpeed)
                return false;

            return (where.Position - Position).Length < where.LandingRadius;
        }

        /// <summary>Fastest a ship may be moving and still land. Upstream's is 1.</summary>
        public const double LandingSpeed = 1.0;
    }
}
