using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The jump autopilot: <see cref="ShipAi.PrepareForHyperspace"/> and
    /// <see cref="ShipAi.Stop"/> against upstream AI.cpp:2666 and AI.cpp:2732.
    /// </summary>
    /// <remarks>
    /// These exist because of a defect a player hit and no test could see: pressing the
    /// jump key left the ship turning at full rate, one direction, forever. It hid in
    /// two blind spots at once. The brake was written in the flight scene, so this
    /// suite could not reach it; and every fixture drive in the suite stated a
    /// "jump speed" while no drive in the generated galaxy did, so the one number that
    /// made the jump impossible was the one number no test ever read. Both halves are
    /// pinned below — the rule, and the content the player actually flies.
    /// </remarks>
    public class JumpAutopilotTests
    {
        /// <summary>Frames a pilot gets to line a jump up: ten seconds at 60fps.</summary>
        private const int Patience = 600;

        /// <summary>Turning further than this to line up is a spin, not a manoeuvre.</summary>
        private const double SpinThreshold = 720.0;

        private static string FixtureData(string driveAttributes) =>
            "ship \"Autopilot Fixture\"\n" +
            "\tattributes\n" +
            "\t\t\"mass\" 192\n" +
            "\t\t\"drag\" 1.8\n" +
            "\t\t\"fuel capacity\" 400\n" +
            "\t\t\"energy capacity\" 2000\n" +
            "\t\t\"energy generation\" 20\n" +
            "\toutfits\n" +
            "\t\t\"Fixture Drive\"\n" +
            "\t\t\"Fixture Jump Gear\"\n" +
            "\n" +
            "outfit \"Fixture Drive\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 24.075\n" +
            "\t\"turn\" 552\n" +
            "\n" +
            "outfit \"Fixture Jump Gear\"\n" +
            "\t\"mass\" 0\n" +
            driveAttributes +
            "\n" +
            "system Home\n" +
            "\tpos 0 0\n" +
            "\tlink Away\n" +
            "\n" +
            "system Away\n" +
            "\tpos 500 0\n" +
            "\tlink Home\n" +
            "\n" +
            // Inside a default jump drive's 100-unit reach, and reachable no other way.
            "system Unlinked\n" +
            "\tpos 0 80\n";

        private static Ship MakeFixture(string driveAttributes, string destination = "Away")
        {
            var data = new GameData();
            data.LoadText(FixtureData(driveAttributes), "autopilot-fixture");
            Ship ship = data.BuildShip("Autopilot Fixture");
            ship.SetLevels(energy: ship.MaxEnergy, fuel: 400.0);
            ship.CurrentSystem = data.Systems["Home"];
            ship.TargetSystem = data.Systems[destination];
            return ship;
        }

        /// <summary>
        /// Flies the autopilot until the jump commits, reporting how long it took and
        /// how far the ship turned getting there. Total rotation is the measurement
        /// that separates "lined up and left" from "spun in circles".
        /// </summary>
        private static (bool committed, int frames, double degreesTurned) FlyToJump(
            Ship ship, int patience = Patience)
        {
            double degrees = 0.0;
            for (int frame = 0; frame < patience; frame++)
            {
                if (ship.TryCommitJump())
                    return (true, frame, degrees);

                Command command = ShipAi.PrepareForHyperspace(ship);
                Angle before = ship.Facing;
                ship.Step(command);
                degrees += Math.Abs((ship.Facing - before).Degrees);
            }

            return (false, patience, degrees);
        }

        // --- The reported defect --------------------------------------------------

        [Test]
        public void ADriveThatStatesNoJumpSpeedLinesUpInsteadOfSpinning()
        {
            // Exactly the galaxy the player was flying: a hyperdrive with no
            // "jump speed", so IsReadyToJump accepts nothing short of a dead stop.
            //
            // Upstream cannot leave from here either — its velocity gate is a strict
            // `> attributes.Get("jump speed")` with no epsilon, and it only gets away
            // with that because every drive it ships states the number. What upstream
            // does NOT do is thrash: AI::Stop gives up at VELOCITY_ZERO and hands the
            // frame to TurnToward, so the ship settles pointing down the lane. The
            // brake this replaces instead chased an exact zero that thrust overshoots
            // and drag only ever approaches, and once velocity decayed far enough for
            // its unit vector to read as zero, TurnToward's zero-vector case turned the
            // ship at full rate, every frame, for as long as the player watched. That
            // was the reported symptom; this is the assertion that it is gone.
            //
            // Making the jump itself POSSIBLE is the data's job, and
            // UniverseTests.EveryDriveStatesTheSpeedItWillJumpAt is what keeps it done.
            Ship ship = MakeFixture("\t\"hyperdrive\" 1\n\t\"jump fuel\" 100\n");
            Assert.AreEqual(0.0, ship.JumpSpeedLimit, "the fixture reproduces the missing attribute");

            ship.Facing = new Angle(180.0);
            ship.Velocity = new Point(0.0, -4.0);

            (_, _, double degrees) = FlyToJump(ship);

            TestContext.WriteLine($"turned {degrees:F0} degrees; |v| {ship.Velocity.Length:F5}, " +
                                  $"facing {ship.Facing.Degrees:F1}, lane " +
                                  $"{Angle.FromPoint(ship.JumpDirection).Degrees:F1}");

            Assert.Less(degrees, SpinThreshold, "lining up is a turn, not a spin");
            Assert.Less(ship.Velocity.Length, ShipAi.VelocityZero, "it came to a stop");
            Assert.AreEqual(Angle.FromPoint(ship.JumpDirection).Degrees, ship.Facing.Degrees, 1.0,
                            "and settled pointing down the lane, ready for the moment it can go");
        }

        // --- The ordinary case ----------------------------------------------------

        [Test]
        public void AShipUnderWayBrakesTurnsAndJumps()
        {
            Ship ship = MakeFixture("\t\"hyperdrive\" 1\n\t\"jump speed\" .2\n\t\"jump fuel\" 100\n");

            // Under way and pointed the wrong way: the state a player is actually in
            // when they reach for the jump key.
            ship.Facing = new Angle(200.0);
            ship.Velocity = new Angle(200.0).Unit() * 6.0;

            (bool committed, int frames, double degrees) = FlyToJump(ship);

            TestContext.WriteLine($"committed in {frames} frames, turning {degrees:F0} degrees");
            Assert.IsTrue(committed, "the autopilot should fly the ship into its jump");
            Assert.LessOrEqual(ship.Velocity.Length, ship.JumpSpeedLimit, "it braked first");
            Assert.Less(degrees, SpinThreshold, "lining up is a turn, not a spin");
        }

        [Test]
        public void AJumpDriveOnlyStopsAndNeverFliesTheLane()
        {
            // A jump drive tears its hole where the ship is, so upstream stops it and
            // asks for no heading at all (AI.cpp:2784). Its facing must be left alone.
            Ship ship = MakeFixture(
                "\t\"jump drive\" 1\n\t\"jump speed\" .3\n\t\"jump fuel\" 200\n",
                destination: "Unlinked");

            Assert.IsTrue(ship.WouldUseJumpDrive, "an unlinked destination is jump-drive work");
            ship.Facing = new Angle(200.0);
            ship.Velocity = Point.Zero;

            Command command = ShipAi.PrepareForHyperspace(ship);
            Assert.AreEqual(0.0, command.Turn, "a stopped jump-drive ship has nothing to line up with");

            ship.Velocity = new Angle(200.0).Unit() * 6.0;
            (bool committed, int frames, _) = FlyToJump(ship);

            TestContext.WriteLine($"committed in {frames} frames");
            Assert.IsTrue(committed, "a jump drive should get away once it has stopped");
        }

        // --- AI::Stop itself ------------------------------------------------------

        [Test]
        public void StopReportsDoneAtTheLimitAndCheatsOnlyWhenAskedForZero()
        {
            Ship ship = MakeFixture("\t\"hyperdrive\" 1\n\t\"jump speed\" .2\n\t\"jump fuel\" 100\n");

            var command = new Command();
            ship.Velocity = new Point(0.15, 0.0);
            Assert.IsTrue(ShipAi.Stop(ship, ref command, 0.2), "already under the limit");
            Assert.IsFalse(command.Stop, "an explicit limit needs no dead-stop cheat");

            command = new Command();
            ship.Velocity = new Point(0.15, 0.0);
            Assert.IsFalse(ShipAi.Stop(ship, ref command, 0.0), "0.15 is not a dead stop");
            Assert.IsTrue(command.Stop, "asked for zero, upstream raises Command.Stop");

            command = new Command();
            ship.Velocity = new Point(ShipAi.VelocityZero / 2.0, 0.0);
            Assert.IsTrue(ShipAi.Stop(ship, ref command, 0.0),
                          "below VELOCITY_ZERO counts as stopped: upstream never chases an exact zero");
        }

        // --- The galaxy the game actually plays -----------------------------------

        [Test]
        public void EveryFlyableShipInTheReachCanFlyItselfIntoAJump()
        {
            GameData universe = GeneratedUniverse.Instance;
            StarSystem home = universe.Systems.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .First(s => s.Links.Count > 0 && universe.Systems.ContainsKey(s.Links.First()));
            StarSystem destination = universe.Systems[home.Links.First()];

            var stranded = new List<string>();
            int flown = 0;

            foreach (string model in universe.Ships.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                Ship ship = universe.BuildShip(model, out List<string> missing);
                if (missing.Count != 0 || ship.Thrust <= 0.0 || !ship.HasHyperdrive)
                    continue;

                ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                               energy: ship.MaxEnergy, fuel: ship.MaxFuel);
                ship.CurrentSystem = home;
                ship.TargetSystem = destination;
                if (!ship.CanReach(destination) || ship.Fuel < ship.JumpFuelCost)
                    continue;

                ship.Facing = new Angle(200.0);
                ship.Velocity = new Angle(200.0).Unit() * 6.0;

                flown++;
                (bool committed, int frames, double degrees) = FlyToJump(ship);
                if (!committed || degrees >= SpinThreshold)
                    stranded.Add($"{model} (jump speed {ship.JumpSpeedLimit}, " +
                                 $"{frames} frames, {degrees:F0} degrees turned)");
            }

            TestContext.WriteLine($"{flown} hyperdrive ships flown, {stranded.Count} stranded");
            foreach (string entry in stranded.Take(5))
                TestContext.WriteLine("  " + entry);

            Assert.Greater(flown, 0, "the galaxy should contain flyable ships with hyperdrives");
            Assert.IsEmpty(stranded, "every ship a player can fly must be able to leave the system");
        }
    }
}
