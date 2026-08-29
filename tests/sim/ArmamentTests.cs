using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Firing cadence and resource cost, against upstream Hardpoint::Step /
    /// Hardpoint::Fire / Ship::CanFire. Engine-free.
    /// </summary>
    [TestFixture]
    public class ArmamentTests
    {
        /// <summary>Builds an outfit from data text, so the real parser is exercised.</summary>
        private static Outfit MakeGun(string name, params string[] weaponLines)
        {
            var lines = new List<string> { "outfit \"" + name + "\"", "\tweapon" };
            foreach (string line in weaponLines)
                lines.Add("\t\t" + line);

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        /// <summary>
        /// An ammunition outfit. Rounds are ordinary outfits with mass, which is why
        /// they are loaded with AddOutfit rather than through any ammunition-specific
        /// call -- that is the route the game itself takes.
        /// </summary>
        private static Outfit MakeAmmo(string name, double mass = 1.0)
        {
            var lines = new List<string> { "outfit \"" + name + "\"", "\t\"mass\" " + mass };

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        /// <summary>A turret outfit: it consumes a turret mount rather than a gun port.</summary>
        private static Outfit MakeTurret(string name, params string[] weaponLines)
        {
            var lines = new List<string> { "outfit \"" + name + "\"", "	\"turret mounts\" -1", "	weapon" };
            foreach (string line in weaponLines)
                lines.Add("		" + line);

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        /// <summary>A hull with one turret mount and plenty of power.</summary>
        private static Ship MakeTurretedShip()
        {
            var lines = new List<string>
            {
                "ship \"Turret Boat\"",
                "	attributes",
                "		\"hull\" 1000",
                "		\"mass\" 100",
                "		\"energy capacity\" 1000",
                "		\"turret mounts\" 1",
                "	turret 0 -10",
            };

            var definition = new ShipDefinition("Turret Boat");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

            var ship = new Ship(definition);
            ship.BuildMounts();
            return ship;
        }

        /// <summary>
        /// A ship with the requested gun mounts and plenty of energy, built through the
        /// real data parser so the mount wiring is exercised rather than faked.
        /// </summary>
        private static Ship MakeArmedShip(int gunMounts = 1, double energy = 1000.0)
        {
            var lines = new List<string>
            {
                "ship \"Test Fighter\"",
                "\tattributes",
                "\t\t\"hull\" 1000",
                "\t\t\"shields\" 1000",
                "\t\t\"mass\" 100",
                "\t\t\"energy capacity\" " + energy.ToString(CultureInfo.InvariantCulture),
                "\t\t\"heat capacity\" 10",
            };
            for (int i = 0; i < gunMounts; i++)
                lines.Add("\tgun 0 -10");

            var definition = new ShipDefinition("Test Fighter");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

            var ship = new Ship(definition);
            ship.BuildMounts();
            return ship;
        }

        [Test]
        public void AGunFiresThenReloadsForItsFullReloadTime()
        {
            Outfit gun = MakeGun("Test Gun", "\"reload\" 5", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(gun);

            Assert.IsNotNull(ship.Fire(mount), "first shot fires immediately");
            Assert.IsNull(ship.Fire(mount), "cannot fire again while reloading");

            // Four frames is not yet enough; the fifth completes the reload.
            for (int i = 0; i < 4; i++) ship.StepArmament();
            Assert.IsNull(ship.Fire(mount));

            ship.StepArmament();
            Assert.IsNotNull(ship.Fire(mount), "ready again after the full reload");
        }

        [Test]
        public void BurstWeaponsFireAClusterThenFallSilent()
        {
            // burst count 3 at burst reload 2, full reload 20: three quick shots, then
            // a long pause. That cadence is what makes burst weapons feel distinct.
            Outfit gun = MakeGun("Burst Gun",
                "\"reload\" 20", "\"burst reload\" 2", "\"burst count\" 3",
                "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(gun);

            int shotsInBurst = 0;
            for (int frame = 0; frame < 12; frame++)
            {
                if (ship.Fire(mount) is not null) shotsInBurst++;
                ship.StepArmament();
            }

            Assert.AreEqual(3, shotsInBurst, "the magazine holds exactly the burst count");

            // Long enough for the full reload to elapse and refill the burst.
            for (int frame = 0; frame < 60; frame++) ship.StepArmament();
            Assert.IsNotNull(ship.Fire(mount), "the burst refills once the full reload completes");
        }

        [Test]
        public void FiringSpendsEnergyAndGeneratesHeat()
        {
            Outfit gun = MakeGun("Hot Gun",
                "\"reload\" 1", "\"firing energy\" 7", "\"firing heat\" 11",
                "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip(energy: 100.0);
            WeaponMount mount = ship.InstallWeapon(gun);

            ship.Fire(mount);

            Assert.AreEqual(93.0, ship.Energy, 1e-9);
            Assert.AreEqual(11.0, ship.Heat, 1e-9);
        }

        [Test]
        public void AShipWithoutEnoughEnergyCannotFire()
        {
            Outfit gun = MakeGun("Hungry Gun",
                "\"reload\" 1", "\"firing energy\" 50", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip(energy: 100.0);
            WeaponMount mount = ship.InstallWeapon(gun);

            Assert.IsNotNull(ship.Fire(mount));
            ship.StepArmament();
            Assert.IsNotNull(ship.Fire(mount));
            ship.StepArmament();

            Assert.AreEqual(0.0, ship.Energy, 1e-9);
            Assert.IsNull(ship.Fire(mount), "no energy, no shot");
        }

        [Test]
        public void AmmunitionIsConsumedAndGatesFiring()
        {
            Outfit launcher = MakeGun("Test Launcher",
                "\"reload\" 1", "ammo \"Test Missile\"", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(launcher);

            Assert.AreEqual("Test Missile", launcher.Weapon.AmmoName);
            Assert.IsNull(ship.Fire(mount), "no ammunition loaded");

            ship.AddOutfit(MakeAmmo("Test Missile"), 2);
            Assert.IsNotNull(ship.Fire(mount));
            Assert.AreEqual(1, ship.AmmoCount("Test Missile"));

            ship.StepArmament();
            Assert.IsNotNull(ship.Fire(mount));
            Assert.AreEqual(0, ship.AmmoCount("Test Missile"));

            ship.StepArmament();
            Assert.IsNull(ship.Fire(mount), "magazine empty");
        }

        [Test]
        public void ADisabledShipCannotFire()
        {
            Outfit gun = MakeGun("Test Gun", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(gun);

            ship.SetLevels(shields: 0.0, hull: 1.0);
            Assert.IsTrue(ship.IsDisabled);
            Assert.IsNull(ship.Fire(mount));
        }

        [Test]
        public void ShotsOriginateFromTheMountAndInheritShipVelocity()
        {
            Outfit gun = MakeGun("Test Gun", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(gun);

            ship.Position = new Point(500.0, -200.0);
            ship.Velocity = new Point(2.0, 0.0);
            ship.Facing = new Angle(90.0);

            Projectile shot = ship.Fire(mount);

            Assert.IsNotNull(shot);
            // Muzzle speed rides on top of the ship's own motion.
            Assert.AreEqual(12.0, shot.Velocity.X, 1e-6);
            // The shot starts at the mount, not the ship's centre.
            Assert.AreNotEqual(ship.Position, shot.Position);
        }

        [Test]
        public void FireAllFiresEveryReadyMount()
        {
            Outfit gun = MakeGun("Test Gun", "\"reload\" 4", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip(gunMounts: 3);

            for (int i = 0; i < 3; i++)
                Assert.IsNotNull(ship.InstallWeapon(gun), "mount " + i + " should accept a gun");

            Assert.AreEqual(3, ship.FireAll().Count, "all three fire together");
            Assert.IsEmpty(ship.FireAll(), "and all three reload together");
        }

        [Test]
        public void InstallingMoreWeaponsThanMountsFails()
        {
            Outfit gun = MakeGun("Test Gun", "\"reload\" 4", "\"velocity\" 10", "\"lifetime\" 100");
            Ship ship = MakeArmedShip(gunMounts: 1);

            Assert.IsNotNull(ship.InstallWeapon(gun));
            Assert.IsNull(ship.InstallWeapon(gun), "only one gun port");
        }

        [Test]
        public void MountsAreBuiltFromTheShipDefinition()
        {
            Ship ship = MakeArmedShip(gunMounts: 2);

            Assert.AreEqual(2, ship.Mounts.Count);
            Assert.IsTrue(ship.Mounts.All(m => !m.IsTurret));
            Assert.IsTrue(ship.Mounts.All(m => m.IsEmpty));
        }

        [Test]
        public void ARealUpstreamLauncherLoadsAndFires()
        {
            GameData data = UpstreamData.Instance;
            Outfit launcher = data.Outfits["Sidewinder Missile Launcher"];

            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(launcher);

            Assert.AreEqual("Sidewinder Missile", launcher.Weapon.AmmoName);
            Assert.IsNull(ship.Fire(mount), "an empty launcher does not fire");

            ship.AddOutfit(data.Outfits["Sidewinder Missile"], 45);
            Projectile shot = ship.Fire(mount);

            Assert.IsNotNull(shot);
            Assert.IsTrue(shot.Weapon.IsHoming);
            Assert.AreEqual(44, ship.AmmoCount("Sidewinder Missile"));
        }

        [Test]
        public void AStockShipCanFireTheAmmunitionItWasBuiltWith()
        {
            // The Raven's stock loadout is one Sidewinder launcher and 45 rounds for
            // it. Upstream reads ammunition straight out of the ship's outfit map, so
            // a hull that was built with its missiles aboard is loaded. Nothing in the
            // game calls AddAmmo, so if ammunition lives in a separate ledger every
            // launcher, torpedo tube and missile pod in the dataset is inert.
            Ship raven = UpstreamData.Instance.BuildShip("Raven");

            Assert.AreEqual(45, raven.AmmoCount("Sidewinder Missile"),
                "the rounds the hull was built with are its ammunition");

            WeaponMount launcher = raven.Mounts.First(
                m => m.Weapon?.AmmoName == "Sidewinder Missile");

            Assert.IsNotNull(raven.Fire(launcher), "a stock Raven can fire its missiles");
            Assert.AreEqual(44, raven.AmmoCount("Sidewinder Missile"));
        }

        [Test]
        public void SpendingAmmunitionRemovesTheOutfitAndItsMass()
        {
            // Upstream spends a round with AddOutfit(ammo, -AmmoUsage), so the hull
            // gets lighter as the magazine empties. Decrementing a private counter
            // instead leaves a ship carrying the mass of ammunition it already fired.
            Ship raven = UpstreamData.Instance.BuildShip("Raven");
            double loadedMass = raven.Mass;
            double roundMass = UpstreamData.Instance.Outfits["Sidewinder Missile"].Attributes.Get("mass");

            Assert.Greater(roundMass, 0.0, "a missile has mass upstream");

            WeaponMount launcher = raven.Mounts.First(
                m => m.Weapon?.AmmoName == "Sidewinder Missile");

            raven.Fire(launcher);

            Assert.AreEqual(loadedMass - roundMass, raven.Mass, 1e-9,
                "firing a missile makes the ship lighter by exactly one round");
        }

        [Test]
        public void FiringInaccuracyIsTriangularByDefaultNotUniform()
        {
            // Distribution.cpp:75. The default is (Random - Random) * value, which
            // peaks at zero deflection: most shots land near the aim point and the
            // spread tails off. A flat draw is upstream's explicitly-opted-in `uniform`
            // mode, and it makes every weapon feel like a shotgun -- a shot at the edge
            // of the cone is exactly as likely as one down the middle.
            Outfit gun = MakeGun("Spray Gun",
                "\"reload\" 1", "\"inaccuracy\" 10", "\"velocity\" 10", "\"lifetime\" 100");

            Ship ship = MakeArmedShip(energy: 100000.0);
            WeaponMount mount = ship.InstallWeapon(gun);

            // Two draws per shot under a triangular distribution; feeding a repeating
            // sequence lets both ends be checked exactly.
            var draws = new Queue<double>();
            ship.RandomSource = () => draws.Dequeue();

            // Both draws equal: dead centre, whatever the value.
            draws.Enqueue(0.9); draws.Enqueue(0.9);
            Assert.AreEqual(ship.Facing.Degrees, ship.Fire(mount)!.Angle.Degrees, 0.01,
                "equal draws cancel, so the shot goes exactly where it was aimed");

            ship.StepArmament();

            // Maximum deflection needs BOTH extremes, which is why the middle is common.
            draws.Enqueue(1.0); draws.Enqueue(0.0);
            double full = ship.Fire(mount)!.Angle.Degrees - ship.Facing.Degrees;
            // Angles quantise to 65536 steps, so the tolerance is one step (0.0055 deg).
            Assert.AreEqual(10.0, full, 0.01, "the extremes give the full cone");
        }

        // --- Turret traverse ------------------------------------------------------

        [Test]
        public void ATurretTurnsTowardATargetOffTheShipsNose()
        {
            // Hardpoint::Aim (Hardpoint.cpp:266-273) turns a turret on its own mount at
            // its own rate. Without it every mount fired along the ship's facing, which
            // makes a turret indistinguishable from a fixed gun — a turret-armed ship
            // could not shoot at anything it was not already pointed at.
            Outfit turret = MakeTurret("Test Turret", "\"turret turn\" 3",
                "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100");

            Ship ship = MakeTurretedShip();
            WeaponMount mount = ship.InstallWeapon(turret, asTurret: true);
            Assert.IsNotNull(mount);

            // Directly abeam: 90 degrees off the nose.
            ship.Facing = new Angle(0.0);
            var abeam = new Point(400.0, 0.0);

            for (int frame = 0; frame < 40; frame++)
                ship.AimTurrets(abeam);

            Assert.AreEqual(90.0, mount.BaseAngle.Degrees, 1.0,
                "the turret came round to bear");
        }

        [Test]
        public void ATurretTurnsNoFasterThanItsRate()
        {
            Outfit turret = MakeTurret("Slow Turret", "\"turret turn\" 2",
                "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100");

            Ship ship = MakeTurretedShip();
            WeaponMount mount = ship.InstallWeapon(turret, asTurret: true);

            ship.Facing = new Angle(0.0);
            ship.AimTurrets(new Point(400.0, 0.0));

            Assert.AreEqual(2.0, mount.BaseAngle.Degrees, 0.01,
                "one frame of traverse is one turn rate");
        }

        [Test]
        public void AFixedGunDoesNotTraverse()
        {
            // Only turrets move. A gun fires along the hull, which is the whole reason
            // aiming the SHIP matters.
            Outfit gun = MakeGun("Fixed Gun", "\"reload\" 1", "\"velocity\" 10", "\"lifetime\" 100");

            Ship ship = MakeArmedShip();
            WeaponMount mount = ship.InstallWeapon(gun);

            ship.AimTurrets(new Point(400.0, 0.0));

            Assert.AreEqual(0.0, mount.BaseAngle.Degrees, 1e-9);
        }
    }
}
