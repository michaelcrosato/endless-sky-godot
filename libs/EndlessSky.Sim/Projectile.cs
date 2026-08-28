using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>Anything a projectile can chase: currently ships, later asteroids too.</summary>
    public interface ITarget
    {
        Point Position { get; }
        Point Velocity { get; }
    }

    /// <summary>
    /// A shot in flight. Port of the motion half of upstream <c>Projectile::Move</c>.
    /// </summary>
    /// <remarks>
    /// Like the rest of the simulation this is fixed-step at 60 fps, because every
    /// upstream weapon value (velocity, lifetime, turn, acceleration) is a per-frame
    /// quantity.
    ///
    /// INCOMPLETE, tracked rather than dropped (directive rule 2): target locking and
    /// jamming, confusion, blindspots, lead prediction, throttle control, penetration
    /// counts, split range and fade-out are not modelled yet. The homing branch below
    /// is upstream's "turn toward the target as fast as you can" core, which is what
    /// the majority of missiles actually use.
    /// </remarks>
    public class Projectile
    {
        private const double RadiansToDegrees = 180.0 / Math.PI;

        private readonly Weapon _weapon;

        /// <summary>
        /// Fires a shot. Velocity starts as the firing ship's, so shots inherit
        /// momentum: a fleeing ship's fire is genuinely slower to close.
        /// </summary>
        public Projectile(Weapon weapon, Point position, Point parentVelocity, Angle angle,
                          ITarget target = null, Government government = null)
        {
            _weapon = weapon ?? throw new ArgumentNullException(nameof(weapon));

            Position = position;
            Angle = angle;
            Velocity = parentVelocity + angle.Unit() * weapon.Velocity;
            Lifetime = (int)weapon.Lifetime;
            Target = target;
            Government = government;
        }

        public Weapon Weapon => _weapon;

        public Point Position;
        public Point Velocity;
        public Angle Angle;

        /// <summary>Frames remaining. Decremented at the top of each step, as upstream does.</summary>
        public int Lifetime { get; private set; }

        /// <summary>What this shot is chasing. Null for unguided fire.</summary>
        public ITarget Target { get; set; }

        /// <summary>Who fired it, so it does not damage its own side.</summary>
        public Government Government { get; }

        /// <summary>True once the projectile has expired and should be removed.</summary>
        public bool IsDead { get; private set; }

        /// <summary>Ends this projectile, as a hit does. Idempotent.</summary>
        public void Kill()
        {
            IsDead = true;
            Lifetime = 0;
        }

        /// <summary>
        /// Advances one frame. Returns the submunitions to spawn when the shot expires,
        /// or an empty list otherwise.
        /// </summary>
        /// <remarks>
        /// Upstream decrements lifetime *before* testing it, so a weapon with lifetime 0
        /// dies on its very first step. That is not a degenerate case: cluster carriers
        /// such as the Ion Hail Turret rely on it to burst immediately.
        /// </remarks>
        public IReadOnlyList<Submunition> Step()
        {
            if (IsDead)
                return Array.Empty<Submunition>();

            if (--Lifetime <= 0)
            {
                IsDead = true;
                return _weapon.Submunitions;
            }

            double turn = _weapon.Turn;
            double acceleration = _weapon.Acceleration;

            if (turn != 0.0)
            {
                if (_weapon.IsHoming)
                {
                    if (Target is not null)
                    {
                        Point toTarget = Target.Position - Position;
                        Point unit = toTarget.Unit();

                        // asin of the cross product gives the signed angle between where
                        // the shot points and where the target is, in degrees.
                        double cross = Angle.Unit().Cross(unit);
                        double desiredTurn = RadiansToDegrees * Math.Asin(Math.Clamp(cross, -1.0, 1.0));

                        turn = Math.Abs(desiredTurn) > turn
                            ? Math.CopySign(turn, desiredTurn)
                            : desiredTurn;
                    }
                    else
                    {
                        // A homing weapon that has lost its target flies straight.
                        turn = 0.0;
                    }
                }

                if (turn != 0.0)
                    Angle += new Angle(turn);
            }

            if (acceleration != 0.0)
            {
                double retained = 1.0 - _weapon.Attributes.Get("drag");
                Point thrust = Angle.Unit() * acceleration;
                Velocity = Velocity * retained + thrust;
            }

            Position += Velocity;
            return Array.Empty<Submunition>();
        }

        public override string ToString() => $"projectile @ {Position} ({Lifetime} frames left)";
    }
}
