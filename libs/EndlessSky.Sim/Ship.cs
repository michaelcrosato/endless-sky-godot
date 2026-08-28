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

        public void AddOutfit(Outfit outfit, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                _outfits.Add(outfit);
            }

            Attributes.Add(outfit.Attributes, count);
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
                IsSteering = true;
                SteeringDirection = turn;
                Facing += new Angle(turn * TurnRate);
            }

            double thrustCommand = (command.Forward ? 1.0 : 0.0) - (command.Back ? 1.0 : 0.0);
            if (thrustCommand != 0.0)
            {
                bool forward = thrustCommand > 0.0;
                double thrust = forward ? Thrust : ReverseThrust;

                // Upstream ignores a reverse command on a ship with no reverse thruster
                // entirely - the ship does not even slow under drag.
                if (thrust != 0.0)
                {
                    IsThrusting = forward;
                    IsReversing = !forward;
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

        public override string ToString() => $"{Definition.DisplayName} @ {Position}";
    }
}
