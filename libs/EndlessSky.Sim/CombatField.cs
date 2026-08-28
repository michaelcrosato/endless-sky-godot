using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>What happened when a projectile struck a ship.</summary>
    public readonly struct HitReport
    {
        public HitReport(Ship target, Projectile projectile, ShipEvent events)
        {
            Target = target;
            Projectile = projectile;
            Events = events;
        }

        public Ship Target { get; }
        public Projectile Projectile { get; }

        /// <summary>Disable/destroy transitions this hit caused, if any.</summary>
        public ShipEvent Events { get; }
    }

    /// <summary>
    /// Everything in flight in one system: ships and the shots between them.
    /// This is the piece that turns the individual models into a fight.
    /// </summary>
    /// <remarks>
    /// Ordering within a frame follows upstream: projectiles move first, then
    /// collisions are resolved along the segment each one travelled. Resolving
    /// against the post-move position alone would let fast shots tunnel through
    /// their targets.
    ///
    /// INCOMPLETE, tracked rather than dropped: asteroids and minables, anti-missile
    /// interception, blast radius and area damage, hit force, penetration counts,
    /// visual effects, and ship-versus-ship collisions.
    /// </remarks>
    public class CombatField
    {
        private readonly List<Ship> _ships = new List<Ship>();
        private readonly List<Projectile> _projectiles = new List<Projectile>();

        /// <summary>Looks up the weapon a submunition names, so clusters can spawn.</summary>
        public Func<string, Weapon?>? WeaponLookup { get; set; }

        public IReadOnlyList<Ship> Ships => _ships;
        public IReadOnlyList<Projectile> Projectiles => _projectiles;

        public void Add(Ship? ship)
        {
            if (ship is not null) _ships.Add(ship);
        }

        public void Add(Projectile? projectile)
        {
            if (projectile is not null) _projectiles.Add(projectile);
        }

        public void Add(IEnumerable<Projectile>? projectiles)
        {
            if (projectiles is null) return;
            foreach (Projectile projectile in projectiles)
                Add(projectile);
        }

        /// <summary>
        /// Advances every projectile one frame and resolves impacts.
        /// Returns the hits that landed, for the presentation layer to visualise.
        /// </summary>
        public List<HitReport> Step()
        {
            var hits = new List<HitReport>();
            var spawned = new List<Projectile>();

            foreach (Projectile projectile in _projectiles)
            {
                if (projectile.IsDead)
                    continue;

                // Collision-test the segment this projectile's velocity describes,
                // BEFORE consuming the frame's lifetime.
                // Upstream appends newly fired projectiles and then runs DoCollisions
                // over every one of them (Engine.cpp:1909 then :1921), so a round is
                // always tested against the ground it is about to cover, and only moves
                // on the following frame.
                //
                // Testing after the move instead loses the hit entirely for a
                // short-lived round, because Projectile.Step marks an expiring
                // projectile dead and returns WITHOUT moving it: the segment collapses
                // to a point. The Beam Laser - the standard human starter gun, and what
                // a stock Sparrow carries - has a lifetime of exactly 1, so it dealt no
                // damage at all. No unit test could see it, because the weapon, the
                // projectile and the damage model were each individually correct.
                Point from = projectile.Position;
                Ship? struck = FirstShipHit(projectile, from, from + projectile.Velocity);

                if (struck is null)
                {
                    IReadOnlyList<Submunition> submunitions = projectile.Step();
                    if (projectile.IsDead)
                        SpawnSubmunitions(projectile, submunitions, spawned, DeathType.Natural);

                    continue;
                }

                ShipEvent events = struck.TakeDamage(projectile.Weapon);
                hits.Add(new HitReport(struck, projectile, events));

                projectile.Kill();
                // A hit is a COLLISION death. Upstream releases submunitions only on
                // the death types a cluster opts into, and the default is natural
                // expiry alone - so a cluster round that strikes a ship head-on does
                // not also shower it with its children.
                SpawnSubmunitions(projectile, projectile.Weapon.Submunitions, spawned, DeathType.Collision);
            }

            _projectiles.AddRange(spawned);
            _projectiles.RemoveAll(p => p.IsDead);
            return hits;
        }

        /// <summary>
        /// The first ship the segment strikes.
        /// </summary>
        /// <remarks>
        /// Port of the filter in upstream <c>CollisionSet::Line</c>, which is broader
        /// than "not my own government": a shot passes through ANY body whose
        /// government is not an enemy of the shooter's, so a pirate firing at the
        /// player does not shred neutral traffic that drifts through the line. The
        /// converse also holds - a shot ALWAYS collides with the body it was aimed at,
        /// even a friendly one, which is how a deliberately targeted shot connects.
        /// </remarks>
        private Ship? FirstShipHit(Projectile projectile, Point from, Point to)
        {
            Ship? closest = null;
            double closestFraction = double.PositiveInfinity;

            foreach (Ship ship in _ships)
            {
                if (ship.IsDestroyed)
                    continue;

                // The aimed-at body is always hittable, whatever its allegiance.
                bool isIntendedTarget = projectile.Target is not null
                    && ReferenceEquals(projectile.Target, ship);

                if (!isIntendedTarget
                    && projectile.Government is not null
                    && ship.Government is not null
                    && !projectile.Government.IsEnemy(ship.Government))
                {
                    continue;
                }

                double? fraction = Collision.SweepCircle(from, to, ship.Position, ship.CollisionRadius);
                if (fraction.HasValue && fraction.Value < closestFraction)
                {
                    closestFraction = fraction.Value;
                    closest = ship;
                }
            }

            return closest;
        }

        private void SpawnSubmunitions(Projectile parent, IReadOnlyList<Submunition>? submunitions,
                                       List<Projectile> into, DeathType death)
        {
            if (submunitions is null || submunitions.Count == 0)
                return;

            foreach (Submunition submunition in submunitions)
            {
                if ((submunition.SpawnOn & death) == DeathType.None)
                    continue;

                Weapon? weapon = submunition.Weapon
                    ?? (WeaponLookup is null ? null : WeaponLookup(submunition.WeaponName));
                if (weapon is null)
                    continue;

                for (int i = 0; i < submunition.Count; i++)
                {
                    // The declared facing offset is what makes a cluster FAN OUT.
                    // Without it every child inherits the parent's exact heading and
                    // the whole cluster flies as a single shot.
                    Angle heading = parent.Angle + new Angle(submunition.Facing);
                    Point spawn = parent.Position + parent.Angle.Rotate(submunition.Offset);

                    into.Add(new Projectile(weapon, spawn, parent.Velocity,
                                            heading, parent.Target, parent.Government));
                }
            }
        }
    }
}
