using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Asteroid belts and the rocks worth mining. Port checks against upstream
    /// <c>System::Asteroid</c> and <c>Minable</c>.
    /// </summary>
    [TestFixture]
    public class AsteroidTests
    {
        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(
                "minable \"copper\"\n\tsprite \"asteroid/gold/spin\"\n\thull 900\n" +
                "\t\"random hull\" 200\n\tpayload \"Copper\" 40\n\t\t\"toughness\" 6500\n" +
                "system \"Rutilicus\"\n\tpos 0 0\n" +
                "\tasteroids \"small rock\" 8 3.3166\n" +
                "\tasteroids \"medium metal\" 149 4.347\n" +
                "\tminables copper 7 2.11726\n");
            return data;
        }

        // --- Belts ----------------------------------------------------------------

        [Test]
        public void ASystemCarriesItsBelts()
        {
            StarSystem system = Load().Systems["Rutilicus"];

            Assert.AreEqual(3, system.Asteroids.Count);

            AsteroidBelt first = system.Asteroids[0];
            Assert.AreEqual("small rock", first.Name);
            Assert.AreEqual(8, first.Count);
            Assert.AreEqual(3.3166, first.Energy, 1e-9);
            Assert.IsFalse(first.IsMinable);
        }

        [Test]
        public void MinableBeltsAreMarkedAsSuch()
        {
            StarSystem system = Load().Systems["Rutilicus"];
            AsteroidBelt minable = system.Asteroids.Single(a => a.IsMinable);

            Assert.AreEqual("copper", minable.Name);
            Assert.AreEqual(7, minable.Count);
        }

        [Test]
        public void TwoBeltsOfTheSameRockAtDifferentEnergiesAreDistinct()
        {
            // Energy drives how fast the rocks move, so the same rock at two energies
            // is slow debris and fast fragments in one system.
            var data = new GameData();
            data.LoadText(
                "system \"Busy\"\n\tpos 0 0\n" +
                "\tasteroids \"small rock\" 5 1.0\n" +
                "\tasteroids \"small rock\" 5 9.0\n");

            var belts = data.Systems["Busy"].Asteroids;
            Assert.AreEqual(2, belts.Count);
            Assert.AreNotEqual(belts[0].Energy, belts[1].Energy);
        }

        [Test]
        public void AMalformedBeltIsSkippedRatherThanCrashingTheLoad()
        {
            var data = new GameData();
            data.LoadText("system \"Broken\"\n\tpos 0 0\n\tasteroids \"small rock\"\n");

            Assert.IsEmpty(data.Systems["Broken"].Asteroids);
        }

        // --- Minable types --------------------------------------------------------

        [Test]
        public void AMinableCarriesItsHullAndPayload()
        {
            Minable copper = Load().Minables["copper"];

            Assert.AreEqual(900.0, copper.Hull, 1e-9);
            Assert.AreEqual(200.0, copper.RandomHull, 1e-9);
            Assert.AreEqual("Copper", copper.PayloadName);
            Assert.AreEqual(40, copper.PayloadCount);
        }

        [Test]
        public void RandomHullVariesRockToRock()
        {
            // A belt of identical rocks would take identical effort to break; the
            // random component is what makes some rocks stubborn.
            Minable copper = Load().Minables["copper"];

            Assert.AreEqual(900.0, copper.HullFor(0.0), 1e-9);
            Assert.AreEqual(1100.0, copper.HullFor(1.0), 1e-9);
            Assert.AreEqual(1000.0, copper.HullFor(0.5), 1e-9);
            Assert.AreEqual(900.0, copper.HullFor(-5.0), 1e-9, "a roll out of range is clamped");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetsBeltsLoad()
        {
            GameData data = UpstreamData.Instance;

            var withBelts = data.Systems.Values.Where(s => s.Asteroids.Count > 0).ToList();
            int rocks = withBelts.Sum(s => s.Asteroids.Sum(a => a.Count));
            int minableBelts = withBelts.Sum(s => s.Asteroids.Count(a => a.IsMinable));

            TestContext.WriteLine(
                $"{withBelts.Count} of {data.Systems.Count} systems have belts, " +
                $"{rocks:n0} rocks in total, {minableBelts} minable belts; " +
                $"{data.Minables.Count} minable types defined");

            Assert.Greater(withBelts.Count, 100, "most systems have asteroids");
            Assert.Greater(data.Minables.Count, 0);
        }

        [Test]
        public void EveryMinableBeltNamesATypeThatExists()
        {
            // A belt naming a type nothing defines would be a rock that cannot be
            // mined, which is worse than no rock at all.
            GameData data = UpstreamData.Instance;

            var unknown = data.Systems.Values
                .SelectMany(s => s.Asteroids.Where(a => a.IsMinable).Select(a => a.Name))
                .Distinct()
                .Where(name => !data.Minables.ContainsKey(name))
                .ToList();

            Assert.IsEmpty(unknown, "undefined minable types: " + string.Join(", ", unknown));
        }

        [Test]
        public void EveryMinableDropsSomethingTheGameDefines()
        {
            GameData data = UpstreamData.Instance;

            var orphans = data.Minables.Values
                .Where(m => m.PayloadName != null && !data.Outfits.ContainsKey(m.PayloadName))
                .Select(m => $"{m.Name} -> {m.PayloadName}")
                .ToList();

            TestContext.WriteLine($"{data.Minables.Count} minable types; " +
                                  $"{data.Minables.Values.Count(m => m.PayloadName != null)} carry a payload");

            Assert.IsEmpty(orphans, "payloads with no matching outfit: " + string.Join(", ", orphans));
        }
    }
}
