using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Populating a system with NPC traffic. Port checks against upstream
    /// <c>Engine::SpawnFleets</c> and <c>Fleet::Enter</c>.
    /// </summary>
    [TestFixture]
    public class FleetSpawnerTests
    {
        private const string Universe =
            "ship \"Freighter\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 900\n" +
            "\t\t\"shields\" 300\n\t\t\"energy capacity\" 100\n\t\t\"cost\" 100000\n" +
            "ship \"Raider\"\n\tattributes\n\t\t\"mass\" 150\n\t\t\"hull\" 600\n" +
            "\t\t\"shields\" 200\n\t\t\"energy capacity\" 100\n\t\t\"cost\" 90000\n" +
            // "attitude toward" is QUOTED in real content, which is what makes it one
            // token rather than two. Written bare it silently never matches, and every
            // faction ends up indifferent to every other.
            "government \"Merchant\"\n\t\"attitude toward\"\n\t\t\"Pirate\" -1\n" +
            "government \"Pirate\"\n\t\"attitude toward\"\n\t\t\"Merchant\" -1\n" +
            "fleet \"Traders\"\n\tgovernment \"Merchant\"\n" +
            "\tvariant 10\n\t\t\"Freighter\"\n" +
            "fleet \"Raiders\"\n\tgovernment \"Pirate\"\n" +
            "\tvariant 10\n\t\t\"Raider\" 2\n" +
            "system \"Sol\"\n\tpos 0 0\n" +
            "\tfleet \"Traders\" 100\n\tfleet \"Raiders\" 500\n";

        private static GameData Load(string text = Universe)
        {
            var data = new GameData();
            data.LoadText(text);
            return data;
        }

        /// <summary>An RNG that always rolls zero, so every fleet triggers.</summary>
        private static readonly System.Func<int, int> AlwaysSpawn = _ => 0;

        /// <summary>An RNG that never rolls zero, so nothing triggers.</summary>
        private static readonly System.Func<int, int> NeverSpawn = n => n > 1 ? 1 : 0;

        // --- Parsing --------------------------------------------------------------

        [Test]
        public void SystemsCarryTheFleetsThatFrequentThem()
        {
            StarSystem sol = Load().Systems["Sol"];

            Assert.AreEqual(2, sol.Fleets.Count);
            Assert.AreEqual("Traders", sol.Fleets[0].Name);
            Assert.AreEqual(100, sol.Fleets[0].Period);
            Assert.AreEqual(500, sol.Fleets[1].Period);
        }

        [Test]
        public void TheRealDatasetsSystemsHaveTraffic()
        {
            GameData data = UpstreamData.Instance;

            var populated = data.Systems.Values.Where(s => s.Fleets.Count > 0).ToList();
            int entries = populated.Sum(s => s.Fleets.Count);

            TestContext.WriteLine(
                $"{populated.Count} of {data.Systems.Count} systems have traffic, " +
                $"{entries} fleet entries in total");

            Assert.Greater(populated.Count, 100, "most of the galaxy should be inhabited");
        }

        // --- Spawning -------------------------------------------------------------

        [Test]
        public void AFleetThatTriggersArrivesAsShips()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            List<Ship> arrived = spawner.Step(data.Systems["Sol"]);

            // Traders contributes one Freighter, Raiders two Raiders.
            Assert.AreEqual(3, arrived.Count);
            Assert.AreEqual(1, arrived.Count(s => s.Definition.DisplayName == "Freighter"));
            Assert.AreEqual(2, arrived.Count(s => s.Definition.DisplayName == "Raider"));
        }

        [Test]
        public void NothingArrivesWhenTheOddsDoNotCome()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, NeverSpawn);

            Assert.IsEmpty(spawner.Step(data.Systems["Sol"]));
        }

        [Test]
        public void ArrivingShipsAreFlyableAndFlyTheirFleetsFlag()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            Ship freighter = spawner.Step(data.Systems["Sol"])
                .First(s => s.Definition.DisplayName == "Freighter");

            Assert.AreEqual("Merchant", freighter.Government!.Name);
            Assert.AreEqual(data.Systems["Sol"], freighter.CurrentSystem);
            Assert.AreEqual(freighter.MaxHull, freighter.Hull, 1e-9);
            Assert.AreEqual(freighter.MaxFuel, freighter.Fuel, 1e-9);
        }

        [Test]
        public void TrafficArrivesAtTheSystemEdgeFacingInward()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn) { ArrivalDistance = 5000.0 };

            Ship ship = spawner.Step(data.Systems["Sol"]).First();

            Assert.Greater(ship.Position.Length, 4000.0, "arrivals start out at the edge");

            // Facing back toward the middle, where anything worth flying to is.
            Assert.Less(ship.Facing.Unit().Dot(ship.Position.Unit()), 0.0);
        }

        [Test]
        public void AMultiShipVariantDoesNotStackHullsOnOneSpot()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            var raiders = spawner.Step(data.Systems["Sol"])
                .Where(s => s.Definition.DisplayName == "Raider")
                .ToList();

            Assert.AreEqual(2, raiders.Count);
            Assert.AreNotEqual(raiders[0].Position, raiders[1].Position);
        }

        // --- The strength check ---------------------------------------------------

        [Test]
        public void NoReinforcementsWhenOneSideAlreadyDominates()
        {
            // Without this a running battle turns into an endless pile-on.
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            // A crowd of merchants and one lone pirate already in-system.
            var present = new List<Ship>();
            for (int i = 0; i < 10; i++)
            {
                Ship ally = data.BuildShip("Freighter");
                ally.Government = data.Governments["Merchant"];
                present.Add(ally);
            }

            Ship enemy = data.BuildShip("Raider");
            enemy.Government = data.Governments["Pirate"];
            present.Add(enemy);

            List<Ship> arrived = spawner.Step(data.Systems["Sol"], present);

            Assert.IsEmpty(arrived.Where(s => s.Government!.Name == "Merchant").ToList(),
                "merchants already dominate and should send no more");
            Assert.IsNotEmpty(arrived.Where(s => s.Government!.Name == "Pirate").ToList(),
                "the outnumbered side still arrives");
        }

        [Test]
        public void OrdinaryTrafficStillFlowsThroughAPeacefulSystem()
        {
            // The check keys off ENEMY strength. With nothing hostile present there is
            // nothing to reinforce against, so traffic must keep coming - otherwise a
            // quiet system fills once and then goes dead.
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            var present = new List<Ship>();
            for (int i = 0; i < 20; i++)
            {
                Ship ally = data.BuildShip("Freighter");
                ally.Government = data.Governments["Merchant"];
                present.Add(ally);
            }

            Assert.IsNotEmpty(spawner.Step(data.Systems["Sol"], present)
                .Where(s => s.Government!.Name == "Merchant").ToList());
        }

        [Test]
        public void DestroyedShipsDoNotCountTowardStrength
            ()
        {
            GameData data = Load();
            var spawner = new FleetSpawner(data, AlwaysSpawn);

            var present = new List<Ship>();
            for (int i = 0; i < 10; i++)
            {
                Ship ally = data.BuildShip("Freighter");
                ally.Government = data.Governments["Merchant"];
                ally.SetLevels(hull: -1.0);
                present.Add(ally);
            }

            Ship enemy = data.BuildShip("Raider");
            enemy.Government = data.Governments["Pirate"];
            present.Add(enemy);

            Assert.IsNotEmpty(spawner.Step(data.Systems["Sol"], present)
                .Where(s => s.Government!.Name == "Merchant").ToList(),
                "a fleet of wrecks is not a garrison");
        }

        // --- Weighting ------------------------------------------------------------

        [Test]
        public void VariantsArePickedByTheirAuthoredWeight()
        {
            var data = Load(
                "ship \"Common\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 100\n" +
                "\t\t\"energy capacity\" 10\n" +
                "ship \"Rare\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 100\n" +
                "\t\t\"energy capacity\" 10\n" +
                "government \"Merchant\"\n" +
                "fleet \"Mixed\"\n\tgovernment \"Merchant\"\n" +
                "\tvariant 90\n\t\t\"Common\"\n" +
                "\tvariant 10\n\t\t\"Rare\"\n" +
                "system \"Sol\"\n\tpos 0 0\n\tfleet \"Mixed\" 1\n");

            // Roll 0 selects the first variant's weight band, 95 the second.
            var common = new FleetSpawner(data, n => n == 100 ? 0 : 0);
            Assert.AreEqual("Common",
                common.Step(data.Systems["Sol"]).Single().Definition.DisplayName);

            var rare = new FleetSpawner(data, n => n == 100 ? 95 : 0);
            Assert.AreEqual("Rare",
                rare.Step(data.Systems["Sol"]).Single().Definition.DisplayName);
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void ARealSystemFillsWithItsOwnTraffic()
        {
            GameData data = UpstreamData.Instance;

            StarSystem busiest = data.Systems.Values
                .Where(s => s.Fleets.Count > 0)
                .OrderBy(s => s.Fleets.Min(f => f.Period))
                .First();

            var spawner = new FleetSpawner(data, AlwaysSpawn);
            List<Ship> arrived = spawner.Step(busiest);

            TestContext.WriteLine(
                $"{busiest.Name} drew {arrived.Count} ships from {busiest.Fleets.Count} fleets: " +
                string.Join(", ", arrived.Take(6).Select(s => s.Definition.DisplayName)));

            Assert.IsNotEmpty(arrived, "a real system should produce real traffic");
            Assert.IsTrue(arrived.All(s => s.Government != null), "all of it flying a flag");
            Assert.IsTrue(arrived.All(s => s.Hull > 0.0), "and all of it intact");
        }
    }
}
