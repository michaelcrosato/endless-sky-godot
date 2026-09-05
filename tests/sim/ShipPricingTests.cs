using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class ShipPricingTests
    {
        private static PlayerState Pilot(out GameData data)
        {
            data = new GameData();
            data.LoadText("ship Skiff\n\tattributes\n\t\tcost 1000\n\t\tmass 100\n\t\thull 500\n" +
                "\t\t\"outfit space\" 100\n\t\t\"cargo space\" 10\n\t\tbunks 10\n\t\tthrust 15\n\t\tturn 10\n" +
                "\toutfits\n\t\tScanner 3\noutfit Scanner\n\tcost 103\n\t\"outfit space\" -1\n" +
                "outfit Cooler\n\tcost 301\n\t\"outfit space\" -1\n" +
                "ship Skiff Variant\n\tattributes\n\t\tcost 9000\n\t\tmass 100\n\t\thull 500\n" +
                "\t\t\"outfit space\" 100\n\toutfits\n\t\tScanner 1\n" +
                "shipyard Yard\n\tSkiff\n\tVariant\noutfitter Shelf\n\tCooler\n" +
                "planet Home\n\tspaceport Busy\n\tshipyard Yard\n\toutfitter Shelf\n" +
                "system A\n\tpos 0 0\n\tobject Home\n");
            var player = new PlayerState(data);
            player.SetCredits(10000);
            player.EnterSystem(data.Systems["A"]);
            Ship ship = data.BuildShip("Skiff");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            player.Land(data.Planets["Home"]);
            return player;
        }

        [Test]
        public void AnOutfitIncludedWithANewShipKeepsItsFullPriceWhenSoldSeparately()
        {
            PlayerState player = Pilot(out GameData data);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out Ship? ship));
            Assert.AreEqual(8691, player.Credits);
            Assert.AreEqual(-3, player.Stock("Scanner"));
            Assert.AreEqual(3, player.Purchases.Count(PurchaseLog.OutfitKey("Scanner")));
            Assert.AreEqual(103, Trading.OutfitSaleValue(player, data.Outfits["Scanner"]));
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, ship!, data.Outfits["Scanner"]));
            Assert.AreEqual(8794, player.Credits);
            Assert.AreEqual(1, player.Stock("Scanner"), "an individual sale resets negative stock before adding the item");
            Assert.AreEqual(103, Trading.OutfitPurchaseValue(player, data, data.Outfits["Scanner"]));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship!, "Scanner"));
            Assert.AreEqual(8691, player.Credits);
            Assert.AreEqual(3, ship!.Outfits.Count);
        }

        [Test]
        public void SellingANewlyBoughtWholeShipDoesNotExposeItsOutfitsAtTheOutfitter()
        {
            PlayerState player = Pilot(out GameData data);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out Ship? ship));
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, ship!));
            Assert.AreEqual(10000, player.Credits);
            Assert.AreEqual(0, player.Stock("Scanner"));
            Assert.AreEqual(3, player.StockDepreciation.Count(PurchaseLog.OutfitKey("Scanner")));
            Assert.IsNull(Trading.OutfitPurchaseValue(player, data, data.Outfits["Scanner"]));
            string saved = SaveGame.Write(player);
            player = SaveGame.Read(saved, data);
            Assert.AreEqual(saved, SaveGame.Write(player));
            Assert.IsNull(Trading.OutfitPurchaseValue(player, data, data.Outfits["Scanner"]));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out ship));
            Assert.AreEqual(8691, player.Credits);
            Assert.AreEqual(0, player.StockDepreciation.Count(PurchaseLog.ShipKey("Skiff")));
            Assert.AreEqual(0, player.StockDepreciation.Count(PurchaseLog.OutfitKey("Scanner")));
        }

        [Test]
        public void NewEquipmentOnAnOldHullIsNotDepreciatedWithTheHull()
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship, "Cooler"));
            // 250 for the unknown hull, floor(3 * .25 * 103) for its old scanners, and 301 for the new cooler.
            Assert.AreEqual((Int128)628, Trading.ShipSaleValue(player, ship));
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, ship));
            Assert.AreEqual(10327, player.Credits);
            Assert.AreEqual(0, player.Purchases.Count(PurchaseLog.OutfitKey("Cooler")));
            Assert.AreEqual(3, player.Stock("Scanner"));
            Assert.AreEqual(0, player.Stock("Cooler"), "whole-ship stock changes retain the prior purchase debit");
            Assert.AreEqual(301, Trading.OutfitPurchaseValue(player, data, data.Outfits["Cooler"]));
        }

        [Test]
        public void MixedAgesAndMissingRecordsAreQuotedAndTransferredAsAGroup()
        {
            PlayerState player = Pilot(out GameData data);
            string key = PurchaseLog.OutfitKey("Scanner");
            player.Purchases.RecordAge(key, player.Date, 1000);
            player.Purchases.Record(key, player.Date);
            // One new, one old, and one unknown scanner: floor(1.5 * 103), plus the old hull.
            Assert.AreEqual((Int128)404, Trading.ShipSaleValue(player, player.Flagship!));
            string before = SaveGame.Write(player);
            Assert.AreEqual((Int128)404, Trading.ShipSaleValue(player, player.Flagship!));
            Assert.AreEqual(before, SaveGame.Write(player), "quotes do not consume a copy more than once");
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, player.Flagship!));
            player = SaveGame.Read(SaveGame.Write(player), data);
            Assert.AreEqual((Int128)404, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out Ship? bought));
            Assert.AreEqual(10000, player.Credits);
            Assert.AreEqual(0, player.Purchases.TakeAge(key, player.Date));
            Assert.AreEqual(1000, player.Purchases.TakeAge(key, player.Date));
            Assert.AreEqual(1000, player.Purchases.TakeAge(key, player.Date));
            Assert.AreEqual(3, bought!.Outfits.Count);
        }

        [Test]
        public void OutfitterPurchasesChangeTheNextEquippedShipQuote()
        {
            PlayerState player = Pilot(out GameData data);
            string key = PurchaseLog.OutfitKey("Scanner");
            player.Purchases.Record(key, player.Date);
            Ship original = player.Flagship!;
            Ship spare = data.BuildShip("Skiff");
            spare.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(spare);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, original));
            Assert.AreEqual((Int128)404, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, spare, "Scanner"));
            Assert.AreEqual(10379, player.Credits);
            Assert.AreEqual(2, player.Stock("Scanner"));
            // The next ship uses the two remaining stock scanners and one new scanner.
            Assert.AreEqual((Int128)481, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out _));
            Assert.AreEqual(9898, player.Credits);
            Assert.AreEqual(-1, player.Stock("Scanner"));
        }

        [Test]
        public void VariantsShareTheirStockChassisPriceAndAge()
        {
            PlayerState player = Pilot(out GameData data);
            Assert.AreEqual((Int128)1103, Trading.ShipPurchaseValue(player, data.BuildShip("Variant")));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Variant", out Ship? variant));
            Assert.AreEqual(8897, player.Credits);
            Assert.AreEqual(1, player.Purchases.Count(PurchaseLog.ShipKey("Skiff")));
            Assert.AreEqual(0, player.Purchases.Count(PurchaseLog.ShipKey("Variant")));
            player.AdvanceDays(1000);
            Assert.AreEqual((Int128)275, Trading.ShipSaleValue(player, variant!));
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, variant!));
            Assert.AreEqual((Int128)481, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
        }

        [Test]
        public void OldVariantAndOutfitterSaveRecordsMigrateWithoutLosingStockOrValue()
        {
            PlayerState player = Pilot(out GameData data);
            string legacy = SaveGame.Write(player) +
                $"\npurchases\n\t\"ship:Variant\" {player.Date.Day} {player.Date.Month} {player.Date.Year}\n" +
                "\"outfit stock\"\n\t\"outfit:Scanner\" 1 1 2000\n\t\"outfit:Scanner\" 1 1 2000\n";
            PlayerState restored = SaveGame.Read(legacy, data);
            Assert.AreEqual(1, restored.Purchases.Count(PurchaseLog.ShipKey("Skiff")));
            Assert.AreEqual(2, restored.Stock("Scanner"));
            Assert.AreEqual(25, Trading.OutfitPurchaseValue(restored, data, data.Outfits["Scanner"]));
            string saved = SaveGame.Write(restored);
            Assert.AreEqual(saved, SaveGame.Write(SaveGame.Read(saved, data)));
            Assert.IsFalse(saved.Contains("\"outfit stock\""));
            Assert.IsTrue(saved.Contains("\"stock depreciation\""));
        }

        [Test]
        public void DepartureExpiresBothSignedStockCountsAndHullDepreciation()
        {
            PlayerState player = Pilot(out GameData data);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, player.Flagship!));
            Assert.AreEqual((Int128)327, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
            Assert.IsFalse(player.TakeOff());
            Assert.AreEqual(1, player.StockDepreciation.Count(PurchaseLog.ShipKey("Skiff")));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out _));
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out _));
            Assert.AreEqual(-3, player.Stock("Scanner"));
            player = SaveGame.Read(SaveGame.Write(player), data);
            Assert.AreEqual(-3, player.Stock("Scanner"));
            Assert.IsTrue(player.TakeOff());
            Assert.IsEmpty(player.OutfitStock);
            Assert.IsEmpty(player.StockDepreciation.Records);
            player.Land(data.Planets["Home"]);
            Assert.AreEqual((Int128)1309, Trading.ShipPurchaseValue(player, data.BuildShip("Skiff")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StockOverflowRefusesTheWholeTransactionBeforeChangingTheSave(bool buy)
        {
            PlayerState player = Pilot(out GameData data);
            long quantity = buy ? long.MinValue : long.MaxValue;
            player = SaveGame.Read(SaveGame.Write(player) + $"\nstock\n\tScanner {quantity}\n", data);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.StockLimit, buy ? Trading.BuyShip(player, data, "Skiff", out _)
                : Trading.SellShip(player, player.Flagship!));
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void EquippedQuotesCannotWrapIntoAffordableNegativePrices()
        {
            PlayerState player = Pilot(out GameData data);
            data.LoadText("outfit Expensive\n\tcost 4000000000000000000\n" +
                "ship Giant\n\tattributes\n\t\tcost 4000000000000000000\n\t\thull 500\n" +
                "\toutfits\n\t\tExpensive 3\nshipyard Yard\n\tGiant\n");
            Ship giant = data.BuildShip("Giant");
            Int128 price = (Int128)4_000_000_000_000_000_000L * 4;
            Assert.AreEqual(price, Trading.ShipPurchaseValue(player, giant));
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.CannotAfford, Trading.BuyShip(player, data, "Giant", out _));
            Assert.AreEqual(before, SaveGame.Write(player));
            giant.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(giant);
            player.Purchases.Record(PurchaseLog.ShipKey("Giant"), player.Date);
            player.Purchases.Record(PurchaseLog.OutfitKey("Expensive"), player.Date, 3);
            Assert.AreEqual(price, Trading.ShipSaleValue(player, giant));
            before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.CreditLimit, Trading.SellShip(player, giant));
            Assert.AreEqual(before, SaveGame.Write(player));
        }
    }
}
