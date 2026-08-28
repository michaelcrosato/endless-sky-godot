using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Derived orbital periods, port of the tail of upstream
    /// <c>System::UpdateSystem</c> (System.cpp:576).
    /// </summary>
    [TestFixture]
    public class OrbitTests
    {
        // Star sprite masses come from upstream's object sprite data; these two are
        // stand-ins, so the tests assert the relationship rather than a magic constant.
        private const double MassA = 400.0;
        private const double MassB = 100.0;

        private static Func<string, double> Masses => sprite => sprite switch
        {
            "star/a" => MassA,
            "star/b" => MassB,
            "planet/big" => 120.0,
            _ => 0.0,
        };

        private static StarSystem Resolve(string text)
        {
            var data = new GameData();
            data.LoadText(text);
            StarSystem system = data.Systems.Values.First();
            system.ResolveOrbits(Masses);
            return system;
        }

        private static StellarObject Object(StarSystem system, string sprite) =>
            system.AllObjects().First(o => o.Sprite == sprite);

        [Test]
        public void ALoneStarKeepsTheDefaultPeriod()
        {
            // A single star sits at the centre; its period is arbitrary and upstream
            // pins it at 10. This is the case that made the binary bug easy to miss.
            StarSystem system = Resolve(
                "system \"Solo\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 0\n" +
                "\tobject\n\t\tsprite planet/rock\n\t\tdistance 500\n");

            // Distance 0 means upstream skips it entirely, leaving speed untouched.
            Assert.AreEqual(0.0, Object(system, "star/a").Speed, 1e-9);
        }

        [Test]
        public void BinaryStarsOrbitOnTheirCombinedSeparationAndMass()
        {
            // The regression. Both stars orbit the common centre of mass, with a period
            // of sqrt(summed distance^3 / summed mass). Left on the default they would
            // both read 360/10 = 36 degrees per day whatever their separation.
            StarSystem system = Resolve(
                "system \"Binary\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 40\n\t\toffset 0\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 160\n\t\toffset 180\n");

            double expectedPeriod = Math.Sqrt(Math.Pow(40.0 + 160.0, 3) / (MassA + MassB));
            double expectedSpeed = 360.0 / expectedPeriod;

            Assert.AreEqual(expectedSpeed, Object(system, "star/a").Speed, 1e-9);
            Assert.AreEqual(expectedSpeed, Object(system, "star/b").Speed, 1e-9);
            Assert.AreNotEqual(36.0, Object(system, "star/a").Speed,
                "36 is the default period's speed, which a binary must not use");
        }

        [Test]
        public void BothStarsOfABinaryShareOneAngularSpeed()
        {
            // Partners must stay diametrically opposite. Different speeds would drift
            // them out of opposition and eventually overlap them.
            StarSystem system = Resolve(
                "system \"Binary\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 30\n\t\toffset 0\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 300\n\t\toffset 180\n");

            Assert.AreEqual(Object(system, "star/a").Speed, Object(system, "star/b").Speed, 1e-12);
        }

        [Test]
        public void WiderBinariesTurnMoreSlowly()
        {
            StarSystem close = Resolve(
                "system \"Close\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 20\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 80\n");
            StarSystem wide = Resolve(
                "system \"Wide\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 200\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 800\n");

            Assert.Greater(Object(close, "star/a").Speed, Object(wide, "star/a").Speed);
        }

        [Test]
        public void PlanetsInABinaryStillOrbitTheCombinedStarMass()
        {
            StarSystem system = Resolve(
                "system \"Binary\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 40\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 160\n" +
                "\tobject\n\t\tsprite planet/rock\n\t\tdistance 2000\n");

            double expected = 360.0 / Math.Sqrt(Math.Pow(2000.0, 3) / (MassA + MassB));
            Assert.AreEqual(expected, Object(system, "planet/rock").Speed, 1e-9);
        }

        [Test]
        public void MoonsOrbitTheirParentPlanetNotTheStars()
        {
            StarSystem system = Resolve(
                "system \"Solo\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 0\n" +
                "\tobject\n\t\tsprite planet/big\n\t\tdistance 900\n" +
                "\t\tobject\n\t\t\tsprite planet/moon\n\t\t\tdistance 60\n");

            double expected = 360.0 / Math.Sqrt(Math.Pow(60.0, 3) / 120.0);
            Assert.AreEqual(expected, Object(system, "planet/moon").Speed, 1e-9);
        }

        [Test]
        public void AnExplicitPeriodIsNeverOverwritten()
        {
            StarSystem system = Resolve(
                "system \"Binary\"\n" +
                "\tobject\n\t\tsprite star/a\n\t\tdistance 40\n\t\tperiod 7\n" +
                "\tobject\n\t\tsprite star/b\n\t\tdistance 160\n");

            Assert.AreEqual(360.0 / 7.0, Object(system, "star/a").Speed, 1e-9);
        }

        [Test]
        public void StarsWithNoDefinedMassFallBackRatherThanDivideByZero()
        {
            StarSystem system = Resolve(
                "system \"Unknown\"\n" +
                "\tobject\n\t\tsprite star/unknown\n\t\tdistance 40\n" +
                "\tobject\n\t\tsprite star/other\n\t\tdistance 160\n");

            foreach (StellarObject o in system.AllObjects())
            {
                Assert.IsFalse(double.IsNaN(o.Speed), "speed must stay finite");
                Assert.IsFalse(double.IsInfinity(o.Speed), "speed must stay finite");
            }
        }

        [Test]
        public void TheRealDatasetContainsBinariesAndNoneSpinAtTheDefaultRate()
        {
            // Guard against the fix quietly regressing on real content: find systems
            // with more than one star and no explicit period, and check they moved off
            // the default.
            GameData data = UpstreamData.Instance;

            int binaries = 0, derived = 0;
            foreach (StarSystem system in data.Systems.Values)
            {
                var stars = system.AllObjects()
                    .Where(o => o.IsStar && o.Distance > 0.0 && !o.ExplicitPeriodSet)
                    .ToList();
                if (stars.Count < 2)
                    continue;

                binaries++;
                if (stars.All(o => Math.Abs(o.Speed - 36.0) > 1e-9))
                    derived++;
            }

            TestContext.WriteLine($"{binaries} multi-star systems without explicit periods; " +
                                  $"{derived} took a derived period");
            Assert.Greater(binaries, 0, "upstream content has binary systems");
            Assert.AreEqual(binaries, derived, "every binary should derive its period");
        }
    }
}
