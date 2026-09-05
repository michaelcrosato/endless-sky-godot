using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class OutfitterTests
    {
        private static PlayerState Pilot(out GameData data)
        {
            data = new GameData();
            data.LoadText("ship Skiff\n\tattributes\n\t\tcost 1000\n\t\tmass 100\n\t\thull 500\n" +
                "\t\tshields 100\n\t\t\"energy capacity\" 50\n\t\t\"fuel capacity\" 500\n" +
                "\t\t\"outfit space\" 100\n\t\t\"weapon capacity\" 100\n\t\t\"cargo space\" 10\n" +
                "\t\tbunks 10\n\t\t\"required crew\" 2\n\t\tthrust 15\n\t\tturn 10\n\t\tdrag 1\n" +
                "\tgun 0 -20\n\tgun 5 -20\noutfit Cannon\n\tcost 1000\n\tmass 4\n" +
                "\t\"outfit space\" -4\n\t\"weapon capacity\" -4\n\t\"gun ports\" -1\n" +
                "\tweapon\n\t\treload 5\n\t\tvelocity 10\n\t\tlifetime 30\n\t\t\"hull damage\" 10\n" +
                "outfit Battery\n\tcost 400\n\t\"energy capacity\" 100\n\t\"outfit space\" -5\n" +
                "\t\"required crew\" 1\n\t\"mandatory crew\" 1\n" +
                "outfitter Shelf\n\tBattery\nplanet Home\n\tspaceport Busy\n\toutfitter Shelf\n" +
                "planet Outpost\n\tspaceport Quiet\nsystem A\n\tpos 0 0\n\tobject Home\n\tobject Outpost\n" +
                "system B\n\tpos 100 0\n");
            var player = new PlayerState(data);
            player.SetCredits(10000);
            player.EnterSystem(data.Systems["A"]);
            Ship ship = data.BuildShip("Skiff");
            ship.CurrentSystem = player.CurrentSystem;
            ship.AddOutfit(data.Outfits["Cannon"]);
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Land(data.Planets["Home"]);
            return player;
        }

        [TestCase("flight", TradeResult.NotSold)]
        [TestCase("no shop", TradeResult.NotSold)]
        [TestCase("unowned", TradeResult.NotOwned)]
        [TestCase("remote", TradeResult.NotHere)]
        [TestCase("unplaced", TradeResult.NotHere)]
        [TestCase("destroyed", TradeResult.NotHere)]
        [TestCase("committed", TradeResult.NotHere)]
        [TestCase("hyperspace", TradeResult.NotHere)]
        public void UnavailableShipsCannotTradeOrChangeState(string reason, TradeResult expected)
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            if (reason == "flight") Assert.IsTrue(player.Depart());
            if (reason == "no shop") player.Land(data.Planets["Outpost"]);
            if (reason == "unowned") player.Fleet.Remove(ship);
            if (reason == "remote") ship.CurrentSystem = data.Systems["B"];
            if (reason == "unplaced") ship.CurrentSystem = null;
            if (reason == "destroyed") ship.SetLevels(hull: -1);
            if (reason is "committed" or "hyperspace")
            {
                ship.Attributes.Set("jump drive", 1);
                ship.SetLevels(fuel: 500);
                ship.TargetSystem = data.Systems["B"];
                Assert.IsTrue(ship.TryCommitJump());
                if (reason == "hyperspace") ship.StepHyperspace();
            }
            string before = SaveGame.Write(player);
            double hull = ship.Hull;
            Assert.AreEqual(expected, Trading.BuyOutfit(player, data, ship, "Battery"));
            Assert.AreEqual(expected, Trading.SellOutfit(player, ship, data.Outfits["Cannon"]));
            Assert.AreEqual(before, SaveGame.Write(player));
            Assert.AreEqual(hull, ship.Hull);
            Assert.AreEqual(1, ship.Outfits.Count);
            Assert.AreEqual(1, ship.Mounts.Count(m => !m.IsEmpty));
        }

        [TestCase("money", TradeResult.CannotAfford)]
        [TestCase("capacity", TradeResult.DoesNotFit)]
        [TestCase("stock", TradeResult.NotSold)]
        [TestCase("overflow", TradeResult.CreditLimit)]
        public void RefusedTransactionsPreserveEquipmentAndBothLedgers(string reason, TradeResult expected)
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            player.Purchases.Record(PurchaseLog.OutfitKey("Cannon"), player.Date);
            player.OutfitStock.RecordAge(PurchaseLog.OutfitKey("Battery"), player.Date, 100);
            if (reason == "money") player.SetCredits(0);
            if (reason == "capacity") ship.Attributes.Set("outfit space", 0);
            if (reason == "overflow") player.SetCredits(long.MaxValue);
            string before = SaveGame.Write(player);
            TradeResult result = reason == "overflow"
                ? Trading.SellOutfit(player, ship, data.Outfits["Cannon"])
                : Trading.BuyOutfit(player, data, ship, reason == "stock" ? "Cannon" : "Battery");
            Assert.AreEqual(expected, result);
            Assert.AreEqual(before, SaveGame.Write(player));
            Assert.AreEqual(1, ship.Mounts.Count(m => !m.IsEmpty));
        }

        [TestCase(1)]
        [TestCase(3013)]
        public void UnstockedEquipmentCanBeSoldReloadedAndBoughtBackAtTheSameValue(int year)
        {
            PlayerState player = Pilot(out GameData data);
            player.SetDate(new DateTime(year, 1, 1));
            Outfit cannon = data.Outfits["Cannon"];
            Assert.IsNull(Trading.OutfitPurchaseValue(player, data, cannon));
            CollectionAssert.Contains(Trading.OutfitsToShow(player, data, player.Flagship).ToArray(), "Cannon");
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, player.Flagship!, cannon));
            Assert.AreEqual(10250, player.Credits);
            Assert.IsTrue(player.Flagship!.Mounts.All(m => m.IsEmpty));
            string saved = SaveGame.Write(player);
            player = SaveGame.Read(saved, data);
            Assert.AreEqual(saved, SaveGame.Write(player));
            Assert.AreEqual(250, Trading.OutfitPurchaseValue(player, data, cannon));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, player.Flagship!, "Cannon"));
            Assert.AreEqual(10000, player.Credits);
            Assert.IsTrue(ShipAi.IsArmed(player.Flagship!));
            Assert.IsNull(Trading.OutfitPurchaseValue(player, data, cannon));
            Assert.AreEqual(250, Trading.OutfitSaleValue(player, cannon));
            player = SaveGame.Read(SaveGame.Write(player), data);
            Assert.AreEqual(250, Trading.OutfitSaleValue(player, cannon), "buyback keeps the original age after another load");
        }

        [Test]
        public void NewestOwnedCopiesSellFirstAndOldestShopCopiesBuyBackFirst()
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            Outfit battery = data.Outfits["Battery"];
            ship.AddOutfit(battery, 2);
            string key = PurchaseLog.OutfitKey("Battery");
            player.Purchases.Record(key, player.Date.AddDays(-100));
            player.Purchases.Record(key, player.Date);
            long used = Depreciation.SaleValue(400, 100);
            Assert.AreEqual(400, Trading.OutfitSaleValue(player, battery));
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, ship, battery));
            Assert.AreEqual(used, Trading.OutfitSaleValue(player, battery));
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, ship, battery));
            Assert.AreEqual(10000 + 400 + used, player.Credits);
            Assert.AreEqual(used, Trading.OutfitPurchaseValue(player, data, battery));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship, "Battery"));
            Assert.AreEqual(used, Trading.OutfitSaleValue(player, battery));
            Assert.AreEqual(400, Trading.OutfitPurchaseValue(player, data, battery));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship, "Battery"));
            Assert.AreEqual(10000, player.Credits);
            Assert.AreEqual(0, player.OutfitStock.Count(key));
            Assert.AreEqual(400, Trading.OutfitPurchaseValue(player, data, battery), "normal stock remains unlimited");
        }

        [Test]
        public void SaleUsesTheInstalledDefinitionAndServicesOnlyTheSelectedEscort()
        {
            PlayerState player = Pilot(out GameData data);
            Ship flagship = player.Flagship!;
            Ship escort = data.BuildShip("Skiff");
            escort.CurrentSystem = player.CurrentSystem;
            escort.IsParked = true;
            escort.AddOutfit(data.Outfits["Battery"]);
            escort.Crew = 4;
            player.Fleet.Add(escort);
            flagship.SetLevels(energy: 1);
            escort.SetLevels(energy: 1);
            var spoof = new Outfit("Battery");
            spoof.Attributes.Set("cost", 1000000);
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, escort, spoof));
            Assert.AreEqual(10100, player.Credits);
            Assert.AreEqual(50, escort.Energy);
            Assert.AreEqual(2, escort.Crew);
            Assert.AreEqual(1, flagship.Energy);
            Assert.AreSame(flagship, player.Flagship);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, escort, "Battery"));
            Assert.AreEqual(150, escort.Energy);
            Assert.AreEqual(4, escort.Crew);
            Assert.AreEqual(10000, player.Credits);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StockSurvivesRefusedDepartureAndExpiresOnlyWhenThePilotLeaves(bool takeOff)
        {
            PlayerState player = Pilot(out GameData data);
            Outfit cannon = data.Outfits["Cannon"];
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, player.Flagship!, cannon));
            player.Fleet.PortCargo!.SetCapacity(20);
            Assert.AreEqual(20, player.Fleet.PortCargo.Add("Food", 20));
            player.Fleet.PortCargo.SetCapacity(10);
            Assert.IsFalse(takeOff ? player.TakeOff() : player.Depart());
            Assert.AreEqual(250, Trading.OutfitPurchaseValue(player, data, cannon));
            player.Fleet.UnloadCargo("Food", 20);
            Assert.IsTrue(takeOff ? player.TakeOff() : player.Depart());
            player.Land(data.Planets["Home"]);
            Assert.IsNull(Trading.OutfitPurchaseValue(player, data, cannon));
            Assert.IsEmpty(player.OutfitStock.Records);
        }

        [Test]
        public void ServicingADisabledHullMakesItAvailableAsFlagshipAgain()
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            ship.Disable();
            Ship parked = data.BuildShip("Skiff");
            parked.CurrentSystem = player.CurrentSystem;
            parked.IsParked = true;
            player.Fleet.Add(parked);
            Assert.IsNull(player.Flagship);
            CollectionAssert.Contains(Trading.ShipsToOutfit(player).ToArray(), ship);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship, "Battery"));
            Assert.IsFalse(ship.IsDisabled);
            Assert.AreEqual(ship.MaxHull, ship.Hull);
            Assert.AreSame(ship, player.Flagship);
            Assert.IsTrue(player.TakeOff());
        }

        [Test]
        public void InvalidStockDatesAndStockWithoutALandedPortAreDiscarded()
        {
            PlayerState player = Pilot(out GameData data);
            string stock = "\n\"outfit stock\"\n\t\"outfit:Cannon\" day -1001\n" +
                "\t\"outfit:Cannon\" day nope\n\t\"outfit:Cannon\" day 999999999999\n" +
                "\t\"outfit:Cannon\" 31 2 3013\n";
            Assert.IsEmpty(SaveGame.Read(SaveGame.Write(player) + stock, data).OutfitStock.Records);
            string valid = "\"outfit stock\"\n\t\"outfit:Cannon\" 1 1 3013\n";
            Assert.IsEmpty(SaveGame.Read(valid, data).OutfitStock.Records);
        }

        [TestCase(long.MaxValue)]
        [TestCase(long.MinValue)]
        public void FullValueGracePeriodDoesNotRoundAcrossTheCreditLimit(long cost)
        {
            Assert.AreEqual(cost, Depreciation.SaleValue(cost, 0));
            Assert.AreEqual(cost, Depreciation.SaleValue(cost, Depreciation.GracePeriod));
        }
    }
}
