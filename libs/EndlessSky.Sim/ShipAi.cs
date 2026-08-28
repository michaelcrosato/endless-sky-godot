using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// NPC pilot behaviour: who to attack, how to close, and when to shoot.
    /// Port of the engagement core of upstream <c>AI</c>.
    /// </summary>
    /// <remarks>
    /// The directive is explicit that upstream NPC behaviour comes before anything
    /// cleverer, so this reproduces the shape of <c>AI::FindTarget</c>,
    /// <c>AI::Attack</c> and <c>AI::AutoFire</c> rather than inventing tactics.
    ///
    /// INCOMPLETE, tracked rather than dropped: escorts and formation flying,
    /// fleeing and repair behaviour, boarding and capture, landing and jumping,
    /// afterburner use, cloaking, mining, fighter bays, personality traits
    /// (timid/heroic/nemesis...), and lead prediction when aiming. Each is a
    /// separate upstream behaviour that layers onto this core.
    /// </remarks>
    public static class ShipAi
    {
        /// <summary>
        /// Picks a target the way upstream does by default: the closest ship this one
        /// is hostile toward. Disabled ships are still valid targets (upstream keeps
        /// attacking them until they are boarded or destroyed), but wrecks are not.
        /// </summary>
        public static Ship? FindTarget(Ship? self, IEnumerable<Ship>? candidates)
        {
            if (self is null || candidates is null)
                return null;

            Ship? best = null;
            double bestDistanceSquared = double.PositiveInfinity;

            foreach (Ship other in candidates)
            {
                if (other is null || ReferenceEquals(other, self) || other.IsDestroyed)
                    continue;

                if (!IsHostile(self, other))
                    continue;

                double distanceSquared = (other.Position - self.Position).LengthSquared;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = other;
                }
            }

            return best;
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
        /// One frame of pursuit. Port of the core of <c>AI::Attack</c>: turn toward
        /// the target, and thrust only while roughly facing it, so a ship does not
        /// accelerate away while still swinging around.
        /// </summary>
        public static Command Attack(Ship? self, Ship? target)
        {
            if (self is null || target is null)
                return Command.None;

            Point toTarget = target.Position - self.Position;

            var command = new Command
            {
                Turn = FlightControls.TurnToward(self, toTarget),
            };

            // Upstream thrusts when the dot product is non-negative, i.e. the target
            // is anywhere in the forward hemisphere.
            command.Forward = self.Facing.Unit().Dot(toTarget) >= 0.0;

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
        /// Returns the shots produced.
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
