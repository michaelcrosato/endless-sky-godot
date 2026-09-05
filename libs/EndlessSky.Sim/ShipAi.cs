using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// NPC pilot behaviour: who to attack, how to close, and when to shoot.
    /// Port of the engagement core of upstream <c>AI</c>.
    /// </summary>
    /// <remarks>
    /// Owned escorts use PlayerFleet.StepEscorts for orders and independent jumps.
    /// INCOMPLETE, tracked rather than dropped: named formations, fleeing and repair,
    /// boarding and capture, independent landing and refuelling, afterburners, cloaking,
    /// mining, fighter bays, personality traits (timid/heroic/vindictive/ramming...),
    /// and lead prediction when aiming. Personalities matter here: several of the
    /// behaviours below are upstream's DEFAULT and a personality flips them.
    /// </remarks>
    public static partial class ShipAi
    {
        /// <summary>
        /// How far a ship will look for a fight, in simulation units. Upstream scans a
        /// bounded neighbourhood rather than the whole system, so ships do not set off
        /// across the map after something they can barely detect.
        /// </summary>
        public const double DefaultEngagementRange = 4000.0;

        /// <summary>
        /// Ceiling on the standoff distance, matching upstream's ShipAICache seed.
        /// A missile boat still closes to 4000 rather than sniping from its full reach.
        /// </summary>
        public const double MaxEngagementStandoff = 4000.0;

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
                if (other is null || ReferenceEquals(other, self) || !other.IsTargetable)
                    continue;

                // Combat coordinates are local to a system. A nearby coordinate in
                // another system is not a nearby target (upstream GetShipsList).
                if (!ReferenceEquals(other.CurrentSystem, self.CurrentSystem))
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
                // Defensive hardpoints do not make a ship a combatant.
                if (!mount.IsEmpty && mount.Weapon is not null && !mount.Weapon.IsSpecial)
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

            // Upstream's ShipAICache seeds this at 4000 and only ever mins into it,
            // so a long-range loadout still engages at 4000 rather than standing off
            // at its full reach.
            double shortest = MaxEngagementStandoff;

            bool armed = false;
            foreach (WeaponMount mount in ship.Mounts)
            {
                // A defensive mount must not set the engagement standoff: an
                // anti-missile turret has a very short reach, and letting it count
                // would pull a warship into knife range of its real guns.
                if (mount.IsEmpty || mount.Weapon!.IsSpecial)
                    continue;

                armed = true;
                double range = mount.Weapon!.Range;
                if (range > 0.0 && range < shortest)
                    shortest = range;
            }

            return armed ? shortest : 0.0;
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

            // Turrets bear on the target regardless of where the hull points. Upstream
            // aims them every frame from the AI (Armament.cpp:233); leaving them fixed
            // is what made a turret indistinguishable from a gun.
            self.AimTurrets(AimPoint(self, target, PrimaryWeapon(self)) + self.Position);

            // The nose stays ON the target at all times. An earlier attempt at a
            // standoff turned the ship's tail to its target to bleed closing speed,
            // which silenced its fixed guns for the whole braking phase - upstream
            // never does this for an ordinary ship. Only artillery and blast-radius
            // carriers back off, and those are not modelled yet.
            double standoff = ShortestWeaponRange(self);
            bool inRange = standoff > 0.0 && distance < standoff;

            // In range, aim where the target WILL BE: fixed guns fire along the hull,
            // so a ship nosed at where its target is can only hit one that is not
            // moving across it, and two evenly matched hulls trade shots forever
            // without either losing a point.
            //
            // Out of range, aim at the target itself. Leading from far away points the
            // nose off to one side, and the thrust gate below - which asks whether the
            // ship is facing what it is chasing - then never opens, so the pursuit
            // never closes and the fight never starts.
            Point aim = toTarget;
            if (inRange)
            {
                Point lead = AimPoint(self, target, PrimaryWeapon(self));
                if (lead.Length > 0.0)
                    aim = lead;
            }

            var command = new Command
            {
                Turn = FlightControls.TurnToward(self, aim),
            };

            // Inside firing range, upstream's AimToAttack only aims: it applies no
            // thrust, so the ship coasts through and past its target and comes round
            // for another pass. That is the shape of an Endless Sky dogfight.
            if (standoff > 0.0 && distance < standoff * 0.75)
                return command;

            // Outside it, close - the MoveToAttack path. Thrust only while roughly
            // facing the target AND further away than the ship's own turning circle,
            // since accelerating inside that circle is how a pursuit becomes a
            // collision.
            double facing = self.Facing.Unit().Dot(toTarget) / distance;

            double turningDiameter = 200.0;
            double turnRate = self.TurnRate;
            if (turnRate > 0.0)
            {
                double stepsInFullTurn = 360.0 / turnRate;
                double circumference = stepsInFullTurn * self.Velocity.Length;
                turningDiameter = Math.Max(200.0, circumference / Math.PI);
            }

            // Two clauses, and the second is the one that is easy to leave out and
            // fatal without it: a ship whose velocity is carrying it AWAY from its
            // target thrusts even inside its own turning circle. Without that, two
            // ships that pass each other at speed coast apart forever - full energy,
            // full shields, out of weapon range, closing on nothing - and the fight
            // simply stops rather than ending.
            bool farEnoughToTurnInto = facing >= 0.0 && distance > turningDiameter;
            bool driftingAway = self.Velocity.Dot(toTarget) < 0.0 && facing >= 0.9;

            command.Forward = farEnoughToTurnInto || driftingAway;

            // Reverse thrusters, where the hull has them, close on a target that has
            // ended up behind it faster than turning right around would.
            if (!command.Forward && facing < -0.75 && self.ReverseAcceleration > 0.0)
                command.Back = true;

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
        /// <summary>
        /// How many frames a projectile of speed <paramref name="projectileSpeed"/>
        /// takes to reach a target that is at <paramref name="offset"/> and moving at
        /// <paramref name="relativeVelocity"/>. Port of upstream
        /// <c>AI::RendezvousTime</c>.
        /// </summary>
        /// <remarks>
        /// Solves for the time at which a shot leaving now and the target arrive at the
        /// same place:
        /// <c>(p.x + v.x*t)^2 + (p.y + v.y*t)^2 = vp^2 * t^2</c>.
        ///
        /// Returns NaN when there is no such time - a target running away faster than
        /// the shot can fly cannot be hit at all, and the caller has to decide what to
        /// do about that rather than being handed a plausible-looking wrong number.
        /// </remarks>
        public static double RendezvousTime(Point offset, Point relativeVelocity,
                                            double projectileSpeed)
        {
            double a = relativeVelocity.Dot(relativeVelocity) - projectileSpeed * projectileSpeed;
            double b = 2.0 * offset.Dot(relativeVelocity);
            double c = offset.Dot(offset);

            double discriminant = b * b - 4.0 * a * c;
            if (discriminant < 0.0)
                return double.NaN;

            discriminant = Math.Sqrt(discriminant);

            // Two roots; a negative one is a rendezvous in the past.
            double first = (-b + discriminant) / (2.0 * a);
            double second = (-b - discriminant) / (2.0 * a);

            if (first >= 0.0 && second >= 0.0)
                return Math.Min(first, second);
            if (first >= 0.0 || second >= 0.0)
                return Math.Max(first, second);

            return double.NaN;
        }

        /// <summary>
        /// Where to point in order to hit a moving target with a given weapon.
        /// </summary>
        /// <remarks>
        /// Aiming at where a ship IS only works against one that is standing still.
        /// Two ships circling each other at combat speed will trade shots for as long
        /// as their reactors hold out and never land one, which is what a fight
        /// between evenly matched hulls looked like before this existed: shots in
        /// flight every frame, and neither hull losing a point.
        ///
        /// Falls back to the target's present position when no intercept exists, so a
        /// ship that cannot be caught is still shot at rather than ignored.
        /// </remarks>
        public static Point AimPoint(Ship self, Ship target, Weapon? weapon)
        {
            Point offset = target.Position - self.Position;

            double speed = weapon?.Velocity ?? 0.0;
            if (speed <= 0.0)
                return offset;

            // Relative to the shooter: a projectile inherits the firing ship's motion
            // upstream, so the closing rate is what matters, not ground speed.
            Point relative = target.Velocity - self.Velocity;

            double time = RendezvousTime(offset, relative, speed);
            if (double.IsNaN(time) || time < 0.0)
                return offset;

            // Upstream caps the lead at the same 600 frames it uses when closing.
            time = Math.Min(time, 600.0);
            return offset + relative * time;
        }

        /// <summary>The fastest weapon the ship can currently fire, for steering.</summary>
        private static Weapon? PrimaryWeapon(Ship self)
        {
            Weapon? best = null;
            foreach (WeaponMount mount in self.Mounts)
            {
                if (mount.IsEmpty || mount.Weapon is null || mount.Weapon.IsSpecial)
                    continue;

                if (best is null || mount.Weapon.Velocity > best.Velocity)
                    best = mount.Weapon;
            }

            return best;
        }

        public static bool ShouldFire(Ship? self, Ship? target, Weapon? weapon, double aimTolerance = 10.0)
        {
            if (self is null || target is null || weapon is null || !weapon.IsWeapon)
                return false;

            // Anti-missile and tractor mounts are serviced on their own path and never
            // shoot at ships.
            if (weapon.IsSpecial)
                return false;

            if (!target.IsTargetable)
                return false;

            // A weapon the ship cannot actually fire must not be reported as one it
            // should. Without this the predicate disagrees with the firing path: an
            // unloaded missile pod answers "yes, fire" on every frame forever, and only
            // Ship.Fire silently declines. Anything that trusts ShouldFire to describe
            // what will happen - AI target selection, or a test - is then misled.
            if (!self.CanFire(weapon))
                return false;

            Point toTarget = target.Position - self.Position;
            double distance = toTarget.Length;

            // Out of reach: firing would only waste ammunition and energy. Range is
            // measured to where the target IS, since that is what the weapon has to
            // cover; the cone below is measured to where it WILL BE.
            double range = weapon.Range;
            if (range > 0.0 && distance > range + target.CollisionRadius)
                return false;

            // Homing weapons steer themselves, so they need no firing cone.
            if (weapon.IsHoming)
                return true;

            if (distance <= 0.0)
                return true;

            // Aim where the target will be when the shot arrives. Testing the cone
            // against its present position means a crossing target is fired at
            // constantly and hit almost never.
            Point aim = AimPoint(self, target, weapon);
            if (aim.Length <= 0.0)
                return true;

            // Widen the cone for nearby targets: a ship a few units away subtends a
            // large angle, and upstream fires whenever the shot would actually connect.
            double angularRadius = Math.Atan2(target.CollisionRadius, distance) * (180.0 / Math.PI);
            double cosine = self.Facing.Unit().Dot(aim.Unit());
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

        // --- Getting to a jump ----------------------------------------------------

        /// <summary>
        /// Speed upstream treats as a dead stop when no explicit limit applies
        /// (<c>AI.cpp:415</c>, <c>VELOCITY_ZERO</c>).
        /// </summary>
        /// <remarks>
        /// This constant is the difference between a stall and a spin. A drive that
        /// states no "jump speed" gives <see cref="Ship.JumpSpeedLimit"/> of zero, and
        /// braking toward an EXACT zero never terminates: thrust overshoots it and drag
        /// only decays velocity asymptotically. Upstream never chases that number.
        /// </remarks>
        public const double VelocityZero = 0.001;

        /// <summary>
        /// Port of <c>AI::Stop</c> (<c>AI.cpp:2666</c>): brake to
        /// <paramref name="maxSpeed"/>, optionally ending up facing
        /// <paramref name="direction"/>. Returns true once the ship is slow enough,
        /// leaving <paramref name="command"/> free for the caller to steer with.
        /// </summary>
        /// <remarks>
        /// Two details here are load-bearing and were both missing from the hand-rolled
        /// brake this replaces. The zero-speed request falls back to
        /// <see cref="VelocityZero"/> rather than literal zero, and it raises
        /// <see cref="Command.Stop"/> — upstream's "cheat to stop" — which is what lets
        /// a ship actually reach a standstill instead of oscillating around one.
        /// </remarks>
        public static bool Stop(Ship? ship, ref Command command, double maxSpeed,
                                Point direction = default)
        {
            if (ship is null)
                return true;

            Point velocity = ship.Velocity;
            Angle angle = ship.Facing;
            double speed = velocity.Length;

            // Asked for a complete stop, the ship needs to be going much slower.
            if (speed <= (maxSpeed != 0.0 ? maxSpeed : VelocityZero))
                return true;

            if (maxSpeed == 0.0)
                command.Stop = true;

            // Moving slowly enough that one frame of braking could finish the job, the
            // ship has to be pointed accurately; the tolerance tightens from 0.8 as the
            // stopping time falls.
            double acceleration = ship.Acceleration;
            double stopTime = acceleration > 0.0 ? speed / acceleration : double.PositiveInfinity;
            double limit = 0.8 + 0.2 / (1.0 + stopTime * stopTime * stopTime * 0.001);

            // With a reverse thruster, work out whether using it beats turning around.
            if (ship.ReverseThrust != 0.0 && ship.TurnRate > 0.0)
            {
                double degreesToTurn = Degrees(-velocity.Unit().Dot(angle.Unit()));
                double forwardTime = degreesToTurn / ship.TurnRate + stopTime;

                double reverseAcceleration = ship.ReverseAcceleration;
                double reverseTime = (180.0 - degreesToTurn) / ship.TurnRate +
                                     (reverseAcceleration > 0.0
                                         ? speed / reverseAcceleration
                                         : double.PositiveInfinity);

                if (direction.IsNonZero)
                {
                    // Ending up on a heading costs turning time from wherever braking
                    // leaves the nose, and the two options leave it 180 degrees apart.
                    forwardTime += Degrees(direction.Unit().Dot(-velocity.Unit())) / ship.TurnRate;
                    reverseTime += Degrees(direction.Unit().Dot(angle.Unit())) / ship.TurnRate;
                }

                if (reverseTime < forwardTime)
                {
                    command.Turn = FlightControls.TurnToward(ship, velocity);
                    if (velocity.Unit().Dot(angle.Unit()) > limit)
                        command.Back = true;
                    return false;
                }
            }

            command.Turn = FlightControls.TurnBackward(ship);
            if (velocity.Unit().Dot(angle.Unit()) < -limit)
                command.Forward = true;

            return false;
        }

        /// <summary>Angle in degrees for a clamped dot product, as upstream's acos calls.</summary>
        private static double Degrees(double dot) =>
            Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * (180.0 / Math.PI);

        /// <summary>
        /// Port of <c>AI::PrepareForHyperspace</c> (<c>AI.cpp:2732</c>): one frame of
        /// flying a ship into position for the jump its <see cref="Ship.TargetSystem"/>
        /// names. The caller commits with <see cref="Ship.TryCommitJump"/>.
        /// </summary>
        /// <remarks>
        /// A jump drive tears its hole where the ship is, so it only has to stop; a
        /// hyperdrive has to stop AND end up lined up with the lane, which is why
        /// upstream passes the departure direction into <see cref="Stop"/> and turns
        /// onto it only once stopped.
        ///
        /// This is simulation, not presentation, and it used to live in the flight
        /// scene as an invented brake with a 0.96 alignment constant found nowhere
        /// upstream. That brake had no terminal condition on a ship whose drive stated
        /// no "jump speed": it retro-thrust forever, and once velocity decayed far
        /// enough that its unit vector read as zero, <c>TurnToward</c>'s documented
        /// zero-vector case turned the ship at full rate, every frame, in one
        /// direction. The reported symptom was a ship spinning in circles and never
        /// jumping, and no test could see it, because the rule was written on the view
        /// side of a boundary the architecture test only checks the direction of.
        ///
        /// INCOMPLETE, tracked rather than dropped: scram drives (their deviation
        /// manoeuvre is upstream's third branch here) and system departure distances,
        /// neither of which <see cref="Ship.IsReadyToJump"/> models either.
        /// </remarks>
        public static Command PrepareForHyperspace(Ship? ship)
        {
            var command = new Command();
            if (ship is null || ship.TargetSystem is null || ship.CurrentSystem is null)
                return command;

            if (!ship.HasHyperdrive && !ship.HasJumpDrive)
                return command;

            Point direction = ship.JumpDirection;

            if (ship.WouldUseJumpDrive)
            {
                // A jump drive just stops. There is no lane to line up with.
                Stop(ship, ref command, ship.JumpSpeedLimit);
            }
            else if (Stop(ship, ref command, ship.JumpSpeedLimit, direction))
            {
                command.Turn = FlightControls.TurnToward(ship, direction);
            }

            return command;
        }
    }
}
