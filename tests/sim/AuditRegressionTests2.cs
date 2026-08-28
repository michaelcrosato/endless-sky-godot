using System.Collections.Generic;
using System.Globalization;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Second batch of parity-audit regressions: hostility, planets, crew and
    /// salaries. Engine-free.
    /// </summary>
    [TestFixture]
    public class AuditRegressionTests2
    {
        private static DataNode Parse(string text) => new DataFile(text, "test.txt").Nodes[0];

        private static Government LoadGovernment(string text)
        {
            DataNode node = Parse(text);
            var government = new Government(node.Token(1));
            government.Load(node);
            return government;
        }

        private static Planet LoadPlanet(string text)
        {
            DataNode node = Parse(text);
            var planet = new Planet(node.Token(1));
            planet.Load(node);
            return planet;
        }

        // --- Hostility is symmetric ----------------------------------------------

        [Test]
        public void EitherSidesDislikeMakesThemEnemies()
        {
            // Upstream: a.AttitudeToward(b) < 0 || b.AttitudeToward(a) < 0. Checking
            // only one side means a government nobody has listed is hostile to nobody,
            // even when half the galaxy has listed it.
            Government pirate = LoadGovernment(
                "government \"Pirate\"\n\t\"attitude toward\"\n\t\t\"Merchant\" -.1\n");
            Government merchant = LoadGovernment("government \"Merchant\"\n");

            Assert.IsTrue(pirate.IsEnemy(merchant), "the side that declared it");
            Assert.IsTrue(merchant.IsEnemy(pirate),
                "and the side that did not: hostility is mutual");
        }

        [Test]
        public void IndifferentGovernmentsAreNotEnemies()
        {
            Government a = LoadGovernment("government \"A\"\n");
            Government b = LoadGovernment("government \"B\"\n");

            Assert.IsFalse(a.IsEnemy(b));
            Assert.IsFalse(b.IsEnemy(a));
            Assert.IsFalse(a.IsEnemy(a), "never its own enemy");
        }

        [Test]
        public void APositiveAttitudeIsNotHostility()
        {
            Government hunter = LoadGovernment(
                "government \"Hunter\"\n\t\"attitude toward\"\n\t\t\"Bounty\" 1.\n");
            Government bounty = LoadGovernment("government \"Bounty\"\n");

            Assert.IsFalse(hunter.IsEnemy(bounty));
        }

        [Test]
        public void DefaultAttitudeAppliesToUnlistedGovernments()
        {
            // A xenophobic faction is hostile to everyone without listing them.
            Government xenophobe = LoadGovernment(
                "government \"Xenophobe\"\n\t\"default attitude\" -1\n");
            Government stranger = LoadGovernment("government \"Stranger\"\n");

            Assert.AreEqual(-1.0, xenophobe.AttitudeToward(stranger), 1e-9);
            Assert.IsTrue(xenophobe.IsEnemy(stranger));
        }

        // --- The player path is reputation-driven --------------------------------

        [Test]
        public void AGovernmentTurnsHostileToThePlayerOnNegativeReputation()
        {
            // Without this path no government is ever hostile to the player, however
            // many of their ships you destroy.
            var player = new Government("Player") { IsPlayer = true };
            Government navy = LoadGovernment("government \"Republic\"\n\t\"player reputation\" 100\n");

            Assert.IsFalse(player.IsEnemy(navy));

            navy.SetReputation(-1);

            Assert.IsTrue(player.IsEnemy(navy));
            Assert.IsTrue(navy.IsEnemy(player), "and the question is the same asked either way");
        }

        [Test]
        public void DestroyingShipsDrivesReputationNegative()
        {
            Government navy = LoadGovernment("government \"Republic\"\n\t\"player reputation\" 2\n");
            var player = new Government("Player") { IsPlayer = true };

            Assert.IsFalse(player.IsEnemy(navy));

            navy.Offend("destroy", 3);

            Assert.Less(navy.Reputation, 0.0);
            Assert.IsTrue(player.IsEnemy(navy));
        }

        [Test]
        public void ABribedGovernmentStopsBeingAnEnemyOfThePlayer()
        {
            var player = new Government("Player") { IsPlayer = true };
            Government pirate = LoadGovernment("government \"Pirate\"\n\t\"player reputation\" -10\n");

            Assert.IsTrue(player.IsEnemy(pirate));

            player.Bribe(pirate);

            Assert.IsFalse(player.IsEnemy(pirate), "a bribe buys passage even at bad standing");
        }

        // --- Planets --------------------------------------------------------------

        [Test]
        public void ThePortNodeCountsAsASpaceport()
        {
            // "port" is upstream's newer spelling for the same thing; ignoring it left
            // 19 vanilla worlds reporting no spaceport and unable to refuel.
            Planet withPort = LoadPlanet("planet \"New World\"\n\tport `A busy port.`\n");
            Planet withSpaceport = LoadPlanet("planet \"Old World\"\n\tspaceport `A busy port.`\n");

            Assert.IsTrue(withPort.HasSpaceport);
            Assert.IsTrue(withSpaceport.HasSpaceport);
            Assert.IsTrue(withPort.IsInhabited);
        }

        [Test]
        public void SecurityDefaultsToAQuarterNotZero()
        {
            // Defaulting to zero makes every world that does not declare it a free
            // port with no smuggling checks at all.
            Assert.AreEqual(0.25, LoadPlanet("planet \"Quiet\"\n").Security, 1e-9);
            Assert.AreEqual(0.05, LoadPlanet("planet \"Lax\"\n\tsecurity 0.05\n").Security, 1e-9);
            Assert.AreEqual(0.0, LoadPlanet("planet \"Free\"\n\tsecurity 0\n").Security, 1e-9);
        }

        [Test]
        public void TheUninhabitedAttributeVetoesLandability()
        {
            // Content tags a world uninhabited to make it scenery even when it still
            // carries other data.
            Planet tagged = LoadPlanet(
                "planet \"Rock\"\n\tattributes uninhabited\n\tspaceport `Ruins.`\n");

            Assert.IsTrue(tagged.HasSpaceport);
            Assert.IsFalse(tagged.IsInhabited, "the tag overrides the services");
        }

        // --- Crew -----------------------------------------------------------------

        private static Ship MakeShip(params string[] attributeLines)
        {
            var lines = new List<string> { "ship \"Test\"", "\tattributes" };
            foreach (string line in attributeLines)
                lines.Add("\t\t" + line);

            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return new Ship(definition);
        }

        [Test]
        public void AnAutomatonNeedsNoCrewAndEverythingElseNeedsAtLeastOne()
        {
            Ship drone = MakeShip("\"hull\" 100", "\"mass\" 50", "\"automaton\" 1", "\"required crew\" 3");
            Assert.AreEqual(0, drone.RequiredCrew, "automation replaces the crew entirely");

            Ship unspecified = MakeShip("\"hull\" 100", "\"mass\" 50");
            Assert.AreEqual(1, unspecified.RequiredCrew,
                "a crewed ship needs somebody aboard however its attributes read");

            Ship freighter = MakeShip("\"hull\" 100", "\"mass\" 50", "\"required crew\" 5");
            Assert.AreEqual(5, freighter.RequiredCrew);
        }

        [Test]
        public void AnUnderCrewedFlagshipReducesTheSalaryBill()
        {
            // The shortfall must not be floored at zero: upstream counts it as a
            // negative, and clamping overcharges the player for crew they do not have.
            var fleet = new PlayerFleet();
            Ship flagship = MakeShip("\"hull\" 100", "\"mass\" 50", "\"required crew\" 5", "\"bunks\" 10");
            fleet.Add(flagship);

            Assert.AreEqual(400L, fleet.DailySalaries(), "fully crewed: 100 * (5 - 1)");

            flagship.Crew = 3;
            Assert.AreEqual(200L, fleet.DailySalaries(), "two berths short: 100 * (3 - 1)");
        }

        // --- Boarding -------------------------------------------------------------

        [Test]
        public void GovernmentCrewPowerOverridesAreHonoured()
        {
            Government elite = LoadGovernment(
                "government \"Elite\"\n\t\"crew attack\" 4\n\t\"crew defense\" 6\n");

            Ship attacker = MakeShip("\"hull\" 100", "\"mass\" 50", "\"bunks\" 10");
            attacker.Crew = 3;
            attacker.Government = elite;

            Ship defender = MakeShip("\"hull\" 100", "\"mass\" 50", "\"bunks\" 10");
            defender.Crew = 3;

            var odds = new CaptureOdds(attacker, defender);

            Assert.AreEqual(12.0, odds.AttackerPower(3), 1e-9, "3 crew at 4.0 each");
            Assert.AreEqual(6.0, odds.DefenderPower(3), 1e-9, "defender falls back to the 2.0 default");
        }

        [Test]
        public void EliteBoardersBeatAnEvenlyCrewedDefender()
        {
            Government elite = LoadGovernment("government \"Elite\"\n\t\"crew attack\" 6\n");

            Ship attacker = MakeShip("\"hull\" 100", "\"mass\" 50", "\"bunks\" 20");
            attacker.Crew = 10;
            attacker.Government = elite;

            Ship defender = MakeShip("\"hull\" 100", "\"mass\" 50", "\"bunks\" 20");
            defender.Crew = 10;

            Assert.Greater(new CaptureOdds(attacker, defender).CaptureChance(10, 10), 0.5,
                "a government that trains its boarders should win an even fight");
        }
    }
}
