using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Running a mission end to end: taking it on one world, carrying it, and handing
    /// it in on another. Ports the lifecycle from upstream <c>PlayerInfo</c> and
    /// <c>Mission::IsSatisfied</c>.
    /// </summary>
    [TestFixture]
    public class MissionLogTests
    {
        private const string Universe =
            "ship \"Hauler\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 900\n" +
            "\t\t\"cargo space\" 50\n\t\t\"energy capacity\" 100\n\t\t\"cost\" 100000\n" +
            "planet \"Home\"\n\tgovernment \"Republic\"\n\tspaceport `Busy.`\n" +
            "planet \"Away\"\n\tgovernment \"Republic\"\n\tspaceport `Quiet.`\n" +
            "system \"Sol\"\n\tpos 0 0\n" +
            "\tobject \"Home\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n" +
            "\tobject \"Away\"\n\t\tsprite planet/desert\n\t\tdistance 900\n\t\tperiod 400\n" +
            "mission \"Deliver Grain\"\n" +
            "\tname `Deliver Grain`\n" +
            "\tdestination \"Away\"\n" +
            "\tcargo \"Grain\" 20\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n" +
            "\ton accept\n\t\tpayment 1000\n" +
            "\ton complete\n\t\tpayment 50000\n\t\tset \"grain delivered\"\n" +
            "mission \"Urgent Parcel\"\n" +
            "\tname `Urgent Parcel`\n" +
            "\tdestination \"Away\"\n" +
            "\tdeadline 5\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n" +
            "\ton complete\n\t\tpayment 20000\n" +
            "\ton fail\n\t\tset \"parcel lost\"\n";

        private static MissionLog Start(out PlayerState player, out GameData data)
        {
            data = new GameData();
            data.LoadText(Universe);

            player = new PlayerState(data);
            Ship hauler = data.BuildShip("Hauler");
            hauler.BuildMounts();
            player.Fleet.Add(hauler);
            player.Fleet.SetFlagship(hauler);
            player.SetCredits(10_000);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);
            return new MissionLog(player);
        }

        // --- Availability ---------------------------------------------------------

        [Test]
        public void AWorldOffersTheMissionsWhoseGatesPass()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);

            var available = log.Available(data).Select(m => m.Name).ToList();
            CollectionAssert.Contains(available, "Deliver Grain");

            // In flight, the "flagship landed" gate closes.
            player.Depart();
            Assert.IsEmpty(log.Available(data).ToList());
        }

        [Test]
        public void AMissionAlreadyTakenIsNotOfferedAgain()
        {
            MissionLog log = Start(out _, out GameData data);
            log.Accept(data.Missions["Deliver Grain"]);

            CollectionAssert.DoesNotContain(
                log.Available(data).Select(m => m.Name).ToList(), "Deliver Grain");
        }

        [Test]
        public void ACompletedNonRepeatingMissionIsNotOfferedAgain()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;
            player.Land(data.Planets["Away"]);
            Assert.IsTrue(log.Complete(taken));

            player.Land(data.Planets["Home"]);
            CollectionAssert.DoesNotContain(
                log.Available(data).Select(m => m.Name).ToList(), "Deliver Grain");
        }

        // --- Accepting ------------------------------------------------------------

        [Test]
        public void AcceptingLoadsCargoAndPaysTheAdvance()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);

            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            Assert.AreEqual(20, taken.CargoLoaded, "the hold should take the whole load");
            Assert.AreEqual(20, player.Fleet.CargoCount("Grain"));
            Assert.AreEqual(11_000, player.Credits, "the advance is paid on acceptance");
            Assert.AreEqual(1, player.Conditions.Get("mission: Deliver Grain"));
        }

        [Test]
        public void AHoldTooSmallLoadsWhatFitsAndSaysSo()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);

            // Fill the hold so only a little room is left.
            player.Fleet.LoadCargo("Ballast", 40);

            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            Assert.AreEqual(10, taken.CargoLoaded, "only what fits goes aboard");
            Assert.AreEqual(0, player.Fleet.CargoFree());
        }

        // --- Completing -----------------------------------------------------------

        [Test]
        public void AMissionCannotBeHandedInAtTheWrongWorld()
        {
            MissionLog log = Start(out _, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            Assert.IsFalse(log.CanComplete(taken), "still standing on the pickup world");
            Assert.IsFalse(log.Complete(taken));
        }

        [Test]
        public void AMissionCannotBeHandedInFromOrbit()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;
            player.Depart();

            Assert.IsFalse(log.CanComplete(taken));
        }

        [Test]
        public void DeliveringAtTheDestinationPaysAndUnloads()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;
            long afterAdvance = player.Credits;

            player.Land(data.Planets["Away"]);

            Assert.IsTrue(log.CanComplete(taken));
            Assert.IsTrue(log.Complete(taken));

            Assert.AreEqual(afterAdvance + 50_000, player.Credits);
            Assert.AreEqual(0, player.Fleet.CargoCount("Grain"), "the cargo is handed over");
            Assert.AreEqual(1, player.Conditions.Get("grain delivered"));
            Assert.AreEqual(MissionOutcome.Completed, taken.Outcome);
            Assert.IsEmpty(log.Active);
            CollectionAssert.Contains(log.Finished, taken);
        }

        [Test]
        public void ThrowingTheCargoOverboardBlocksCompletion()
        {
            // The cargo has to still be aboard on arrival.
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            player.Fleet.UnloadCargo("Grain", 20);
            player.Land(data.Planets["Away"]);

            Assert.IsFalse(log.CanComplete(taken));
        }

        [Test]
        public void CompletingClearsTheActiveConditionAndRecordsTheSuccess()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;
            player.Land(data.Planets["Away"]);
            log.Complete(taken);

            Assert.AreEqual(0, player.Conditions.Get("mission: Deliver Grain"));
            Assert.AreEqual(1, player.Conditions.Get("mission completed: Deliver Grain"));
        }

        // --- Deadlines and failure ------------------------------------------------

        [Test]
        public void ADeadlineThatPassesFailsTheMission()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Urgent Parcel"])!;

            Assert.IsNotNull(taken.Deadline);
            Assert.IsEmpty(log.Step(), "not overdue yet");

            player.AdvanceDays(10);
            var ended = log.Step();

            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(MissionOutcome.Failed, taken.Outcome);
            Assert.AreEqual(1, player.Conditions.Get("parcel lost"), "the fail action fires");
            Assert.IsEmpty(log.Active);
        }

        [Test]
        public void AMissionWithNoDeadlineNeverExpires()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            Assert.IsNull(taken.Deadline);
            player.AdvanceDays(10_000);

            Assert.IsEmpty(log.Step());
            Assert.AreEqual(MissionOutcome.Active, taken.Outcome);
        }

        [Test]
        public void AbortingGivesBackTheHoldAndFiresTheFailAction()
        {
            MissionLog log = Start(out PlayerState player, out GameData data);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            log.Abort(taken);

            Assert.AreEqual(MissionOutcome.Aborted, taken.Outcome);
            Assert.AreEqual(0, player.Fleet.CargoCount("Grain"));
            Assert.AreEqual(0, player.Conditions.Get("mission: Deliver Grain"));
        }

        // --- NPC objectives -------------------------------------------------------

        [Test]
        public void AMissionWithAnNpcToKillIsNotCompleteUntilItIsDead()
        {
            var data = new GameData();
            data.LoadText(Universe +
                "mission \"Bounty\"\n" +
                "\tdestination \"Away\"\n" +
                "\tto offer\n\t\thas \"flagship landed\"\n" +
                "\ton complete\n\t\tpayment 90000\n" +
                "\tnpc kill\n\t\tgovernment \"Pirate\"\n\t\tship \"Hauler\"\n");

            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Hauler");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);

            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Bounty"])!;
            player.Land(data.Planets["Away"]);

            Assert.IsFalse(log.CanComplete(taken), "the target is still flying");

            log.RecordNpcEvent(taken, taken.Mission.Npcs.Single(), ShipEvent.Destroy);

            Assert.IsTrue(log.CanComplete(taken), "and now it is not");
            Assert.IsTrue(log.Complete(taken));
        }

        [Test]
        public void LosingAnEscortedNpcFailsTheMission()
        {
            var data = new GameData();
            data.LoadText(Universe +
                "mission \"Escort Duty\"\n" +
                "\tdestination \"Away\"\n" +
                "\tto offer\n\t\thas \"flagship landed\"\n" +
                "\ton fail\n\t\tset \"escort lost\"\n" +
                "\tnpc save\n\t\tgovernment \"Merchant\"\n\t\tship \"Hauler\"\n");

            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Hauler");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);

            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Escort Duty"])!;

            log.RecordNpcEvent(taken, taken.Mission.Npcs.Single(), ShipEvent.Destroy);
            var ended = log.Step();

            Assert.AreEqual(1, ended.Count, "losing the ship being escorted ends it");
            Assert.AreEqual(MissionOutcome.Failed, taken.Outcome);
            Assert.AreEqual(1, player.Conditions.Get("escort lost"));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void ARealJobCanBeTakenFromARealWorld()
        {
            GameData data = UpstreamData.Instance;

            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Star Barge");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.SetCredits(100_000);

            Planet start = data.Planets.Values.First(p => p.HasSpaceport && p.IsInhabited);
            player.EnterSystem(data.Systems.Values
                .FirstOrDefault(s => s.AllObjects().Any(o => o.PlanetName == start.Name)));
            player.Land(start);

            var log = new MissionLog(player);
            var offers = log.Available(data).ToList();

            TestContext.WriteLine($"{start.Name} offers {offers.Count} missions; " +
                                  $"e.g. {string.Join(", ", offers.Take(3).Select(m => m.DisplayName))}");

            Assert.IsNotEmpty(offers, "a real world should have something on its board");

            ActiveMission? taken = log.Accept(offers[0]);
            Assert.IsNotNull(taken);
            Assert.AreEqual(1, log.Active.Count);
            Assert.AreEqual(1, player.Conditions.Get($"mission: {offers[0].Name}"));
        }
    }
}
