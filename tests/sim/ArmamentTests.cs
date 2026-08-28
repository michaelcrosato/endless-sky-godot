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

            ship.AddAmmo("Test Missile", 2);
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

            ship.AddAmmo("Sidewinder Missile", 45);
            Projectile shot = ship.Fire(mount);

            Assert.IsNotNull(shot);
            Assert.IsTrue(shot.Weapon.IsHoming);
            Assert.AreEqual(44, ship.AmmoCount("Sidewinder Missile"));
        }
    }
}
