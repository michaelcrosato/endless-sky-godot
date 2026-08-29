using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Projectile motion parity with upstream Projectile::Move. Engine-free.
    /// </summary>
    [TestFixture]
    public class ProjectileTests
    {
        private sealed class FixedTarget : ITarget
        {
            public FixedTarget(Point position, Point velocity = default)
            {
                Position = position;
                Velocity = velocity;
            }

            public Point Position { get; }
            public Point Velocity { get; }
        }

        private static Weapon MakeWeapon(params (string Key, double Value)[] numbers)
        {
            var text = new System.Text.StringBuilder("weapon\n");
            foreach ((string key, double value) in numbers)
            {
                text.Append('\t').Append('"').Append(key).Append("\" ")
                    .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            var weapon = new Weapon();
            weapon.Load(new DataFile(text.ToString(), "test.txt").Nodes[0]);
            return weapon;
        }

        private static Weapon MakeHomingWeapon(params (string Key, double Value)[] numbers)
        {
            var text = new System.Text.StringBuilder("weapon\n\t\"homing\"\n");
            foreach ((string key, double value) in numbers)
            {
                text.Append('\t').Append('"').Append(key).Append("\" ")
                    .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            var weapon = new Weapon();
            weapon.Load(new DataFile(text.ToString(), "test.txt").Nodes[0]);
            return weapon;
        }

        [Test]
        public void MuzzleVelocityIsAddedToTheFiringShipsVelocity()
        {
            // Shots inherit momentum upstream, which is why fire from a fleeing ship
            // closes more slowly than fire from a charging one.
            var weapon = MakeWeapon(("velocity", 10.0), ("lifetime", 100.0));
            var shot = new Projectile(weapon, Point.Zero, new Point(3.0, 0.0), new Angle(90.0));

            Assert.AreEqual(13.0, shot.Velocity.X, 1e-9);
            Assert.AreEqual(0.0, shot.Velocity.Y, 1e-6);
        }

        [Test]
        public void AProjectileTravelsAtConstantVelocityWithoutAcceleration()
        {
            var weapon = MakeWeapon(("velocity", 10.0), ("lifetime", 100.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            shot.Step();
            shot.Step();

            Assert.AreEqual(20.0, shot.Position.X, 1e-6);
            Assert.IsFalse(shot.IsDead);
        }

        [Test]
        public void LifetimeIsDecrementedBeforeItIsTested()
        {
            // Upstream uses --lifetime <= 0, so a lifetime of 2 gives exactly one
            // moving frame and dies on the second.
            var weapon = MakeWeapon(("velocity", 10.0), ("lifetime", 2.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            shot.Step();
            Assert.IsFalse(shot.IsDead);

            shot.Step();
            Assert.IsTrue(shot.IsDead);
        }

        [Test]
        public void ACarrierWithNoLifetimeBurstsOnItsFirstStep()
        {
            // This is why the Ion Hail Turret works: its carrier round has velocity but
            // no lifetime, so it splits immediately rather than being a dud.
            var text = "weapon\n\t\"velocity\" 12\n\t\"submunition\" \"ion hail\" 4\n";
            var weapon = new Weapon();
            weapon.Load(new DataFile(text, "test.txt").Nodes[0]);

            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));
            var spawned = shot.Step();

            Assert.IsTrue(shot.IsDead);
            Assert.AreEqual(1, spawned.Count);
            Assert.AreEqual(4, spawned[0].Count);
            Assert.AreEqual("ion hail", spawned[0].WeaponName);
        }

        [Test]
        public void AnExpiringProjectileReturnsItsSubmunitions()
        {
            var text = "weapon\n\t\"velocity\" 3\n\t\"lifetime\" 2\n\t\"submunition\" \"mine\" 11\n";
            var weapon = new Weapon();
            weapon.Load(new DataFile(text, "test.txt").Nodes[0]);

            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            Assert.IsEmpty(shot.Step(), "nothing spawns while it is still alive");
            var spawned = shot.Step();

            Assert.AreEqual(11, spawned.Single().Count);
        }

        [Test]
        public void AHomingProjectileTurnsTowardItsTarget()
        {
            // Fired due east at a target due north: it must steer left (toward -Y in
            // upstream's screen coordinates) rather than fly on.
            var weapon = MakeHomingWeapon(("velocity", 5.0), ("lifetime", 500.0), ("turn", 4.0));
            var target = new FixedTarget(new Point(0.0, -100.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0), target);

            double before = shot.Angle.Degrees;
            shot.Step();
            double after = shot.Angle.Degrees;

            Assert.AreNotEqual(before, after, "a homing shot with a target must turn");

            // Keep stepping; it should end up pointing at the target.
            for (int i = 0; i < 200; i++) shot.Step();

            Point toTarget = (target.Position - shot.Position).Unit();
            double alignment = shot.Angle.Unit().Dot(toTarget);
            Assert.Greater(alignment, 0.9, "after steering it should be pointed at its target");
        }

        [Test]
        public void HomingTurnIsCappedByTheWeaponsTurnRate()
        {
            // Target 90 degrees off the nose: the shot wants to turn 90 degrees but may
            // only spend `turn` degrees this frame.
            var weapon = MakeHomingWeapon(("velocity", 5.0), ("lifetime", 500.0), ("turn", 3.0));
            var target = new FixedTarget(new Point(0.0, -100.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0), target);

            double before = shot.Angle.AbsDegrees;
            shot.Step();
            double turned = System.Math.Abs(shot.Angle.AbsDegrees - before);

            // Angles are quantized to 65536 steps, so allow a step of slack.
            Assert.AreEqual(3.0, turned, 0.01);
        }

        [Test]
        public void AMissileDirectlyBehindItsTargetDoesNotTurn()
        {
            // Faithful upstream quirk, not a bug on our side. Steering is driven by
            // asin(cross product), and at exactly 180 degrees the cross product is zero,
            // so the desired turn is zero: a perfectly antiparallel missile sits in an
            // unstable equilibrium and flies straight. Upstream behaves the same way;
            // in a real fight, target motion breaks the tie almost immediately.
            var weapon = MakeHomingWeapon(("velocity", 5.0), ("lifetime", 500.0), ("turn", 4.0));
            var target = new FixedTarget(new Point(-100.0, 0.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0), target);

            int before = shot.Angle.Step;
            shot.Step();

            Assert.AreEqual(before, shot.Angle.Step);
        }

        [Test]
        public void AHomingProjectileWithNoTargetFliesStraight()
        {
            var weapon = MakeHomingWeapon(("velocity", 5.0), ("lifetime", 500.0), ("turn", 4.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0), target: null);

            double before = shot.Angle.Step;
            shot.Step();

            Assert.AreEqual(before, shot.Angle.Step, "no target means no turning");
        }

        [Test]
        public void AcceleratingProjectilesBuildUpSpeed()
        {
            var weapon = MakeWeapon(("velocity", 1.0), ("lifetime", 500.0), ("acceleration", 0.5));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            double first = shot.Velocity.Length;
            shot.Step();
            double second = shot.Velocity.Length;
            shot.Step();
            double third = shot.Velocity.Length;

            Assert.Greater(second, first);
            Assert.Greater(third, second);
        }

        [Test]
        public void ProjectileDragGivesAccelerationATerminalSpeed()
        {
            var weapon = MakeWeapon(("velocity", 0.0), ("lifetime", 5000.0),
                                    ("acceleration", 1.0), ("drag", 0.1));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            for (int i = 0; i < 500; i++) shot.Step();

            // Terminal speed is acceleration / drag.
            Assert.AreEqual(10.0, shot.Velocity.Length, 1e-6);
        }

        [Test]
        public void ADeadProjectileStopsMoving()
        {
            var weapon = MakeWeapon(("velocity", 10.0), ("lifetime", 1.0));
            var shot = new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0));

            shot.Step();
            Assert.IsTrue(shot.IsDead);

            Point resting = shot.Position;
            shot.Step();
            Assert.AreEqual(resting, shot.Position);
        }

        [Test]
        public void ASubmunitionFliesAlongItsOwnHeadingNotItsParents()
        {
            // Projectile.cpp:119-120 redirects the parent's own impulse along the
            // child's heading: dV = child.angle.Unit() * (|parent.dV| + child velocity),
            // then velocity += dV - parent.dV. Handing the child the parent's whole
            // velocity vector instead made the declared facing offset cosmetic — the
            // cluster LOOKED like it fanned out and every fragment still travelled the
            // way the carrier had been going, so a shotgun round landed as one shot.
            var field = new CombatField();

            var carrier = new Weapon();
            carrier.Load(new EndlessSky.Data.DataFile(string.Join("\n",
                "weapon",
                "\t\"velocity\" 10",
                "\t\"lifetime\" 1",
                "\tsubmunition \"Fragment\" 1",
                "\t\tfacing 90") + "\n", "test.txt").Nodes[0]);

            var fragment = new Weapon();
            fragment.Load(new EndlessSky.Data.DataFile(string.Join("\n",
                "weapon",
                "\t\"velocity\" 0",
                "\t\"lifetime\" 30") + "\n", "test.txt").Nodes[0]);

            carrier.ResolveSubmunitions(name => name == "Fragment" ? fragment : null);

            // Fired from a ship at rest, heading along +Y, so the parent's whole speed
            // is its own impulse.
            var shot = new Projectile(carrier, Point.Zero, Point.Zero, new Angle(0.0));
            var children = new List<Projectile>();
            field.SpawnSubmunitions(shot, carrier.Submunitions, children, DeathType.Natural);

            Assert.AreEqual(1, children.Count);

            Point heading = children[0].Angle.Unit();
            Point velocity = children[0].Velocity;

            Assert.AreEqual(10.0, velocity.Length, 1e-6,
                "the carrier's speed is carried over, not added to");
            Assert.AreEqual(10.0, velocity.Dot(heading), 1e-6,
                "and it travels along its OWN heading");
        }
    }
}
