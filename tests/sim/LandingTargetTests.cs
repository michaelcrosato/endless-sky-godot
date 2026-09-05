using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Choosing somewhere to land and flying there: <see cref="ShipAi.SelectLandingTarget"/>
    /// and <see cref="ShipAi.MoveToPlanet"/>, against upstream AI.cpp:4590 and AI.cpp:2592.
    /// </summary>
    /// <remarks>
    /// Reported from play: "I am finding it hard to know which planets I can land on."
    /// Landing used to require the player to already BE inside a world's landing radius
    /// and nearly stopped — there was no target, no autopilot and nothing on screen
    /// saying which of a system's bodies would even accept a ship. These pin the two
    /// halves that live in the simulation: which object the pilot gets when they ask,
    /// and whether the ship can actually fly itself there.
    /// </remarks>
    public class LandingTargetTests
    {
        /// <summary>
        /// A star at the centre, a bare rock close in, and an inhabited world further
        /// out. The ordering is deliberate: the nearest body is the one a naive
        /// "closest object" rule would pick, and it is the wrong answer.
        /// </summary>
        private const string SystemData =
            "system Testbed\n" +
            "\tpos 0 0\n" +
            "\tobject\n" +
            "\t\tsprite star/g5\n" +
            "\t\tdistance 0\n" +
            "\t\tperiod 10\n" +
            "\tobject \"Bare Rock\"\n" +
            "\t\tsprite planet/rock\n" +
            "\t\tdistance 1000\n" +
            "\t\tperiod 100\n" +
            "\tobject \"Port World\"\n" +
            "\t\tsprite planet/ocean\n" +
            "\t\tdistance 3000\n" +
            "\t\tperiod 300\n" +
            "\tobject \"Far Port\"\n" +
            "\t\tsprite planet/desert\n" +
            "\t\tdistance 6000\n" +
            "\t\tperiod 600\n" +
            "\n" +
            "planet \"Bare Rock\"\n" +
            "\tattributes uninhabited\n" +
            "\n" +
            "planet \"Port World\"\n" +
            "\tspaceport `A working port.`\n" +
            "\n" +
            "planet \"Far Port\"\n" +
            "\tspaceport `Another working port.`\n";

        private const string ShipData =
            "ship \"Lander\"\n" +
            "\tattributes\n" +
            "\t\t\"mass\" 192\n" +
            "\t\t\"drag\" 1.8\n" +
            "\t\t\"fuel capacity\" 400\n" +
            "\t\t\"energy capacity\" 4000\n" +
            "\t\t\"energy generation\" 40\n" +
            "\toutfits\n" +
            "\t\t\"Lander Drive\"\n" +
            "\n" +
            "outfit \"Lander Drive\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 24.075\n" +
            "\t\"turn\" 552\n";

        private static (Ship ship, StarSystem system) MakeFixture()
        {
            var data = new GameData();
            data.LoadText(SystemData + "\n" + ShipData, "landing-fixture");
            StarSystem system = data.Systems["Testbed"];
            system.SetDate(0.0);

            Ship ship = data.BuildShip("Lander");
            ship.SetLevels(energy: ship.MaxEnergy, fuel: 400.0);
            ship.CurrentSystem = system;
            // At the system centre: distance to each world is its orbital radius.
            ship.Position = Point.Zero;
            return (ship, system);
        }

        private static StellarObject Find(StarSystem system, string name) =>
            system.AllObjects().First(o => o.PlanetName == name);

        // --- What counts as somewhere to land -------------------------------------

        [Test]
        public void ScenerySuchAsAStarIsNeverALandingTarget()
        {
            (Ship ship, StarSystem system) = MakeFixture();
            StellarObject star = system.AllObjects().First(o => o.IsStar);

            Assert.IsFalse(ship.CanEverLandOn(star), "a star is scenery, not a destination");
            Assert.IsTrue(ship.CanEverLandOn(Find(system, "Bare Rock")),
                          "an uninhabited world is still somewhere a ship can put down");
            Assert.IsFalse(ship.CanEverLandOn(null));
        }

        // --- Which one you get ----------------------------------------------------

        [Test]
        public void TheFirstAskPrefersAWorldThatCanRefuelYouOverANearerRock()
        {
            // Upstream pushes anything that cannot recharge fuel 10,000 units down the
            // ranking (AI.cpp:4677), so "nearest" means nearest USEFUL world. Bare Rock
            // is 1,000 out and Port World 3,000; the rock's penalty puts it at 11,000.
            (Ship ship, _) = MakeFixture();

            LandingChoice choice = ShipAi.SelectLandingTarget(ship);

            Assert.AreEqual("Port World", choice.Target?.PlanetName,
                            "the nearest body is a rock; the nearest PORT is what the pilot meant");
            Assert.AreSame(choice.Target, ship.TargetStellar, "the choice is recorded on the ship");
            StringAssert.Contains("Port World", choice.Message);
        }

        [Test]
        public void AmongEquallyUsefulWorldsTheNearestWins()
        {
            (Ship ship, StarSystem system) = MakeFixture();

            // From out past Far Port, it is Far Port that is closest.
            ship.Position = Find(system, "Far Port").Position * 1.05;

            LandingChoice choice = ShipAi.SelectLandingTarget(ship);

            Assert.AreEqual("Far Port", choice.Target?.PlanetName);
        }

        [Test]
        public void ASystemWithNowhereToLandSaysSoRatherThanSelectingNothingQuietly()
        {
            var data = new GameData();
            data.LoadText(
                "system Empty\n\tpos 0 0\n\tobject\n\t\tsprite star/g5\n\t\tdistance 0\n\t\tperiod 10\n"
                + "\n" + ShipData, "empty-fixture");
            StarSystem system = data.Systems["Empty"];
            system.SetDate(0.0);

            Ship ship = data.BuildShip("Lander");
            ship.CurrentSystem = system;

            LandingChoice choice = ShipAi.SelectLandingTarget(ship);

            Assert.IsFalse(choice.Succeeded);
            Assert.IsNull(ship.TargetStellar);
            Assert.IsNotEmpty(choice.Message, "the pilot is told why nothing happened");
        }

        [Test]
        public void DriftingOverAWorldMeansThatWorldEvenWhenAPortIsNearer()
        {
            // Upstream AI.cpp:4604. The player who has already flown themselves onto a
            // rock has been explicit; a selection that answers "actually, the port" and
            // turns the ship around is overriding them at the worst possible moment.
            (Ship ship, StarSystem system) = MakeFixture();
            StellarObject rock = Find(system, "Bare Rock");
            ship.Position = rock.Position;
            ship.Velocity = Point.Zero;

            Assert.AreEqual("Bare Rock", ShipAi.SelectLandingTarget(ship).Target?.PlanetName);

            // Under way, the same position is just somewhere the ship is passing.
            ship.TargetStellar = null;
            ship.Velocity = new Point(ShipAi.HoveringSpeed * 2.0, 0.0);
            Assert.AreEqual("Port World", ShipAi.SelectLandingTarget(ship).Target?.PlanetName,
                            "passing overhead at speed is not a decision to land");
        }

        // --- Cycling --------------------------------------------------------------

        [Test]
        public void AskingAgainCyclesThroughEveryLandableAndWrapsAround()
        {
            (Ship ship, _) = MakeFixture();

            var seen = new List<string>();
            for (int press = 0; press < 4; press++)
            {
                LandingChoice choice = ShipAi.SelectLandingTarget(ship, cycle: press > 0);
                seen.Add(choice.Target?.PlanetName ?? "(none)");
            }

            TestContext.WriteLine("presses: " + string.Join(" → ", seen));

            Assert.AreEqual(3, seen.Distinct().Count(),
                            "all three landables are reachable by pressing again");
            Assert.AreEqual(seen[0], seen[3], "and the fourth press wraps back to the first");
        }

        [Test]
        public void CyclingDoesNotAbandonTheWorldYouAreAlreadyArrivingAt()
        {
            // Upstream's special case (AI.cpp:4637): with two landables and one
            // selected, pressing land again inside its radius must NOT toggle away --
            // otherwise the last press before touchdown throws the approach away.
            (Ship ship, StarSystem system) = MakeFixture();
            StellarObject port = Find(system, "Port World");
            ship.TargetStellar = port;
            ship.Position = port.Position;

            LandingChoice choice = ShipAi.SelectLandingTarget(ship, cycle: true);

            Assert.AreSame(port, choice.Target, "already overhead: the target stands");
        }

        [Test]
        public void ATargetInAnotherSystemIsDroppedRatherThanFlownTo()
        {
            // Upstream requires the selection to be one of the CURRENT system's objects
            // (AI.cpp:4616), or a stale target survives a jump and the autopilot flies
            // at a set of coordinates that mean nothing in the new system.
            (Ship ship, StarSystem system) = MakeFixture();
            var elsewhere = new StarSystem("Elsewhere");
            ship.TargetStellar = Find(system, "Port World");
            ship.CurrentSystem = elsewhere;

            LandingChoice choice = ShipAi.SelectLandingTarget(ship, cycle: true);

            Assert.IsFalse(choice.Succeeded, "nothing in Elsewhere to land on");
            Assert.IsNull(ship.TargetStellar, "and the stale target is gone");
        }

        // --- Flying there ---------------------------------------------------------

        [Test]
        public void TheAutopilotFliesToTheTargetAndArrivesSlowEnoughToLand()
        {
            (Ship ship, StarSystem system) = MakeFixture();
            ShipAi.SelectLandingTarget(ship);
            StellarObject target = ship.TargetStellar!;
            double startDistance = (target.Position - ship.Position).Length;

            int frames = 0;
            const int patience = 6000;
            while (frames < patience && !ship.CanLandOn(target, target.Planet))
            {
                Command command = ShipAi.MoveToPlanet(ship, out bool arrived);
                ship.Step(command);
                frames++;
                if (arrived && ship.CanLandOn(target, target.Planet))
                    break;
            }

            double finalDistance = (target.Position - ship.Position).Length;
            TestContext.WriteLine($"{startDistance:F0} → {finalDistance:F0} units in {frames} frames, " +
                                  $"|v| {ship.Velocity.Length:F3} (landing radius {target.LandingRadius})");

            Assert.Less(frames, patience, "the autopilot should reach the world it selected");
            Assert.IsTrue(ship.CanLandOn(target, target.Planet),
                          "and arrive slow enough and close enough to actually put down");
        }

        // --- The galaxy the game actually plays -----------------------------------

        [Test]
        public void FromTheStartingSystemAPilotCanSelectAWorldAndFlyToIt()
        {
            GameData universe = GeneratedUniverse.Instance;
            StartScenario start = universe.Starts.Values.First();
            StarSystem system = universe.Systems[start.SystemName!];
            system.SetDate(0.0);

            Ship ship = universe.Ships.Keys
                .Select(name => universe.BuildShip(name, out List<string> missing) is Ship s &&
                                missing.Count == 0 && s.Thrust > 0.0 ? s : null)
                .First(s => s != null)!;
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);
            ship.CurrentSystem = system;
            ship.Position = Point.Zero;

            LandingChoice choice = ShipAi.SelectLandingTarget(ship);
            Assert.IsTrue(choice.Succeeded,
                          $"{system.Name} is where the game starts; it must have somewhere to land");

            StellarObject target = ship.TargetStellar!;
            int frames = 0;
            const int patience = 20000;
            while (frames < patience && !ship.CanLandOn(target, target.Planet))
            {
                ship.Step(ShipAi.MoveToPlanet(ship, out _));
                frames++;
            }

            TestContext.WriteLine($"{system.Name}: landed on {target.PlanetName} after {frames} frames " +
                                  $"({frames / Ship.FramesPerSecond:F0}s)");

            Assert.IsTrue(ship.CanLandOn(target, target.Planet),
                          "a new player must be able to press land and get to the ground");
        }
    }
}
