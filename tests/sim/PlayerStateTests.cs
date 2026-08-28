using System;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Autoconditions: the computed condition keys content reads to interrogate the
    /// player. Partial port of upstream <c>PlayerInfo</c>'s condition providers.
    /// </summary>
    [TestFixture]
    public class PlayerStateTests
    {
        private static GameData Universe()
        {
            var data = new GameData();
            data.LoadText(
                "ship \"Shuttle\"\n\tattributes\n\t\t\"mass\" 80\n\t\t\"hull\" 400\n" +
                "\t\t\"shields\" 300\n\t\t\"bunks\" 5\n\t\t\"required crew\" 1\n\t\t\"cargo space\" 20\n" +
                "ship \"Freighter\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 600\n\t\t\"cargo space\" 70\n" +
                "outfit \"Blaster\"\n\tcost 20000\n\t\"mass\" 5\n" +
                "planet \"Earth\"\n\tgovernment \"Republic\"\n\tattributes urban rich\n\tspaceport `Busy.`\n" +
                "planet \"Luna\"\n\tgovernment \"Republic\"\n\tattributes moon\n" +
                "system \"Sol\"\n\tpos 0 0\n\tgovernment \"Republic\"\n\tlink \"Alpha Centauri\"\n" +
                "\tobject \"Earth\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n" +
                "\tobject \"Luna\"\n\t\tsprite planet/luna\n\t\tdistance 900\n\t\tperiod 400\n" +
                "system \"Alpha Centauri\"\n\tpos 100 0\n\tgovernment \"Republic\"\n" +
                "\tlink \"Sol\"\n\tlink \"Beta\"\n" +
                "system \"Beta\"\n\tpos 200 0\n\tlink \"Alpha Centauri\"\n");
            return data;
        }

        private static PlayerState Player(out GameData data)
        {
            data = Universe();
            var player = new PlayerState(data);
            var ship = new Ship(data.Ships["Shuttle"]);
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Sol"]);
            return player;
        }

        private static long Read(PlayerState player, string condition) =>
            player.Conditions.Get(condition);

        // --- The landing conditions upstream's own tests assert -------------------

        [Test]
        public void FlagshipLandedTracksLandingAndDeparture()
        {
            // Upstream's "Landing in a system with multiple planets" test asserts
            // exactly this key around every land and depart.
            PlayerState player = Player(out GameData data);

            Assert.AreEqual(0, Read(player, "flagship landed"), "starts in flight");

            player.Land(data.Planets["Earth"]);
            Assert.AreEqual(1, Read(player, "flagship landed"));

            player.Depart();
            Assert.AreEqual(0, Read(player, "flagship landed"));
        }

        [Test]
        public void FlagshipPlanetNamesTheWorldUnderTheShip()
        {
            PlayerState player = Player(out GameData data);
            player.Land(data.Planets["Earth"]);

            Assert.AreEqual(1, Read(player, "flagship planet: Earth"));
            Assert.AreEqual(0, Read(player, "flagship planet: Luna"));

            player.Land(data.Planets["Luna"]);
            Assert.AreEqual(0, Read(player, "flagship planet: Earth"));
            Assert.AreEqual(1, Read(player, "flagship planet: Luna"));
        }

        [Test]
        public void PlanetGovernmentAndAttributesAreReadable()
        {
            PlayerState player = Player(out GameData data);
            player.Land(data.Planets["Earth"]);

            Assert.AreEqual(1, Read(player, "flagship planet government: Republic"));
            Assert.AreEqual(1, Read(player, "flagship planet attribute: urban"));
            Assert.AreEqual(0, Read(player, "flagship planet attribute: moon"));
        }

        [Test]
        public void SystemConditionsFollowTheFlagship()
        {
            PlayerState player = Player(out GameData data);

            Assert.AreEqual(1, Read(player, "flagship system: Sol"));
            Assert.AreEqual(1, Read(player, "flagship system government: Republic"));

            player.EnterSystem(data.Systems["Beta"]);
            Assert.AreEqual(0, Read(player, "flagship system: Sol"));
            Assert.AreEqual(1, Read(player, "flagship system: Beta"));
        }

        // --- Travel history -------------------------------------------------------

        [Test]
        public void VisitingIsRecordedForSystemsAndPlanets()
        {
            PlayerState player = Player(out GameData data);

            Assert.AreEqual(1, Read(player, "visited system: Sol"));
            Assert.AreEqual(0, Read(player, "visited system: Beta"));
            Assert.AreEqual(0, Read(player, "visited planet: Earth"));

            player.Land(data.Planets["Earth"]);
            Assert.AreEqual(1, Read(player, "visited planet: Earth"));

            // Departing does not un-visit.
            player.Depart();
            Assert.AreEqual(1, Read(player, "visited planet: Earth"));
        }

        [Test]
        public void HyperjumpsToSystemCountsLinksNotDistance()
        {
            PlayerState player = Player(out _);

            Assert.AreEqual(0, Read(player, "hyperjumps to system: Sol"), "already there");
            Assert.AreEqual(1, Read(player, "hyperjumps to system: Alpha Centauri"));
            Assert.AreEqual(2, Read(player, "hyperjumps to system: Beta"));
            Assert.AreEqual(0, Read(player, "hyperjumps to system: Nowhere"),
                "an unreachable system reads as an unset condition");
        }

        // --- Money, date, fleet ---------------------------------------------------

        [Test]
        public void CreditsAndNetWorthAreProvided()
        {
            PlayerState player = Player(out _);
            player.SetCredits(12000);

            Assert.AreEqual(12000, Read(player, "credits"));
            Assert.GreaterOrEqual(Read(player, "net worth"), 12000,
                "net worth includes the fleet as well as cash");
        }

        [Test]
        public void TheDateBreaksIntoItsComponents()
        {
            PlayerState player = Player(out _);
            player.SetDate(new DateTime(3013, 11, 16));

            Assert.AreEqual(16, Read(player, "day"));
            Assert.AreEqual(11, Read(player, "month"));
            Assert.AreEqual(3013, Read(player, "year"));
            Assert.AreEqual(0, Read(player, "days since start"));

            player.AdvanceDays(30);
            Assert.AreEqual(30, Read(player, "days since start"));
            Assert.AreEqual(12, Read(player, "month"));
        }

        [Test]
        public void FleetAndFlagshipCountsAreProvided()
        {
            PlayerState player = Player(out GameData data);

            Assert.AreEqual(1, Read(player, "total ships"));
            Assert.AreEqual(1, Read(player, "ship model: Shuttle"));
            Assert.AreEqual(1, Read(player, "flagship model: Shuttle"));
            Assert.AreEqual(0, Read(player, "flagship model: Freighter"));

            var second = new Ship(data.Ships["Freighter"]);
            second.BuildMounts();
            player.Fleet.Add(second);

            Assert.AreEqual(2, Read(player, "total ships"));
            Assert.AreEqual(1, Read(player, "ship model: Freighter"));
            Assert.AreEqual(0, Read(player, "flagship model: Freighter"),
                "the flagship did not change");
        }

        [Test]
        public void FlagshipAttributesAreReadableAsConditions()
        {
            PlayerState player = Player(out _);

            Assert.AreEqual(400, Read(player, "flagship attribute: hull"));
            Assert.AreEqual(300, Read(player, "flagship attribute: shields"));
            Assert.AreEqual(5, Read(player, "flagship bunks"));
            Assert.AreEqual(1, Read(player, "flagship required crew"));
            Assert.AreEqual(0, Read(player, "flagship attribute: nonsense"),
                "an unknown attribute reads as 0, not an error");
        }

        [Test]
        public void InstalledOutfitsAreCounted()
        {
            PlayerState player = Player(out GameData data);

            Assert.AreEqual(0, Read(player, "outfit (flagship installed): Blaster"));

            player.Flagship!.AddOutfit(data.Outfits["Blaster"]);

            Assert.AreEqual(1, Read(player, "outfit (flagship installed): Blaster"));
            Assert.AreEqual(1, Read(player, "outfit (installed): Blaster"));
        }

        // --- Store semantics ------------------------------------------------------

        [Test]
        public void ProvidedConditionsAreReadOnly()
        {
            // Writing a derived condition would be discarded on the next read anyway;
            // upstream treats these entries as read-only rather than letting content
            // believe it changed the player's money by setting a variable.
            PlayerState player = Player(out _);
            player.SetCredits(500);

            player.Conditions.Set("credits", 999999);

            Assert.AreEqual(500, Read(player, "credits"));
            Assert.IsTrue(player.Conditions.IsProvided("credits"));
            Assert.IsTrue(player.Conditions.IsProvided("flagship planet: Earth"));
            Assert.IsFalse(player.Conditions.IsProvided("some story flag"));
        }

        [Test]
        public void OrdinaryConditionsStillStoreNormally()
        {
            PlayerState player = Player(out _);

            player.Conditions.Set("chapter", 3);
            Assert.AreEqual(3, Read(player, "chapter"));
        }

        [Test]
        public void TheLongestMatchingPrefixWins()
        {
            // "outfit (flagship installed): X" must not be swallowed by a shorter
            // "outfit (installed): " style prefix.
            var conditions = new Conditions();
            conditions.ProvidePrefixed("outfit: ", _ => 1);
            conditions.ProvidePrefixed("outfit (installed): ", _ => 2);
            conditions.ProvidePrefixed("outfit (flagship installed): ", _ => 3);

            Assert.AreEqual(1, conditions.Get("outfit: Blaster"));
            Assert.AreEqual(2, conditions.Get("outfit (installed): Blaster"));
            Assert.AreEqual(3, conditions.Get("outfit (flagship installed): Blaster"));
        }

        // --- Conditions integrate with the gating layer ---------------------------

        [Test]
        public void MissionGatesCanTestAutoconditionsDirectly()
        {
            // The real payoff: content gates on these keys, so a ConditionSet has to
            // see them without anything having written them.
            PlayerState player = Player(out GameData data);
            player.SetCredits(20000);
            player.Land(data.Planets["Earth"]);

            DataNode gate = new DataFile(
                "to offer\n\t\"credits\" >= 10000\n\thas \"flagship landed\"\n" +
                "\thas \"flagship planet attribute: urban\"\n", "test.txt").Nodes[0];

            Assert.IsTrue(ConditionSet.Load(gate).Test(player.Conditions));

            player.Depart();
            Assert.IsFalse(ConditionSet.Load(gate).Test(player.Conditions),
                "leaving the ground should close the gate");
        }
    }
}
