using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class MissionCargoTests
    {
        private static PlayerState Pilot(out GameData data, out MissionLog log)
        {
            data = new GameData();
            data.LoadText("trade\n\tcommodity Food 100 600\n\tcommodity Metal 100 600\n" +
                "ship Hauler\n\tattributes\n\t\tmass 100\n\t\thull 1000\n\t\t\"cargo space\" 25\n" +
                "planet Home\n\tspaceport Busy\nplanet Away\n\tspaceport Busy\n" +
                "system Sol\n\tpos 0 0\n\tobject Home\n\ttrade Food 100\n\tlink Vega\n" +
                "system Vega\n\tpos 100 0\n\tobject Away\n\ttrade Food 200\n\tlink Sol\n" +
                "mission A\n\tjob\n\tcargo Food 20\n\tdestination Away\n\tdeadline 1\n\ton complete\n\t\tpayment 1000\n\ton fail\n\t\tpayment -75\n" +
                "mission B\n\tjob\n\tcargo Food 20\n\tdestination Away\n\ton complete\n\t\tpayment 2000\n" +
                "mission Parcel\n\tjob\n\tcargo documents 0\n\tdestination Away\n");
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);
            player.SetCredits(10000);
            AddShip(player, data);
            log = new MissionLog(player);
            return player;
        }

        private static Ship AddShip(PlayerState player, GameData data)
        {
            Ship ship = data.BuildShip("Hauler");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            return ship;
        }

        private static void Arrive(PlayerState player, GameData data)
        {
            player.EnterSystem(data.Systems["Vega"]);
            player.Land(data.Planets["Away"]);
            foreach (Ship ship in player.Fleet.Ships) ship.CurrentSystem = player.CurrentSystem;
        }

        [Test]
        public void FreightUsesSpaceAndMassWithoutBecomingThePlayersCommodity()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            double empty = player.Flagship!.Mass;
            log.Accept(data.Missions["A"]);
            Assert.Multiple(() =>
            {
                Assert.AreEqual(20, player.Flagship.Cargo.Used);
                Assert.AreEqual(empty + 20, player.Flagship.Mass, 1e-9);
                Assert.AreEqual(0, player.Fleet.CargoCount("Food"));
                Assert.AreEqual(0, player.Fleet.CargoValueAt(data.Trade, "Sol"));
            });
        }

        [Test]
        public void FreightCannotBeSoldThroughTheCommodityCounter()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            log.Accept(data.Missions["A"]);
            Assert.AreEqual(TradeResult.NotOwned, Trading.SellCommodity(player, data, "Food", 20, out int sold));
            Assert.AreEqual(0, sold);
            Assert.AreEqual(10000, player.Credits);
            Assert.AreEqual(20, player.Flagship!.Cargo.Used);
        }

        [Test]
        public void SellingPersonalGoodsLeavesTheSameNamedFreightAboard()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Trading.BuyCommodity(player, data, "Food", 5, out _);
            ActiveMission taken = log.Accept(data.Missions["A"])!;
            Assert.AreEqual(TradeResult.Ok, Trading.SellCommodity(player, data, "Food", 25, out int sold));
            Assert.AreEqual(5, sold);
            Assert.AreEqual(10000, player.Credits);
            Arrive(player, data);
            Assert.IsTrue(log.Complete(taken));
            Assert.AreEqual(0, player.Flagship!.Cargo.Used);
        }

        [Test]
        public void LosingOneJobsShipCannotBeReplacedWithAnotherJobsFreight()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Ship first = player.Flagship!;
            first.LoadCargo("Metal", 5);
            ActiveMission a = log.Accept(data.Missions["A"])!;
            Ship second = AddShip(player, data);
            ActiveMission b = log.Accept(data.Missions["B"])!;
            player.Fleet.Remove(first);
            Arrive(player, data);
            Assert.IsFalse(log.CanComplete(a));
            Assert.IsFalse(log.Complete(a));
            Assert.AreEqual(20, second.Cargo.Used);
            Assert.IsTrue(log.Complete(b));
            Assert.AreEqual(12000, player.Credits);
        }

        [Test]
        public void AbortingRemovesOnlyThatJobsFreightEvenOnAParkedShip()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Ship first = player.Flagship!;
            first.LoadCargo("Food", 5);
            ActiveMission a = log.Accept(data.Missions["A"])!;
            Ship second = AddShip(player, data);
            ActiveMission b = log.Accept(data.Missions["B"])!;
            first.IsParked = true;
            log.Abort(a);
            Assert.AreEqual(5, first.Cargo.Used);
            Assert.AreEqual(20, second.Cargo.Used);
            Arrive(player, data);
            Assert.IsTrue(log.Complete(b));
        }

        [Test]
        public void ExpiredFreightDoesNotBecomeFreeSaleableCargo()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            player.Flagship!.LoadCargo("Food", 5);
            ActiveMission taken = log.Accept(data.Missions["A"])!;
            player.AdvanceDays(2);
            log.Step();
            Assert.AreEqual(MissionOutcome.Failed, taken.Outcome);
            Assert.AreEqual(5, player.Flagship.Cargo.Used);
            Assert.AreEqual(5, player.Fleet.CargoCount("Food"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AcceptanceRequiresTheWholeLoadToFitLocally(bool remoteHold)
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            player.Flagship!.LoadCargo("Metal", 20);
            if (remoteHold) AddShip(player, data).CurrentSystem = data.Systems["Vega"];
            Assert.IsNull(log.Accept(data.Missions["A"]));
            Assert.IsEmpty(log.Active);
            Assert.AreEqual(20, player.Fleet.CargoUsed());
            Assert.AreEqual(0, player.Conditions.Get("A: offered"));
        }

        [Test]
        public void FreightInAnotherSystemCannotBeDeliveredHere()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Ship carrier = player.Flagship!;
            ActiveMission taken = log.Accept(data.Missions["A"])!;
            Ship pilot = AddShip(player, data);
            player.Fleet.SetFlagship(pilot);
            Arrive(player, data);
            carrier.CurrentSystem = data.Systems["Sol"];
            Assert.IsFalse(log.Complete(taken));
            Assert.AreEqual(10000, player.Credits);
        }

        [Test]
        public void ReloadPreservesWhichShipCarriesEachJob()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            player.Flagship!.LoadCargo("Metal", 5);
            log.Accept(data.Missions["A"]);
            AddShip(player, data);
            log.Accept(data.Missions["B"]);
            string saved = SaveGame.Write(player, log);
            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(saved, data, p => restoredLog = new MissionLog(p));
            Assert.AreEqual(saved, SaveGame.Write(restored, restoredLog));
            Assert.AreEqual(0, restored.Fleet.CargoCount("Food"));
            restored.Fleet.Remove(restored.Flagship!);
            Arrive(restored, data);
            Assert.IsFalse(restoredLog!.CanComplete(restoredLog.Active.Single(a => a.Mission.Name == "A")));
            Assert.IsTrue(restoredLog.CanComplete(restoredLog.Active.Single(a => a.Mission.Name == "B")));
        }

        [Test]
        public void LegacySavesReserveRecordedFreightFromExistingCommodities()
        {
            Pilot(out GameData data, out _);
            MissionLog? log = null;
            PlayerState restored = SaveGame.Read("system Vega\nplanet Away\nship Hauler\n\tflagship\n" +
                "\tcargo\n\t\tcommodity Food 25\nmission A\n\tcargo 20\n", data,
                p => log = new MissionLog(p));
            Assert.AreEqual(25, restored.Flagship!.Cargo.Used);
            Assert.AreEqual(5, restored.Fleet.CargoCount("Food"));
            Assert.IsTrue(log!.Complete(log.Active.Single()));
            Assert.AreEqual(5, restored.Flagship.Cargo.Used);
        }

        [Test]
        public void EvenZeroTonFreightMustStillBeOnAShipAtTheDestination()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Ship carrier = player.Flagship!;
            ActiveMission taken = log.Accept(data.Missions["Parcel"])!;
            AddShip(player, data);
            player.Fleet.Remove(carrier);
            Arrive(player, data);
            Assert.IsFalse(log.Complete(taken));
        }

        [Test]
        public void ReloadDoesNotReplaceLostFreightWithNewlyPurchasedCommodities()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            ActiveMission taken = log.Accept(data.Missions["A"])!;
            player.Fleet.RemoveMissionCargo(taken.Id);
            Trading.BuyCommodity(player, data, "Food", 20, out _);
            Arrive(player, data);
            for (int i = 0; i < 2; i++)
            {
                MissionLog? restoredLog = null;
                player = SaveGame.Read(SaveGame.Write(player, log), data,
                    p => restoredLog = new MissionLog(p));
                log = restoredLog!;
                Assert.AreEqual(taken.Id, log.Active.Single().Id);
                Assert.IsFalse(log.CanComplete(log.Active.Single()));
                Assert.AreEqual(20, player.Fleet.CargoCount("Food"));
                Assert.IsEmpty(player.Flagship!.Cargo.MissionCargo);
            }
        }

        [TestCase(0)]
        [TestCase(5)]
        public void LegacyPartialLoadsCannotBecomeACompleteDelivery(int tons)
        {
            Pilot(out GameData data, out _);
            MissionLog? log = null;
            PlayerState restored = SaveGame.Read("system Vega\nplanet Away\nship Hauler\n\tflagship\n" +
                $"\tcargo\n\t\tcommodity Food {tons}\nmission A\n\tcargo {tons}\n", data,
                p => log = new MissionLog(p));
            Assert.AreEqual(tons, restored.Flagship!.Cargo.Used);
            Assert.AreEqual(20, log!.Active.Single().CargoLoaded);
            Assert.IsFalse(log.Complete(log.Active.Single()));
        }

        [Test]
        public void ZeroTonParcelsSurviveCommodityUnloadingAndReload()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            ActiveMission taken = log.Accept(data.Missions["Parcel"])!;
            player.Flagship!.LoadCargo("Food", 25);
            Assert.AreEqual(25, player.Flagship.Cargo.RemoveAll()["Food"]);
            Assert.AreEqual(0, player.Flagship.Cargo.Used);
            Assert.IsFalse(player.Flagship.Cargo.IsEmpty, "the parcel still exists despite its zero mass");
            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(SaveGame.Write(player, log), data,
                p => restoredLog = new MissionLog(p));
            Assert.AreEqual(0, restored.Flagship!.Cargo.MissionCargo[taken.Id]);
            Arrive(restored, data);
            Assert.IsTrue(restoredLog!.Complete(restoredLog.Active.Single()));
            Assert.IsTrue(restored.Flagship.Cargo.IsEmpty);
        }

        [TestCase("A", false)]
        [TestCase("A", true)]
        [TestCase("Parcel", false)]
        [TestCase("Parcel", true)]
        public void LosingACarrierFailsItsJobsAndReleasesTheSurvivingFreight(string name, bool destroyed)
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            Ship carrier = player.Flagship!;
            carrier.LoadCargo("Metal", 20);
            Ship survivor = AddShip(player, data);
            ActiveMission taken = log.Accept(data.Missions[name])!;
            if (destroyed) carrier.SetLevels(hull: -1);
            else player.Fleet.Remove(carrier);

            CollectionAssert.AreEqual(new[] { taken }, log.Step());
            Assert.AreEqual(MissionOutcome.Failed, taken.Outcome);
            Assert.IsEmpty(survivor.Cargo.MissionCargo);
            Assert.AreEqual(0, survivor.Cargo.Used);
            long expected = name == "A" ? 9925 : 10000;
            Assert.AreEqual(expected, player.Credits);
            Assert.IsEmpty(log.Step());
            Assert.AreEqual(expected, player.Credits, "the failure action fires once");
        }

        [Test]
        public void FreightWaitingInAnotherSystemIsNotLost()
        {
            PlayerState player = Pilot(out GameData data, out MissionLog log);
            ActiveMission taken = log.Accept(data.Missions["A"])!;
            player.Flagship!.CurrentSystem = data.Systems["Vega"];
            Assert.IsEmpty(log.Step());
            Assert.AreEqual(MissionOutcome.Active, taken.Outcome);
        }
    }
}
