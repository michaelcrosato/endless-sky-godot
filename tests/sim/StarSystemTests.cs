using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// System layout and orbital mechanics. Upstream derives an orbital period from the
    /// mass being orbited unless the content states one explicitly, so the sprite mass
    /// tables in stars.txt are load-bearing, not decoration.
    /// </summary>
    public class StarSystemTests
    {
        private const string Fixture =
            "star \"star/g0\"\n" +
            "\tmass 4000\n" +
            "\n" +
            "\"planet mass\" 329.28\n" +
            "\tplanet/rock17\n" +
            "\tplanet/gas9-b\n" +
            "\n" +
            "system \"Testbed\"\n" +
            "\tpos 10 -20\n" +
            "\tgovernment Independent\n" +
            "\tlink \"Elsewhere\"\n" +
            "\tobject\n" +
            "\t\tsprite \"star/g0\"\n" +
            "\t\tdistance 0\n" +
            "\tobject \"Rock\"\n" +
            "\t\tsprite \"planet/rock17\"\n" +
            "\t\tdistance 400\n" +
            "\tobject\n" +
            "\t\tsprite \"planet/gas9-b\"\n" +
            "\t\tdistance 1200\n" +
            "\t\tperiod 40\n" +
            "\t\tobject\n" +
            "\t\t\tsprite \"planet/ice8\"\n" +
            "\t\t\tdistance 150\n";

        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(Fixture, "system-fixture");
            return data;
        }

        [Test]
        public void SystemMetadataIsParsed()
        {
            StarSystem system = Load().Systems["Testbed"];

            Assert.AreEqual(10.0, system.MapPosition.X, 1e-9);
            Assert.AreEqual(-20.0, system.MapPosition.Y, 1e-9);
            Assert.AreEqual("Independent", system.Government);
            CollectionAssert.Contains(system.Links.ToList(), "Elsewhere");
        }

        [Test]
        public void NestedObjectsBecomeMoonsOfTheirParent()
        {
            StarSystem system = Load().Systems["Testbed"];

            StellarObject gasGiant = system.Objects.First(o => o.Sprite == "planet/gas9-b");
            Assert.AreEqual(1, gasGiant.Children.Count);

            StellarObject moon = gasGiant.Children[0];
            Assert.IsTrue(moon.IsMoon, "an object nested under a planet is a moon");
            Assert.AreSame(gasGiant, moon.Parent);
        }

        [Test]
        public void StarIsIdentifiedBySpritePrefix()
        {
            StarSystem system = Load().Systems["Testbed"];

            Assert.AreEqual(1, system.AllObjects().Count(o => o.IsStar));
            Assert.IsFalse(system.Objects.First(o => o.Sprite == "planet/rock17").IsStar);
        }

        [Test]
        public void NamedObjectKeepsItsPlanetName()
        {
            StarSystem system = Load().Systems["Testbed"];

            Assert.AreEqual("Rock", system.Objects.First(o => o.Sprite == "planet/rock17").PlanetName);
        }

        [Test]
        public void ExplicitPeriodOverridesDerivedOrbit()
        {
            StarSystem system = Load().Systems["Testbed"];
            StellarObject gasGiant = system.Objects.First(o => o.Sprite == "planet/gas9-b");

            Assert.IsTrue(gasGiant.ExplicitPeriodSet);
            Assert.AreEqual(360.0 / 40.0, gasGiant.Speed, 1e-9, "speed is degrees per day");
        }

        [Test]
        public void PlanetOrbitalPeriodIsDerivedFromStarMass()
        {
            StarSystem system = Load().Systems["Testbed"];
            StellarObject rock = system.Objects.First(o => o.Sprite == "planet/rock17");

            // period = sqrt(distance^3 / starMass)
            double expectedPeriod = System.Math.Sqrt(System.Math.Pow(400.0, 3) / 4000.0);
            Assert.AreEqual(360.0 / expectedPeriod, rock.Speed, 1e-9);
        }

        [Test]
        public void MoonOrbitalPeriodUsesItsParentPlanetMass()
        {
            // This is why the "planet mass" table matters: without it the moon would fall
            // back to a flat 10-day period and orbit visibly wrong.
            StarSystem system = Load().Systems["Testbed"];
            StellarObject moon = system.Objects
                .First(o => o.Sprite == "planet/gas9-b").Children[0];

            double expectedPeriod = System.Math.Sqrt(System.Math.Pow(150.0, 3) / 329.28);
            Assert.AreEqual(360.0 / expectedPeriod, moon.Speed, 1e-6);
        }

        [Test]
        public void MoonPositionIsRelativeToItsParent()
        {
            StarSystem system = Load().Systems["Testbed"];
            system.SetDate(3.0);

            StellarObject planet = system.Objects.First(o => o.Sprite == "planet/gas9-b");
            StellarObject moon = planet.Children[0];

            double separation = (moon.Position - planet.Position).Length;
            Assert.AreEqual(150.0, separation, 1e-6, "the moon orbits the planet, not the star");
        }

        [Test]
        public void ObjectAtTheCentreDoesNotMove()
        {
            StarSystem system = Load().Systems["Testbed"];
            StellarObject star = system.Objects.First(o => o.IsStar);

            system.SetDate(0.0);
            Point first = star.Position;
            system.SetDate(500.0);

            Assert.AreEqual(first, star.Position);
            Assert.AreEqual(0.0, star.Position.Length, 1e-12);
        }
    }
}
