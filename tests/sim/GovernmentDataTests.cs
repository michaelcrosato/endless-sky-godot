using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Factions as the dataset actually defines them, and the ships that belong to
    /// them. The behavioural rules of <see cref="Government"/> are covered elsewhere;
    /// this is about the data reaching them at all.
    /// </summary>
    [TestFixture]
    public class GovernmentDataTests
    {
        private static GameData Data => UpstreamData.Instance;

        [Test]
        public void TheDatasetsGovernmentsLoad()
        {
            // They were not parsed at all until this suite existed: the Government type
            // was fully implemented and nothing ever fed it, so every faction in the
            // game had default attitudes and no enemies.
            Assert.Greater(Data.Governments.Count, 50, "the dataset defines many factions");

            TestContext.WriteLine($"{Data.Governments.Count} governments; " +
                $"{Data.Governments.Values.Count(g => g.Enemies.Count > 0)} have declared enemies");

            foreach (string name in new[] { "Republic", "Pirate", "Merchant", "Free Worlds" })
                Assert.IsTrue(Data.Governments.ContainsKey(name), name);
        }

        [Test]
        public void AttitudesAreLoadedAndProduceRealHostility()
        {
            Government republic = Data.Governments["Republic"];
            Government pirate = Data.Governments["Pirate"];
            Government merchant = Data.Governments["Merchant"];

            Assert.IsNotEmpty(republic.Attitudes, "attitudes come from the data file");

            Assert.IsTrue(republic.IsEnemy(pirate), "the Republic fights pirates");
            Assert.IsTrue(pirate.IsEnemy(republic), "and hostility is mutual here");
            Assert.IsFalse(republic.IsEnemy(merchant), "but not its own traders");
        }

        [Test]
        public void CrewStrengthsComeFromTheData()
        {
            // Boarding odds depend on these, so a default of 1.0/2.0 everywhere would
            // make every faction's crew identical.
            Government republic = Data.Governments["Republic"];

            Assert.Greater(republic.CrewAttack, 1.0,
                "Republic crews are trained above the default");
            Assert.Greater(republic.CrewDefense, 2.0);
        }

        [Test]
        public void ShipsAreBuiltFlyingTheirOwnFlag()
        {
            // The loop that was open: the ship-to-government index resolved a NAME, and
            // nothing turned that name into the Government object the simulation uses.
            Ship barge = Data.BuildShip("Star Barge");
            Ship raven = Data.BuildShip("Marauder Raven");

            Assert.IsNotNull(barge.Government, "a stock freighter belongs to someone");
            Assert.AreEqual("Merchant", barge.Government!.Name);
            Assert.AreEqual("Pirate", raven.Government!.Name);

            Assert.IsTrue(raven.Government.IsEnemy(barge.Government),
                "a pirate raider and a merchant hauler are on opposing sides");
        }

        [Test]
        public void MostShipsInTheGameHaveAGovernment()
        {
            var built = Data.Ships.Values
                .Where(d => d.Attributes.Get("mass") > 0)
                .Select(d => Data.BuildShip(d.DisplayName))
                .ToList();

            int flagged = built.Count(s => s.Government != null);
            double coverage = (double)flagged / built.Count;

            TestContext.WriteLine($"{flagged} of {built.Count} hulls fly a flag ({coverage:P0})");
            Assert.Greater(coverage, 0.5);
        }

        [Test]
        public void TopLevelConversationsLoad()
        {
            // Missions reference conversations by name, so a mission whose conversation
            // lives at top level had nothing to show.
            Assert.Greater(Data.Conversations.Count, 10);

            var referenced = Data.Missions.Values
                .SelectMany(m => m.Actions.Values)
                .Where(a => a?.Conversation != null)
                .Select(a => a!.Conversation!)
                .Distinct()
                .ToList();

            int resolvable = referenced.Count(name => Data.Conversations.ContainsKey(name));

            TestContext.WriteLine(
                $"{Data.Conversations.Count} conversations defined; missions reference " +
                $"{referenced.Count} by name, {resolvable} of which resolve");

            Assert.Greater(resolvable, 0, "named references should find their conversation");
        }

        // --- Politics: offence propagates across governments -----------------------

        [Test]
        public void AGovernmentIsWhollyOnItsOwnSide()
        {
            // Government.cpp:588-589 returns 1 for self. Returning 0 made a government
            // indifferent to what was done to it, which would leave any penalty
            // calculation weighted by zero -- a no-op dressed up as a rule.
            var data = new GameData();
            data.LoadText("government \"Republic\"" + "\n");

            Government republic = data.Governments["Republic"];
            Assert.AreEqual(1.0, republic.AttitudeToward(republic), 1e-9);
        }

        [Test]
        public void OffendingOneGovernmentMovesItsFriendsAndItsEnemies()
        {
            // Politics.cpp:111-149 walks EVERY government and weights the penalty by
            // that government's attitude toward the offended one. This is the mechanism
            // by which shooting pirates earns navy goodwill: nothing else in the game
            // makes an ally like you for hurting their enemy.
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "government \"Navy\"",
                "\t\"attitude toward\"",
                "\t\t\"Pirates\" -1",
                "\t\t\"Militia\" 0.5",
                "government \"Militia\"",
                "\t\"attitude toward\"",
                "\t\t\"Pirates\" -0.02",
                "government \"Pirates\"") + "\n");

            var politics = new Politics(data);
            politics.Offend(data.Governments["Pirates"], "destroy", count: 1);

            Assert.Less(data.Governments["Pirates"].Reputation, 0.0,
                "the offended government itself, at full weight");
            Assert.Greater(data.Governments["Navy"].Reputation, 0.0,
                "an enemy of the offended government is pleased");
            Assert.AreEqual(0.0, data.Governments["Militia"].Reputation, 1e-9,
                "a weight under 5% never moves reputation at all");
        }

        [Test]
        public void OffendingThePlayersOwnGovernmentDoesNothing()
        {
            // Politics.cpp:116-117 returns immediately for the player's flag.
            var data = new GameData();
            data.LoadText("government \"Player\"" + "\n" + "government \"Navy\"" + "\n");
            data.Governments["Player"].IsPlayer = true;

            new Politics(data).Offend(data.Governments["Player"], "destroy");

            Assert.AreEqual(0.0, data.Governments["Navy"].Reputation, 1e-9);
        }

    }
}
