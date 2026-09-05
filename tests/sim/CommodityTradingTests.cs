using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>TradingPanel::Buy, including partial fills and unavailable markets.</summary>
    [TestFixture]
    public class CommodityTradingTests
    {
        private static PlayerState Landed(out GameData data, long credits = 10000, int capacity = 10)
        {
            data = new GameData();
            data.LoadText("trade\n\tcommodity Food 100 600\n\tcommodity Metal 300 1300\n" +
                "\tcommodity Garbage\n\tcommodity Missing 100 600\n" +
                "\tcommodity Free 100 600\n\tcommodity Reverse 100 600\n" +
                "ship Freighter\n\tattributes\n\t\tcost 1000\n\t\tmass 80\n\t\thull 500\n" +
                $"\t\t\"cargo space\" {capacity}\n" +
                "shipyard Yard\n\tFreighter\n" +
                "planet Home\n\tspaceport Busy\n\tshipyard Yard\nplanet Barren\n" +
                "system Sol\n\tpos 0 0\n\tobject Home\n\tobject Barren\n" +
                "\ttrade Food 100\n\ttrade Metal 200\n\ttrade Garbage 400\n" +
                "\ttrade Free 0\n\ttrade Reverse -10\n" +
                "system Elsewhere\n\tpos 100 0\n\ttrade Food 800\n");
            var player = new PlayerState(data);
            player.SetCredits(credits);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);
            AddShip(player, "Sol");
            return player;
        }

        private static Ship AddShip(PlayerState player, string system)
        {
            Ship ship = player.Data!.BuildShip("Freighter");
            ship.CurrentSystem = player.Data.Systems[system];
            player.Fleet.Add(ship);
            return ship;
        }

        [TestCase(250L, 5, 2, 50L)]
        [TestCase(2000L, 15, 10, 1000L)]
        [TestCase(long.MaxValue, 3, 3, long.MaxValue - 300)]
        public void PurchasesChargeOnlyForWhatFitsAndCanBePaidFor(long credits, int requested,
                                                                  int expectedTons, long remaining)
        {
            PlayerState player = Landed(out GameData data, credits);
            Assert.AreEqual(TradeResult.Ok,
                Trading.BuyCommodity(player, data, "Food", requested, out int bought));
            Assert.AreEqual(expectedTons, bought);
            Assert.AreEqual(expectedTons, player.Flagship!.Cargo.Count("Food"));
            Assert.AreEqual(expectedTons, player.Flagship.CargoMass);
            Assert.AreEqual(remaining, player.Credits);
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(-429_496_729_500L)]
        [TestCase(long.MinValue)]
        public void AZeroOrNegativeBalanceCannotBuyCargo(long credits)
        {
            PlayerState player = Landed(out GameData data, credits);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.CannotAfford,
                Trading.BuyCommodity(player, data, "Food", 5, out int bought));
            Assert.AreEqual(0, bought);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [TestCase("Unknown", "Home", TradeResult.NoSuchThing)]
        [TestCase("Garbage", "Home", TradeResult.NotSold)]
        [TestCase("Missing", "Home", TradeResult.NotSold)]
        [TestCase("Free", "Home", TradeResult.NotSold)]
        [TestCase("Reverse", "Home", TradeResult.NotSold)]
        [TestCase("Food", "Barren", TradeResult.NotSold)]
        [TestCase("Food", "flight", TradeResult.NotSold)]
        public void UnavailableGoodsAndPortsCannotChangeMoneyOrCargo(string commodity, string where,
                                                                     TradeResult expected)
        {
            PlayerState player = Landed(out GameData data);
            player.Flagship!.LoadCargo(commodity, 7);
            if (where == "flight") player.Depart();
            else player.Land(data.Planets[where]);
            string before = SaveGame.Write(player);

            Assert.AreEqual(expected, Trading.BuyCommodity(player, data, commodity, 5, out int bought));
            Assert.AreEqual(expected, Trading.SellCommodity(player, data, commodity, 5, out int sold));
            Assert.AreEqual(0, bought);
            Assert.AreEqual(0, sold);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void NonpositiveRequestsDoNotTurnIntoTheOppositeTransaction(int requested)
        {
            PlayerState player = Landed(out GameData data);
            player.Flagship!.LoadCargo("Food", 7);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.InvalidAmount,
                Trading.BuyCommodity(player, data, "Food", requested, out int bought));
            Assert.AreEqual(TradeResult.InvalidAmount,
                Trading.SellCommodity(player, data, "Food", requested, out int sold));
            Assert.AreEqual(0, bought);
            Assert.AreEqual(0, sold);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void TransactionsUseTheCurrentSystemQuote()
        {
            PlayerState player = Landed(out GameData data, credits: 1000);
            TradeQuote oldQuote = Trading.CommoditiesFor(data, player).First(q => q.Commodity == "Food");
            data.Trade.SetPrice("Sol", "Food", 200);
            Assert.AreEqual(100, oldQuote.Price);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 3, out int bought));
            Assert.AreEqual(3, bought);
            Assert.AreEqual(400, player.Credits);

            data.Trade.SetPrice("Sol", "Food", 150);
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 2, out int sold));
            Assert.AreEqual(2, sold);
            Assert.AreEqual(700, player.Credits);
            Assert.AreEqual(1, player.Flagship!.Cargo.Count("Food"));
            Assert.AreEqual(1, player.Flagship.CargoMass);
        }

        [Test]
        public void TradingUsesLocalHoldsAndLeavesRemoteParkedAndDestroyedCargoAlone()
        {
            PlayerState player = Landed(out GameData data);
            player.Flagship!.LoadCargo("Food", 8);
            Ship escort = AddShip(player, "Sol");
            Ship remote = AddShip(player, "Elsewhere");
            Ship parked = AddShip(player, "Sol");
            Ship wreck = AddShip(player, "Sol");
            foreach (Ship ship in new[] { remote, parked, wreck }) ship.LoadCargo("Food", 9);
            parked.IsParked = true;
            wreck.SetLevels(hull: -1);

            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 50, out int bought));
            Assert.AreEqual(12, bought);
            Assert.AreEqual(10, player.Flagship.Cargo.Count("Food"));
            Assert.AreEqual(10, escort.Cargo.Count("Food"));
            Assert.AreEqual(20, player.Fleet.CargoCapacity(player.CurrentSystem));
            Assert.AreEqual(20, player.Fleet.CargoUsed(player.CurrentSystem));
            Assert.AreEqual(0, player.Fleet.CargoFree(player.CurrentSystem));
            Assert.AreEqual(8800, player.Credits);

            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 50, out int sold));
            Assert.AreEqual(20, sold);
            Assert.AreEqual(10800, player.Credits);
            Assert.AreEqual(0, player.Fleet.CargoCount("Food", player.CurrentSystem));
            foreach (Ship ship in new[] { remote, parked, wreck }) Assert.AreEqual(9, ship.Cargo.Count("Food"));
        }

        [Test]
        public void FullHoldsAndMissingCargoRefuseWithoutChangingCredits()
        {
            PlayerState player = Landed(out GameData data);
            player.Flagship!.LoadCargo("Metal", 10);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.DoesNotFit,
                Trading.BuyCommodity(player, data, "Food", 5, out int bought));
            Assert.AreEqual(TradeResult.NotOwned,
                Trading.SellCommodity(player, data, "Food", 5, out int sold));
            Assert.AreEqual(0, bought);
            Assert.AreEqual(0, sold);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void CargoPaymentsUse64BitMultiplication()
        {
            PlayerState player = Landed(out GameData data, credits: 20_000_000_000L, capacity: 5000);
            data.Trade.SetPrice("Sol", "Food", 1_000_000);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 4000, out int bought));
            Assert.AreEqual(4000, bought);
            Assert.AreEqual(16_000_000_000L, player.Credits);
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 4000, out int sold));
            Assert.AreEqual(4000, sold);
            Assert.AreEqual(20_000_000_000L, player.Credits);
        }

        [Test]
        public void CreditOverflowLeavesUnpaidCargoAboard()
        {
            PlayerState player = Landed(out GameData data, credits: long.MaxValue - 250);
            player.Flagship!.LoadCargo("Food", 5);
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 5, out int sold));
            Assert.AreEqual(2, sold);
            Assert.AreEqual(long.MaxValue - 50, player.Credits);
            Assert.AreEqual(3, player.Flagship.Cargo.Count("Food"));
            Assert.AreEqual(TradeResult.CreditLimit,
                Trading.SellCommodity(player, data, "Food", 5, out sold));
            Assert.AreEqual(0, sold);
            Assert.AreEqual(3, player.Flagship.Cargo.Count("Food"));

            player.SetCredits(long.MinValue);
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 5, out sold));
            Assert.AreEqual(3, sold);
            Assert.AreEqual(long.MinValue + 300, player.Credits);
        }

        [Test]
        public void ANewlyBoughtShipHasLocalCargoSpaceBeforeTakeoff()
        {
            PlayerState player = Landed(out GameData data);
            player.Flagship!.LoadCargo("Food", 10);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Freighter", out Ship? bought));
            Assert.AreSame(player.CurrentSystem, bought!.CurrentSystem);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 5, out int tons));
            Assert.AreEqual(5, tons);
            Assert.AreEqual(5, bought.Cargo.Count("Food"));
            Assert.AreEqual(8500, player.Credits);
        }

        [Test]
        public void AvailableQuotesExcludeUnavailableGoodsAndPorts()
        {
            PlayerState player = Landed(out GameData data);
            CollectionAssert.AreEquivalent(new[] { "Food", "Metal" },
                Trading.CommoditiesFor(data, player).Select(q => q.Commodity));
            player.Land(data.Planets["Barren"]);
            Assert.IsEmpty(Trading.CommoditiesFor(data, player));
            player.Depart();
            Assert.IsEmpty(Trading.CommoditiesFor(data, player));
        }
    }
}
