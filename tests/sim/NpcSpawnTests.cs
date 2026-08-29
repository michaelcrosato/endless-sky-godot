using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Mission NPCs as ships: placed where the template says, satisfied only when
    /// every hull has met the objective, and failed when the player destroys
    /// something they still owed an action to.
    /// </summary>
    /// <remarks>
    /// These pin the half of the mission system that had no coverage because it had no
    /// implementation. Parsing an npc block was tested; building a ship from one was
    /// not, because nothing did it - which is how 429 of the generated universe's 1000
    /// jobs came to be impossible to finish.
    /// </remarks>
    [TestFixture]
    public class NpcSpawnTests
    {
        /// <summary>
        /// A galaxy small enough to reason about: two linked systems, a world in each,
        /// two governments and one very simple hull.
        /// </summary>
        private static GameData Galaxy()
        {
            var data = new GameData();
            string[] lines =
            {
                "system \"Alpha\"",
                "\tpos 0 0",
                "\tgovernment \"Republic\"",
                "\tlink \"Beta\"",
                "\tobject \"Anvil\"",
                "\t\tsprite \"planet/rock\"",
                "\t\tdistance 400",
                "\t\tperiod 300",
                "system \"Beta\"",
                "\tpos 100 0",
                "\tgovernment \"Republic\"",
                "\tlink \"Alpha\"",
                "\tobject \"Bellows\"",
                "\t\tsprite \"planet/rock\"",
                "\t\tdistance 400",
                "\t\tperiod 300",
                "planet \"Anvil\"",
                "\tgovernment \"Republic\"",
                "\tspaceport `A quiet pad.`",
                "planet \"Bellows\"",
                "\tgovernment \"Republic\"",
                "\tspaceport `A louder pad.`",
                "government \"Republic\"",
                "\t\"player reputation\" 1",
                "government \"Raider\"",
                "\t\"player reputation\" -1000",
                "ship \"Skiff\"",
                "\tattributes",
                "\t\tcategory \"Light Freighter\"",
                "\t\tcost 100000",
                "\t\tmass 80",
                "\t\tdrag 1",
                "\t\t\"heat dissipation\" 0.7",
                "\t\t\"hull\" 400",
                "\t\t\"shields\" 200",
                "\t\t\"required crew\" 1",
                "\t\t\"bunks\" 3",
                "\t\t\"cargo space\" 20",
                "\t\t\"fuel capacity\" 300",
                "\t\t\"engine capacity\" 40",
                "\t\t\"weapon capacity\" 10",
                "\t\t\"outfit space\" 100",
                "\tgun 0 -10",
                "\tengine 0 20",
            };

            data.LoadText(string.Join("\n", lines) + "\n");
            return data;
        }

        private static Mission MissionFrom(params string[] lines)
        {
            var file = new DataFile(string.Join("\n", lines) + "\n", "mission.txt");
            var mission = new Mission(file.Nodes[0].Token(1));
            mission.Load(file.Nodes[0]);
            return mission;
        }

        /// <summary>A spawner whose every roll is zero, so placement is exact.</summary>
        private static NpcSpawner Fixed(GameData data) =>
            new NpcSpawner(data, random: _ => 0);

        [Test]
        public void ThreeShipLinesPlaceThreeHulls()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Bounty\"",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"",
                "\t\tship \"Skiff\"",
                "\t\tship \"Skiff\"");

            List<NpcInstance> placed =
                Fixed(data).Place(mission, data.Systems["Alpha"], data.Systems["Beta"]);

            Assert.That(placed, Has.Count.EqualTo(1));
            Assert.That(placed[0].Ships, Has.Count.EqualTo(3),
                "three ship lines have to become three hulls, not one");
        }

        [Test]
        public void SystemDestinationPlacesAtTheDestination()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Bounty\"",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tsystem destination",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data)
                .Place(mission, data.Systems["Alpha"], data.Systems["Beta"])[0];

            Assert.That(npc.System, Is.SameAs(data.Systems["Beta"]),
                "`system destination` is a keyword, not a system named destination");
            Assert.That(npc.Ships[0].CurrentSystem, Is.SameAs(data.Systems["Beta"]));
        }

        [Test]
        public void WithNoSystemNamedTheyAppearWhereTheJobWasTaken()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Bounty\"",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data)
                .Place(mission, data.Systems["Alpha"], data.Systems["Beta"])[0];

            Assert.That(npc.System, Is.SameAs(data.Systems["Alpha"]));
        }

        [Test]
        public void PlacedShipsFlyTheGovernmentTheTemplateNames()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Bounty\"",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data)
                .Place(mission, data.Systems["Alpha"], null)[0];

            Assert.That(npc.Ships[0].Government?.Name, Is.EqualTo("Raider"));
            Assert.That(npc.Ships[0].Hull, Is.GreaterThan(0.0), "placed hulls arrive whole");
        }

        [Test]
        public void ADerelictArrivesDisabled()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Salvage\"",
                "\tnpc board",
                "\t\tgovernment \"Raider\"",
                "\t\tpersonality derelict",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];

            Assert.That(npc.Template.IsDerelict, Is.True);
            Assert.That(npc.Ships[0].IsDisabled, Is.True,
                "a derelict is a hull with nobody aboard, not a fight");
        }

        [Test]
        public void KillingOneOfThreeDoesNotSatisfyTheBounty()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Bounty\"",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"",
                "\t\tship \"Skiff\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];

            npc.Record(npc.Ships[0], ShipEvent.Destroy);
            Assert.That(npc.HasSucceeded(data.Systems["Alpha"]), Is.False,
                "a bounty on three raiders is not paid for one kill");

            npc.Record(npc.Ships[1], ShipEvent.Destroy);
            npc.Record(npc.Ships[2], ShipEvent.Destroy);
            Assert.That(npc.HasSucceeded(data.Systems["Alpha"]), Is.True);
        }

        [Test]
        public void DestroyingAShipYouStillOweAnActionToFailsTheMission()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Salvage\"",
                "\tnpc board",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];

            npc.Record(npc.Ships[0], ShipEvent.Destroy);

            Assert.That(npc.HasFailed(), Is.True,
                "blow up the derelict and there is nothing left to board");
        }

        [Test]
        public void BoardingTheDerelictSatisfiesIt()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Salvage\"",
                "\tnpc board",
                "\t\tgovernment \"Raider\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];
            npc.Record(npc.Ships[0], ShipEvent.Board);

            Assert.That(npc.HasFailed(), Is.False);
            Assert.That(npc.HasSucceeded(data.Systems["Alpha"]), Is.True);
        }

        [Test]
        public void AnEscortThatIsDestroyedFailsTheMission()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Escort\"",
                "\tnpc save accompany",
                "\t\tgovernment \"Republic\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];
            npc.Record(npc.Ships[0], ShipEvent.Destroy);

            Assert.That(npc.HasFailed(), Is.True);
        }

        [Test]
        public void AnEscortLeftBehindIsNotAccompanying()
        {
            GameData data = Galaxy();
            Mission mission = MissionFrom(
                "mission \"Escort\"",
                "\tnpc save accompany",
                "\t\tgovernment \"Republic\"",
                "\t\tship \"Skiff\"");

            NpcInstance npc = Fixed(data).Place(mission, data.Systems["Alpha"], null)[0];

            Assert.That(npc.HasSucceeded(data.Systems["Alpha"]), Is.True,
                "still together");
            Assert.That(npc.HasSucceeded(data.Systems["Beta"]), Is.False,
                "the player jumped and left the convoy behind");
        }

        // --- The whole job, start to finish ---------------------------------------

        /// <summary>A player standing on Anvil in a Skiff, ready to take work.</summary>
        private static PlayerState Pilot(GameData data)
        {
            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Skiff");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Alpha"]);
            player.Land(data.Planets["Anvil"]);
            return player;
        }

        private static GameData GalaxyWith(params string[] extra)
        {
            GameData data = Galaxy();
            data.LoadText(string.Join("\n", extra) + "\n");
            return data;
        }

        [Test]
        public void ABountyCanActuallyBeFlownAndCollected()
        {
            GameData data = GalaxyWith(
                "mission \"Clear the lane\"",
                "\tdestination \"Bellows\"",
                "\tto offer",
                "\t\thas \"flagship landed\"",
                "\ton complete",
                "\t\tpayment 90000",
                "\tnpc kill",
                "\t\tgovernment \"Raider\"",
                "\t\tsystem destination",
                "\t\tship \"Skiff\"",
                "\t\tship \"Skiff\"");

            PlayerState player = Pilot(data);
            var log = new MissionLog(player, Fixed(data));

            ActiveMission taken = log.Accept(data.Missions["Clear the lane"])!;
            Assert.That(taken.Npcs.Single().Ships, Has.Count.EqualTo(2),
                "accepting the job is what puts the raiders in the galaxy");

            // Fly there. The raiders are at the destination, not where it was taken.
            player.Depart();
            player.EnterSystem(data.Systems["Beta"]);
            Assert.That(log.NpcShipsIn(data.Systems["Beta"]).Count(), Is.EqualTo(2),
                "and they are waiting in the system the job pointed at");
            Assert.That(log.NpcShipsIn(data.Systems["Alpha"]), Is.Empty);

            List<Ship> raiders = log.NpcShipsIn(data.Systems["Beta"]).ToList();
            player.Land(data.Planets["Bellows"]);
            Assert.That(log.CanComplete(taken), Is.False, "nothing has been shot yet");

            log.ReportShipEvent(raiders[0], ShipEvent.Destroy);
            Assert.That(log.CanComplete(taken), Is.False, "one of two is not the job");

            log.ReportShipEvent(raiders[1], ShipEvent.Destroy);
            Assert.That(log.CanComplete(taken), Is.True);

            long before = player.Credits;
            Assert.That(log.Complete(taken), Is.True);
            Assert.That(player.Credits - before, Is.EqualTo(90_000));
        }

        [Test]
        public void AnEscortJumpsWithTheFlagship()
        {
            GameData data = GalaxyWith(
                "mission \"See them home\"",
                "\tdestination \"Bellows\"",
                "\tto offer",
                "\t\thas \"flagship landed\"",
                "\ton complete",
                "\t\tpayment 40000",
                "\tnpc save accompany",
                "\t\tgovernment \"Republic\"",
                "\t\tship \"Skiff\"");

            PlayerState player = Pilot(data);
            var log = new MissionLog(player, Fixed(data));
            ActiveMission taken = log.Accept(data.Missions["See them home"])!;

            Ship convoy = taken.Npcs.Single().Ships.Single();
            Assert.That(convoy.CurrentSystem, Is.SameAs(data.Systems["Alpha"]),
                "an escort starts where the player does");

            player.Depart();
            IReadOnlyList<Ship> came =
                log.CarryAccompanying(data.Systems["Alpha"], data.Systems["Beta"]);
            player.EnterSystem(data.Systems["Beta"]);

            Assert.That(came, Has.Count.EqualTo(1));
            Assert.That(convoy.CurrentSystem, Is.SameAs(data.Systems["Beta"]));

            player.Land(data.Planets["Bellows"]);
            Assert.That(log.CanComplete(taken), Is.True,
                "arriving together is the whole objective");
        }

        [Test]
        public void ADisabledEscortIsLeftBehindAndTheJobStaysOpen()
        {
            GameData data = GalaxyWith(
                "mission \"See them home\"",
                "\tdestination \"Bellows\"",
                "\tto offer",
                "\t\thas \"flagship landed\"",
                "\tnpc save accompany",
                "\t\tgovernment \"Republic\"",
                "\t\tship \"Skiff\"");

            PlayerState player = Pilot(data);
            var log = new MissionLog(player, Fixed(data));
            ActiveMission taken = log.Accept(data.Missions["See them home"])!;

            Ship convoy = taken.Npcs.Single().Ships.Single();
            convoy.Disable();

            player.Depart();
            IReadOnlyList<Ship> came =
                log.CarryAccompanying(data.Systems["Alpha"], data.Systems["Beta"]);
            player.EnterSystem(data.Systems["Beta"]);
            player.Land(data.Planets["Bellows"]);

            Assert.That(came, Is.Empty, "a crippled ship cannot make the jump");
            Assert.That(convoy.CurrentSystem, Is.SameAs(data.Systems["Alpha"]));
            Assert.That(log.CanComplete(taken), Is.False);
            Assert.That(taken.Outcome, Is.EqualTo(MissionOutcome.Active),
                "left behind is not the same as lost - it can still be gone back for");
        }
    }
}
