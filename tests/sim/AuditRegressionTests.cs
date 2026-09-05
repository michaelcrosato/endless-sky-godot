using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Regressions for divergences found by an independent parity audit against the
    /// upstream C++ source. Each of these behaviours was wrong in the port and, in
    /// several cases, asserted as correct by an earlier test of mine.
    /// </summary>
    [TestFixture]
    public class AuditRegressionTests
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

        private static Ship MakeShip(params string[] attributeLines)
        {
            var lines = new List<string> { "ship \"Test\"", "\tattributes" };
            foreach (string line in attributeLines)
                lines.Add("\t\t" + line);

            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return new Ship(definition);
        }

        // --- "never disabled" is a bare flag, not a numeric attribute -------------

        [Test]
        public void NeverDisabledIsReadFromTheBareShipLevelFlag()
        {
            // Upstream writes it as a quoted, valueless child of the SHIP node, not
            // inside the attributes block. A parser that only records numeric
            // attributes never sees it, and 49 vanilla ships that should fight to
            // destruction instead go derelict at 10-50% hull.
            var definition = new ShipDefinition("Archon");
            definition.Load(new DataFile(
                "ship \"Archon\"\n\t\"never disabled\"\n\tattributes\n\t\t\"hull\" 1000\n", "test.txt").Nodes[0]);

            Assert.IsTrue(definition.IsNeverDisabled);
            Assert.AreEqual(0.0, new Ship(definition).MinimumHull, 1e-9);
        }

        [Test]
        public void FlagsDoNotDisinheritAVariantsAttributes()
        {
            // The trap when fixing the above: InheritFrom copies the base hull's
            // attributes only when the variant's own bag is EMPTY. Storing a flag in
            // that bag silently strips the variant of hull, mass, drag and thrust.
            var data = new GameData();
            data.LoadText(
                "ship \"Base\"\n\tattributes\n\t\t\"hull\" 500\n\t\t\"drag\" 2\n\t\t\"mass\" 100\n" +
                "\t\t\"thrust\" 20\n\t\t\"turn\" 300\n", "test.txt");
            data.LoadText("ship \"Base\" \"Base (Derelict)\"\n\t\"uncapturable\"\n", "test.txt");
            data.FinishLoading();

            ShipDefinition variant = data.Ships["Base (Derelict)"];

            Assert.IsTrue(variant.IsUncapturable);
            Assert.AreEqual(500.0, variant.Attributes.Get("hull"), 1e-9, "hull must still be inherited");
            Assert.AreEqual(2.0, variant.Attributes.Get("drag"), 1e-9, "drag must still be inherited");
        }

        [Test]
        public void VariantsDoNotInheritTheirBaseFlags()
        {
            // Upstream states the exception in Ship::FinishLoading: "uncapturable and
            // 'never disabled' flags don't carry over." Inheriting them was a
            // regression, and this test previously asserted it as correct.
            var data = new GameData();
            data.LoadText("ship \"Base\"\n\t\"never disabled\"\n\tattributes\n\t\t\"hull\" 500\n", "test.txt");
            data.LoadText("ship \"Base\" \"Base (Variant)\"\n", "test.txt");
            data.FinishLoading();

            Assert.IsTrue(data.Ships["Base"].IsNeverDisabled);
            Assert.IsFalse(data.Ships["Base (Variant)"].IsNeverDisabled,
                "a variant that does not declare the flag is disable-able");
        }

        [Test]
        public void ADisableableVariantOfANeverDisabledHullCanBeDisabled()
        {
            // Vanilla content depends on this: kahet ships.txt defines
            // ship "Fetri'sei" "Fetri'sei (Disable-able)" whose ONLY child is
            // "uncapturable" - its entire reason to exist is dropping the base's
            // "never disabled". Inheriting the flag makes that variant impossible.
            GameData data = UpstreamData.Instance;

            Assert.IsTrue(data.Ships.TryGetValue("Fetri'sei (Disable-able)", out ShipDefinition variant),
                "the pinned upstream dataset defines Fetri'sei (Disable-able)");

            Assert.IsTrue(data.Ships["Fetri'sei"].IsNeverDisabled, "the base cannot be disabled");
            Assert.IsFalse(variant.IsNeverDisabled, "but the variant exists precisely so it can be");
            Assert.Greater(new Ship(variant).MinimumHull, 0.0);
        }

        [Test]
        public void WeaponCapacitiesAreDerivedFromHardpointCounts()
        {
            // No vanilla ship declares "gun ports" or "turret mounts"; upstream sets
            // them from the armament in FinishLoading. Without that every ship reports
            // zero of both, and since a gun outfit carries "gun ports" -1 the outfitter
            // refuses to install any weapon on any ship in the game.
            var data = new GameData();
            data.LoadText(
                "ship \"Gunship\"\n\tattributes\n\t\t\"hull\" 500\n\t\t\"outfit space\" 200\n" +
                "\tgun 0 -10\n\tgun 0 10\n\tturret 0 0\n", "test.txt");
            data.FinishLoading();

            ShipDefinition definition = data.Ships["Gunship"];
            Assert.AreEqual(2.0, definition.Attributes.Get("gun ports"), 1e-9);
            Assert.AreEqual(1.0, definition.Attributes.Get("turret mounts"), 1e-9);
        }

        [Test]
        public void ARealUpstreamShipCanActuallyMountAGun()
        {
            GameData data = UpstreamData.Instance;

            Ship shuttle = data.BuildShip("Shuttle");
            Assert.IsNotNull(shuttle);

            Outfit blaster = data.Outfits["Energy Blaster"];
            Assert.Less(blaster.Attributes.Get("gun ports"), 0.0, "a gun consumes a port");

            // The end-to-end symptom of the missing capacities: no weapon fitted
            // anywhere, on any ship, ever.
            var gunned = data.Ships.Values
                .Where(s => s.Guns.Count > 0)
                .Take(20)
                .Select(s => new Ship(s))
                .ToList();

            Assert.IsNotEmpty(gunned);
            Assert.IsTrue(gunned.Any(s => Outfitting.Fits(s, blaster)),
                "at least some hulls with gun ports should accept a basic gun");
        }

        [Test]
        public void DroneCategoryImpliesAutomaton()
        {
            // Upstream infers it rather than requiring every drone to declare it.
            var data = new GameData();
            data.LoadText(
                "ship \"Probe\"\n\tattributes\n\t\tcategory \"Drone\"\n\t\t\"hull\" 100\n", "test.txt");
            data.FinishLoading();

            Assert.AreEqual(1.0, data.Ships["Probe"].Attributes.Get("automaton"), 1e-9);
        }

        // --- "disabled damage" defaults to hull damage ----------------------------

        [Test]
        public void DisabledDamageDefaultsToHullDamageWhenUnset()
        {
            Weapon plain = MakeWeapon("\"hull damage\" 40");
            Assert.AreEqual(40.0, plain.DisabledDamage, 1e-9);

            Weapon relative = MakeWeapon("\"relative hull damage\" 0.2");
            Assert.AreEqual(0.2, relative.RelativeDisabledDamage, 1e-9);
        }

        [Test]
        public void AnExplicitDisabledDamageIsRespected()
        {
            // Content that declares 0 means 0; the default only fills in the gap.
            Weapon stunner = MakeWeapon("\"hull damage\" 40", "\"disabled damage\" 0");
            Assert.AreEqual(0.0, stunner.DisabledDamage, 1e-9);
        }

        // --- weapon damage includes submunitions ----------------------------------

        [Test]
        public void ClusterDamageFoldsInItsSubmunitions()
        {
            Weapon shard = MakeWeapon("\"shield damage\" 350", "\"hull damage\" 280");
            Weapon carrier = MakeWeapon(
                "\"shield damage\" -3200", "\"hull damage\" -2400",
                "\"submunition\" \"shard\" 11");

            // Before resolution the carrier reports only its own (negative) damage.
            Assert.AreEqual(-3200.0, carrier.OwnDamage("shield damage"), 1e-9);

            carrier.ResolveSubmunitions(name => name == "shard" ? shard : null);

            // 11 * 350 - 3200 = +650, which is what makes the mine hurt rather than heal.
            Assert.AreEqual(650.0, carrier.ShieldDamage, 1e-9);
            Assert.AreEqual(11 * 280.0 - 2400.0, carrier.HullDamage, 1e-9);
        }

        [Test]
        public void TheRealKorathMinelayerDamagesRatherThanRepairs()
        {
            GameData data = UpstreamData.Instance;
            Weapon minelayer = data.Outfits["Korath Minelayer"].Weapon;

            // Its own attributes are negative; the submunitions more than cover it.
            Assert.Less(minelayer.OwnDamage("shield damage"), 0.0);
            Assert.Greater(minelayer.ShieldDamage, 0.0,
                "a mine that heals what it hits means submunition damage is not folded in");
            Assert.Greater(minelayer.HullDamage, 0.0);
        }

        [Test]
        public void ACarrierHittingAShipActuallyHurtsIt()
        {
            GameData data = UpstreamData.Instance;
            Weapon minelayer = data.Outfits["Korath Minelayer"].Weapon;

            var target = MakeShip("\"shields\" 5000", "\"hull\" 5000", "\"mass\" 100");
            double before = target.Shields;

            target.TakeDamage(minelayer);

            Assert.Less(target.Shields, before, "the mine must not repair its target");
        }

        // --- submunitions release on the right death ------------------------------

        [Test]
        public void SubmunitionsDefaultToReleasingOnNaturalExpiryOnly()
        {
            Weapon carrier = MakeWeapon("\"velocity\" 10", "\"lifetime\" 5", "\"submunition\" \"shard\" 4");

            Assert.AreEqual(DeathType.Natural, carrier.Submunitions[0].SpawnOn);
        }

        [Test]
        public void ACollisionDoesNotReleaseANaturalOnlyCluster()
        {
            Weapon shard = MakeWeapon("\"velocity\" 5", "\"lifetime\" 20", "\"hull damage\" 5");
            Weapon carrier = MakeWeapon(
                "\"velocity\" 20", "\"lifetime\" 50", "\"hull damage\" 10", "\"submunition\" \"shard\" 6");
            carrier.ResolveSubmunitions(name => name == "shard" ? shard : null);

            var field = new CombatField { WeaponLookup = name => name == "shard" ? shard : null };
            Ship target = MakeShip("\"shields\" 0", "\"hull\" 1000", "\"mass\" 100");
            target.Position = new Point(40.0, 0.0);
            target.CollisionRadius = 20.0;
            field.Add(target);
            field.Add(new Projectile(carrier, Point.Zero, Point.Zero, new Angle(90.0)));

            for (int frame = 0; frame < 5; frame++) field.Step();

            Assert.IsEmpty(field.Projectiles,
                "a natural-only cluster must not shower its target on impact");
        }

        // --- projectiles pass through non-enemies ---------------------------------

        [Test]
        public void ShotsPassThroughAnyNonEnemyNotJustTheirOwnSide()
        {
            var pirate = new Government("Pirate");
            var merchant = new Government("Merchant");
            // Pirate is not at war with Merchant in this setup.

            var field = new CombatField();
            Ship bystander = MakeShip("\"shields\" 100", "\"hull\" 100", "\"mass\" 100");
            bystander.Position = new Point(40.0, 0.0);
            bystander.CollisionRadius = 20.0;
            bystander.Government = merchant;
            field.Add(bystander);

            field.Add(new Projectile(MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 50"),
                Point.Zero, Point.Zero, new Angle(90.0), government: pirate));

            for (int frame = 0; frame < 6; frame++) field.Step();

            Assert.AreEqual(100.0, bystander.Shields, 1e-9,
                "neutral traffic drifting through the line of fire is not shredded");
        }

        [Test]
        public void AShotAlwaysConnectsWithTheBodyItWasAimedAt()
        {
            var navy = new Government("Republic");

            var field = new CombatField();
            Ship friendly = MakeShip("\"shields\" 100", "\"hull\" 100", "\"mass\" 100");
            friendly.Position = new Point(40.0, 0.0);
            friendly.CollisionRadius = 20.0;
            friendly.Government = navy;
            field.Add(friendly);

            // Deliberately fired AT a ship of the shooter's own government.
            field.Add(new Projectile(MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 50"),
                Point.Zero, Point.Zero, new Angle(90.0), target: friendly, government: navy));

            for (int frame = 0; frame < 6; frame++) field.Step();

            Assert.Less(friendly.Shields, 100.0, "an aimed shot connects even with a friendly");
        }

        [Test]
        public void EnemiesAreStillHit()
        {
            var pirate = new Government("Pirate");
            var navy = new Government("Republic");
            pirate.Enemies.Add("Republic");

            var field = new CombatField();
            Ship enemy = MakeShip("\"shields\" 100", "\"hull\" 100", "\"mass\" 100");
            enemy.Position = new Point(40.0, 0.0);
            enemy.CollisionRadius = 20.0;
            enemy.Government = navy;
            field.Add(enemy);

            field.Add(new Projectile(MakeWeapon("\"velocity\" 20", "\"lifetime\" 50", "\"shield damage\" 50"),
                Point.Zero, Point.Zero, new Angle(90.0), government: pirate));

            for (int frame = 0; frame < 6; frame++) field.Step();

            Assert.AreEqual(50.0, enemy.Shields, 1e-9);
        }

        // --- firing costs ---------------------------------------------------------

        [Test]
        public void AFuelBurningWeaponStopsWhenTheTankIsEmpty()
        {
            var lines = new List<string>
            {
                "ship \"Burner\"",
                "\tattributes",
                "\t\t\"hull\" 500",
                "\t\t\"mass\" 100",
                "\t\t\"energy capacity\" 1000",
                "\t\t\"fuel capacity\" 10",
                "\tgun 0 -10",
            };
            var definition = new ShipDefinition("Burner");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();

            var flamethrower = new Outfit("Flamethrower");
            flamethrower.Load(new DataFile(
                "outfit \"Flamethrower\"\n\tweapon\n\t\t\"reload\" 1\n\t\t\"firing fuel\" 4\n" +
                "\t\t\"velocity\" 5\n\t\t\"lifetime\" 20\n", "test.txt").Nodes[0]);

            WeaponMount mount = ship.InstallWeapon(flamethrower);

            Assert.IsNotNull(ship.Fire(mount)); ship.StepArmament();   // 10 -> 6
            Assert.IsNotNull(ship.Fire(mount)); ship.StepArmament();   // 6 -> 2
            Assert.AreEqual(2.0, ship.Fuel, 1e-9);

            Assert.IsNull(ship.Fire(mount), "cannot afford the fuel for another shot");
        }

        // --- heat capacity --------------------------------------------------------

        [Test]
        public void MaxHeatIsMassBasedNotAMultipleOfTheHeatCapacityAttribute()
        {
            // A ship with no heatsink outfit still has a real heat ceiling; reading
            // "heat capacity" as a multiplier made it zero and overheating instant.
            Ship bare = MakeShip("\"hull\" 100", "\"mass\" 80");
            Assert.AreEqual(100.0 * 80.0, bare.MaxHeat, 1e-9);

            Ship sinked = MakeShip("\"hull\" 100", "\"mass\" 80", "\"heat capacity\" 20");
            Assert.AreEqual(100.0 * (80.0 + 20.0), sinked.MaxHeat, 1e-9);
        }

        [Test]
        public void HeatNeverGoesNegative()
        {
            Ship ship = MakeShip("\"hull\" 100", "\"mass\" 100", "\"shields\" 0");

            ship.TakeDamage(MakeWeapon("\"heat damage\" -500"));

            Assert.GreaterOrEqual(ship.Heat, 0.0);
        }

        // --- weapon attribute parsing --------------------------------------------

        [Test]
        public void BurstReloadDefaultsToOneNotTheFullReload()
        {
            // Defaulting it to the full reload collapses a burst into a single shot.
            Weapon weapon = MakeWeapon("\"reload\" 60", "\"burst count\" 3");
            Assert.AreEqual(1.0, weapon.BurstReload, 1e-9);
        }

        [Test]
        public void AmmoUsageComesFromTheAmmoLineNotASeparateKey()
        {
            Weapon single = MakeWeapon("ammo \"Sidewinder Missile\"");
            Assert.AreEqual("Sidewinder Missile", single.AmmoName);
            Assert.AreEqual(1, single.AmmoUsage);

            Weapon salvo = MakeWeapon("ammo \"Rocket\" 4");
            Assert.AreEqual(4, salvo.AmmoUsage);
        }

        [Test]
        public void RangeUsesTheVelocityOverrideWhenPresent()
        {
            Weapon plain = MakeWeapon("\"velocity\" 10", "\"lifetime\" 40");
            Assert.AreEqual(400.0, plain.Range, 1e-9);

            Weapon overridden = MakeWeapon("\"velocity\" 10", "\"velocity override\" 4", "\"lifetime\" 40");
            Assert.AreEqual(160.0, overridden.Range, 1e-9);
        }

        [Test]
        public void ClusterRangeIncludesTheSubmunitionsFlight()
        {
            Weapon shard = MakeWeapon("\"velocity\" 5", "\"lifetime\" 30");
            Weapon carrier = MakeWeapon("\"velocity\" 10", "\"lifetime\" 20", "\"submunition\" \"shard\" 3");
            carrier.ResolveSubmunitions(name => name == "shard" ? shard : null);

            // Total lifetime is the carrier's plus the longest-lived child.
            Assert.AreEqual(50.0, carrier.TotalLifetime, 1e-9);
            Assert.AreEqual(10.0 * 50.0, carrier.Range, 1e-9);
        }
    }
}
