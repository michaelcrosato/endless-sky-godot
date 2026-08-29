using System.Collections.Generic;
using System.Globalization;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Third batch of parity-audit regressions: hardpoint geometry, firing spread and
    /// mission gates. Engine-free.
    /// </summary>
    [TestFixture]
    public class AuditRegressionTests3
    {
        private static Outfit MakeGun(string name, params string[] weaponLines)
        {
            var lines = new List<string> { "outfit \"" + name + "\"", "\tweapon" };
            foreach (string line in weaponLines)
                lines.Add("\t\t" + line);

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        private static Ship MakeArmedShip(string gunLine = "\tgun 0 -40")
        {
            var lines = new List<string>
            {
                "ship \"Test\"",
                "\tattributes",
                "\t\t\"hull\" 500",
                "\t\t\"mass\" 100",
                "\t\t\"energy capacity\" 1000",
                "\t\t\"fuel capacity\" 100",
                gunLine,
            };

            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();
            return ship;
        }

        // --- Hardpoint geometry ---------------------------------------------------

        [Test]
        public void HardpointOffsetsAreHalvedBecauseSpriteCoordinatesAreDoubleScale()
        {
            // Upstream's Hardpoint constructor stores `point * .5`. Using the raw
            // value puts every mount at twice its true distance from the hull centre,
            // which shows up as muzzle flashes detached from the ship.
            Ship ship = MakeArmedShip("\tgun 0 -40");

            Assert.AreEqual(1, ship.Mounts.Count);
            Assert.AreEqual(0.0, ship.Mounts[0].Point.X, 1e-9);
            Assert.AreEqual(-20.0, ship.Mounts[0].Point.Y, 1e-9);
        }

        [Test]
        public void ShotsOriginateAtTheHalvedMountPosition()
        {
            Ship ship = MakeArmedShip("\tgun 0 -40");
            ship.RandomSource = () => 0.5;                  // dead centre: no spread
            ship.InstallWeapon(MakeGun("Gun", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100"));

            ship.Position = Point.Zero;
            ship.Velocity = Point.Zero;
            ship.Facing = new Angle(0.0);                   // unit is (0, -1)

            Projectile shot = ship.Fire(ship.Mounts[0]);

            Assert.IsNotNull(shot);
            Assert.AreEqual(-20.0, shot.Position.Y, 1e-6, "20 units ahead, not 40");
        }

        [Test]
        public void ShotsAreSpawnedBackByHalfTheShipsVelocity()
        {
            // Upstream offsets the spawn by -0.5 * velocity so a shot renders in the
            // right place relative to a moving hull.
            Ship ship = MakeArmedShip("\tgun 0 0");
            ship.RandomSource = () => 0.5;
            ship.InstallWeapon(MakeGun("Gun", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100"));

            ship.Position = Point.Zero;
            ship.Velocity = new Point(8.0, 0.0);
            ship.Facing = new Angle(90.0);

            Projectile shot = ship.Fire(ship.Mounts[0]);

            Assert.IsNotNull(shot);
            Assert.AreEqual(-4.0, shot.Position.X, 1e-6);
        }

        // --- Firing spread --------------------------------------------------------

        [Test]
        public void InaccuracyIsAppliedToTheFiringAngle()
        {
            // Parsed but never used means every weapon is perfectly accurate, which
            // removes the reason a stream of fire spreads at all.
            Ship ship = MakeArmedShip("\tgun 0 0");
            ship.InstallWeapon(MakeGun("Scattergun",
                "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100", "\"inaccuracy\" 30"));

            ship.Facing = new Angle(90.0);

            // The distribution is triangular (Distribution.cpp:75), so each shot draws
            // TWICE and the deflection is the difference. A constant source therefore
            // means dead centre; reaching the ends of the cone needs both extremes.
            var draws = new System.Collections.Generic.Queue<double>();
            ship.RandomSource = () => draws.Dequeue();

            draws.Enqueue(1.0); draws.Enqueue(0.0);         // full deflection one way
            Projectile high = ship.Fire(ship.Mounts[0]);
            ship.StepArmament();

            draws.Enqueue(0.0); draws.Enqueue(1.0);         // and the other
            Projectile low = ship.Fire(ship.Mounts[0]);

            Assert.IsNotNull(high);
            Assert.IsNotNull(low);
            Assert.AreNotEqual(high.Angle.Step, low.Angle.Step,
                "opposite ends of the cone must not fire along the same line");

            double spread = System.Math.Abs(high.Angle.AbsDegrees - low.Angle.AbsDegrees);
            Assert.AreEqual(60.0, spread, 0.1, "a 30 degree inaccuracy is a 60 degree cone");
        }

        [Test]
        public void AnAccurateWeaponFiresStraight()
        {
            Ship ship = MakeArmedShip("\tgun 0 0");
            ship.RandomSource = () => 0.123;                // whatever it is, unused
            ship.InstallWeapon(MakeGun("Laser", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100"));

            ship.Facing = new Angle(90.0);
            Projectile shot = ship.Fire(ship.Mounts[0]);

            Assert.IsNotNull(shot);
            Assert.AreEqual(ship.Facing.Step, shot.Angle.Step);
        }

        // --- Mission gates --------------------------------------------------------

        private static Mission LoadMission(string text)
        {
            DataNode node = new DataFile(text, "test.txt").Nodes[0];
            var mission = new Mission(node.Token(1));
            mission.Load(node);
            return mission;
        }

        [Test]
        public void TheAcceptGateIsParsedAndEnforced()
        {
            // Previously parsed away entirely, letting the player take on missions
            // upstream would refuse.
            Mission mission = LoadMission(
                "mission \"Restricted\"\n" +
                "\tto offer\n" +
                "\t\thas \"heard about it\"\n" +
                "\tto accept\n" +
                "\t\thas \"licence\"\n");

            var conditions = new Conditions();
            conditions.Set("heard about it", 1);

            Assert.IsTrue(mission.CanOffer(conditions), "it is still offered");
            Assert.IsFalse(mission.CanAccept(conditions), "but cannot be taken without the licence");

            conditions.Set("licence", 1);
            Assert.IsTrue(mission.CanAccept(conditions));
        }

        [Test]
        public void AMissionWithNoAcceptGateCanAlwaysBeAccepted()
        {
            Mission mission = LoadMission("mission \"Simple\"\n");

            Assert.IsTrue(mission.ToAccept.IsEmpty);
            Assert.IsTrue(mission.CanAccept(new Conditions()));
        }

        [Test]
        public void AnAlreadyFailedMissionIsNotOffered()
        {
            // Offering a mission whose failure condition already holds hands the
            // player something dead on arrival.
            Mission mission = LoadMission(
                "mission \"Rescue\"\n" +
                "\tto offer\n" +
                "\t\thas \"knows about the crash\"\n" +
                "\tto fail\n" +
                "\t\thas \"survivors died\"\n");

            var conditions = new Conditions();
            conditions.Set("knows about the crash", 1);
            Assert.IsTrue(mission.CanOffer(conditions));

            conditions.Set("survivors died", 1);
            Assert.IsTrue(mission.HasFailed(conditions));
            Assert.IsFalse(mission.CanOffer(conditions));
        }
    }
}
