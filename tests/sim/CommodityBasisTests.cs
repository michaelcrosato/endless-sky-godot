using System;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class CommodityBasisTests
    {
        private static PlayerState Pilot(out GameData data, int capacity = 30, long credits = 10000)
        {
            data = new GameData();
            data.LoadText("trade\n\tcommodity Food 100 600\n" +
                "ship Hauler\n\tattributes\n\t\tmass 100\n\t\thull 1000\n" +
                $"\t\t\"cargo space\" {capacity}\n" +
                "planet Home\n\tspaceport Busy\n" +
                "system Sol\n\tpos 0 0\n\tobject Home\n\ttrade Food 100\n" +
                "system Vega\n\tpos 100 0\n\ttrade Food 200\n" +
                "mission Freight\n\tcargo Food 20\n\tdestination Home\n");
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);
            player.SetCredits(credits);
            AddShip(player);
            return player;
        }

        private static Ship AddShip(PlayerState player)
        {
            Ship ship = player.Data!.BuildShip("Hauler");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            return ship;
        }

        private static long SavedBasis(PlayerState player) =>
            new DataFile(SaveGame.Write(player)).Nodes.FirstOrDefault(n => n.Token(0) == "basis")?
                .Children.FirstOrDefault(n => n.Token(0) == "Food")?.IntegerValue(1) ?? 0;

        [Test]
        public void DifferentPurchasePricesAreAveragedAcrossAllOwnedTons()
        {
            PlayerState player = Pilot(out GameData data);
            Trading.BuyCommodity(player, data, "Food", 3, out _);
            data.Trade.SetPrice("Sol", "Food", 201);
            Trading.BuyCommodity(player, data, "Food", 2, out _);
            Assert.AreEqual(702, SavedBasis(player));
            Trading.SellCommodity(player, data, "Food", 2, out _);
            Assert.AreEqual(422, SavedBasis(player), "702 * 2 / 5 truncates to a 280-credit cost of sale");
            Assert.AreEqual(9700, player.Credits);
            Trading.SellCommodity(player, data, "Food", 20, out _);
            Assert.AreEqual(0, SavedBasis(player));
        }

        [TestCase(30, 205L, 200L)]
        [TestCase(3, 5000L, 300L)]
        public void OnlyAnActualPurchaseAddsToTheBasis(int capacity, long credits, long expected)
        {
            PlayerState player = Pilot(out GameData data, capacity, credits);
            Trading.BuyCommodity(player, data, "Food", 100, out _);
            Assert.AreEqual(expected, SavedBasis(player));
            string before = SaveGame.Write(player);
            Trading.BuyCommodity(player, data, "Missing", 1, out _);
            Trading.SellCommodity(player, data, "Food", -1, out _);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void RoundingAppliesToTheWholeSaleEvenWhenItSpansHolds()
        {
            PlayerState player = Pilot(out GameData data, capacity: 2);
            AddShip(player);
            data.Trade.SetPrice("Sol", "Food", 1);
            Trading.BuyCommodity(player, data, "Food", 1, out _);
            data.Trade.SetPrice("Sol", "Food", 2);
            Trading.BuyCommodity(player, data, "Food", 2, out _);
            player.Fleet.SetFlagship(player.Fleet.Ships[1]);
            Trading.SellCommodity(player, data, "Food", 2, out _);
            Assert.AreEqual(2, SavedBasis(player), "one sale costs 5 * 2 / 3 = 3, regardless of its carriers");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoteAndParkedHoldingsStillShareTheCommodityBasis(bool parked)
        {
            PlayerState player = Pilot(out GameData data, capacity: 10);
            Ship first = player.Flagship!;
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            Ship second = AddShip(player);
            data.Trade.SetPrice("Sol", "Food", 200);
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            player.Fleet.SetFlagship(second);
            Assert.IsTrue(player.Depart());
            if (parked) first.IsParked = true;
            else first.CurrentSystem = data.Systems["Vega"];
            player.Land(data.Planets["Home"]);
            Trading.SellCommodity(player, data, "Food", 5, out _);
            Assert.AreEqual(2250, SavedBasis(player));
            Assert.AreEqual(10, first.Cargo.Count("Food"));
        }

        [Test]
        public void MissionFreightDoesNotDiluteTheCostOfPersonalGoods()
        {
            PlayerState player = Pilot(out GameData data);
            Trading.BuyCommodity(player, data, "Food", 5, out _);
            var log = new MissionLog(player);
            log.Accept(data.Missions["Freight"]);
            Trading.SellCommodity(player, data, "Food", 2, out _);
            Assert.AreEqual(300, SavedBasis(player));
            Assert.AreEqual(23, player.Fleet.CargoUsed());
            log.Abort(log.Active.Single());
            Assert.AreEqual(300, SavedBasis(player));
        }

        [TestCase(9007199254740993L, 3002399751580331L)]
        [TestCase(long.MaxValue, 3074457345618258603L)]
        public void LargeSavedCostsRemainExactThroughAProportionalSale(long basis, long remaining)
        {
            Pilot(out GameData data);
            PlayerState player = SaveGame.Read("system Sol\nplanet Home\nship Hauler\n\tflagship\n" +
                $"\tcargo\n\t\tcommodity Food 3\nbasis\n\tFood {basis}\n", data);
            Assert.AreEqual(basis, SavedBasis(player));
            Trading.SellCommodity(player, data, "Food", 2, out _);
            Assert.AreEqual(remaining, SavedBasis(player));
            PlayerState restored = SaveGame.Read(SaveGame.Write(player), data);
            Assert.AreEqual(remaining, SavedBasis(restored));
        }

        [Test]
        public void RemovingACargoShipRemovesItsShareOfTheCost()
        {
            PlayerState player = Pilot(out GameData data, capacity: 10);
            Ship first = player.Flagship!;
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            AddShip(player);
            data.Trade.SetPrice("Sol", "Food", 200);
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            Assert.IsTrue(player.Depart());
            Assert.IsTrue(player.Fleet.Remove(first));
            Assert.AreEqual(1500, SavedBasis(player));
            Assert.IsFalse(player.Fleet.Remove(first));
            Assert.AreEqual(1500, SavedBasis(player));
        }

        [Test]
        public void AFullCostLedgerCannotWrapWhenMoreGoodsArePurchased()
        {
            Pilot(out GameData data);
            PlayerState player = SaveGame.Read("system Sol\nplanet Home\naccount\n\tcredits 1000\n" +
                "ship Hauler\n\tflagship\n\tcargo\n\t\tcommodity Food 1\n" +
                $"basis\n\tFood {long.MaxValue - 50}\n", data);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.CreditLimit, Trading.BuyCommodity(player, data, "Food", 2, out int bought));
            Assert.AreEqual(0, bought);
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [TestCase(80, -100L)]
        [TestCase(100, 0L)]
        [TestCase(200, 500L)]
        public void SaleProfitUsesActualTonsAndTheirPurchaseCost(int salePrice, long expected)
        {
            PlayerState player = Pilot(out GameData data);
            Trading.BuyCommodity(player, data, "Food", 5, out _);
            data.Trade.SetPrice("Sol", "Food", salePrice);
            Assert.AreEqual(TradeResult.Ok,
                Trading.SellCommodity(player, data, "Food", 100, out int sold, out long profit));
            Assert.AreEqual(5, sold);
            Assert.AreEqual(expected, profit);
            Assert.AreEqual(0, player.GetBasis("Food"));
            Assert.IsEmpty(player.CostBasis);
        }

        [Test]
        public void ACreditLimitedSaleKeepsTheCostOfUnsoldGoods()
        {
            PlayerState player = Pilot(out GameData data);
            Trading.BuyCommodity(player, data, "Food", 5, out _);
            player.SetCredits(long.MaxValue - 150);
            Trading.SellCommodity(player, data, "Food", 5, out int sold, out long profit);
            Assert.AreEqual(1, sold);
            Assert.AreEqual(0, profit);
            Assert.AreEqual(400, SavedBasis(player));
            Assert.AreEqual(long.MaxValue - 50, player.Credits);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GoodsWithoutAPurchaseRecordHaveZeroCost(bool legacySave)
        {
            PlayerState player = Pilot(out GameData data);
            player.Flagship!.LoadCargo("Food", 5);
            if (legacySave) player = SaveGame.Read(SaveGame.Write(player), data);
            Assert.AreEqual(0, player.GetBasis("Food"));
            Trading.BuyCommodity(player, data, "Food", 5, out _);
            Assert.AreEqual(50, player.GetBasis("Food"));
            Trading.SellCommodity(player, data, "Food", 10, out _, out long profit);
            Assert.AreEqual(500, profit);
            Assert.IsEmpty(player.CostBasis);
        }

        [Test]
        public void PurchasesStopAtTheRemainingCostLedgerCapacity()
        {
            PlayerState player = Pilot(out GameData data);
            player.Flagship!.LoadCargo("Food", 1);
            player.AdjustBasis("Food", long.MaxValue - 250);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyCommodity(player, data, "Food", 5, out int bought));
            Assert.AreEqual(2, bought);
            Assert.AreEqual(long.MaxValue - 50, SavedBasis(player));
            Assert.AreEqual(9800, player.Credits);
        }

        [Test]
        public void CommodityTotalsCannotOverflowWhenTheFleetExceedsOneIntegerHold()
        {
            PlayerState player = Pilot(out GameData data, capacity: int.MaxValue, credits: long.MaxValue);
            AddShip(player);
            Trading.BuyCommodity(player, data, "Food", int.MaxValue, out _);
            Trading.BuyCommodity(player, data, "Food", int.MaxValue, out _);
            Assert.AreEqual(4294967294L, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(100, player.GetBasis("Food"));
            Trading.SellCommodity(player, data, "Food", 1, out int sold, out long profit);
            Assert.AreEqual(1, sold);
            Assert.AreEqual(0, profit);
            Assert.AreEqual(429496729300L, SavedBasis(player));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LandingRemovesDestroyedCarriersBeforeTheNextTrade(bool remote)
        {
            PlayerState player = Pilot(out GameData data, capacity: 10);
            Ship lost = player.Flagship!;
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            Ship survivor = AddShip(player);
            data.Trade.SetPrice("Sol", "Food", 200);
            Trading.BuyCommodity(player, data, "Food", 10, out _);
            player.Fleet.SetFlagship(survivor);
            Assert.IsTrue(player.Depart());
            if (remote) lost.CurrentSystem = data.Systems["Vega"];
            lost.SetLevels(hull: -1);
            player.Depart();
            player.Land(data.Planets["Home"]);
            CollectionAssert.DoesNotContain(player.Fleet.Ships, lost);
            Assert.AreEqual(1500, SavedBasis(player));
            Trading.SellCommodity(player, data, "Food", 10, out _, out long profit);
            Assert.AreEqual(500, profit);
            Assert.IsEmpty(player.CostBasis);
        }

        [Test]
        public void CargoLostTogetherUsesOneProportionalCostAdjustment()
        {
            PlayerState player = Pilot(out GameData data, capacity: 1);
            data.Trade.SetPrice("Sol", "Food", 1);
            Trading.BuyCommodity(player, data, "Food", 1, out _);
            AddShip(player);
            Trading.BuyCommodity(player, data, "Food", 1, out _);
            AddShip(player).LoadCargo("Food", 1);
            Ship survivor = AddShip(player);
            survivor.LoadCargo("Food", 1);
            player.Fleet.SetFlagship(survivor);
            Assert.IsTrue(player.Depart());
            player.Fleet.Ships[0].SetLevels(hull: -1);
            player.Fleet.Ships[1].SetLevels(hull: -1);
            player.Land(data.Planets["Home"]);
            Assert.AreEqual(1, SavedBasis(player), "half of four tons were lost, so remove half their two-credit cost");
            Assert.AreEqual(2, player.Fleet.Ships.Count);
        }
    }
}
