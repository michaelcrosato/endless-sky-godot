using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The moving economy: supply drifts and prices follow it. Port checks against
    /// upstream <c>System::StepEconomy</c> and <c>System::Price::Update</c>.
    /// </summary>
    [TestFixture]
    public class EconomyStepTests
    {
        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(
                "trade\n\tcommodity \"Food\" 100 600\n\t\t\"grain\"\n" +
                "system \"Alpha\"\n\tpos 0 0\n\ttrade Food 300\n" +
                "system \"Beta\"\n\tpos 100 0\n\ttrade Food 500\n");
            return data;
        }

        // --- The price curve ------------------------------------------------------

        [Test]
        public void NoSupplyMeansTheBasePrice()
        {
            Assert.AreEqual(300, TradeData.PriceFor(300, 0.0));
        }

        [Test]
        public void PlentyPushesThePriceDownAndScarcityPushesItUp()
        {
            // Supply and price move opposite ways, which is the whole point of trade.
            Assert.Less(TradeData.PriceFor(300, 20000.0), 300);
            Assert.Greater(TradeData.PriceFor(300, -20000.0), 300);
        }

        [Test]
        public void ThePriceSwingIsBounded()
        {
            // At most 100 credits either side of base, however extreme supply gets, so
            // a route stays broadly worth running instead of collapsing or exploding.
            foreach (double supply in new[] { 1e5, 1e6, -1e5, -1e6 })
            {
                int price = TradeData.PriceFor(300, supply);
                Assert.GreaterOrEqual(price, 200, $"supply {supply}");
                Assert.LessOrEqual(price, 400, $"supply {supply}");
            }
        }

        [Test]
        public void ThePriceCurveIsMonotonic()
        {
            int previous = int.MaxValue;
            for (double supply = -40000.0; supply <= 40000.0; supply += 2000.0)
            {
                int price = TradeData.PriceFor(300, supply);
                Assert.LessOrEqual(price, previous, $"at supply {supply}");
                previous = price;
            }
        }

        // --- Stepping -------------------------------------------------------------

        [Test]
        public void TheBasePriceIsKeptSeparateFromTheMovingPrice()
        {
            GameData data = Load();

            Assert.AreEqual(300, data.Trade.BasePrice("Alpha", "Food"));
            Assert.AreEqual(300, data.Trade.Price("Alpha", "Food"), "starts at base");

            data.Trade.StepEconomy(() => 1.0);

            Assert.AreEqual(300, data.Trade.BasePrice("Alpha", "Food"), "the base never moves");
            Assert.AreNotEqual(300, data.Trade.Price("Alpha", "Food"), "the price does");
        }

        [Test]
        public void AGoodDayForSupplyLowersThePrice()
        {
            GameData data = Load();

            data.Trade.StepEconomy(() => 1.0);   // a positive supply shock

            Assert.Greater(data.Trade.Supply("Alpha", "Food"), 0.0);
            Assert.Less(data.Trade.Price("Alpha", "Food")!.Value, 300);
        }

        [Test]
        public void AShortageRaisesIt()
        {
            GameData data = Load();

            data.Trade.StepEconomy(() => -1.0);

            Assert.Less(data.Trade.Supply("Alpha", "Food"), 0.0);
            Assert.Greater(data.Trade.Price("Alpha", "Food")!.Value, 300);
        }

        [Test]
        public void SupplyDecaysTowardZeroWhenNothingDisturbsIt()
        {
            // Upstream keeps 89% of yesterday's supply, so a shock fades rather than
            // marking a world permanently.
            GameData data = Load();
            data.Trade.StepEconomy(() => 5.0);
            double shocked = data.Trade.Supply("Alpha", "Food");

            for (int day = 0; day < 40; day++)
                data.Trade.StepEconomy(() => 0.0);

            double settled = data.Trade.Supply("Alpha", "Food");
            Assert.Less(Math.Abs(settled), Math.Abs(shocked) * 0.05,
                "the shock should have faded away");
        }

        [Test]
        public void EverySystemMovesIndependently()
        {
            GameData data = Load();

            data.Trade.StepEconomy(() => 1.0);

            // Both moved, but from different bases, so they stay different.
            Assert.AreNotEqual(data.Trade.Price("Alpha", "Food"), data.Trade.Price("Beta", "Food"));
            Assert.AreEqual(200, data.Trade.BasePrice("Beta", "Food")!.Value -
                                 data.Trade.BasePrice("Alpha", "Food")!.Value);
        }

        [Test]
        public void PricesStayWithinBoundsOverALongWalk()
        {
            // A random walk that drifts unboundedly would eventually make a commodity
            // free or priceless.
            var random = new Random(12345);
            GameData data = Load();

            int lowest = int.MaxValue, highest = int.MinValue;
            for (int day = 0; day < 5000; day++)
            {
                data.Trade.StepEconomy(() =>
                {
                    double u1 = 1.0 - random.NextDouble();
                    double u2 = random.NextDouble();
                    return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                });

                int price = data.Trade.Price("Alpha", "Food")!.Value;
                lowest = Math.Min(lowest, price);
                highest = Math.Max(highest, price);
            }

            TestContext.WriteLine($"over 5000 days Food at Alpha ranged {lowest}..{highest} (base 300)");

            Assert.GreaterOrEqual(lowest, 200);
            Assert.LessOrEqual(highest, 400);
            Assert.Less(lowest, highest, "and it did actually move");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealEconomyMovesWithoutBreakingAnyPrice()
        {
            GameData data = UpstreamData.Instance;

            string commodity = data.Trade.Commodities.Keys
                .First(c => data.Trade.PricedSystems.Count(s => data.Trade.Price(s, c).HasValue) > 10);

            var before = data.Trade.PricedSystems
                .ToDictionary(s => s, s => data.Trade.Price(s, commodity));

            var random = new Random(7);
            for (int day = 0; day < 100; day++)
                data.Trade.StepEconomy(() =>
                {
                    double u1 = 1.0 - random.NextDouble();
                    double u2 = random.NextDouble();
                    return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                });

            int moved = 0;
            foreach (string system in data.Trade.PricedSystems)
            {
                int? now = data.Trade.Price(system, commodity);
                if (!now.HasValue)
                    continue;

                Assert.Greater(now.Value, 0, $"{commodity} at {system} must stay worth something");

                if (before.TryGetValue(system, out int? was) && was != now)
                    moved++;
            }

            TestContext.WriteLine($"after 100 days, {commodity} moved in {moved} systems");
            Assert.Greater(moved, 0, "the economy should actually be alive");
        }
    }
}
