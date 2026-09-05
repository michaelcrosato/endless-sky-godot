using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class PortCargoTests
    {
        private static PlayerState Pilot(out GameData data, int tons = 20)
        {
            data = new GameData();
            data.LoadText("trade\n\tcommodity Food 100 600\n" +
                "ship Hauler\n\tattributes\n\t\tcost 1000\n\t\tmass 100\n\t\thull 1000\n\t\t\"cargo space\" 25\n" +
                "ship Tiny\n\tattributes\n\t\tcost 100\n\t\tmass 80\n\t\thull 800\n\t\t\"cargo space\" 10\n" +
                "shipyard Yard\n\tHauler\nplanet Home\n\tspaceport Busy\n\tshipyard Yard\n" +
                "system Sol\n\tpos 0 0\n\tobject Home\n\ttrade Food 100\n" +
                "system Vega\n\tpos 100 0\n\ttrade Food 200\n" +
                "mission Freight\n\tcargo Food 20\n\tdestination Home\n\ton complete\n\t\tpayment 1000\n" +
                "\ton abort\n\t\tpayment -25\nmission Parcel\n\tcargo documents 0\n\tdestination Home\n");
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["Sol"]);
            player.SetCredits(100000);
            for (int i = 0; i < 2; i++)
            {
                Ship ship = data.BuildShip("Hauler");
                ship.CurrentSystem = player.CurrentSystem;
                player.Fleet.Add(ship);
            }
            player.Fleet.LoadCargo("Food", tons);
            player.AdjustBasis("Food", tons * 100L);
            player.Land(data.Planets["Home"]);
            return player;
        }

        [Test]
        public void LandingPoolsCargoAshoreAndRemovesItsMassFromTheHulls()
        {
            PlayerState player = Pilot(out _);
            Assert.AreEqual(20, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(20, player.Fleet.CargoUsed());
            Assert.AreEqual(2000, player.CostBasis["Food"]);
            foreach (Ship ship in player.Fleet.Ships)
            {
                Assert.IsTrue(ship.Cargo.IsEmpty);
                Assert.AreEqual(100, ship.Mass);
            }
        }

        [Test]
        public void SellingTheFormerCarrierKeepsItsCargoAndPurchaseCost()
        {
            PlayerState player = Pilot(out _);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, player.Flagship!));
            Assert.AreEqual(20, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(2000, player.CostBasis["Food"]);
            player.TakeOff();
            Assert.IsNull(player.CurrentPlanet);
            Assert.AreEqual(20, player.Flagship!.Cargo.Count("Food"));
            Assert.AreEqual(120, player.Flagship.Mass);
        }

        [Test]
        public void AReducedFleetRetainsItsExcessCargoUntilThePlayerDecides()
        {
            PlayerState player = Pilot(out _, tons: 40);
            Trading.SellShip(player, player.Flagship!);
            long credits = player.Credits;
            Assert.AreEqual(40, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(25, player.Fleet.CargoCapacity());
            Assert.AreEqual(0, player.Fleet.CargoFree());
            player.TakeOff();
            Assert.IsNotNull(player.CurrentPlanet, "unconfirmed excess sales must not launch the ship");
            Assert.AreEqual(credits, player.Credits);
            Assert.AreEqual(4000, player.CostBasis["Food"]);
        }

        [Test]
        public void AnOverfullPortInventorySurvivesSaveAndLoad()
        {
            PlayerState player = Pilot(out GameData data, tons: 40);
            Trading.SellShip(player, player.Flagship!);
            string save = SaveGame.Write(player);
            Assert.IsTrue(new DataFile(save).Nodes.Any(n => n.Token(0) == "cargo"),
                "ashore cargo has its own save block");
            PlayerState restored = SaveGame.Read(save, data);
            Assert.AreEqual(40, restored.Fleet.CargoCount("Food"));
            Assert.AreEqual(4000, restored.CostBasis["Food"]);
            Assert.IsTrue(restored.Flagship!.Cargo.IsEmpty);
            Assert.AreEqual(save, SaveGame.Write(restored));
        }

        [Test]
        public void PurchasesStayAshoreUntilDeparture()
        {
            PlayerState player = Pilot(out GameData data, tons: 0);
            Trading.BuyCommodity(player, data, "Food", 40, out int bought);
            Assert.AreEqual(40, bought);
            Assert.IsTrue(player.Fleet.Ships.All(s => s.Cargo.IsEmpty));
            player.TakeOff();
            Assert.AreEqual(25, player.Flagship!.Cargo.Count("Food"));
            Assert.AreEqual(15, player.Fleet.Ships[1].Cargo.Count("Food"));
        }

        [Test]
        public void SellingAHullDoesNotLoseFreightThatWasUnloadedAtThePort()
        {
            PlayerState player = Pilot(out GameData data, tons: 5);
            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Freight"])!;
            Trading.SellShip(player, player.Flagship!);
            Assert.IsTrue(log.CanComplete(taken));
            Assert.IsTrue(log.Complete(taken));
            Assert.AreEqual(5, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(5, player.Fleet.CargoUsed());
        }

        [Test]
        public void FreightBoardsBeforeOrdinaryGoodsWhenTheRemainingFleetIsTooSmall()
        {
            PlayerState player = Pilot(out GameData data, tons: 25);
            var log = new MissionLog(player);
            log.Accept(data.Missions["Freight"]);
            Trading.SellShip(player, player.Fleet.Ships[1]);
            Assert.AreEqual(45, player.Fleet.CargoUsed());
            Assert.IsTrue(log.CanComplete(log.Active.Single()), "the job is still ashore and intact");
            CargoDeparture departure = player.PreviewTakeOff(log);
            Assert.IsEmpty(departure.MissionsToAbort);
            Assert.AreEqual(20, departure.CommoditiesToSell["Food"]);
            Assert.IsTrue(player.TakeOff(log, acceptCargoLoss: true));
            Assert.AreEqual(20, player.Flagship!.Cargo.MissionCargo[log.Active.Single().Id]);
            Assert.AreEqual(5, player.Flagship.Cargo.Count("Food"));
            Assert.AreEqual(500, player.CostBasis["Food"]);
        }

        [Test]
        public void DeparturePreviewIsReversibleAndConfirmedSalesUseTheCurrentPrice()
        {
            PlayerState player = Pilot(out GameData data, tons: 40);
            Trading.SellShip(player, player.Flagship!);
            player.Flagship!.SetLevels(hull: 500);
            data.Trade.SetPrice("Sol", "Food", 150);
            long credits = player.Credits;
            string before = SaveGame.Write(player);
            CargoDeparture plan = player.PreviewTakeOff();
            Assert.IsTrue(plan.CanDepart);
            Assert.IsTrue(plan.NeedsConfirmation);
            Assert.AreEqual(15, plan.CommoditiesToSell["Food"]);
            Assert.AreEqual(2250, plan.Income);
            Assert.AreEqual(750, plan.Profit);
            Assert.AreEqual(before, SaveGame.Write(player));
            Assert.IsFalse(player.TakeOff());
            Assert.AreEqual(before, SaveGame.Write(player), "declining the loss also avoids servicing or moving the ships");
            Assert.IsTrue(player.TakeOff(acceptCargoLoss: true));
            Assert.IsNull(player.CurrentPlanet);
            Assert.IsNull(player.Fleet.PortCargo);
            Assert.AreEqual(25, player.Flagship.Cargo.Count("Food"));
            Assert.AreEqual(2500, player.CostBasis["Food"]);
            Assert.AreEqual(credits + 2250, player.Credits);
        }

        [Test]
        public void UncarryableFreightIsOnlyAbandonedAfterConfirmation()
        {
            PlayerState player = Pilot(out GameData data, tons: 5);
            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Freight"])!;
            Ship tiny = data.BuildShip("Tiny");
            tiny.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(tiny);
            foreach (Ship hauler in player.Fleet.Ships.Where(s => s != tiny).ToArray())
                Trading.SellShip(player, hauler);
            long credits = player.Credits;
            CargoDeparture plan = player.PreviewTakeOff(log);
            CollectionAssert.AreEqual(new[] { taken.Id }, plan.MissionsToAbort);
            Assert.AreEqual(5, plan.CommoditiesToSell["Food"]);
            Assert.IsFalse(player.TakeOff(log));
            Assert.AreEqual(MissionOutcome.Active, taken.Outcome);
            Assert.AreEqual(25, player.Fleet.CargoUsed());
            Assert.IsTrue(player.TakeOff(log, acceptCargoLoss: true));
            Assert.AreEqual(MissionOutcome.Aborted, taken.Outcome);
            Assert.IsTrue(tiny.Cargo.IsEmpty, "departure does not refill space freed by aborting the job");
            Assert.AreEqual(credits + 500 - 25, player.Credits);
            Assert.IsEmpty(player.CostBasis);
        }

        [Test]
        public void AZeroTonParcelMovesEvenWhenEveryTonOfSpaceIsUsed()
        {
            PlayerState player = Pilot(out GameData data, tons: 50);
            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Parcel"])!;
            Assert.AreEqual(0, player.Fleet.PortCargo!.MissionCargo[taken.Id]);
            Assert.IsFalse(player.PreviewTakeOff(log).NeedsConfirmation);
            Assert.IsTrue(player.TakeOff(log));
            Assert.IsTrue(player.Fleet.HasMissionCargo(taken.Id, 0));
            player.Land(data.Planets["Home"]);
            Assert.IsTrue(log.Complete(taken));
            Assert.AreEqual(50, player.Fleet.CargoCount("Food"));
        }

        [TestCase(0, 0L)]
        [TestCase(-10, -150L)]
        public void ConfirmedExcessDisposalUsesEvenAnUnavailableQuote(int price, long income)
        {
            PlayerState player = Pilot(out GameData data, tons: 40);
            Trading.SellShip(player, player.Flagship!);
            data.Trade.SetPrice("Sol", "Food", price);
            long credits = player.Credits;
            Assert.AreEqual(income, player.PreviewTakeOff().Income);
            Assert.IsTrue(player.TakeOff(acceptCargoLoss: true));
            Assert.AreEqual(credits + income, player.Credits);
            Assert.AreEqual(25, player.Fleet.CargoCount("Food"));
        }

        [Test]
        public void AnOverflowingPaymentCannotConsumeCargoOrLaunchTheShip()
        {
            PlayerState player = Pilot(out _, tons: 40);
            Trading.SellShip(player, player.Flagship!);
            player.SetCredits(long.MaxValue - 100);
            string before = SaveGame.Write(player);
            Assert.IsFalse(player.PreviewTakeOff().CanDepart);
            Assert.IsFalse(player.TakeOff(acceptCargoLoss: true));
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void ReloadPreservesOverfullFreightAndTheJobsNamedByTheDepartureWarning()
        {
            PlayerState player = Pilot(out GameData data, tons: 25);
            var log = new MissionLog(player);
            ActiveMission freight = log.Accept(data.Missions["Freight"])!;
            ActiveMission parcel = log.Accept(data.Missions["Parcel"])!;
            Ship tiny = data.BuildShip("Tiny");
            tiny.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(tiny);
            foreach (Ship ship in player.Fleet.Ships.Where(s => s != tiny).ToArray())
                Trading.SellShip(player, ship);
            string saved = SaveGame.Write(player, log);
            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(saved, data, p => restoredLog = new MissionLog(p));
            CollectionAssert.AreEqual(new[] { freight.Id }, restored.PreviewTakeOff(restoredLog).MissionsToAbort);
            Assert.AreEqual(20, restored.Fleet.PortCargo!.MissionCargo[freight.Id]);
            Assert.AreEqual(0, restored.Fleet.PortCargo.MissionCargo[parcel.Id]);
            Assert.AreEqual(saved, SaveGame.Write(restored, restoredLog));
            Assert.IsTrue(restored.TakeOff(restoredLog, acceptCargoLoss: true));
            Assert.AreEqual(parcel.Id, restoredLog!.Active.Single().Id);
            Assert.IsTrue(restored.Fleet.HasMissionCargo(parcel.Id, 0));
            Assert.AreEqual(0, restored.Fleet.CargoUsed());
        }

        [Test]
        public void RemoteAndParkedHoldsStayWithTheirShipsDuringPoolingAndDeparture()
        {
            PlayerState player = Pilot(out GameData data, tons: 0);
            Assert.IsTrue(player.Depart());
            player.Flagship!.LoadCargo("Food", 10);
            Ship parked = player.Fleet.Ships[1];
            parked.LoadCargo("Food", 5);
            parked.IsParked = true;
            Ship remote = data.BuildShip("Hauler");
            remote.CurrentSystem = data.Systems["Vega"];
            remote.LoadCargo("Food", 20);
            player.Fleet.Add(remote);
            player.Land(data.Planets["Home"]);
            string before = SaveGame.Write(player);
            Assert.IsFalse(player.PreviewTakeOff().NeedsConfirmation);
            Assert.AreEqual(before, SaveGame.Write(player));
            Assert.AreEqual(10, player.Fleet.PortCargo!.Count("Food"));
            Assert.IsTrue(player.TakeOff());
            Assert.AreEqual(10, player.Flagship.Cargo.Count("Food"));
            Assert.AreEqual(5, parked.Cargo.Count("Food"));
            Assert.AreEqual(20, remote.Cargo.Count("Food"));
            Assert.AreSame(data.Systems["Vega"], remote.CurrentSystem);
        }

        [Test]
        public void PooledFleetTonnageBeyondTheIntegerLimitSurvivesReload()
        {
            PlayerState player = Pilot(out GameData data, tons: 0);
            data.LoadText("ship Bulk\n\tattributes\n\t\tmass 100\n\t\thull 1000\n\t\t\"cargo space\" 2000000000\n");
            foreach (Ship ship in player.Fleet.Ships.ToArray()) player.Fleet.Remove(ship);
            for (int i = 0; i < 2; i++)
            {
                Ship ship = data.BuildShip("Bulk");
                ship.CurrentSystem = player.CurrentSystem;
                player.Fleet.Add(ship);
                Assert.AreEqual(2_000_000_000, player.Fleet.LoadCargo("Food", 2_000_000_000));
            }
            player.AdjustBasis("Food", 400_000_000_000L);
            string saved = SaveGame.Write(player);
            PlayerState restored = SaveGame.Read(saved, data);
            Assert.AreEqual(4_000_000_000L, restored.Fleet.PortCargo!.Count("Food"));
            Assert.AreEqual(400_000_000_000L, restored.CostBasis["Food"]);
            Assert.AreEqual(saved, SaveGame.Write(restored));
            Assert.IsTrue(restored.TakeOff());
            Assert.IsTrue(restored.Fleet.Ships.All(s => s.Cargo.Count("Food") == 2_000_000_000));
        }

        [Test]
        public void DisposingTheEntireMinimumSignedCostDoesNotOverflowItsRemoval()
        {
            PlayerState player = Pilot(out GameData data);
            player.AdjustBasis("Food", -2000);
            player.AdjustBasis("Food", long.MinValue);
            player.Flagship!.Attributes.Set("cargo space", 0);
            player.Fleet.Ships[1].IsParked = true;
            data.Trade.SetPrice("Sol", "Food", -1);
            Assert.IsTrue(player.TakeOff(acceptCargoLoss: true));
            Assert.IsEmpty(player.CostBasis);
            Assert.AreEqual(99980, player.Credits);
            Assert.AreEqual(0, player.Fleet.CargoUsed());
        }

        [Test]
        public void ParkingAnEmptyHullDoesNotParkTheCargoUnloadedFromIt()
        {
            PlayerState player = Pilot(out _, tons: 40);
            player.Fleet.Ships[1].IsParked = true;
            Assert.AreEqual(40, player.Fleet.CargoCount("Food"));
            Assert.AreEqual(15, player.PreviewTakeOff().CommoditiesToSell["Food"]);
            player.Fleet.Ships[1].IsParked = false;
            Assert.IsFalse(player.PreviewTakeOff().NeedsConfirmation);
        }
    }
}
