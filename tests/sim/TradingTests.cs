using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The shipyard and outfitter counters, and what a used hull is worth.
    /// Ports the transaction rules from upstream's shop panels and
    /// <c>Depreciation</c>.
    /// </summary>
    [TestFixture]
    public class TradingTests
    {
        private const string Universe =
            "ship \"Shuttle\"\n\tattributes\n\t\t\"cost\" 180000\n\t\t\"mass\" 80\n" +
            "\t\t\"hull\" 600\n\t\t\"outfit space\" 120\n\t\t\"weapon capacity\" 10\n" +
            "\t\t\"fuel capacity\" 400\n\t\t\"energy capacity\" 100\n\tgun 0 -31\n" +
            "ship \"Freighter\"\n\tattributes\n\t\t\"cost\" 900000\n\t\t\"mass\" 200\n" +
            "\t\t\"hull\" 900\n\t\t\"outfit space\" 200\n\t\t\"energy capacity\" 100\n" +
            "outfit \"Blaster\"\n\tcategory \"Guns\"\n\tcost 25000\n" +
            "\t\"mass\" 4\n\t\"outfit space\" -4\n\t\"weapon capacity\" -4\n\t\"gun ports\" -1\n" +
            "\tweapon\n\t\t\"reload\" 10\n\t\t\"velocity\" 12\n\t\t\"lifetime\" 30\n\t\t\"hull damage\" 20\n" +
            "outfit \"Enormous Engine\"\n\tcategory \"Engines\"\n\tcost 5000\n" +
            "\t\"mass\" 500\n\t\"outfit space\" -500\n" +
            "shipyard \"Basics\"\n\t\"Shuttle\"\n" +
            "outfitter \"Basics\"\n\t\"Blaster\"\n\t\"Enormous Engine\"\n" +
            "planet \"Home\"\n\tgovernment \"Republic\"\n\tspaceport `Busy.`\n" +
            "\tshipyard \"Basics\"\n\toutfitter \"Basics\"\n" +
            "planet \"Barren\"\n\tgovernment \"Republic\"\n" +
            "system \"Sol\"\n\tpos 0 0\n" +
            "\tobject \"Home\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n";

        private static PlayerState Landed(out GameData data, string planet = "Home",
                                          long credits = 1_000_000)
        {
            data = new GameData();
            data.LoadText(Universe);

            var player = new PlayerState(data);
            player.SetCredits(credits);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets[planet]);
            return player;
        }

        // --- Depreciation ---------------------------------------------------------

        [Test]
        public void SomethingBoughtThisWeekIsStillWorthFullPrice()
        {
            Assert.AreEqual(1.0, Depreciation.Fraction(0), 1e-9);
            Assert.AreEqual(1.0, Depreciation.Fraction(7), 1e-9, "the grace period is inclusive");
            Assert.Less(Depreciation.Fraction(8), 1.0, "and ends");
        }

        [Test]
        public void ValueFallsWithAgeAndBottomsOutAtAQuarter()
        {
            Assert.Greater(Depreciation.Fraction(100), Depreciation.Fraction(500));
            Assert.AreEqual(Depreciation.Minimum, Depreciation.Fraction(1000), 1e-9);
            Assert.AreEqual(Depreciation.Minimum, Depreciation.Fraction(50000), 1e-9,
                "it never falls below the floor");
        }

        [Test]
        public void AShipCannotBeFlippedForProfit()
        {
            // The reason depreciation exists: without it the shipyard is an infinite
            // credit machine and no purchase is ever a commitment.
            long cost = 180000;
            Assert.Less(Depreciation.SaleValue(cost), cost);
        }

        // --- Buying ships ---------------------------------------------------------

        [Test]
        public void BuyingAShipTakesTheMoneyAndAddsItToTheFleet()
        {
            PlayerState player = Landed(out GameData data);

            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Shuttle", out Ship? bought));

            Assert.IsNotNull(bought);
            Assert.AreEqual(1_000_000 - 180_000, player.Credits);
            CollectionAssert.Contains(player.Fleet.Ships, bought);
            Assert.AreEqual(bought, player.Fleet.Flagship, "the first ship bought is the flagship");
        }

        [Test]
        public void ANewlyBoughtShipArrivesFuelledAndIntact()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? bought);

            Assert.AreEqual(bought!.MaxHull, bought.Hull, 1e-9);
            Assert.AreEqual(bought.MaxFuel, bought.Fuel, 1e-9);
        }

        [Test]
        public void AShopOnlySellsWhatItStocks()
        {
            PlayerState player = Landed(out GameData data);

            Assert.AreEqual(TradeResult.NotSold,
                Trading.BuyShip(player, data, "Freighter", out _),
                "the Freighter is defined but nobody here stocks it");
            Assert.AreEqual(1_000_000, player.Credits, "a refusal must not cost anything");
        }

        [Test]
        public void AWorldWithNoShipyardSellsNothing()
        {
            PlayerState player = Landed(out GameData data, planet: "Barren");

            Assert.AreEqual(TradeResult.NotSold, Trading.BuyShip(player, data, "Shuttle", out _));
        }

        [Test]
        public void APlayerInFlightCannotShop()
        {
            PlayerState player = Landed(out GameData data);
            player.Depart();

            Assert.AreEqual(TradeResult.NotSold, Trading.BuyShip(player, data, "Shuttle", out _));
        }

        [Test]
        public void APlayerWhoCannotAffordItKeepsTheirMoney()
        {
            PlayerState player = Landed(out GameData data, credits: 1000);

            Assert.AreEqual(TradeResult.CannotAfford,
                Trading.BuyShip(player, data, "Shuttle", out _));
            Assert.AreEqual(1000, player.Credits);
            Assert.IsEmpty(player.Fleet.Ships);
        }

        // --- Selling ships --------------------------------------------------------

        [Test]
        public void SellingAShipPaysItsDepreciatedValue()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? first);
            Trading.BuyShip(player, data, "Shuttle", out Ship? second);

            long before = player.Credits;
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, second!));

            long sameDay = player.Credits - before;
            Assert.AreEqual(180_000, sameDay,
                "bought today and sold today is break-even; it is not a used ship yet");

            CollectionAssert.DoesNotContain(player.Fleet.Ships, second);
            CollectionAssert.Contains(player.Fleet.Ships, first);

            // Held for years, it is. Depreciation is a function of how long the player
            // kept the thing, which is why it needs a purchase record at all. A second
            // ship first, so selling the old one is not blocked as the last flyable
            // hull — and bought BEFORE the years pass, because the player sells their
            // newest copy first and a fresh purchase would be the one consumed.
            Trading.BuyShip(player, data, "Shuttle", out Ship? spare);
            Assert.IsNotNull(spare);

            player.AdvanceDays(Depreciation.MaxAge);
            before = player.Credits;
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, first!));

            long aged = player.Credits - before;
            Assert.AreEqual(Depreciation.SaleValue(180_000, Depreciation.MaxAge), aged,
                "a ship kept a thousand days is worth the depreciation floor");
        }

        [Test]
        public void ThePlayersLastFlyableShipCannotBeSold()
        {
            // Selling it would strand the player on the ground with no way to leave.
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? only);

            Assert.AreEqual(TradeResult.LastShip, Trading.SellShip(player, only!));
            CollectionAssert.Contains(player.Fleet.Ships, only);
        }

        [Test]
        public void AShipThePlayerDoesNotOwnCannotBeSold()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out _);

            Ship stranger = data.BuildShip("Freighter");
            Assert.AreEqual(TradeResult.NotOwned, Trading.SellShip(player, stranger));
        }

        // --- Outfits --------------------------------------------------------------

        [Test]
        public void BuyingAnOutfitInstallsItAndChargesForIt()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? ship);
            long before = player.Credits;

            Assert.AreEqual(TradeResult.Ok, Trading.BuyOutfit(player, data, ship!, "Blaster"));

            Assert.AreEqual(before - 25_000, player.Credits);
            CollectionAssert.Contains(ship!.Outfits.Select(o => o.Name).ToList(), "Blaster");
            Assert.IsFalse(ship.Mounts.First().IsEmpty, "and it goes on the hardpoint");
        }

        [Test]
        public void AnOutfitThatDoesNotFitIsRefusedBeforeAnyMoneyMoves()
        {
            // The fit check has to come first, or a refusal costs the player credits.
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? ship);
            long before = player.Credits;

            Assert.AreEqual(TradeResult.DoesNotFit,
                Trading.BuyOutfit(player, data, ship!, "Enormous Engine"));
            Assert.AreEqual(before, player.Credits);
        }

        [Test]
        public void SellingAnOutfitRemovesItFromTheHardpointToo()
        {
            // An outfitter that took the gun off the books but left it on the mount
            // would leave the ship firing a weapon it no longer owns.
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? ship);
            Trading.BuyOutfit(player, data, ship!, "Blaster");

            Outfit blaster = data.Outfits["Blaster"];
            Assert.AreEqual(TradeResult.Ok, Trading.SellOutfit(player, ship!, blaster));

            CollectionAssert.DoesNotContain(ship!.Outfits.Select(o => o.Name).ToList(), "Blaster");
            Assert.IsTrue(ship.Mounts.First().IsEmpty, "the hardpoint must be empty again");
            Assert.IsFalse(ShipAi.IsArmed(ship));
        }

        [Test]
        public void SellingAnOutfitRestoresTheCapacityItUsed()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? ship);
            double spaceBefore = ship!.Attributes.Get("outfit space");

            Trading.BuyOutfit(player, data, ship, "Blaster");
            Assert.AreEqual(spaceBefore - 4, ship.Attributes.Get("outfit space"), 1e-9);

            Trading.SellOutfit(player, ship, data.Outfits["Blaster"]);
            Assert.AreEqual(spaceBefore, ship.Attributes.Get("outfit space"), 1e-9);
        }

        [Test]
        public void AnOutfitTheShipDoesNotCarryCannotBeSold()
        {
            PlayerState player = Landed(out GameData data);
            Trading.BuyShip(player, data, "Shuttle", out Ship? ship);

            Assert.AreEqual(TradeResult.NotOwned,
                Trading.SellOutfit(player, ship!, data.Outfits["Blaster"]));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void ARealStartingWorldSellsShipsAPlayerCouldFly()
        {
            GameData data = UpstreamData.Instance;

            Planet? shop = data.Planets.Values
                .FirstOrDefault(p => p.Shipyards.Count > 0 &&
                                     Trading.ShipsFor(data, p).Any());

            Assert.IsNotNull(shop, "some world in the galaxy sells ships");

            var forSale = Trading.ShipsFor(data, shop!).ToList();
            TestContext.WriteLine($"{shop!.Name} sells {forSale.Count} models: " +
                                  string.Join(", ", forSale.Take(5)));

            foreach (string model in forSale.Where(data.Ships.ContainsKey).Take(5))
            {
                Ship ship = data.BuildShip(model);
                Assert.Greater(ship.Cost, 0, $"{model} should have a price");
            }
        }

        // --- Depreciation records -------------------------------------------------

        [Test]
        public void BuyingAndSellingBackTheSameDayIsBreakEven()
        {
            // Upstream records the day of purchase in the player's own depreciation
            // (Depreciation.cpp:167-202), so an item sold straight back is worth what
            // it cost. Without a record every sale fell through to the no-record
            // default, which is the 0.25 floor: buying anything and changing your mind
            // cost three quarters of its price.
            PlayerState player = Landed(out GameData data);
            Ship flagship = data.BuildShip("Shuttle");
            player.Fleet.Add(flagship);
            player.Fleet.SetFlagship(flagship);
            long before = player.Credits;
            Ship ship = player.Fleet.Flagship!;

            Assert.AreEqual(TradeResult.Ok,
                Trading.BuyOutfit(player, data, ship, "Blaster"));
            Assert.Less(player.Credits, before, "buying costs money");

            Assert.AreEqual(TradeResult.Ok,
                Trading.SellOutfit(player, ship, data.Outfits["Blaster"]));
            Assert.AreEqual(before, player.Credits, "and selling it straight back returns it");
        }

        [Test]
        public void AnOutfitHeldForYearsSellsForLessThanItCost()
        {
            // The same record, read later: depreciation is a function of how long the
            // player kept it, not a flat penalty for selling anything at all.
            PlayerState player = Landed(out GameData data);
            Ship flagship = data.BuildShip("Shuttle");
            player.Fleet.Add(flagship);
            player.Fleet.SetFlagship(flagship);
            Ship ship = player.Fleet.Flagship!;
            Trading.BuyOutfit(player, data, ship, "Blaster");

            long afterBuying = player.Credits;
            player.AdvanceDays(Depreciation.MaxAge);
            Trading.SellOutfit(player, ship, data.Outfits["Blaster"]);

            long paid = player.Credits - afterBuying;
            Assert.AreEqual(Depreciation.SaleValue(data.Outfits["Blaster"].Cost, Depreciation.MaxAge),
                paid, "a thousand days old is the depreciation floor");
        }

        [Test]
        public void AnOutfitWithNoPurchaseRecordStillSellsAtTheFloor()
        {
            // A hull's stock loadout was never bought by the player, so there is no
            // record for it and upstream treats it as fully depreciated.
            PlayerState player = Landed(out GameData data);
            Ship flagship = data.BuildShip("Shuttle");
            player.Fleet.Add(flagship);
            player.Fleet.SetFlagship(flagship);
            Ship ship = player.Fleet.Flagship!;
            ship.AddOutfit(data.Outfits["Blaster"]);

            long before = player.Credits;
            Trading.SellOutfit(player, ship, data.Outfits["Blaster"]);

            Assert.AreEqual(Depreciation.SaleValue(data.Outfits["Blaster"].Cost, Depreciation.MaxAge),
                player.Credits - before);
        }

    }
}
