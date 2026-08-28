using System.Collections.Generic;
using System.Globalization;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Capture odds when boarding, against upstream CaptureOdds. Engine-free.
    /// </summary>
    [TestFixture]
    public class BoardingTests
    {
        private static Ship MakeShip(int crew, int bunks = 100)
        {
            var lines = new List<string>
            {
                "ship \"Boarder\"",
                "\tattributes",
                "\t\t\"hull\" 1000",
                "\t\t\"mass\" 100",
                "\t\t\"required crew\" 1",
                "\t\t\"bunks\" " + bunks.ToString(CultureInfo.InvariantCulture),
            };

            var definition = new ShipDefinition("Boarder");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

            var ship = new Ship(definition);
            ship.Crew = crew;
            return ship;
        }

        private static Outfit MakeHandWeapon(string name, double attack = 0.0, double defense = 0.0)
        {
            var lines = new List<string> { "outfit \"" + name + "\"" };
            if (attack != 0.0)
                lines.Add("\t\"capture attack\" " + attack.ToString(CultureInfo.InvariantCulture));
            if (defense != 0.0)
                lines.Add("\t\"capture defense\" " + defense.ToString(CultureInfo.InvariantCulture));

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        // --- Power ----------------------------------------------------------------

        [Test]
        public void CrewDefendBetterThanTheyAttack()
        {
            // Upstream's 1.0 attack against 2.0 defense is the reason boarding an
            // evenly-crewed ship is a losing proposition rather than a coin flip.
            var odds = new CaptureOdds(MakeShip(crew: 5), MakeShip(crew: 5));

            Assert.AreEqual(5.0, odds.AttackerPower(5), 1e-9);
            Assert.AreEqual(10.0, odds.DefenderPower(5), 1e-9);
        }

        [Test]
        public void PowerScalesWithCrewCount()
        {
            var odds = new CaptureOdds(MakeShip(crew: 4), MakeShip(crew: 4));

            Assert.AreEqual(1.0, odds.AttackerPower(1), 1e-9);
            Assert.AreEqual(2.0, odds.AttackerPower(2), 1e-9);
            Assert.AreEqual(4.0, odds.AttackerPower(4), 1e-9);
            Assert.AreEqual(0.0, odds.AttackerPower(5), "beyond the crew aboard");
        }

        [Test]
        public void EachCrewMemberWieldsExactlyOneWeaponBestFirst()
        {
            // Three crew, four cutlasses: the fourth is dead weight.
            Ship attacker = MakeShip(crew: 3);
            attacker.AddOutfit(MakeHandWeapon("Cutlass", attack: 3.0), 4);

            var odds = new CaptureOdds(attacker, MakeShip(crew: 1));

            // 3 crew * 1.0 innate + 3 armed * 3.0 = 12, not 3 + 12.
            Assert.AreEqual(12.0, odds.AttackerPower(3), 1e-9);
        }

        [Test]
        public void TheStrongestWeaponsAreHandedOutFirst()
        {
            Ship attacker = MakeShip(crew: 2);
            attacker.AddOutfit(MakeHandWeapon("Knife", attack: 1.0));
            attacker.AddOutfit(MakeHandWeapon("Rifle", attack: 9.0));

            var odds = new CaptureOdds(attacker, MakeShip(crew: 1));

            // One crew: innate 1.0 + the best weapon (9.0).
            Assert.AreEqual(10.0, odds.AttackerPower(1), 1e-9);
            // Two crew: + innate 1.0 + the knife (1.0).
            Assert.AreEqual(12.0, odds.AttackerPower(2), 1e-9);
        }

        [Test]
        public void DefenceOutfitsOnlyHelpTheDefender()
        {
            Ship defender = MakeShip(crew: 2);
            defender.AddOutfit(MakeHandWeapon("Security Station", defense: 5.0), 2);

            var odds = new CaptureOdds(MakeShip(crew: 2), defender);

            // 2 * (2.0 innate + 5.0 station) = 14.
            Assert.AreEqual(14.0, odds.DefenderPower(2), 1e-9);
            // The attacker, with no gear, is unaffected.
            Assert.AreEqual(2.0, odds.AttackerPower(2), 1e-9);
        }

        // --- Capture probability --------------------------------------------------

        [Test]
        public void AnAttackerDownToOneCrewCanNeverCapture()
        {
            // Somebody has to be left alive to fly the prize.
            var odds = new CaptureOdds(MakeShip(crew: 20), MakeShip(crew: 1));

            Assert.AreEqual(0.0, odds.CaptureChance(1, 1), 1e-12);
        }

        [Test]
        public void AnUndefendedShipIsCapturedOutright()
        {
            var odds = new CaptureOdds(MakeShip(crew: 5), MakeShip(crew: 1));

            Assert.AreEqual(1.0, odds.CaptureChance(5, 0), 1e-12);
        }

        [Test]
        public void MoreAttackingCrewImprovesTheOdds()
        {
            var odds = new CaptureOdds(MakeShip(crew: 30), MakeShip(crew: 5));

            double few = odds.CaptureChance(5, 5);
            double more = odds.CaptureChance(15, 5);
            double many = odds.CaptureChance(30, 5);

            Assert.Less(few, more);
            Assert.Less(more, many);
            Assert.That(many, Is.LessThanOrEqualTo(1.0));
        }

        [Test]
        public void MoreDefendingCrewWorsensTheOdds()
        {
            var odds = new CaptureOdds(MakeShip(crew: 20), MakeShip(crew: 20));

            Assert.Greater(odds.CaptureChance(10, 2), odds.CaptureChance(10, 10));
        }

        [Test]
        public void BoardingAnEvenlyCrewedShipIsALosingProposition()
        {
            // The direct consequence of 1.0 attack against 2.0 defense: equal numbers
            // strongly favour the defender, so boarding needs an edge in crew or gear.
            var odds = new CaptureOdds(MakeShip(crew: 10), MakeShip(crew: 10));

            Assert.Less(odds.CaptureChance(10, 10), 0.5);
        }

        [Test]
        public void HandToHandGearCanOvercomeADefendersNumbers()
        {
            Ship bare = MakeShip(crew: 10);
            double withoutGear = new CaptureOdds(bare, MakeShip(crew: 10)).CaptureChance(10, 10);

            Ship armed = MakeShip(crew: 10);
            armed.AddOutfit(MakeHandWeapon("Rifle", attack: 4.0), 10);
            double withGear = new CaptureOdds(armed, MakeShip(crew: 10)).CaptureChance(10, 10);

            Assert.Greater(withGear, withoutGear);
            Assert.Greater(withGear, 0.5, "arming the boarding party should flip the odds");
        }

        [Test]
        public void CaptureChanceIsAProbability()
        {
            var odds = new CaptureOdds(MakeShip(crew: 12), MakeShip(crew: 9));

            for (int a = 1; a <= 12; a++)
            {
                for (int d = 0; d <= 9; d++)
                {
                    double chance = odds.CaptureChance(a, d);
                    Assert.That(chance, Is.InRange(0.0, 1.0), $"a={a} d={d}");
                }
            }
        }

        [Test]
        public void OddsAreMonotonicInBothDirections()
        {
            // A larger boarding party is never worse; a larger garrison is never better.
            var odds = new CaptureOdds(MakeShip(crew: 15), MakeShip(crew: 12));

            for (int d = 1; d <= 12; d++)
            {
                for (int a = 2; a < 15; a++)
                {
                    Assert.That(odds.CaptureChance(a + 1, d), Is.GreaterThanOrEqualTo(odds.CaptureChance(a, d) - 1e-12),
                        $"more attackers should not hurt (a={a}, d={d})");
                }
            }

            for (int a = 2; a <= 15; a++)
            {
                for (int d = 1; d < 12; d++)
                {
                    Assert.That(odds.CaptureChance(a, d + 1), Is.LessThanOrEqualTo(odds.CaptureChance(a, d) + 1e-12),
                        $"more defenders should not help the attacker (a={a}, d={d})");
                }
            }
        }

        [Test]
        public void AnUncrewedDefenderOffersNoResistance()
        {
            var odds = new CaptureOdds(MakeShip(crew: 5), MakeShip(crew: 0));

            Assert.AreEqual(0, odds.MaxDefendingCrew);
            Assert.AreEqual(1.0, odds.CaptureChance(5, 0), 1e-12);
        }
    }
}
