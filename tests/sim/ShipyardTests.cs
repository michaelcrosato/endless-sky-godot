using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class ShipyardTests
    {
        private static PlayerState Pilot(out GameData data)
        {
            data = new GameData();
            data.LoadText("ship Skiff\n\tattributes\n\t\tcost 1000\n\t\tmass 100\n\t\thull 500\n" +
                "\t\t\"cargo space\" 20\nship Other\n\tattributes\n\t\tcost 2000\n\t\thull 600\n" +
                "shipyard Yard\n\tSkiff\nplanet Home\n\tspaceport Busy\n\tshipyard Yard\n" +
                "planet Outpost\n\tspaceport Quiet\nsystem A\n\tpos 0 0\n\tobject Home\n\tobject Outpost\n" +
                "system B\n\tpos 100 0\nmission Parcel\n\tcargo documents 1\n\tdestination Home\n");
            var player = new PlayerState(data);
            player.SetCredits(10000);
            player.EnterSystem(data.Systems["A"]);
            Ship ship = data.BuildShip("Skiff");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            player.Land(data.Planets["Home"]);
            return player;
        }

        [TestCase("flight")]
        [TestCase("no yard")]
        [TestCase("remote")]
        [TestCase("destroyed")]
        [TestCase("committed")]
        [TestCase("hyperspace")]
        public void AnUnavailableShipCannotBeSoldOrChangeTheSave(string reason)
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = player.Flagship!;
            Ship spare = data.BuildShip("Skiff");
            spare.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(spare);
            if (reason == "flight") player.Depart();
            if (reason == "no yard") player.Land(data.Planets["Outpost"]);
            if (reason == "remote") ship.CurrentSystem = data.Systems["B"];
            if (reason == "destroyed") ship.SetLevels(hull: -1);
            if (reason is "committed" or "hyperspace")
            {
                ship.Attributes.Set("jump drive", 1);
                ship.Attributes.Set("fuel capacity", 500);
                ship.SetLevels(fuel: 500);
                ship.TargetSystem = data.Systems["B"];
                Assert.IsTrue(ship.TryCommitJump());
                if (reason == "hyperspace") ship.StepHyperspace();
            }
            string before = SaveGame.Write(player);
            Assert.AreNotEqual(TradeResult.Ok, Trading.SellShip(player, ship));
            Assert.AreEqual(before, SaveGame.Write(player));
        }

        [Test]
        public void SellingTheLastShipPreservesFreightAndCargoThroughReloadAndReplacement()
        {
            PlayerState player = Pilot(out GameData data);
            var log = new MissionLog(player);
            ActiveMission job = log.Accept(data.Missions["Parcel"])!;
            Assert.IsNotNull(job);
            Assert.AreEqual(5, player.Fleet.LoadCargo("Food", 5));
            player.AdjustBasis("Food", 500);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, player.Flagship!));
            Assert.IsFalse(player.PreviewTakeOff(log).CanDepart);
            Assert.IsFalse(player.TakeOff(log, acceptCargoLoss: true));
            Assert.AreEqual(6, player.Fleet.PortCargo!.Used);

            string saved = SaveGame.Write(player, log);
            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(saved, data, p => restoredLog = new MissionLog(p));
            Assert.AreEqual(saved, SaveGame.Write(restored, restoredLog));
            Assert.IsNull(restored.Flagship);
            Assert.IsEmpty(restored.Fleet.Ships);
            Assert.AreEqual(500, restored.CostBasis["Food"]);
            Assert.AreEqual(job.Id, restoredLog!.Active.Single().Id);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(restored, data, "Skiff", out Ship? bought));
            Assert.AreSame(bought, restored.Flagship);
            Assert.IsTrue(restored.TakeOff(restoredLog));
            Assert.AreEqual(5, bought!.Cargo.Count("Food"));
            Assert.AreEqual(1, bought.Cargo.MissionCargo[job.Id]);
        }

        [TestCase("remote")]
        [TestCase("parked")]
        [TestCase("disabled")]
        public void SellingTheFlagshipNeverPromotesAnUnavailableHull(string reason)
        {
            PlayerState player = Pilot(out GameData data);
            Ship previous = player.Flagship!;
            Ship spare = data.BuildShip("Skiff");
            spare.CurrentSystem = reason == "remote" ? data.Systems["B"] : player.CurrentSystem;
            spare.IsParked = reason == "parked";
            if (reason == "disabled") spare.SetLevels(hull: spare.MinimumHull - 1);
            player.Fleet.Add(spare);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, previous));
            Assert.IsNull(player.Flagship);
            Assert.IsFalse(player.TakeOff());
            PlayerState back = SaveGame.Read(SaveGame.Write(player), data);
            Assert.IsNull(back.Flagship);
            Assert.AreSame(spare.CurrentSystem, back.Fleet.Ships.Single().CurrentSystem);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(back, data, "Skiff", out Ship? bought));
            Assert.AreSame(bought, back.Flagship);
        }

        [Test]
        public void AShipyardCanBuyAnOwnedModelItDoesNotStock()
        {
            PlayerState player = Pilot(out GameData data);
            Ship ship = data.BuildShip("Other");
            ship.CurrentSystem = player.CurrentSystem;
            player.Fleet.Add(ship);
            long before = player.Credits;
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, ship));
            Assert.AreEqual(before + 500, player.Credits);
            Assert.AreEqual(1, player.Fleet.Ships.Count);
        }

        [Test]
        public void AnOverflowingSaleKeepsTheShipAndPurchaseHistory()
        {
            PlayerState player = Pilot(out GameData data);
            Assert.AreEqual(TradeResult.Ok, Trading.BuyShip(player, data, "Skiff", out Ship? bought));
            player.SetCredits(long.MaxValue - 500);
            string before = SaveGame.Write(player);
            Assert.AreEqual(TradeResult.CreditLimit, Trading.SellShip(player, bought!));
            Assert.AreEqual(before, SaveGame.Write(player));
            player.SetCredits(0);
            Assert.AreEqual(TradeResult.Ok, Trading.SellShip(player, bought!));
            Assert.AreEqual(1000, player.Credits);
        }
    }
}
