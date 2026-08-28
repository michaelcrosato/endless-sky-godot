using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>Per-frame pilot input. Mirrors upstream's <c>Command</c> for the flight subset.</summary>
    public struct Command
    {
        /// <summary>Forward thrust held.</summary>
        public bool Forward;

        /// <summary>Reverse thrust held (only acts if the ship has reverse thrust).</summary>
        public bool Back;

        /// <summary>Turn amount in [-1, 1]. Negative is left (counter-clockwise on screen).</summary>
        public double Turn;

        /// <summary>Auto-brake: allows the "cheat to stop" behaviour upstream uses.</summary>
        public bool Stop;

        public static readonly Command None = default;
    }

    /// <summary>
    /// A ship in flight. Port of the movement half of upstream <c>Ship</c>.
    ///
    /// The simulation is fixed-step at <see cref="FramesPerSecond"/>; upstream's data
    /// values (thrust, turn, drag) are all per-frame quantities at 60 fps, so running
    /// at any other rate would change the handling of every ship in the game.
    ///
    /// Deliberately preserved upstream behaviours that look like bugs but are not:
    ///
    ///  * A ship under no thrust does NOT slow down. Drag is applied only inside the
    ///    acceleration block, so coasting is lossless. Only disabled ships drift to a halt.
    ///  * Drag is scaled by the dot-product term, which softens drag when it opposes
    ///    the commanded thrust direction.
    /// </summary>
    public partial class Ship
    {
        public const double FramesPerSecond = 60.0;

        private readonly List<Outfit> _outfits = new List<Outfit>();

        public Ship(ShipDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Attributes = new Attributes();
            Attributes.Add(definition.Attributes);
        }

        public ShipDefinition Definition { get; }

        /// <summary>Hull attributes plus every installed outfit's attributes.</summary>
        public Attributes Attributes { get; }

        public IReadOnlyList<Outfit> Outfits => _outfits;

        public Point Position;
        public Point Velocity;
        public Angle Facing;

        public bool IsDisabled { get; set; }

        /// <summary>Set each step; true while forward thrust was actually applied (drives engine flares).</summary>
        public bool IsThrusting { get; private set; }

        public bool IsReversing { get; private set; }

        public bool IsSteering { get; private set; }

        /// <summary>Sign of the steering applied this step, for visual banking.</summary>
        public double SteeringDirection { get; private set; }

        /// <summary>
        /// Adds an outfit to this ship, loading it into a hardpoint if it is a weapon
        /// and one is free.
        /// </summary>
        /// <param name="arm">
        /// False when the caller has already placed the weapon itself, so it is not
        /// loaded into a second hardpoint.
        /// </param>
        public void AddOutfit(Outfit outfit, int count = 1, bool arm = true)
        {
            for (int i = 0; i < count; i++)
            {
                _outfits.Add(outfit);
            }

            Attributes.Add(outfit.Attributes, count);

            // Giving a ship a gun has to put the gun on the ship. Upstream's Armament
            // takes every weapon outfit as it is installed; keeping the two apart meant
            // a ship could carry weapons it had no way to fire.
            if (arm && outfit.Weapon is not null && outfit.Weapon.IsWeapon)
                for (int i = 0; i < count; i++)
                    ArmMount(outfit);
        }

        // --- Derived quantities, matching upstream accessor semantics -------------

        public double Mass => Attributes.Get("mass") + CargoMass;

        /// <summary>Cargo currently carried, in tons. Contributes to mass exactly like outfit mass.</summary>
        public double CargoMass { get; set; }

        private double InertiaReduction => 1.0 + Attributes.Get("inertia reduction");

        public double InertialMass => Mass / InertiaReduction;

        private double AccelerationMultiplier => 1.0 + Attributes.Get("acceleration multiplier");

        private double TurnMultiplier => 1.0 + Attributes.Get("turn multiplier");

        private double DragReduction => 1.0 + Attributes.Get("drag reduction");

        public double Thrust => Attributes.Get("thrust");

        public double ReverseThrust => Attributes.Get("reverse thrust");

        public double TurnTorque => Attributes.Get("turn");

        /// <summary>Effective drag, clamped to mass so a ship can never reverse under drag.</summary>
        public double Drag
        {
            get
            {
                double drag = Attributes.Get("drag") / DragReduction;
                double mass = InertialMass;
                return drag >= mass ? mass : drag;
            }
        }

        /// <summary>Drag as a per-frame fraction of velocity.</summary>
        public double DragForce
        {
            get
            {
                double drag = Attributes.Get("drag") / DragReduction;
                double mass = InertialMass;
                return drag >= mass ? 1.0 : drag / mass;
            }
        }

        public double Acceleration => Thrust / InertialMass * AccelerationMultiplier;

        public double ReverseAcceleration => ReverseThrust / InertialMass * AccelerationMultiplier;

        /// <summary>Degrees of turn per frame at full deflection.</summary>
        public double TurnRate => TurnTorque / InertialMass * TurnMultiplier;

        /// <summary>Terminal speed in units per frame: thrust balanced against drag.</summary>
        public double MaxVelocity => Drag > 0.0 ? Thrust / Drag : double.PositiveInfinity;

        // --- Simulation ----------------------------------------------------------

        /// <summary>
        /// Advances one simulation frame. Port of upstream <c>Ship::DoMovement</c>
        /// followed by the <c>position += velocity</c> in <c>Ship::Move</c>.
        /// </summary>
        public void Step(Command command)
        {
            // Reload clocks advance with the ship, as upstream's Ship::Move does
            // (armament.Step). Leaving it to the caller meant it was simply forgotten:
            // the flight scene stepped the drone's armament and not the player's, so
            // the player could fire each gun exactly once and then never again, and
            // every scenario written against the simulation had the same hole.
            StepArmament();

            // Power, cooling and repair advance with the ship too, and must run before
            // this frame's manoeuvring spends anything.
            StepResources();

            IsThrusting = false;
            IsReversing = false;
            IsSteering = false;
            SteeringDirection = 0.0;

            Point acceleration = Point.Zero;
            double dragForce = DragForce;

            if (IsDisabled)
            {
                // A disabled ship can only coast to a stop.
                Velocity *= 1.0 - dragForce;
                Position += Velocity;
                return;
            }

            if (command.Turn != 0.0)
            {
                double turn = Math.Max(-1.0, Math.Min(1.0, command.Turn));

                // Manoeuvring costs energy and makes heat. A ship short of power turns
                // at a FRACTION of its rate rather than not at all, which is upstream's
                // FractionalUsage: the ship stays controllable as its reactor browns
                // out instead of locking solid.
                turn *= AffordableFraction(
                    Attributes.Get("turning energy"), Attributes.Get("turning heat"));

                if (turn != 0.0)
                {
                    IsSteering = true;
                    SteeringDirection = turn;
                    Spend(Attributes.Get("turning energy"), Attributes.Get("turning heat"),
                          Math.Abs(turn));
                    Facing += new Angle(turn * TurnRate);
                }
            }

            double thrustCommand = (command.Forward ? 1.0 : 0.0) - (command.Back ? 1.0 : 0.0);
            if (thrustCommand != 0.0)
            {
                bool forward = thrustCommand > 0.0;
                double thrust = forward ? Thrust : ReverseThrust;

                double energyCost = forward
                    ? Attributes.Get("thrusting energy")
                    : Attributes.Get("reverse thrusting energy");
                double heatCost = forward
                    ? Attributes.Get("thrusting heat")
                    : Attributes.Get("reverse thrusting heat");

                thrustCommand *= AffordableFraction(energyCost, heatCost);

                // Upstream ignores a reverse command on a ship with no reverse thruster
                // entirely - the ship does not even slow under drag.
                if (thrust != 0.0 && thrustCommand != 0.0)
                {
                    IsThrusting = forward;
                    IsReversing = !forward;
                    Spend(energyCost, heatCost, Math.Abs(thrustCommand));
                    acceleration += Facing.Unit() * thrustCommand *
                                    (forward ? Acceleration : ReverseAcceleration);
                }
            }

            if (acceleration.IsNonZero)
            {
                // The acceleration multiplier must also scale drag, or it would change
                // the ship's top speed rather than just how fast it gets there.
                Point dragAcceleration = acceleration - Velocity * dragForce * AccelerationMultiplier;

                if (dragAcceleration.IsNonZero)
                {
                    // Soften drag when it opposes the commanded thrust direction.
                    dragAcceleration *= 0.5 * (acceleration.Unit().Dot(dragAcceleration.Unit()) + 1.0);

                    if (command.Stop)
                    {
                        // A ship may "cheat" to a dead stop only when it is slow enough
                        // to stop within this frame, which avoids overshoot oscillation.
                        double vNormal = Velocity.Dot(Facing.Unit());
                        double aNormal = dragAcceleration.Dot(Facing.Unit());
                        if (aNormal > 0.0 != vNormal > 0.0 && Math.Abs(aNormal) > Math.Abs(vNormal))
                        {
                            dragAcceleration = -vNormal * Facing.Unit();
                        }
                    }

                    Velocity += dragAcceleration;
                }
            }

            Position += Velocity;
        }

        /// <summary>
        /// Removes an outfit, undoing everything <see cref="AddOutfit"/> did.
        /// </summary>
        /// <remarks>
        /// Unloading the hardpoint matters as much as the attributes: an outfitter that
        /// took the gun off the books but left it on the mount would leave a ship
        /// firing a weapon it no longer owns.
        /// </remarks>
        public int RemoveOutfit(Outfit outfit, int count = 1)
        {
            if (outfit is null || count <= 0)
                return 0;

            int removed = 0;
            for (int i = 0; i < count && _outfits.Remove(outfit); i++)
                removed++;

            if (removed == 0)
                return 0;

            Attributes.Add(outfit.Attributes, -removed);

            if (outfit.Weapon is not null && outfit.Weapon.IsWeapon)
            {
                int toClear = removed;
                foreach (WeaponMount mount in _mounts)
                {
                    if (toClear == 0)
                        break;

                    if (ReferenceEquals(mount.InstalledOutfit, outfit))
                    {
                        mount.Uninstall();
                        toClear--;
                    }
                }
            }

            return removed;
        }

        /// <summary>
        /// How much of a commanded manoeuvre this ship can currently pay for, in
        /// [0, 1]. Port of upstream's <c>ResourceLevels::FractionalUsage</c>.
        /// </summary>
        /// <remarks>
        /// Upstream scales the command rather than refusing it, so a ship low on power
        /// turns and accelerates weakly instead of freezing. A cost of zero is free and
        /// always affordable, which is what an unpowered manoeuvring system means.
        /// </remarks>
        private double AffordableFraction(double energyCost, double heatCost)
        {
            if (energyCost <= 0.0)
                return 1.0;

            if (Energy <= 0.0)
                return 0.0;

            return Math.Min(1.0, Energy / energyCost);
        }

        /// <summary>Pays for a manoeuvre performed at <paramref name="fraction"/> of full.</summary>
        private void Spend(double energyCost, double heatCost, double fraction)
        {
            if (energyCost > 0.0)
                Energy = Math.Max(0.0, Energy - energyCost * fraction);

            if (heatCost > 0.0)
                Heat += heatCost * fraction;
        }

        public override string ToString() => $"{Definition.DisplayName} @ {Position}";
    }
}
