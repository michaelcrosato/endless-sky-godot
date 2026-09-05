using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>GameData::StepEconomy and the market block in PlayerInfo saves.</summary>
    [TestFixture]
    public class EconomyIntegrationTests
    {
        private static GameData Market(bool reverse = false)
        {
            var data = new GameData();
            data.LoadText("trade\n\tcommodity Food 100 600\n" +
                "ship Hauler\n\tattributes\n\t\tmass 100\n\t\thull 1000\n\t\t\"cargo space\" 5000\n" +
                "planet Home\n\tspaceport Busy\n");
            string[] systems =
            {
                "system Alpha\n\tpos 0 0\n\tobject Home\n\tlink Beta\n\tlink Gamma\n\ttrade Food 300\n",
                "system Beta\n\tpos 100 0\n\tlink Alpha\n\ttrade Food 400\n",
                "system Gamma\n\tpos 0 100\n\tlink Alpha\n\ttrade Food 500\n",
            };
            foreach (string system in reverse ? systems.AsEnumerable().Reverse() : systems) data.LoadText(system);
            return data;
        }

        private static PlayerState Pilot(GameData data)
        {
            var player = new PlayerState(data);
            player.SetCredits(10_000_000);
            player.EnterSystem(data.Systems["Alpha"]);
            player.Land(data.Planets["Home"]);
            Ship ship = data.BuildShip("Hauler");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            return player;
        }

        private static void Seed(GameData data)
        {
            data.Trade.SetSupply("Alpha", "Food", 10000);
            data.Trade.SetSupply("Beta", "Food", 2000);
            data.Trade.SetSupply("Gamma", "Food", 4000);
        }

        [Test]
        public void SalesWaitUntilTheNextDayAndBuyingDoesNotCancelTheirSupply()
        {
            GameData data = Market();
            PlayerState player = Pilot(data);
            player.Flagship!.LoadCargo("Food", 1000);
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 1000, out int sold));
            Assert.AreEqual(1000, sold);
            Assert.AreEqual(0, data.Trade.Supply("Alpha", "Food"));
            Assert.AreEqual(300, data.Trade.Price("Alpha", "Food"));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 1000, out int bought));
            Assert.AreEqual(1000, bought);

            data.StepEconomy(() => 0);
            Assert.AreEqual(890, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(50, data.Trade.Supply("Beta", "Food"), 1e-9);
            Assert.AreEqual(50, data.Trade.Supply("Gamma", "Food"), 1e-9);
            Assert.Less(data.Trade.Price("Alpha", "Food"), 300);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExportsUsePreviousSupplyAndSplitAcrossTheSourceSystemsLinks(bool reverse)
        {
            GameData data = Market(reverse);
            Seed(data);
            data.StepEconomy(() => 0);
            // KEEP = .89 and EXPORT = .10. Alpha has two links; its neighbors have one.
            Assert.AreEqual(9500, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(2280, data.Trade.Supply("Beta", "Food"), 1e-9);
            Assert.AreEqual(4060, data.Trade.Supply("Gamma", "Food"), 1e-9);
            Assert.AreEqual(15840, data.Trade.PricedSystems.Sum(s => data.Trade.Supply(s, "Food")), 1e-9);
        }

        [Test]
        public void MarketLinksAreReadFromTheCurrentUniverse()
        {
            GameData data = Market();
            Seed(data);
            data.Systems["Alpha"].RemoveLink("Gamma");
            data.Systems["Gamma"].RemoveLink("Alpha");
            data.StepEconomy(() => 0);
            Assert.AreEqual(9100, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(2780, data.Trade.Supply("Beta", "Food"), 1e-9);
            Assert.AreEqual(3560, data.Trade.Supply("Gamma", "Food"), 1e-9);
        }

        [Test]
        public void AFreshProcessRestoresSupplyPricesAndPendingSales()
        {
            GameData original = Market();
            PlayerState player = Pilot(original);
            Seed(original);
            player.Flagship!.LoadCargo("Food", 700);
            Trading.SellCommodity(player, original, "Food", 700, out _);
            string saved = SaveGame.Write(player);

            GameData fresh = Market();
            PlayerState restored = SaveGame.Read(saved, fresh);
            Assert.AreEqual(original.Trade.Price("Alpha", "Food"), fresh.Trade.Price("Alpha", "Food"));
            Assert.AreEqual(10000, fresh.Trade.Supply("Alpha", "Food"));
            Assert.AreEqual(saved, SaveGame.Write(restored));
            original.StepEconomy(() => 0);
            fresh.StepEconomy(() => 0);
            foreach (string system in original.Trade.PricedSystems)
                Assert.AreEqual(original.Trade.Supply(system, "Food"), fresh.Trade.Supply(system, "Food"), 1e-9);
        }

        [Test]
        public void ReloadRewindsMarketsAndDoesNotApplyPendingSalesTwice()
        {
            GameData data = Market();
            PlayerState player = Pilot(data);
            player.Flagship!.LoadCargo("Food", 700);
            Trading.SellCommodity(player, data, "Food", 700, out _);
            string saved = SaveGame.Write(player);
            data.StepEconomy(() => 3);
            player = SaveGame.Read(saved, data);
            player = SaveGame.Read(saved, data);
            Assert.AreEqual(saved, SaveGame.Write(player));
            Assert.AreEqual(0, data.Trade.Supply("Alpha", "Food"));
            data.StepEconomy(() => 0);
            Assert.AreEqual(623, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(35, data.Trade.Supply("Beta", "Food"), 1e-9);
            data.StepEconomy(() => 0);
            Assert.AreEqual(561.47, data.Trade.Supply("Alpha", "Food"), 1e-9);
        }

        [Test]
        public void OlderSavesWithoutEconomyDoNotInheritTheAbandonedSessionsMarket()
        {
            GameData data = Market();
            PlayerState player = Pilot(data);
            Seed(data);
            player.Flagship!.LoadCargo("Food", 700);
            Trading.SellCommodity(player, data, "Food", 700, out _);
            SaveGame.Read("system Alpha\n", data);
            Assert.AreEqual(0, data.Trade.Supply("Alpha", "Food"));
            Assert.AreEqual(300, data.Trade.Price("Alpha", "Food"));
            data.StepEconomy(() => 0);
            Assert.AreEqual(0, data.Trade.Supply("Alpha", "Food"));
        }

        [Test]
        public void UpstreamEconomyHeadersAndPurchasesAreUnderstood()
        {
            GameData data = Market();
            SaveGame.Read("system Alpha\neconomy\n\tpurchases\n\t\tAlpha Food -100\n" +
                "\tsystem Food Removed\n\tAlpha 1234.5 9000\n\tBeta -200.25 0\n\tUnknown 800 0\n", data);
            Assert.AreEqual(1234.5, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(-200.25, data.Trade.Supply("Beta", "Food"), 1e-9);
            Assert.IsNull(data.Trade.Price("Unknown", "Food"));
            Assert.IsNull(data.Trade.Price("Alpha", "Removed"));
            data.StepEconomy(() => 0);
            Assert.AreEqual(1167.68, data.Trade.Supply("Alpha", "Food"), 1e-9);
        }

        [Test]
        public void StagedLoadsLeaveTheActiveMarketAloneUntilAccepted()
        {
            GameData savedData = Market();
            PlayerState savedPlayer = Pilot(savedData);
            savedData.Trade.SetSupply("Alpha", "Food", 123);
            string saved = SaveGame.Write(savedPlayer);
            GameData active = Market();
            Seed(active);

            PlayerState invalid = SaveGame.Read("system Alpha\n", active, null, out _);
            Assert.IsNull(invalid.Flagship);
            Assert.AreEqual(10000, active.Trade.Supply("Alpha", "Food"));
            PlayerState candidate = SaveGame.Read(saved, active, null, out Action activate);
            Assert.AreEqual(10000, active.Trade.Supply("Alpha", "Food"));
            activate();
            Assert.AreEqual(123, active.Trade.Supply("Alpha", "Food"));
            Assert.AreEqual(saved, SaveGame.Write(candidate));
        }

        [Test]
        public void SavingNearPriceBoundariesRetainsTheDisplayedQuote()
        {
            GameData data = Market();
            PlayerState player = Pilot(data);
            for (int discount = 1; discount < 99; ++discount)
            {
                double low = 0, high = 100000;
                for (int step = 0; step < 55; ++step)
                {
                    double middle = (low + high) / 2;
                    if (TradeData.PriceFor(300, middle) <= 300 - discount) high = middle;
                    else low = middle;
                }
                data.Trade.SetSupply("Alpha", "Food", high + 1e-8);
                int? quote = data.Trade.Price("Alpha", "Food");
                string saved = SaveGame.Write(player);
                GameData fresh = Market();
                PlayerState restored = SaveGame.Read(saved, fresh);
                Assert.AreEqual(quote, fresh.Trade.Price("Alpha", "Food"), $"discount {discount}");
                Assert.AreEqual(saved, SaveGame.Write(restored));
            }
        }

        [Test]
        public void AnExplicitQuoteOverrideAlsoSurvivesReload()
        {
            GameData data = Market();
            PlayerState player = Pilot(data);
            data.Trade.SetPrice("Alpha", "Food", 321);
            string saved = SaveGame.Write(player);
            GameData fresh = Market();
            PlayerState restored = SaveGame.Read(saved, fresh);
            Assert.AreEqual(321, fresh.Trade.Price("Alpha", "Food"));
            Assert.AreEqual(saved, SaveGame.Write(restored));
        }

        [TestCase("-9007199254740993")]
        [TestCase("-9223372036854775807")]
        public void PendingSaleQuantitiesKeepAllTheirIntegerBits(string tons)
        {
            GameData data = Market();
            PlayerState player = SaveGame.Read($"economy\n\tpurchases\n\t\tAlpha Food {tons}\n", data);
            string saved = SaveGame.Write(player);
            StringAssert.Contains($"Alpha Food {tons}", saved);
            Assert.AreEqual(saved, SaveGame.Write(SaveGame.Read(saved, Market())));
        }

        [Test]
        public void InvalidSavedNumbersCannotReplaceValidMarketPrices()
        {
            GameData data = Market();
            SaveGame.Read("economy\n\tsystem Food\n\tAlpha 1e999\n" +
                "\t\tprice Food 999999999999999999999999999999\n\tBeta nope\n", data);
            Assert.AreEqual(0, data.Trade.Supply("Alpha", "Food"));
            Assert.AreEqual(300, data.Trade.Price("Alpha", "Food"));
            Assert.AreEqual(400, data.Trade.Price("Beta", "Food"));
        }

        [Test]
        public void CancellingLegacyPurchaseRecordsSerializeStably()
        {
            GameData data = Market();
            PlayerState player = SaveGame.Read("economy\n\tpurchases\n\t\tAlpha Food 20\n\t\tAlpha Food -20\n", data);
            string saved = SaveGame.Write(player);
            Assert.AreEqual(saved, SaveGame.Write(SaveGame.Read(saved, Market())));
        }

        private sealed class SequenceRandom : Random
        {
            private readonly Queue<double> _values;
            public SequenceRandom(params double[] values) => _values = new Queue<double>(values);
            public override double NextDouble() => _values.Count > 0 ? _values.Dequeue() : 0;
        }

        [Test]
        public void SessionRandomnessIsConvertedToNormalProductionShocks()
        {
            GameData data = Market();
            // Box-Muller produces +2 standard deviations from this pair of uniforms.
            data.StepEconomy(new SequenceRandom(1 - Math.Exp(-2), 0));
            Assert.AreEqual(4000, data.Trade.Supply("Alpha", "Food"), 1e-9);
            Assert.AreEqual(0, data.Trade.Supply("Beta", "Food"), 1e-9);
            Assert.AreEqual(0, data.Trade.Supply("Gamma", "Food"), 1e-9);
        }
    }
}
