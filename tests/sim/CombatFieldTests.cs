using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Impact geometry and the shot-resolution loop. Engine-free.
    /// </summary>
    [TestFixture]
    public class CombatFieldTests
    {
        private static Weapon MakeWeapon(params string[] lines)
        {
            var text = new List<string> { "weapon" };
            foreach (string line in lines)
                text.Add("\t" + line);

            var weapon = new Weapon();
            weapon.Load(new DataFile(string.Join("\n", text) + "\n", "test.txt").Nodes[0]);
            return weapon;
        }

        private static Ship MakeTarget(double shields = 100.0, double hull = 100.0, double radius = 10.0)
        {
            var definition = new ShipDefinition("Target");
            definition.Attributes.Set("shields", shields);
            definition.Attributes.Set("hull", hull);
            definition.Attributes.Set("mass", 100.0);
            return new Ship(definition) { CollisionRadius = radius };
        }

        // --- Sweep geometry -------------------------------------------------------

        [Test]
        public void ASegmentPassingThroughACircleReportsTheEntryFraction()
        {
            double? t = Collision.SweepCircle(new Point(-10.0, 0.0), new Point(10.0, 0.0),
                                              Point.Zero, 5.0);

            Assert.IsTrue(t.HasValue);
            // Enters at x = -5, a quarter of the way along a 20-unit segment.
            Assert.AreEqual(0.25, t.Value, 1e-9);
        }

        [Test]
        public void ASegmentThatMissesReportsNothing()
        {
            Assert.IsNull(Collision.SweepCircle(new Point(-10.0, 20.0), new Point(10.0, 20.0),
                                                Point.Zero, 5.0));
        }

        [Test]
        public void ASegmentStartingInsideIsAnImmediateHit()
        {
            double? t = Collision.SweepCircle(new Point(1.0, 0.0), new Point(50.0, 0.0),
                                              Point.Zero, 5.0);

            Assert.IsTrue(t.HasValue);
            Assert.AreEqual(0.0, t.Value, 1e-9);
        }

        [Test]
        public void ASegmentStoppingShortDoesNotHit()
        {
            Assert.IsNull(Collision.SweepCircle(new Point(-10.0, 0.0), new Point(-6.0, 0.0),
                                                Point.Zero, 5.0));
        }

        [Test]
        public void AFastShotCannotTunnelThroughATarget()
        {
            // The whole reason impacts are swept rather than sampled: this step jumps
            // clean over the target, so a point test at either endpoint would miss.
            var from = new Point(-100.0, 0.0);
            var to = new Point(100.0, 0.0);
            const double radius = 5.0;

            Assert.Greater((from - Point.Zero).Length, radius, "starts well outside");
            Assert.Greater((to - Point.Zero).Length, radius, "ends well outside");
            Assert.IsTrue(Collision.SegmentHitsCircle(from, to, Point.Zero, radius),
                "yet the path crosses the target");
        }

        // --- Resolution loop ------------------------------------------------------

        [Test]
        public void AShotThatReachesAShipDamagesIt()
        {
            var field = new CombatField();
            Ship target = MakeTarget(shields: 100.0);
            target.Position = new Point(30.0, 0.0);
            field.Add(target);

            Weapon weapon = MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 25");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0)));

            var hits = new List<HitReport>();
            for (int frame = 0; frame < 5; frame++)
                hits.AddRange(field.Step());

            Assert.AreEqual(1, hits.Count);
            Assert.AreSame(target, hits[0].Target);
            Assert.AreEqual(75.0, target.Shields, 1e-9);
            Assert.IsEmpty(field.Projectiles, "a shot is consumed by the ship it hits");
        }

        [Test]
        public void AShotAimedAwayNeverConnects()
        {
            var field = new CombatField();
            Ship target = MakeTarget();
            target.Position = new Point(30.0, 0.0);
            field.Add(target);

            // Fired due north; the target is due east.
            Weapon weapon = MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 25");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(0.0)));

            for (int frame = 0; frame < 10; frame++) field.Step();

            Assert.AreEqual(100.0, target.Shields, 1e-9);
        }

        [Test]
        public void ShotsPassThroughTheGovernmentThatFiredThem()
        {
            var navy = new Government("Republic");
            var field = new CombatField();

            Ship friendly = MakeTarget();
            friendly.Position = new Point(30.0, 0.0);
            friendly.Government = navy;
            field.Add(friendly);

            Weapon weapon = MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 25");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0), government: navy));

            for (int frame = 0; frame < 5; frame++) field.Step();

            Assert.AreEqual(100.0, friendly.Shields, 1e-9, "no friendly fire");
        }

        [Test]
        public void TheNearestShipOnTheLineIsTheOneHit()
        {
            var field = new CombatField();

            Ship near = MakeTarget(shields: 100.0);
            near.Position = new Point(40.0, 0.0);
            Ship far = MakeTarget(shields: 100.0);
            far.Position = new Point(120.0, 0.0);
            field.Add(far);          // added first, to prove ordering does not decide
            field.Add(near);

            Weapon weapon = MakeWeapon("\"velocity\" 200", "\"lifetime\" 50", "\"shield damage\" 30");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0)));

            field.Step();

            Assert.AreEqual(70.0, near.Shields, 1e-9, "the nearer ship absorbs the shot");
            Assert.AreEqual(100.0, far.Shields, 1e-9);
        }

        [Test]
        public void HitsReportDisableAndDestroyTransitions()
        {
            var field = new CombatField();
            Ship target = MakeTarget(shields: 0.0, hull: 100.0);
            target.Position = new Point(30.0, 0.0);
            field.Add(target);

            Weapon weapon = MakeWeapon("\"velocity\" 40", "\"lifetime\" 50", "\"hull damage\" 1000");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0)));

            var hits = new List<HitReport>();
            for (int frame = 0; frame < 5; frame++)
                hits.AddRange(field.Step());

            Assert.AreEqual(1, hits.Count);
            Assert.IsTrue(hits[0].Events.HasFlag(ShipEvent.Disable));
            Assert.IsTrue(target.IsDisabled);
        }

        [Test]
        public void ExpiredProjectilesAreRemoved()
        {
            var field = new CombatField();
            Weapon weapon = MakeWeapon("\"velocity\" 5", "\"lifetime\" 3");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0)));

            field.Step();
            Assert.AreEqual(1, field.Projectiles.Count);

            field.Step();
            field.Step();
            Assert.IsEmpty(field.Projectiles, "a projectile is dropped once its lifetime ends");
        }

        [Test]
        public void AnExpiringClusterSpawnsItsSubmunitions()
        {
            Weapon shrapnel = MakeWeapon("\"velocity\" 4", "\"lifetime\" 40", "\"hull damage\" 5");
            var field = new CombatField { WeaponLookup = name => name == "shrapnel" ? shrapnel : null };

            Weapon carrier = MakeWeapon("\"velocity\" 6", "\"lifetime\" 2", "\"submunition\" \"shrapnel\" 7");
            field.Add(new Projectile(carrier, Point.Zero, Point.Zero, new Angle(90.0)));

            field.Step();
            Assert.AreEqual(1, field.Projectiles.Count, "still the carrier");

            field.Step();
            Assert.AreEqual(7, field.Projectiles.Count, "the carrier burst into its cluster");
            Assert.IsTrue(field.Projectiles.All(p => !p.IsDead));
        }

        [Test]
        public void ADestroyedShipStopsAbsorbingShots()
        {
            var field = new CombatField();
            Ship wreck = MakeTarget(shields: 0.0, hull: 100.0);
            wreck.Position = new Point(30.0, 0.0);
            wreck.SetLevels(hull: -1.0);
            field.Add(wreck);
            Assert.IsTrue(wreck.IsDestroyed);

            Weapon weapon = MakeWeapon("\"velocity\" 40", "\"lifetime\" 50", "\"hull damage\" 10");
            field.Add(new Projectile(weapon, Point.Zero, Point.Zero, new Angle(90.0)));

            var hits = new List<HitReport>();
            for (int frame = 0; frame < 5; frame++)
                hits.AddRange(field.Step());

            Assert.IsEmpty(hits, "wreckage is not a target");
        }
    }
}
