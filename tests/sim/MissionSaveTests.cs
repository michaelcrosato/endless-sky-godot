using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class MissionSaveTests
    {
        private GameData _data = null!;
        private PlayerState _player = null!;
        private MissionLog _log = null!;

        [SetUp]
        public void SetUp()
        {
            _data = new GameData();
            _data.LoadText(
                "government Republic\ngovernment Pirate\n\t\"player reputation\" -100\n" +
                "planet Home\n\tspaceport Busy\nplanet Away\n\tspaceport Quiet\n" +
                "system Sol\n\tpos 0 0\n\tlink Vega\n\tobject Home\n" +
                "system Vega\n\tpos 100 0\n\tlink Sol\n\tobject Away\n" +
                "outfit Blaster\n\t\"gun ports\" -1\n\tweapon\n\t\tvelocity 10\n" +
                "\t\tlifetime 30\n\t\t\"hull damage\" 5\n" +
                "ship Raider\n\tattributes\n\t\tmass 80\n\t\thull 600\n" +
                "\t\tshields 300\n\t\t\"energy capacity\" 100\n\t\t\"fuel capacity\" 500\n" +
                "\t\t\"cargo space\" 40\n\t\tbunks 10\n\t\t\"gun ports\" 1\n" +
                "\tgun 0 -10\n\toutfits\n\t\tBlaster\n");
            _player = new PlayerState(_data);
            _player.SetDate(new DateTime(3014, 3, 21));
            _player.Fleet.Add(_data.BuildShip("Raider"));
            _player.EnterSystem(_data.Systems["Sol"]);
            _player.Flagship!.CurrentSystem = _player.CurrentSystem;
            _player.Land(_data.Planets["Home"]);
            _log = new MissionLog(_player, new NpcSpawner(_data, random: _ => 0));
        }

        private ActiveMission Take(string objective = "kill", string location = "")
        {
            _data.LoadText("mission Test\n\tdestination Home\n\tcargo Grain 3\n\tpassengers 2\n" +
                "\ton accept\n\t\tpayment 100\n\ton complete\n\t\tpayment 500\n" +
                $"\tnpc {objective}\n\t\tgovernment Pirate\n" + location +
                "\t\tship Raider First\n\t\tship Raider Second\n");
            return _log.Accept(_data.Missions["Test"])!;
        }

        private (PlayerState Player, MissionLog Log) Reload()
        {
            MissionLog? log = null;
            PlayerState player = SaveGame.Read(SaveGame.Write(_player, _log), _data,
                restored => log = new MissionLog(restored));
            return (player, log!);
        }

        [Test]
        public void APartlyWonBountyKeepsEveryShipsOwnProgress()
        {
            ActiveMission taken = Take();
            Ship killed = taken.Npcs.Single().Ships[0];
            killed.SetLevels(hull: -1);
            _log.ReportShipEvent(killed, ShipEvent.Disable | ShipEvent.Destroy);

            var (player, log) = Reload();
            ActiveMission back = log.Active.Single();
            Assert.AreEqual(1, back.Npcs.Count, "loading must retain the mission's real ships");
            NpcInstance npc = back.Npcs.Single();
            Assert.AreEqual(2, npc.Ships.Count, "dead ships retain their objective history");
            Assert.AreEqual(1, npc.Survivors.Count());
            Assert.AreEqual("Second", npc.Survivors.Single().GivenName);
            Assert.AreEqual(ShipEvent.Disable | ShipEvent.Destroy, npc.EventsFor(npc.Ships[0]));
            Assert.AreEqual(ShipEvent.None, npc.EventsFor(npc.Ships[1]));
            Assert.IsFalse(log.CanComplete(back), "one kill cannot satisfy a two-ship bounty");

            Ship remaining = npc.Ships[1];
            remaining.SetLevels(hull: -1);
            Assert.AreEqual(1, log.ReportShipEvent(remaining, ShipEvent.Destroy).Count);
            long before = player.Credits;
            Assert.IsTrue(log.Complete(back), "the restored target must still belong to this job");
            Assert.AreEqual(before + 500, player.Credits);
            Assert.IsFalse(log.Complete(back), "a completed bounty cannot pay twice");
        }

        [Test]
        public void MissionShipConditionLoadoutAndAllegianceSurvive()
        {
            ActiveMission taken = Take(location: "\t\tsystem Sol\n\t\tplanet Home\n");
            Ship original = taken.Npcs.Single().Ships[1];
            original.CurrentSystem = _data.Systems["Vega"];
            original.Position = new Point(320.5, -185.25);
            original.Velocity = new Point(1.25, -0.5);
            original.Facing = new Angle(93.0);
            original.Crew = 7;
            original.LoadCargo("Metal", 9);
            original.SetLevels(shields: 21.5, hull: 12, energy: 9.5, heat: 300, fuel: 45.25);
            _log.ReportShipEvent(original, ShipEvent.Disable | ShipEvent.Board);

            var (_, log) = Reload();
            Assert.AreEqual(1, log.Active.Single().Npcs.Count);
            NpcInstance npc = log.Active.Single().Npcs.Single();
            Ship back = npc.Ships.Single(s => s.GivenName == "Second");
            Assert.Multiple(() =>
            {
                Assert.AreSame(_data.Systems["Sol"], npc.System, "placement and current system differ");
                Assert.AreEqual("Home", npc.Planet);
                Assert.AreSame(_data.Systems["Vega"], back.CurrentSystem);
                Assert.AreSame(_data.Governments["Pirate"], back.Government);
                Assert.AreEqual(original.Position, back.Position);
                Assert.AreEqual(original.Velocity, back.Velocity);
                Assert.AreEqual(original.Facing, back.Facing);
                Assert.AreEqual(7, back.Crew);
                Assert.AreEqual(21.5, back.Shields);
                Assert.AreEqual(12, back.Hull);
                Assert.AreEqual(9.5, back.Energy);
                Assert.AreEqual(300, back.Heat);
                Assert.AreEqual(45.25, back.Fuel);
                Assert.IsTrue(back.IsDisabled);
                Assert.AreEqual(9, back.Cargo.Count("Metal"));
                Assert.AreEqual(1, back.Outfits.Count);
                Assert.AreEqual(1, back.Mounts.Count(m => !m.IsEmpty));
                Assert.AreEqual(ShipEvent.Disable | ShipEvent.Board, npc.EventsFor(back));
            });
        }

        [Test]
        public void AnEscortKeepsItsLocationAfterJumpingAndReloading()
        {
            Take("accompany save");
            _log.CarryAccompanying(_data.Systems["Sol"], _data.Systems["Vega"]);
            _player.EnterSystem(_data.Systems["Vega"]);

            var (player, log) = Reload();
            ActiveMission back = log.Active.Single();
            Assert.AreEqual(2, log.NpcShipsIn(player.CurrentSystem).Count());
            Assert.IsEmpty(log.NpcShipsIn(_data.Systems["Sol"]));
            Assert.IsTrue(back.Npcs.Single().HasSucceeded(player.CurrentSystem));
            Assert.AreEqual(2, log.CarryAccompanying(player.CurrentSystem, _data.Systems["Sol"]).Count);
            player.EnterSystem(_data.Systems["Sol"]);
            player.Land(_data.Planets["Home"]);
            Assert.IsTrue(log.Complete(back));
        }

        [Test]
        public void ADestroyedUnboardedTargetStillFailsTheMissionAfterLoading()
        {
            ActiveMission taken = Take("board");
            Ship killed = taken.Npcs.Single().Ships[0];
            killed.SetLevels(hull: -1);
            _log.ReportShipEvent(killed, ShipEvent.Destroy);

            var (_, log) = Reload();
            Assert.AreEqual(1, log.Step().Count);
            Assert.AreEqual(MissionOutcome.Failed, log.Finished.Single().Outcome);
        }

        [Test]
        public void RestoringMissionPayloadDoesNotRepeatAcceptance()
        {
            ActiveMission taken = Take();
            var (player, log) = Reload();
            Assert.Multiple(() =>
            {
                Assert.AreEqual(taken.PassengersCarried, log.Active.Single().PassengersCarried);
                Assert.AreEqual(3, player.Fleet.PortCargo!.MissionCargo[taken.Id]);
                Assert.AreEqual(0, player.Fleet.CargoCount("Grain"));
                Assert.AreEqual(_player.Credits, player.Credits);
                Assert.AreEqual(_player.Conditions.Get("Test: active"), player.Conditions.Get("Test: active"));
                Assert.AreEqual(_player.Conditions.Get("Test: offered"), player.Conditions.Get("Test: offered"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OldSavesRebuildMissingTargetsAfterResolvingThePlayersLocation(bool atDestination)
        {
            Take(location: atDestination ? "\t\tsystem destination\n" : "");
            MissionLog? log = null;
            // Mission nodes can precede placement and the date. Loading must use the
            // restored pilot, and must not execute the job's advance payment again.
            PlayerState player = SaveGame.Read(
                "mission Test\n\tdestination Home\nship Raider\nsystem Vega\ndate 7 4 3014\n",
                _data, restored => log = new MissionLog(restored));
            StarSystem expected = _data.Systems[atDestination ? "Sol" : "Vega"];
            Assert.AreEqual(2, log!.NpcShipsIn(expected).Count());
            Assert.AreEqual(player.Date, log.Active.Single().Accepted);
            Assert.AreEqual(0, player.Credits);
        }

        [Test]
        public void AnExplicitlyEmptyNpcDoesNotRegenerateItsShips()
        {
            ActiveMission taken = Take();
            taken.Npcs.Clear();
            taken.Npcs.Add(new NpcInstance(taken.Mission.Npcs[0], _data.Systems["Sol"], null, null));

            var (_, log) = Reload();
            Assert.AreEqual(1, log.Active.Single().Npcs.Count);
            Assert.IsEmpty(log.Active.Single().Npcs.Single().Ships);
        }

        [Test]
        public void AggregateOnlyProgressStillWorksWithoutPhysicalInstances()
        {
            ActiveMission taken = Take();
            taken.Npcs.Clear();
            _log.RecordNpcEvent(taken, taken.Mission.Npcs[0], ShipEvent.Destroy);

            var (_, log) = Reload();
            ActiveMission back = log.Active.Single();
            Assert.IsEmpty(back.Npcs, "a saved aggregate-only log must not roll new ships");
            Assert.IsTrue(log.CanComplete(back));
        }

        [Test]
        public void MissionStateStabilizesAcrossRepeatedSaves()
        {
            ActiveMission taken = Take();
            _log.ReportShipEvent(taken.Npcs.Single().Ships[1], ShipEvent.Disable);
            var (player, log) = Reload();
            string first = SaveGame.Write(player, log);
            MissionLog? secondLog = null;
            PlayerState second = SaveGame.Read(first, _data, p => secondLog = new MissionLog(p));
            Assert.AreEqual(first, SaveGame.Write(second, secondLog));
            Assert.AreEqual(2, secondLog!.Active.Single().Npcs.Single().Ships.Count);
        }
    }
}
