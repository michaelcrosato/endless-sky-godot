using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Weapon loading checked against the real upstream dataset rather than
    /// hand-written fixtures: the point of the data layer is to consume unmodified
    /// Endless Sky content, so that is what these assert on.
    /// </summary>
    [TestFixture]
    public class WeaponTests
    {
        private static GameData Data => UpstreamData.Instance;

        [Test]
        public void EnergyBlasterLoadsItsUpstreamStats()
        {
            Outfit blaster = Data.Outfits["Energy Blaster"];

            Assert.IsTrue(blaster.IsWeapon, "an outfit with a weapon block is a weapon");
            Assert.AreEqual(10.6, blaster.Weapon.ShieldDamage, 1e-9);
            Assert.AreEqual(6.6, blaster.Weapon.HullDamage, 1e-9);
            Assert.AreEqual(10.625, blaster.Weapon.Velocity, 1e-9);
            Assert.AreEqual(48.0, blaster.Weapon.Lifetime, 1e-9);
            Assert.AreEqual(12.0, blaster.Weapon.Reload, 1e-9);
            Assert.AreEqual(5.8, blaster.Weapon.FiringEnergy, 1e-9);
            Assert.AreEqual(18.0, blaster.Weapon.FiringHeat, 1e-9);
            Assert.AreEqual(3.0, blaster.Weapon.Inaccuracy, 1e-9);
        }

        [Test]
        public void NonWeaponOutfitsHaveNoWeaponBlock()
        {
            Outfit thruster = Data.Outfits["X1700 Ion Thruster"];

            Assert.IsFalse(thruster.IsWeapon);
            Assert.AreEqual(0.0, thruster.Weapon.ShieldDamage, 1e-9);
        }

        [Test]
        public void TheWeaponBlockDoesNotLeakIntoOutfitAttributes()
        {
            // "shield damage" belongs to the weapon, not to the ship that installs it.
            // If it leaked into outfit attributes it would be summed into the ship's
            // own attribute bag and corrupt every derived stat.
            Outfit blaster = Data.Outfits["Energy Blaster"];

            Assert.AreEqual(0.0, blaster.Attributes.Get("shield damage"), 1e-9);
            Assert.AreEqual(5.0, blaster.Attributes.Get("mass"), 1e-9);
            Assert.AreEqual(-5.0, blaster.Attributes.Get("outfit space"), 1e-9);
        }

        [Test]
        public void TheDatasetContainsAWorkableNumberOfWeapons()
        {
            int weapons = Data.Outfits.Values.Count(o => o.IsWeapon);

            Assert.Greater(weapons, 100,
                $"expected the upstream dataset to define many weapons, found {weapons}");
        }

        [Test]
        public void HomingIsReadAsAValuelessFlag()
        {
            // Upstream writes homing as a bare key with no value, and treats a
            // following number as deprecated legacy syntax. A parser that only
            // records key/number pairs silently drops every missile in the game.
            Weapon sidewinder = Data.Outfits["Sidewinder Missile Launcher"].Weapon;

            Assert.IsTrue(sidewinder.IsHoming);
            Assert.AreEqual(14.0, sidewinder.Velocity, 1e-9);
            Assert.AreEqual(350.0, sidewinder.Lifetime, 1e-9);
            Assert.AreEqual(4.0, sidewinder.Turn, 1e-9);
            Assert.AreEqual(1.2, sidewinder.Acceleration, 1e-9);
        }

        [Test]
        public void GunsAreNotHoming()
        {
            Assert.IsFalse(Data.Outfits["Energy Blaster"].Weapon.IsHoming);
        }

        [Test]
        public void TheDatasetContainsBothHomingAndStraightFiringWeapons()
        {
            var homing = Data.Outfits.Values.Where(o => o.IsWeapon && o.Weapon.IsHoming).ToList();
            var straight = Data.Outfits.Values.Where(o => o.IsWeapon && !o.Weapon.IsHoming).ToList();

            // Upstream declares homing on ~68 weapon blocks across all factions.
            Assert.Greater(homing.Count, 30, "missiles exist upstream");
            Assert.IsNotEmpty(straight, "guns exist upstream");
        }

        [Test]
        public void EveryWeaponHasAPositiveReload()
        {
            // Note: damage may legitimately be NEGATIVE upstream. The Korath Minelayer
            // gives its carrier shell -3200 shield damage so that hitting the round
            // before it splits into submunitions deals less damage than the cloud
            // would. So this asserts only on reload, which genuinely must be positive
            // or the weapon would fire every frame.
            foreach (Outfit outfit in Data.Outfits.Values.Where(o => o.IsWeapon))
            {
                Assert.Greater(outfit.Weapon.Reload, 0.0, $"{outfit.Name} reload must be positive");
            }
        }

        [Test]
        public void NegativeDamageIsPreservedRatherThanClampedAway()
        {
            Weapon minelayer = Data.Outfits["Korath Minelayer"].Weapon;

            Assert.AreEqual(-3200.0, minelayer.ShieldDamage, 1e-9);
            Assert.AreEqual(-2400.0, minelayer.HullDamage, 1e-9);
        }

        [Test]
        public void ClusterWeaponsRecordTheirSubmunitions()
        {
            Weapon minelayer = Data.Outfits["Korath Minelayer"].Weapon;

            Assert.IsTrue(minelayer.HasSubmunitions);
            Assert.AreEqual(1, minelayer.Submunitions.Count);
            Assert.AreEqual("Korath Mine Submunition", minelayer.Submunitions[0].WeaponName);
            Assert.AreEqual(11, minelayer.Submunitions[0].Count);
        }

        [Test]
        public void SubmunitionCarriersMayHaveNoLifetimeOfTheirOwn()
        {
            // The carrier exists only to split, so upstream gives it velocity but no
            // lifetime: it bursts on its first frame.
            Weapon turret = Data.Outfits["Ion Hail Turret"].Weapon;

            Assert.IsTrue(turret.HasSubmunitions);
            Assert.AreEqual(4, turret.Submunitions[0].Count);
            Assert.Greater(turret.Velocity, 0.0);
            Assert.AreEqual(0.0, turret.Lifetime, 1e-9);
        }

        [Test]
        public void OrdinaryWeaponsHaveNoSubmunitions()
        {
            Assert.IsFalse(Data.Outfits["Energy Blaster"].Weapon.HasSubmunitions);
            Assert.IsEmpty(Data.Outfits["Energy Blaster"].Weapon.Submunitions);
        }

        [Test]
        public void ProjectileWeaponsCarryALifetimeAndVelocity()
        {
            // A projectile with no lifetime would never expire; one with no velocity
            // would never leave the muzzle. Beams are the exception upstream, so this
            // only checks weapons that actually declare a velocity.
            // Submunition carriers are exempt: a round like the Ion Hail Turret's
            // exists only to split, so upstream gives it no lifetime of its own and
            // it bursts on its first frame.
            var moving = Data.Outfits.Values
                .Where(o => o.IsWeapon && o.Weapon.Velocity > 0.0 && !o.Weapon.HasSubmunitions)
                .ToList();

            Assert.IsNotEmpty(moving);
            var noLifetime = moving.Where(o => o.Weapon.Lifetime <= 0.0).Select(o => o.Name).ToList();
            Assert.IsEmpty(noLifetime,
                "moving projectiles without a lifetime: " + string.Join(", ", noLifetime));
        }
    }
}
