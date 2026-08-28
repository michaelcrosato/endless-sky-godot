using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Planet services, shared stock lists, and ship valuation. Engine-free.
    /// </summary>
    [TestFixture]
    public class PlanetShopTests
    {
        private static DataNode Parse(string text) => new DataFile(text, "test.txt").Nodes[0];

        private static Planet LoadPlanet(string text)
        {
            DataNode node = Parse(text);
            var planet = new Planet(node.Token(1));
            planet.Load(node);
            return planet;
        }

        private static Sale LoadSale(string text)
        {
            DataNode node = Parse(text);
            var sale = new Sale(node.Token(1));
            sale.Load(node);
            return sale;
        }

        // --- Stock lists ----------------------------------------------------------

        [Test]
        public void AStockListCollectsItsEntries()
        {
            Sale sale = LoadSale("shipyard \"Basic Ships\"\n\t\"Shuttle\"\n\t\"Star Barge\"\n");

            Assert.AreEqual("Basic Ships", sale.Name);
            Assert.AreEqual(2, sale.Items.Count);
            Assert.IsTrue(sale.Contains("Shuttle"));
            Assert.IsFalse(sale.Contains("Falcon"));
        }

        [Test]
        public void RepeatedDeclarationsAccumulateSoContentCanExtendAList()
        {
            // Upstream lets one file add to a list another file defined; replacing
            // instead of accumulating would make plugins silently wipe stock.
            var sale = new Sale("Basic Ships");
            sale.Load(Parse("shipyard \"Basic Ships\"\n\t\"Shuttle\"\n"));
            sale.Load(Parse("shipyard \"Basic Ships\"\n\t\"Star Barge\"\n"));

            Assert.AreEqual(2, sale.Items.Count);
        }

        [Test]
        public void AListCanHaveEntriesRemoved()
        {
            var sale = new Sale("Basic Ships");
            sale.Load(Parse("shipyard \"Basic Ships\"\n\t\"Shuttle\"\n\t\"Star Barge\"\n"));
            sale.Load(Parse("shipyard \"Basic Ships\"\n\tremove \"Shuttle\"\n"));

            Assert.IsFalse(sale.Contains("Shuttle"));
            Assert.IsTrue(sale.Contains("Star Barge"));
        }

        // --- Planets --------------------------------------------------------------

        [Test]
        public void APlanetNamesTheStockListsItSellsFrom()
        {
            Planet planet = LoadPlanet(
                "planet \"New Boston\"\n" +
                "\tattributes \"dirt belt\" farming textiles\n" +
                "\tgovernment Republic\n" +
                "\tshipyard \"Basic Ships\"\n" +
                "\toutfitter \"Basic Outfits\"\n" +
                "\toutfitter \"Ammo South\"\n" +
                "\tspaceport `A shabby spaceport.`\n" +
                "\tsecurity 0.05\n" +
                "\ttribute 300\n");

            Assert.AreEqual("Republic", planet.Government);
            Assert.AreEqual(new[] { "Basic Ships" }, planet.Shipyards.ToArray());
            Assert.AreEqual(new[] { "Basic Outfits", "Ammo South" }, planet.Outfitters.ToArray());
            Assert.IsTrue(planet.HasSpaceport);
            Assert.AreEqual(0.05, planet.Security, 1e-9);
            Assert.AreEqual(300, planet.Tribute);
            Assert.IsTrue(planet.Attributes.Contains("dirt belt"));
            Assert.IsTrue(planet.Attributes.Contains("farming"));
        }

        [Test]
        public void LandabilityIsImpliedByServicesRatherThanFlagged()
        {
            Planet scenery = LoadPlanet("planet \"Barren Rock\"\n\tattributes uninhabited\n");
            Planet port = LoadPlanet("planet \"Port\"\n\tspaceport `Busy.`\n");
            Planet yardOnly = LoadPlanet("planet \"Yard\"\n\tshipyard \"Basic Ships\"\n");

            Assert.IsFalse(scenery.IsInhabited, "a world with no services is scenery");
            Assert.IsTrue(port.IsInhabited);
            Assert.IsTrue(yardOnly.IsInhabited, "a shipyard alone makes a world landable");
        }

        [Test]
        public void StockIsTheUnionOfTheListsAPlanetNames()
        {
            var catalogue = new Dictionary<string, Sale>
            {
                ["Basic Outfits"] = LoadSale("outfitter \"Basic Outfits\"\n\t\"Blaster\"\n\t\"Hyperdrive\"\n"),
                ["Ammo South"] = LoadSale("outfitter \"Ammo South\"\n\t\"Sidewinder Missile\"\n\t\"Blaster\"\n"),
            };

            Planet planet = LoadPlanet(
                "planet \"Port\"\n\toutfitter \"Basic Outfits\"\n\toutfitter \"Ammo South\"\n");

            var stock = Planet.Stock(planet.Outfitters, catalogue).ToList();

            Assert.AreEqual(3, stock.Count, "the shared entry appears once");
            CollectionAssert.AreEquivalent(
                new[] { "Blaster", "Hyperdrive", "Sidewinder Missile" }, stock);
        }

        [Test]
        public void UnknownStockListsAreSkippedRatherThanThrowing()
        {
            // Content routinely names lists defined in files that are not loaded.
            Planet planet = LoadPlanet("planet \"Port\"\n\toutfitter \"Nonexistent\"\n");

            Assert.IsEmpty(Planet.Stock(planet.Outfitters, new Dictionary<string, Sale>()).ToList());
        }

        // --- Ship valuation and cargo --------------------------------------------

        private static Ship MakeShip(double hullCost, double cargoSpace = 0.0)
        {
            var lines = new List<string>
            {
                "ship \"Test Hauler\"",
                "\tattributes",
                "\t\t\"cost\" " + hullCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "\t\t\"mass\" 100",
                "\t\t\"cargo space\" " + cargoSpace.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "\t\t\"outfit space\" 200",
                "\t\t\"thrust\" 20",
                "\t\t\"turn\" 400",
                "\t\t\"bunks\" 3",
                "\t\t\"required crew\" 1",
            };

            var definition = new ShipDefinition("Test Hauler");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return new Ship(definition);
        }

        [Test]
        public void ShipValueIsTheSummedCostAttribute()
        {
            // No bookkeeping: an outfit's price is its "cost" attribute, and
            // installing it adds that to the ship's total.
            Ship ship = MakeShip(190000.0);
            Assert.AreEqual(190000L, ship.Cost);
            Assert.AreEqual(190000L, ship.ChassisCost);
            Assert.AreEqual(0L, ship.OutfitCost);

            var outfit = new Outfit("Hyperdrive");
            outfit.Load(new DataFile("outfit \"Hyperdrive\"\n\t\"cost\" 45000\n\t\"outfit space\" -20\n",
                                     "test.txt").Nodes[0]);
            ship.AddOutfit(outfit);

            Assert.AreEqual(235000L, ship.Cost);
            Assert.AreEqual(190000L, ship.ChassisCost, "the hull's own price does not move");
            Assert.AreEqual(45000L, ship.OutfitCost);
        }

        [Test]
        public void CargoCapacityFollowsTheCargoSpaceAttribute()
        {
            Ship ship = MakeShip(100000.0, cargoSpace: 50.0);

            Assert.AreEqual(50, ship.Cargo.Capacity);
            Assert.AreEqual(30, ship.LoadCargo("Food", 30));
            Assert.AreEqual(20, ship.LoadCargo("Metal", 40), "only what fits");
            Assert.AreEqual(0, ship.Cargo.Free);
        }

        [Test]
        public void LoadedCargoAddsToShipMass()
        {
            // The coupling that makes a full freighter handle worse than an empty one.
            Ship ship = MakeShip(100000.0, cargoSpace: 100.0);
            double emptyMass = ship.Mass;

            ship.LoadCargo("Metal", 60);

            Assert.AreEqual(emptyMass + 60.0, ship.Mass, 1e-9);
            Assert.Less(ship.Acceleration, ship.Thrust / emptyMass,
                "a loaded ship accelerates more slowly");

            ship.UnloadCargo("Metal", 60);
            Assert.AreEqual(emptyMass, ship.Mass, 1e-9);
        }

        [Test]
        public void InstallingACargoExpanderResizesTheHold()
        {
            Ship ship = MakeShip(100000.0, cargoSpace: 20.0);
            Assert.AreEqual(20, ship.Cargo.Capacity);

            var expander = new Outfit("Cargo Expansion");
            expander.Load(new DataFile("outfit \"Cargo Expansion\"\n\t\"cargo space\" 15\n\t\"outfit space\" -10\n",
                                       "test.txt").Nodes[0]);
            ship.AddOutfit(expander);

            Assert.AreEqual(35, ship.Cargo.Capacity);
        }

        [Test]
        public void AShipWithoutThrustOrCrewBerthsIsNotFlyable()
        {
            Ship ok = MakeShip(100000.0);
            Assert.IsTrue(ok.IsFlyable);

            var lines = new List<string>
            {
                "ship \"Hulk\"",
                "\tattributes",
                "\t\t\"cost\" 1000",
                "\t\t\"mass\" 100",
                "\t\t\"turn\" 400",
                "\t\t\"bunks\" 1",
                "\t\t\"required crew\" 1",
            };
            var definition = new ShipDefinition("Hulk");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

            Assert.IsFalse(new Ship(definition).IsFlyable, "no thrust, no launch");
        }

        // --- Against real upstream content ---------------------------------------

        [Test]
        public void ARealUpstreamShipValuesItselfFromItsOutfits()
        {
            GameData data = UpstreamData.Instance;

            Ship shuttle = data.BuildShip("Shuttle");
            Assert.IsNotNull(shuttle);

            Assert.Greater(shuttle.Cost, 0L, "a stock ship has a price");
            Assert.Greater(shuttle.ChassisCost, 0L);
            Assert.Greater(shuttle.Cost, shuttle.ChassisCost,
                "a stock ship ships with outfits, so it is worth more than its hull");
            Assert.IsTrue(shuttle.IsFlyable, "the stock Shuttle should be launchable as built");
        }
    }
}
