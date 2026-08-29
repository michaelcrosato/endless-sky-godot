using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Damage model parity with upstream DamageProfile / Ship::TakeDamage.
    /// Engine-free: no Godot process is involved.
    /// </summary>
    [TestFixture]
    public class ShipCombatTests
    {
        /// <summary>A ship with explicit shields/hull and no outfits, for arithmetic that is easy to check by hand.</summary>
        private static Ship MakeShip(double shields = 100.0, double hull = 100.0, double mass = 100.0)
        {
            var definition = new ShipDefinition("Test Ship");
            definition.Attributes.Set("shields", shields);
            definition.Attributes.Set("hull", hull);
            definition.Attributes.Set("mass", mass);
            definition.Attributes.Set("energy capacity", 100.0);
            definition.Attributes.Set("fuel capacity", 100.0);
            definition.Attributes.Set("heat capacity", 1.0);
            return new Ship(definition);
        }

        private static Weapon MakeWeapon(params (string Key, double Value)[] attributes)
        {
            var weapon = new Weapon();
            var node = new DataFile(BuildWeaponText(attributes), "test.txt").Nodes[0];
            weapon.Load(node);
            return weapon;
        }

        private static string BuildWeaponText((string Key, double Value)[] attributes)
        {
            var text = new System.Text.StringBuilder("weapon\n");
            foreach ((string key, double value) in attributes)
            {
                text.Append('\t').Append('"').Append(key).Append('"').Append(' ')
                    .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            return text.ToString();
        }

        [Test]
        public void LevelsStartFull()
        {
            var ship = MakeShip(shields: 250.0, hull: 400.0);

            Assert.AreEqual(250.0, ship.Shields, 1e-9);
            Assert.AreEqual(400.0, ship.Hull, 1e-9);
            Assert.AreEqual(0.0, ship.Heat, 1e-9);
            Assert.IsFalse(ship.IsDisabled);
            Assert.IsFalse(ship.IsDestroyed);
        }

        [Test]
        public void ShieldsBlockHullDamageEntirelyWhileTheyHold()
        {
            // This is the defining property of Endless Sky combat: hull takes nothing
            // at all until shields are gone, rather than absorbing a share of it.
            var ship = MakeShip(shields: 100.0, hull: 100.0);
            var weapon = MakeWeapon(("shield damage", 10.0), ("hull damage", 40.0));

            ship.TakeDamage(weapon);

            Assert.AreEqual(90.0, ship.Shields, 1e-9);
            Assert.AreEqual(100.0, ship.Hull, 1e-9, "hull must be untouched while shields remain");
        }

        [Test]
        public void ExcessDamageBleedsThroughInTheFrameShieldsRunOut()
        {
            // 10 shields left, a 40-shield-damage shot: the shields can pay for 25% of
            // it, so 75% of the hull damage lands in this same frame.
            var ship = MakeShip(shields: 100.0, hull: 100.0);
            ship.SetLevels(shields: 10.0);
            var weapon = MakeWeapon(("shield damage", 40.0), ("hull damage", 40.0));

            ship.TakeDamage(weapon);

            Assert.AreEqual(0.0, ship.Shields, 1e-9);
            Assert.AreEqual(100.0 - 30.0, ship.Hull, 1e-9);
        }

        [Test]
        public void WithShieldsDownAllHullDamageLands()
        {
            var ship = MakeShip(shields: 100.0, hull: 100.0);
            ship.SetLevels(shields: 0.0);
            var weapon = MakeWeapon(("hull damage", 25.0));

            ship.TakeDamage(weapon);

            Assert.AreEqual(75.0, ship.Hull, 1e-9);
        }

        [Test]
        public void PiercingBypassesShieldsProportionally()
        {
            // piercing 0.25 => a quarter of the hull damage lands through full shields.
            var ship = MakeShip(shields: 1000.0, hull: 100.0);
            var weapon = MakeWeapon(("hull damage", 40.0), ("piercing", 0.25));

            ship.TakeDamage(weapon);

            Assert.AreEqual(100.0 - 10.0, ship.Hull, 1e-9);
        }

        [Test]
        public void RelativeDamageScalesWithTargetCapacity()
        {
            var ship = MakeShip(shields: 500.0, hull: 100.0);
            var weapon = MakeWeapon(("relative shield damage", 0.1));

            ship.TakeDamage(weapon);

            Assert.AreEqual(450.0, ship.Shields, 1e-9, "10% of a 500-point shield pool");
        }

        [Test]
        public void EnergyAndHeatAreOnlyHalfBlockedByShields()
        {
            var ship = MakeShip(shields: 1000.0, hull: 100.0, mass: 100.0);
            ship.SetLevels(energy: 100.0);
            var weapon = MakeWeapon(("energy damage", 40.0), ("heat damage", 40.0));

            ship.TakeDamage(weapon);

            // Full shields => shieldFraction 1 => scale 0.5.
            Assert.AreEqual(80.0, ship.Energy, 1e-9);
            Assert.AreEqual(20.0, ship.Heat, 1e-9);
        }

        [Test]
        public void ShipIsDisabledWhenHullFallsBelowTheThreshold()
        {
            var ship = MakeShip(shields: 0.0, hull: 100.0);
            double threshold = ship.MinimumHull;
            Assert.Greater(threshold, 0.0, "a 100-hull ship should have a positive disable threshold");

            var weapon = MakeWeapon(("hull damage", 100.0 - threshold + 1.0));
            ShipEvent events = ship.TakeDamage(weapon);

            Assert.IsTrue(ship.IsDisabled);
            Assert.IsTrue(events.HasFlag(ShipEvent.Disable));
            Assert.IsFalse(ship.IsDestroyed, "crossing the disable threshold must not destroy");
        }

        [Test]
        public void DisableEventFiresOnlyOnTheTransition()
        {
            var ship = MakeShip(shields: 0.0, hull: 100.0);
            var bigHit = MakeWeapon(("hull damage", 95.0));

            ShipEvent first = ship.TakeDamage(bigHit);
            Assert.IsTrue(first.HasFlag(ShipEvent.Disable));

            // Already disabled: a second hit must not re-report the transition.
            ShipEvent second = ship.TakeDamage(MakeWeapon(("hull damage", 1.0)));
            Assert.IsFalse(second.HasFlag(ShipEvent.Disable));
        }

        [Test]
        public void ShipIsDestroyedOnlyWhenHullGoesBelowZero()
        {
            // Uses a never-disabled hull so the threshold clamp does not interfere:
            // this test is about the destroyed boundary alone.
            var definition = new ShipDefinition("Drone");
            definition.Attributes.Set("hull", 100.0);
            definition.Attributes.Set("never disabled", 1.0);
            var ship = new Ship(definition);

            // Exactly zero hull is explicitly NOT destroyed upstream.
            ship.TakeDamage(MakeWeapon(("hull damage", 100.0)));
            Assert.AreEqual(0.0, ship.Hull, 1e-9);
            Assert.IsFalse(ship.IsDestroyed);

            ShipEvent events = ship.TakeDamage(MakeWeapon(("hull damage", 1.0)));
            Assert.Less(ship.Hull, 0.0);
            Assert.IsTrue(ship.IsDestroyed);
            Assert.IsTrue(events.HasFlag(ShipEvent.Destroy));
        }

        [Test]
        public void HullDamageCanCrossTheThresholdByExactlyTheUpstreamEpsilon()
        {
            // Upstream clamps hull damage to (hull + 0.25 - minimumHull). The 0.25 is
            // what allows a weapon to land a ship strictly below its threshold.
            // "disabled damage" is declared explicitly as 0 here to isolate that
            // clamp: left unset it DEFAULTS to hull damage and the shot carries on
            // past the threshold.
            var ship = MakeShip(shields: 0.0, hull: 100.0);
            double threshold = ship.MinimumHull;

            ship.TakeDamage(MakeWeapon(("hull damage", 10000.0), ("disabled damage", 0.0)));

            Assert.AreEqual(threshold - 0.25, ship.Hull, 1e-9);
            Assert.IsTrue(ship.IsDisabled, "landing 0.25 below the threshold disables");
            Assert.IsFalse(ship.IsDestroyed);
        }

        [Test]
        public void DisabledDamageDefaultsToHullDamageSoOrdinaryWeaponsCanKill()
        {
            // No vanilla weapon declares "disabled damage"; upstream fills it in from
            // hull damage after parsing. Without that default the overshoot past the
            // disable threshold is paid at zero, and a ship becomes indestructible by
            // gunfire the moment it is disabled.
            var weapon = MakeWeapon(("hull damage", 40.0));
            Assert.AreEqual(40.0, weapon.DisabledDamage, 1e-9,
                "disabled damage should mirror hull damage when unset");

            var ship = MakeShip(shields: 0.0, hull: 100.0);
            ship.TakeDamage(MakeWeapon(("hull damage", 10000.0)));

            Assert.IsTrue(ship.IsDestroyed, "an overwhelming hit destroys rather than merely disabling");
        }

        [Test]
        public void ADisabledShipCanStillBeFinishedOff()
        {
            // The regression this guards: once hull sits at the threshold,
            // HullUntilDisabled is ~0, so if disabled damage were 0 every later shot
            // would do literally nothing and the wreck would be immortal.
            var ship = MakeShip(shields: 0.0, hull: 100.0);

            ship.TakeDamage(MakeWeapon(("hull damage", 60.0)));
            Assert.IsTrue(ship.IsDisabled);
            Assert.IsFalse(ship.IsDestroyed);

            for (int shot = 0; shot < 20 && !ship.IsDestroyed; shot++)
                ship.TakeDamage(MakeWeapon(("hull damage", 20.0)));

            Assert.IsTrue(ship.IsDestroyed, "sustained fire must eventually destroy a disabled ship");
        }

        [Test]
        public void DisabledDamageGovernsTheOvershootPastTheThreshold()
        {
            // Two identical shots, differing only in "disabled damage". The portion of
            // a hit that would carry a target past its disable threshold is paid for by
            // that attribute alone, which is how upstream separates weapons that
            // cripple from weapons that kill.
            var stunned = MakeShip(shields: 0.0, hull: 100.0);
            double threshold = stunned.MinimumHull;
            stunned.TakeDamage(MakeWeapon(("hull damage", 1000.0), ("disabled damage", 0.0)));

            Assert.AreEqual(threshold - 0.25, stunned.Hull, 1e-9,
                "with no disabled damage the shot stops just past the threshold");
            Assert.IsTrue(stunned.IsDisabled);
            Assert.IsFalse(stunned.IsDestroyed, "a pure hull-damage weapon cannot overkill a healthy ship");

            var killed = MakeShip(shields: 0.0, hull: 100.0);
            killed.TakeDamage(MakeWeapon(("hull damage", 1000.0), ("disabled damage", 1000.0)));

            Assert.IsTrue(killed.IsDestroyed, "disabled damage carries the shot through to destruction");
        }

        [Test]
        public void NeverDisabledShipsHaveNoThreshold()
        {
            var definition = new ShipDefinition("Drone");
            definition.Attributes.Set("hull", 100.0);
            definition.Attributes.Set("never disabled", 1.0);
            var ship = new Ship(definition);

            Assert.AreEqual(0.0, ship.MinimumHull, 1e-9);

            ship.TakeDamage(MakeWeapon(("hull damage", 101.0)));
            Assert.IsTrue(ship.IsDestroyed, "with no threshold, damage runs straight through to destruction");
        }

        [Test]
        public void AbsoluteThresholdOverridesTheHullCurve()
        {
            var definition = new ShipDefinition("Bulk Freighter");
            definition.Attributes.Set("hull", 1000.0);
            definition.Attributes.Set("absolute threshold", 250.0);
            var ship = new Ship(definition);

            Assert.AreEqual(250.0, ship.MinimumHull, 1e-9);
        }

        [Test]
        public void DisableThresholdIsALargerFractionForSmallHulls()
        {
            // Upstream slides the threshold from ~50% of hull toward ~10% as ships grow.
            double smallFraction = MakeShip(hull: 100.0).MinimumHull / 100.0;
            double largeFraction = MakeShip(hull: 20000.0).MinimumHull / 20000.0;

            Assert.Greater(smallFraction, largeFraction);
            Assert.That(smallFraction, Is.LessThanOrEqualTo(0.5));
            Assert.That(largeFraction, Is.GreaterThanOrEqualTo(0.1));
        }

        [Test]
        public void ThresholdPercentageOverridesTheCurve()
        {
            var definition = new ShipDefinition("Test");
            definition.Attributes.Set("hull", 1000.0);
            definition.Attributes.Set("threshold percentage", 0.25);
            var ship = new Ship(definition);

            Assert.AreEqual(250.0, ship.MinimumHull, 1e-9);
        }

        [Test]
        public void LevelsAreClampedToTheirCapacities()
        {
            var ship = MakeShip(shields: 100.0, hull: 100.0);
            ship.SetLevels(shields: 999.0, hull: 999.0);

            Assert.AreEqual(100.0, ship.Shields, 1e-9);
            Assert.AreEqual(100.0, ship.Hull, 1e-9);
        }

        [Test]
        public void BeingShotByANonEnemyProvokesTheTarget()
        {
            // Ship.cpp:3275-3285 returns PROVOKE whenever the shooter is not already an
            // enemy of the target's government -- which is what turns a stray shot into
            // a fight. MissionNpc.ApplyObjective accepts the `provoke` token and sets
            // the bit as a completion requirement, so a `provoke` objective could be
            // written, parsed, and never satisfied by anything.
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "government \"Neutral\"",
                "government \"Stranger\"") + "\n");

            Ship target = MakeShip();
            target.Government = data.Governments["Neutral"];

            ShipEvent events = target.TakeDamage(
                MakeWeapon(("hull damage", 1.0)), data.Governments["Stranger"]);

            Assert.IsTrue(events.HasFlag(ShipEvent.Provoke),
                "somebody who was not an enemy just shot at it");
        }

        [Test]
        public void BeingShotByAnExistingEnemyProvokesNothingNew()
        {
            // Already at war: there is nothing to provoke.
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "government \"Navy\"",
                "\t\"attitude toward\"",
                "\t\t\"Raiders\" -1",
                "government \"Raiders\"",
                "\t\"attitude toward\"",
                "\t\t\"Navy\" -1") + "\n");

            Ship target = MakeShip();
            target.Government = data.Governments["Navy"];

            ShipEvent events = target.TakeDamage(
                MakeWeapon(("hull damage", 1.0)), data.Governments["Raiders"]);

            Assert.IsFalse(events.HasFlag(ShipEvent.Provoke));
        }
    }
}
