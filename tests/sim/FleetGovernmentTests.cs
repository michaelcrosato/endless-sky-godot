using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Fleet parsing, and the ship-to-government index built from fleets and
    /// shipyards. Port checks against upstream <c>Fleet::Load</c>.
    /// </summary>
    [TestFixture]
    public class FleetGovernmentTests
    {
        private static GameData Load(string text)
        {
            var data = new GameData();
            data.LoadText(text);
            return data;
        }

        // --- Parsing --------------------------------------------------------------

        [Test]
        public void AFleetKeepsItsGovernmentAndWeightedVariants()
        {
            GameData data = Load(
                "fleet \"Traders\"\n\tgovernment \"Merchant\"\n\tnames \"civilian\"\n" +
                "\tvariant 60\n\t\t\"Shuttle\"\n" +
                "\tvariant 30\n\t\t\"Star Barge\"\n");

            Fleet fleet = data.Fleets["Traders"];
            Assert.AreEqual("Merchant", fleet.Government);
            Assert.AreEqual("civilian", fleet.Names);
            Assert.AreEqual(2, fleet.Variants.Count);
            Assert.AreEqual(60, fleet.Variants[0].Weight);
            Assert.AreEqual(30, fleet.Variants[1].Weight);
        }

        [Test]
        public void AVariantWithNoWeightDefaultsToOne()
        {
            GameData data = Load("fleet \"F\"\n\tgovernment \"G\"\n\tvariant\n\t\t\"Shuttle\"\n");

            Assert.AreEqual(1, data.Fleets["F"].Variants[0].Weight);
        }

        [Test]
        public void ACountAfterAShipNameRepeatsThatHull()
        {
            // `"Star Barge" 2` is two hulls, not one hull with a property.
            GameData data = Load(
                "fleet \"F\"\n\tgovernment \"G\"\n" +
                "\tvariant 10\n\t\t\"Star Barge\" 2\n\t\t\"Sparrow\"\n");

            var ships = data.Fleets["F"].Variants[0].Ships;
            Assert.AreEqual(3, ships.Count);
            Assert.AreEqual(2, ships.Count(s => s == "Star Barge"));
            Assert.AreEqual(1, ships.Count(s => s == "Sparrow"));
        }

        [Test]
        public void ASecondDefinitionReplacesTheVariantsRatherThanAppending()
        {
            // Upstream's resetVariants: reloading a fleet replaces its composition, so
            // a plugin redefining a fleet does not double its size.
            GameData data = Load(
                "fleet \"F\"\n\tgovernment \"G\"\n\tvariant 10\n\t\t\"Shuttle\"\n" +
                "fleet \"F\"\n\tvariant 5\n\t\t\"Sparrow\"\n");

            Fleet fleet = data.Fleets["F"];
            Assert.AreEqual(1, fleet.Variants.Count);
            Assert.AreEqual("Sparrow", fleet.Variants[0].Ships.Single());
            Assert.AreEqual("G", fleet.Government, "the government should survive the reload");
        }

        [Test]
        public void AddedVariantsDoNotTriggerTheReplacement()
        {
            GameData data = Load(
                "fleet \"F\"\n\tgovernment \"G\"\n\tvariant 10\n\t\t\"Shuttle\"\n" +
                "fleet \"F\"\n\tadd variant 5\n\t\t\"Sparrow\"\n");

            Fleet fleet = data.Fleets["F"];
            Assert.AreEqual(2, fleet.Variants.Count);
            Assert.AreEqual(5, fleet.Variants[1].Weight, "the weight follows \"add variant\"");
        }

        [Test]
        public void RemoveVariantDropsAMatchingComposition()
        {
            GameData data = Load(
                "fleet \"F\"\n\tgovernment \"G\"\n" +
                "\tvariant 10\n\t\t\"Shuttle\"\n" +
                "\tvariant 10\n\t\t\"Sparrow\"\n" +
                "fleet \"F\"\n\tremove variant\n\t\t\"Shuttle\"\n");

            Fleet fleet = data.Fleets["F"];
            Assert.AreEqual(1, fleet.Variants.Count);
            Assert.AreEqual("Sparrow", fleet.Variants[0].Ships.Single());
        }

        // --- The index ------------------------------------------------------------

        [Test]
        public void AShipTakesTheGovernmentOfTheFleetThatFliesIt()
        {
            GameData data = Load(
                "ship \"Shuttle\"\n\tattributes\n\t\t\"mass\" 80\n" +
                "fleet \"Traders\"\n\tgovernment \"Merchant\"\n\tvariant 10\n\t\t\"Shuttle\"\n");

            Assert.AreEqual("Merchant", data.GovernmentOf("Shuttle"));
        }

        [Test]
        public void FleetsOutrankShipyards()
        {
            // Independent worlds stock other factions' hulls, so a shipyard-first index
            // would label most of the galaxy Independent.
            GameData data = Load(
                "ship \"Shuttle\"\n\tattributes\n\t\t\"mass\" 80\n" +
                "fleet \"Navy\"\n\tgovernment \"Republic\"\n\tvariant 10\n\t\t\"Shuttle\"\n" +
                "shipyard \"Basics\"\n\t\"Shuttle\"\n" +
                "planet \"Freeport\"\n\tgovernment \"Independent\"\n\tshipyard \"Basics\"\n");

            Assert.AreEqual("Republic", data.GovernmentOf("Shuttle"));
        }

        [Test]
        public void AShipNobodyFliesFallsBackToTheShipyardThatSellsIt()
        {
            GameData data = Load(
                "ship \"Rare Hull\"\n\tattributes\n\t\t\"mass\" 80\n" +
                "shipyard \"Boutique\"\n\t\"Rare Hull\"\n" +
                "planet \"Homeworld\"\n\tgovernment \"Syndicate\"\n\tshipyard \"Boutique\"\n");

            Assert.AreEqual("Syndicate", data.GovernmentOf("Rare Hull"));
        }

        [Test]
        public void VariantsInheritTheirBaseHullsGovernment()
        {
            // Most variants are never named by a fleet directly.
            GameData data = Load(
                "ship \"Star Barge\"\n\tattributes\n\t\t\"mass\" 200\n" +
                "ship \"Star Barge\" \"Star Barge (Armed)\"\n\tattributes\n\t\t\"mass\" 210\n" +
                "fleet \"Traders\"\n\tgovernment \"Merchant\"\n\tvariant 10\n\t\t\"Star Barge\"\n");

            Assert.AreEqual("Merchant", data.GovernmentOf("Star Barge (Armed)"));
        }

        [Test]
        public void AnUnknownShipHasNoGovernment()
        {
            Assert.IsNull(Load("ship \"Orphan\"\n\tattributes\n\t\t\"mass\" 80\n")
                .GovernmentOf("Orphan"));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetResolvesAGovernmentForMostShips()
        {
            GameData data = UpstreamData.Instance;

            Assert.Greater(data.Fleets.Count, 100, "upstream defines a lot of fleets");

            var ships = data.Ships.Values.ToList();
            var resolved = ships.Where(s => data.GovernmentOf(s.DisplayName) != null).ToList();
            double coverage = (double)resolved.Count / ships.Count;

            var byGovernment = resolved
                .GroupBy(s => data.GovernmentOf(s.DisplayName)!)
                .OrderByDescending(g => g.Count())
                .ToList();

            TestContext.WriteLine(
                $"{resolved.Count} of {ships.Count} ships resolved ({coverage:P0}) across " +
                $"{byGovernment.Count} governments");
            TestContext.WriteLine("top: " + string.Join(", ",
                byGovernment.Take(8).Select(g => $"{g.Key} {g.Count()}")));

            Assert.Greater(coverage, 0.5, "fleets and shipyards should cover most of the fleet");
            Assert.Greater(byGovernment.Count, 5, "several factions should be represented");
        }

        [Test]
        public void KnownHullsLandInTheirExpectedFaction()
        {
            // Spot checks a reader can verify against the data by eye. The Star Barge
            // is the case that broke first-claimant scoring: 181 weight of Merchant
            // fleets fly it against 2 of "Hai Merchant (Human)", so the majority rule
            // has to pick Merchant.
            GameData data = UpstreamData.Instance;

            foreach ((string ship, string government) in new[]
            {
                ("Star Barge", "Merchant"),
                ("Shuttle", "Merchant"),
                ("Bactrian", "Merchant"),
                ("Falcon", "Free Worlds"),
            })
            {
                if (!data.Ships.ContainsKey(ship))
                    continue;

                TestContext.WriteLine($"{ship} -> {data.GovernmentOf(ship) ?? "(none)"}");
                Assert.AreEqual(government, data.GovernmentOf(ship), ship);
            }
        }

        [Test]
        public void AHullNobodyFliesOrStocksYetHasNoFaction()
        {
            // The Kestrel is deliberately unavailable at the start of the game: no
            // fleet flies it, and its shipyard is defined EMPTY, stocked only by events
            // that fire later. Null is the correct answer, and inventing a faction for
            // it would paint a ship the player cannot yet see in someone's colours.
            GameData data = UpstreamData.Instance;

            Assert.IsTrue(data.Ships.ContainsKey("Kestrel"), "the pinned dataset defines Kestrel");

            Assert.IsNull(data.GovernmentOf("Kestrel"));
        }

        [Test]
        public void TheIndexIsStableAcrossReloads()
        {
            // Ties are common - the Leviathan is flown at equal weight by Merchant and
            // Pirate fleets - so the tie-break must not depend on dictionary or file
            // iteration order, or a hull would change colour between runs.
            GameData first = UpstreamData.Instance;

            var second = new GameData();
            second.LoadDirectory(UpstreamData.RequiredPath);

            foreach (ShipDefinition ship in first.Ships.Values)
            {
                Assert.AreEqual(first.GovernmentOf(ship.DisplayName),
                                second.GovernmentOf(ship.DisplayName), ship.DisplayName);
            }
        }
    }
}
