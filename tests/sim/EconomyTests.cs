using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Trade goods, cargo holds and outfit installation. Engine-free.
    /// </summary>
    [TestFixture]
    public class EconomyTests
    {
        private static DataNode Parse(string text) => new DataFile(text, "test.txt").Nodes[0];

        // --- Commodities ----------------------------------------------------------

        [Test]
        public void ACommodityCarriesItsPriceBandAndFlavourItems()
        {
            var trade = new TradeData();
            trade.LoadTradeDefinition(Parse(
                "trade\n\tcommodity \"Food\" 100 600\n\t\t\"acorns\"\n\t\t\"alfalfa\"\n"));

            Commodity food = trade.Commodities["Food"];

            Assert.AreEqual(100, food.LowPrice);
            Assert.AreEqual(600, food.HighPrice);
            Assert.AreEqual(500, food.PriceSpread);
            Assert.IsTrue(food.IsTradeable);
            Assert.AreEqual(new[] { "acorns", "alfalfa" }, food.Items.ToArray());
        }

        [Test]
        public void CommoditiesWithoutAPriceBandAreNotTradeable()
        {
            // Garbage, Military and the illegal categories exist so missions can
            // reference them; they are never sold on the open market. A shop that
            // iterates all commodities must filter on this.
            var trade = new TradeData();
            trade.LoadTradeDefinition(Parse(
                "trade\n\tcommodity \"Food\" 100 600\n\tcommodity \"Garbage\"\n"));

            Assert.IsTrue(trade.Commodities["Food"].IsTradeable);
            Assert.IsFalse(trade.Commodities["Garbage"].IsTradeable);
            Assert.AreEqual(0, trade.Commodities["Garbage"].PriceSpread);
        }

        // --- Prices and runs ------------------------------------------------------

        [Test]
        public void SystemPricesAreReadFromTheSystemDefinition()
        {
            var trade = new TradeData();
            trade.LoadSystemPrices("1 Axis", Parse(
                "system \"1 Axis\"\n\tgovernment Coalition\n\ttrade Food 231\n\ttrade \"Heavy Metals\" 1241\n"));

            Assert.AreEqual(231, trade.Price("1 Axis", "Food"));
            Assert.AreEqual(1241, trade.Price("1 Axis", "Heavy Metals"));
            Assert.IsNull(trade.Price("1 Axis", "Clothing"), "not quoted here");
            Assert.IsNull(trade.Price("Nowhere", "Food"), "unknown system");
        }

        [Test]
        public void ProfitIsTheDifferenceBetweenTwoSystemsQuotes()
        {
            var trade = new TradeData();
            trade.SetPrice("Alpha", "Food", 200);
            trade.SetPrice("Beta", "Food", 550);

            Assert.AreEqual(350, trade.ProfitPerTon("Alpha", "Beta", "Food"));
            Assert.AreEqual(-350, trade.ProfitPerTon("Beta", "Alpha", "Food"),
                "the same run backwards loses exactly as much");
            Assert.IsNull(trade.ProfitPerTon("Alpha", "Beta", "Clothing"));
        }

        [Test]
        public void TheBestRunIsTheMostProfitableGoodBothEndsTrade()
        {
            var trade = new TradeData();
            trade.SetPrice("Alpha", "Food", 200);
            trade.SetPrice("Alpha", "Metal", 300);
            trade.SetPrice("Alpha", "Plastic", 400);
            trade.SetPrice("Beta", "Food", 250);        // +50
            trade.SetPrice("Beta", "Metal", 900);       // +600
            // Beta does not trade Plastic at all.

            Assert.AreEqual("Metal", trade.BestRun("Alpha", "Beta"));
        }

        [Test]
        public void ThereIsNoBestRunWhenNothingTurnsAProfit()
        {
            var trade = new TradeData();
            trade.SetPrice("Alpha", "Food", 500);
            trade.SetPrice("Beta", "Food", 200);

            Assert.IsNull(trade.BestRun("Alpha", "Beta"));
        }

        // --- Cargo ----------------------------------------------------------------

        [Test]
        public void ACargoHoldFillsUpToItsCapacity()
        {
            var hold = new CargoHold(50);

            Assert.AreEqual(30, hold.Add("Food", 30));
            Assert.AreEqual(20, hold.Free);
            // Only the remaining 20 tons fit.
            Assert.AreEqual(20, hold.Add("Metal", 100));
            Assert.AreEqual(0, hold.Free);
            Assert.AreEqual(0, hold.Add("Plastic", 5));
        }

        [Test]
        public void UnloadingReturnsWhatWasActuallyAboard()
        {
            var hold = new CargoHold(100);
            hold.Add("Food", 40);

            Assert.AreEqual(40, hold.Remove("Food", 999), "cannot unload more than is held");
            Assert.AreEqual(0, hold.Count("Food"));
            Assert.IsTrue(hold.IsEmpty);
        }

        [Test]
        public void EmptiedGoodsAreDroppedNotLeftAsZeroes()
        {
            var hold = new CargoHold(100);
            hold.Add("Food", 10);
            hold.Remove("Food", 10);

            Assert.IsEmpty(hold.Commodities, "callers should not see phantom goods");
        }

        [Test]
        public void ShrinkingAHoldReportsOverfullRatherThanDumpingCargo()
        {
            // An outfitter needs to be able to refuse a change that would strand
            // goods, so the hold must not silently jettison them.
            var hold = new CargoHold(100);
            hold.Add("Food", 80);

            hold.SetCapacity(50);

            Assert.IsTrue(hold.IsOverfull);
            Assert.AreEqual(80, hold.Count("Food"), "nothing was thrown overboard");
            Assert.AreEqual(0, hold.Free);
        }

        [Test]
        public void AHoldIsWorthWhatTheLocalMarketPays()
        {
            var trade = new TradeData();
            trade.SetPrice("Beta", "Food", 550);
            trade.SetPrice("Beta", "Metal", 400);

            var hold = new CargoHold(100);
            hold.Add("Food", 10);
            hold.Add("Metal", 5);
            hold.Add("Contraband", 20);          // Beta does not trade it

            Assert.AreEqual(10 * 550 + 5 * 400, hold.ValueAt(trade, "Beta"));
        }

        // --- Outfit installation --------------------------------------------------

        private static Ship MakeHull(params (string Key, double Value)[] attributes)
        {
            var lines = new List<string> { "ship \"Test Hull\"", "\tattributes" };
            foreach ((string key, double value) in attributes)
                lines.Add("\t\t\"" + key + "\" " + value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var definition = new ShipDefinition("Test Hull");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return new Ship(definition);
        }

        private static Outfit MakeOutfit(string name, params (string Key, double Value)[] attributes)
        {
            var lines = new List<string> { "outfit \"" + name + "\"" };
            foreach ((string key, double value) in attributes)
                lines.Add("\t\"" + key + "\" " + value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        [Test]
        public void CapacityIsJustAnAttributeGoingNegative()
        {
            // There is no bespoke "does a gun fit" rule upstream: the outfit carries
            // "outfit space" -30 and installation is legal while the total stays >= 0.
            Ship ship = MakeHull(("outfit space", 100.0));
            Outfit big = MakeOutfit("Big Thing", ("outfit space", -30.0));

            Assert.AreEqual(3, Outfitting.CanInstall(ship, big, 5), "only three fit in 100 space");
            Assert.IsTrue(Outfitting.Fits(ship, big));
        }

        [Test]
        public void InstallingConsumesCapacity()
        {
            Ship ship = MakeHull(("outfit space", 100.0));
            Outfit thing = MakeOutfit("Thing", ("outfit space", -40.0));

            Assert.AreEqual(2, Outfitting.Install(ship, thing, 2));
            Assert.AreEqual(20.0, ship.Attributes.Get("outfit space"), 1e-9);
            Assert.AreEqual(0, Outfitting.Install(ship, thing), "no room left");
        }

        [Test]
        public void TheTightestConstraintWins()
        {
            // Space for four, but only one free gun port.
            Ship ship = MakeHull(("outfit space", 100.0), ("gun ports", 1.0));
            Outfit gun = MakeOutfit("Gun", ("outfit space", -20.0), ("gun ports", -1.0));

            Assert.AreEqual(1, Outfitting.CanInstall(ship, gun, 4));
            Assert.AreEqual("gun ports", Outfitting.LimitingAttribute(
                MakeHull(("outfit space", 100.0), ("gun ports", 0.0)), gun));
        }

        [Test]
        public void AttributesUpstreamAllowsToGoNegativeDoNotBlockInstallation()
        {
            // "energy consumption" is in upstream's unbounded table, so an outfit that
            // drives it negative is still legal.
            Assert.IsNull(Outfitting.Minimum("energy consumption"));
            Assert.AreEqual(0.0, Outfitting.Minimum("outfit space"));

            Ship ship = MakeHull(("outfit space", 100.0));
            Outfit oddity = MakeOutfit("Oddity", ("outfit space", -10.0), ("energy consumption", -50.0));

            Assert.AreEqual(1, Outfitting.CanInstall(ship, oddity));
        }

        [Test]
        public void MultipliersHaveAFloorSoTheyCannotInvert()
        {
            // Upstream floors these at -1 so a stack of reducers cannot flip a ship's
            // acceleration negative or divide by zero.
            Assert.AreEqual(-1.0, Outfitting.Minimum("acceleration multiplier"));
            Assert.AreEqual(-0.99, Outfitting.Minimum("inertia reduction"));
        }

        [Test]
        public void ARealUpstreamShipRejectsAnOutfitThatIsTooLarge()
        {
            GameData data = UpstreamData.Instance;

            Ship shuttle = data.BuildShip("Shuttle");
            Assert.IsNotNull(shuttle);

            // Something far larger than a Shuttle's remaining outfit space.
            Outfit huge = MakeOutfit("Enormous Reactor", ("outfit space", -500.0));

            Assert.AreEqual(0, Outfitting.CanInstall(shuttle, huge));
            Assert.AreEqual("outfit space", Outfitting.LimitingAttribute(shuttle, huge));
        }

        [Test]
        public void TheUpstreamDatasetDefinesTheExpectedCommodities()
        {
            // Sanity that the real commodities file parses through the same path.
            string dataPath = UpstreamData.Path;
            Assert.IsNotNull(dataPath, "upstream data required");

            var trade = new TradeData();
            var file = DataFile.FromPath(System.IO.Path.Combine(dataPath, "commodities.txt"));
            foreach (DataNode node in file.Nodes)
            {
                if (node.Token(0) == "trade")
                    trade.LoadTradeDefinition(node);
            }

            Assert.Greater(trade.Commodities.Count, 15);
            Assert.IsTrue(trade.Commodities["Food"].IsTradeable);
            Assert.AreEqual(100, trade.Commodities["Food"].LowPrice);
            Assert.AreEqual(600, trade.Commodities["Food"].HighPrice);
            Assert.IsFalse(trade.Commodities["Garbage"].IsTradeable);
        }
    }
}
