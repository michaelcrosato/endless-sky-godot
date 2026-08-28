using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// NPC pilot behaviour: who to attack, how to close, and when to shoot.
    /// Port of the engagement core of upstream <c>AI</c>.
    /// </summary>
    /// <remarks>
    /// INCOMPLETE, tracked rather than dropped: escorts and formation flying, fleeing
    /// and repair, boarding and capture, landing and jumping, afterburners, cloaking,
    /// mining, fighter bays, personality traits (timid/heroic/vindictive/ramming...),
    /// and lead prediction when aiming. Personalities matter here: several of the
    /// behaviours below are upstream's DEFAULT and a personality flips them.
    /// </remarks>
    public static class ShipAi
    {
        /// <summary>
        /// How far a ship will look for a fight, in simulation units. Upstream scans a
        /// bounded neighbourhood rather than the whole system, so ships do not set off
        /// across the map after something they can barely detect.
        /// </summary>
        public const double DefaultEngagementRange = 4000.0;

        /// <summary>
        /// Picks a target the way upstream does by default: the closest hostile ship
        /// within engagement range that is still worth attacking.
        /// </summary>
        /// <param name="engagementRange">Search radius; pass 0 for unlimited.</param>
        /// <param name="attackDisabled">
        /// Upstream re-picks a target once it is disabled unless the pilot is
        /// vindictive or is deliberately disabling, so the default is to move on.
        /// </param>
        public static Ship? FindTarget(Ship? self, IEnumerable<Ship>? candidates,
                                       double engagementRange = DefaultEngagementRange,
                                       bool attackDisabled = false)
        {
            if (self is null || candidates is null)
                return null;

            // A ship with nothing to shoot with does not go looking for a fight.
            if (!IsArmed(self))
                return null;

            Ship? best = null;
            double bestDistanceSquared = double.PositiveInfinity;
            double maxSquared = engagementRange > 0.0 ? engagementRange * engagementRange : double.PositiveInfinity;

            foreach (Ship other in candidates)
            {
                if (other is null || ReferenceEquals(other, self) || other.IsDestroyed)
                    continue;

                // A crippled ship is no longer a threat; upstream looks for a live one.
                if (other.IsDisabled && !attackDisabled)
                    continue;

                if (!IsHostile(self, other))
                    continue;

                double distanceSquared = (other.Position - self.Position).LengthSquared;
                if (distanceSquared > maxSquared)
                    continue;

                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = other;
                }
            }

            return best;
        }

        /// <summary>Whether this ship has any weapon installed.</summary>
        public static bool IsArmed(Ship? ship)
        {
            if (ship is null)
                return false;

            foreach (WeaponMount mount in ship.Mounts)
            {
                if (!mount.IsEmpty)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="self"/> considers <paramref name="other"/> an enemy.
        /// Ships with no government are treated as neutral rather than universally
        /// hostile, so an unconfigured scene does not turn into a free-for-all.
        /// </summary>
        public static bool IsHostile(Ship? self, Ship? other)
        {
            Government? mine = self?.Government;
            Government? theirs = other?.Government;

            if (mine is null || theirs is null)
                return false;

            return mine.IsEnemy(theirs);
        }

        /// <summary>
        /// The shortest range among the ship's installed weapons: the distance at
        /// which everything it carries can reach.
        /// </summary>
        public static double ShortestWeaponRange(Ship? ship)
        {
            if (ship is null)
                return 0.0;

            double shortest = double.PositiveInfinity;
            foreach (WeaponMount mount in ship.Mounts)
            {
                if (mount.IsEmpty)
                    continue;

                double range = mount.Weapon!.Range;
                if (range > 0.0 && range < shortest)
                    shortest = range;
            }

            return double.IsPositiveInfinity(shortest) ? 0.0 : shortest;
        }

        /// <summary>
        /// One frame of pursuit. Port of the core of <c>AI::Attack</c> and
        /// <c>AI::MoveToAttack</c>.
        /// </summary>
        /// <remarks>
        /// The ship turns to face its target always, but thrusts only while it still
        /// needs to close. Thrusting whenever the target is merely ahead makes every
        /// NPC charge to point-blank and ram, because nothing ever tells it to stop:
        /// upstream gates thrust on the target being further away than the ship's own
        /// turning circle, and standoff pilots break off once inside weapon range.
        /// </remarks>
        public static Command Attack(Ship? self, Ship? target)
        {
            if (self is null || target is null)
                return Command.None;

            Point toTarget = target.Position - self.Position;
            double distance = toTarget.Length;
            if (distance <= 0.0)
                return Command.None;

            Point approachUnit = toTarget / distance;
            double standoff = ShortestWeaponRange(self);

            // Closing speed along the line to the target. Coasting is LOSSLESS in this
            // simulation, so a ship that merely stops thrusting keeps its velocity and
            // sails straight into whatever it was shooting at. Holding station means
            // actively shedding that speed.
            double approachSpeed = (self.Velocity - target.Velocity).Dot(approachUnit);

            double acceleration = Math.Max(1e-9, self.Acceleration);
            double turnRate = self.TurnRate;

            // Distance needed to brake from the current closing speed, including the
            // frames spent swinging the nose round. Upstream's slowdown estimate.
            double brakingAllowance = acceleration + (turnRate > 0.0 ? 160.0 / turnRate : 0.0);
            double slowdownDistance = approachSpeed > 0.0
                ? approachSpeed * approachSpeed / brakingAllowance / 2.0
                : 0.0;

            bool insideStandoff = standoff > 0.0 && distance < standoff * 0.75;
            bool wouldOvershoot = standoff > 0.0 && distance - slowdownDistance < standoff * 0.5;

            if (approachSpeed > 0.0 && (insideStandoff || wouldOvershoot))
            {
                // Brake: point away from the closing vector and burn. Upstream prefers
                // reverse thrusters when the ship has them, which keeps the guns on
                // target; without them it accepts turning away to kill the approach.
                return new Command
                {
                    Turn = FlightControls.TurnToward(self, -self.Velocity),
                    Forward = self.Facing.Unit().Dot(-self.Velocity) > 0.0,
                };
            }

            var command = new Command
            {
                Turn = FlightControls.TurnToward(self, toTarget),
            };

            double facing = self.Facing.Unit().Dot(approachUnit);

            // Smallest circle the ship can turn at its current speed. Closing inside
            // it means it can no longer bring its nose round, which is how a pursuit
            // turns into a collision.
            double turningDiameter = 200.0;
            if (turnRate > 0.0)
            {
                double stepsInFullTurn = 360.0 / turnRate;
                double circumference = stepsInFullTurn * self.Velocity.Length;
                turningDiameter = Math.Max(200.0, circumference / Math.PI);
            }

            bool needsToClose = standoff <= 0.0 || distance > standoff * 0.75;
            command.Forward = needsToClose && facing >= 0.0 && distance > turningDiameter;

            return command;
        }

        /// <summary>
        /// Whether a weapon should fire at a target this frame. Port of the range and
        /// aim gates in <c>AI::AutoFire</c>.
        /// </summary>
        /// <param name="aimTolerance">
        /// Half-angle of the firing cone in degrees. Upstream derives this per weapon
        /// from projectile speed and target size; a fixed cone stands in for now.
        /// </param>
        public static bool ShouldFire(Ship? self, Ship? target, Weapon? weapon, double aimTolerance = 10.0)
        {
            if (self is null || target is null || weapon is null || !weapon.IsWeapon)
                return false;

            if (target.IsDestroyed)
                return false;

            Point toTarget = target.Position - self.Position;
            double distance = toTarget.Length;

            // Out of reach: firing would only waste ammunition and energy.
            double range = weapon.Range;
            if (range > 0.0 && distance > range + target.CollisionRadius)
                return false;

            // Homing weapons steer themselves, so they need no firing cone.
            if (weapon.IsHoming)
                return true;

            if (distance <= 0.0)
                return true;

            // Widen the cone for nearby targets: a ship a few units away subtends a
            // large angle, and upstream fires whenever the shot would actually connect.
            double angularRadius = Math.Atan2(target.CollisionRadius, distance) * (180.0 / Math.PI);
            double cosine = self.Facing.Unit().Dot(toTarget.Unit());
            double offBy = Math.Acos(Math.Clamp(cosine, -1.0, 1.0)) * (180.0 / Math.PI);

            return offBy <= aimTolerance + angularRadius;
        }

        /// <summary>
        /// Fires every mount that is ready, affordable and pointed at the target.
        /// </summary>
        public static List<Projectile> AutoFire(Ship? self, Ship? target, double aimTolerance = 10.0)
        {
            var shots = new List<Projectile>();
            if (self is null || target is null)
                return shots;

            var asTarget = target as ITarget;

            foreach (WeaponMount mount in self.Mounts)
            {
                if (mount.IsEmpty || !ShouldFire(self, target, mount.Weapon, aimTolerance))
                    continue;

                Projectile? shot = self.Fire(mount, asTarget, self.Government);
                if (shot is not null)
                    shots.Add(shot);
            }

            return shots;
        }
    }
}
