using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Hyperspace arrival: where a ship comes out, and how far short of it.
    /// Port checks against upstream <c>Ship::DoHyperspaceLogic</c> (Ship.cpp:4640).
    /// </summary>
    [TestFixture]
    public class ArrivalTests
    {
        // --- Parsing --------------------------------------------------------------

        private static StarSystem LoadSystem(string text)
        {
            var data = new GameData();
            data.LoadText(text);
            return data.Systems.Values.First();
        }

        [Test]
        public void ABareArrivalValueSetsBothDriveTypes()
        {
            StarSystem system = LoadSystem("system \"Test\"\n\tarrival 5000\n");

            Assert.AreEqual(5000.0, system.ExtraHyperArrivalDistance, 1e-9);
            Assert.AreEqual(5000.0, system.ExtraJumpArrivalDistance, 1e-9);
        }

        [Test]
        public void JumpArrivalDistanceIsAlwaysPositiveButHyperMayBeNegative()
        {
            // Upstream takes fabs() for the jump distance only. A negative hyper
            // distance is meaningful - it arrives PAST the target - but a negative
            // jump radius is not.
            StarSystem system = LoadSystem("system \"Test\"\n\tarrival -3000\n");

            Assert.AreEqual(-3000.0, system.ExtraHyperArrivalDistance, 1e-9);
            Assert.AreEqual(3000.0, system.ExtraJumpArrivalDistance, 1e-9);
        }

        [Test]
        public void PerDriveChildrenOverrideTheBareValue()
        {
            StarSystem system = LoadSystem(
                "system \"Test\"\n\tarrival 5000\n\t\tlink 1200\n\t\tjump -900\n");

            Assert.AreEqual(1200.0, system.ExtraHyperArrivalDistance, 1e-9);
            Assert.AreEqual(900.0, system.ExtraJumpArrivalDistance, 1e-9);
        }

        [Test]
        public void SystemsWithoutAnArrivalNodeReportZero()
        {
            StarSystem system = LoadSystem("system \"Test\"\n\thabitable 100\n");

            Assert.AreEqual(0.0, system.ExtraHyperArrivalDistance, 1e-9);
            Assert.AreEqual(0.0, system.ExtraJumpArrivalDistance, 1e-9);
        }

        // --- Arrival target -------------------------------------------------------

        private const string Ship =
            "ship \"Courier\"\n\tattributes\n\t\t\"hull\" 500\n\t\t\"mass\" 100\n" +
            "\t\t\"fuel capacity\" 500\n\t\t\"hyperdrive\" 1\n\t\t\"thrust\" 20\n" +
            "\t\t\"drag\" 2\n\t\t\"turn\" 200\n";

        private static GameData Universe(string arrivalNode, string extraPlanets = "")
        {
            var data = new GameData();
            data.LoadText(
                Ship +
                "planet \"Barren\"\n\tattributes uninhabited\n" +
                "planet \"Portside\"\n\tspaceport `A port.`\n" + extraPlanets +
                "system \"Origin\"\n\tpos 0 0\n\tlink \"Destination\"\n" +
                "system \"Destination\"\n\tpos 100 0\n\tlink \"Origin\"\n" + arrivalNode +
                "\tobject\n\t\tsprite planet/rock\n\t\tdistance 300\n\t\tperiod 100\n" +
                "\tobject \"Barren\"\n\t\tsprite planet/desert\n\t\tdistance 900\n\t\tperiod 200\n" +
                "\tobject \"Portside\"\n\t\tsprite planet/earth\n\t\tdistance 1500\n\t\tperiod 300\n");
            return data;
        }

        private static Ship Arrive(GameData data)
        {
            StarSystem origin = data.Systems["Origin"];
            StarSystem destination = data.Systems["Destination"];
            foreach (StarSystem system in data.Systems.Values)
                system.SetDate(0.0);

            var ship = new Ship(data.Ships["Courier"]) { CurrentSystem = origin };
            ship.BuildMounts();
            ship.SetLevels(fuel: ship.MaxFuel);
            ship.TargetSystem = destination;
            // Upstream gates the jump on facing the departure direction.
            ship.Facing = Angle.FromPoint(ship.JumpDirection);

            Assert.IsTrue(ship.TryCommitJump(), "the jump should be legal");
            for (int i = 0; i < 400 && ship.CurrentSystem != destination; i++)
                ship.StepHyperspace();

            Assert.AreEqual(destination, ship.CurrentSystem, "the ship should have arrived");
            return ship;
        }

        [Test]
        public void WithNoExtraDistanceArrivalAimsAtAWorldWithServices()
        {
            // Not merely the first NAMED object: the rock is unnamed, Barren is named
            // but uninhabited, and Portside is the one with a spaceport.
            GameData data = Universe(arrivalNode: "");
            StarSystem destination = data.Systems["Destination"];
            Ship ship = Arrive(data);

            StellarObject portside = destination.AllObjects().First(o => o.PlanetName == "Portside");
            StellarObject barren = destination.AllObjects().First(o => o.PlanetName == "Barren");

            double toPortside = (ship.Position - portside.Position).Length;
            double toBarren = (ship.Position - barren.Position).Length;

            Assert.Less(toPortside, toBarren,
                "arrival should be lined up on the world with services, not the uninhabited one");
        }

        [Test]
        public void AnExtraArrivalDistanceSwitchesTheTargetToTheSystemCentre()
        {
            // The behaviour that matters: a system sets an arrival distance to keep
            // ships away from its worlds. Aiming at a planet anyway and then adding the
            // distance drops arrivals exactly where the setting exists to prevent.
            GameData data = Universe(arrivalNode: "\tarrival 5000\n");
            StarSystem destination = data.Systems["Destination"];
            Ship ship = Arrive(data);

            StellarObject portside = destination.AllObjects().First(o => o.PlanetName == "Portside");

            Assert.Greater((ship.Position - portside.Position).Length, 1500.0,
                "arrival must not be lined up on a planet when the system sets a distance");

            // The ship sits back along its own facing from the system centre.
            double alongFacing = -ship.Position.Dot(ship.Facing.Unit());
            Assert.Greater(alongFacing, 5000.0,
                "the extra distance must actually push the arrival further out");
        }

        [Test]
        public void TheExtraDistanceIsAddedToTheArrivalOffset()
        {
            GameData baseline = Universe(arrivalNode: "");
            GameData pushedOut = Universe(arrivalNode: "\tarrival 5000\n");

            // Both aim at the system centre only in the second case, so compare the
            // distance from each ship to its own target rather than to a fixed point.
            Ship near = Arrive(baseline);
            Ship far = Arrive(pushedOut);

            StellarObject portside = baseline.Systems["Destination"]
                .AllObjects().First(o => o.PlanetName == "Portside");

            double nearOffset = (near.Position - portside.Position).Length;
            double farOffset = far.Position.Length;   // target was the centre

            Assert.AreEqual(nearOffset + 5000.0, farOffset, 1.0,
                "the arrival offset should differ by exactly the extra distance");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void UpstreamContentActuallyUsesArrivalDistances()
        {
            // A guard on the parse: if this drops to zero the feature has silently
            // stopped loading, and every test above would still pass on stub data.
            GameData data = UpstreamData.Instance;

            var withArrival = data.Systems.Values
                .Where(s => s.ExtraHyperArrivalDistance != 0.0 || s.ExtraJumpArrivalDistance != 0.0)
                .ToList();

            TestContext.WriteLine(
                $"{withArrival.Count} of {data.Systems.Count} systems set an arrival distance; " +
                $"e.g. {string.Join(", ", withArrival.Take(5).Select(s => $"{s.Name} {s.ExtraHyperArrivalDistance:F0}"))}");

            Assert.Greater(withArrival.Count, 0, "upstream data sets arrival distances");
        }

        [Test]
        public void EveryStellarObjectWithAKnownPlanetNameIsLinkedToIt()
        {
            // The link is what lets arrival ask about services at all.
            GameData data = UpstreamData.Instance;

            int named = 0, linked = 0;
            foreach (StarSystem system in data.Systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                {
                    if (obj.PlanetName == null || !data.Planets.ContainsKey(obj.PlanetName))
                        continue;
                    named++;
                    if (obj.Planet != null)
                        linked++;
                }

            TestContext.WriteLine($"{linked} of {named} named stellar objects linked to a planet");
            Assert.Greater(named, 100);
            Assert.AreEqual(named, linked);
        }
    }
}
