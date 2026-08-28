using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Milestone 8's art rules, checked against the real fleet. Engine-free: these
    /// decide what a hull should look like, not how it is drawn.
    /// </summary>
    [TestFixture]
    public class ShipAppearanceTests
    {
        private static ShipDefinition MakeDefinition(string category, double mass,
                                                     params string[] extraLines)
        {
            var lines = new List<string>
            {
                "ship \"Test\"",
                "\tattributes",
                "\t\tcategory \"" + category + "\"",
                "\t\t\"mass\" " + mass.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            lines.AddRange(extraLines);

            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return definition;
        }

        // --- Scale ----------------------------------------------------------------

        [Test]
        public void LengthScalesWithTheCubeRootOfMass()
        {
            // Mass tracks volume, so a linear dimension is its cube root. Eight times
            // the mass is twice the length, not eight times.
            var small = new ShipAppearance(MakeDefinition("Medium Warship", 630));
            var eightfold = new ShipAppearance(MakeDefinition("Medium Warship", 630 * 8));

            Assert.AreEqual(2.0, eightfold.Length / small.Length, 1e-6);
        }

        [Test]
        public void TheWholeFleetFitsInADrawableSizeRange()
        {
            // The dataset spans 10 to 67400 mass - a factor of nearly 7000. Linear
            // scaling would make a fighter a pixel beside a capital ship. The cube
            // root must compress that into something a single frame can hold.
            GameData data = UpstreamData.Instance;
            var lengths = data.Ships.Values
                .Where(s => s.Attributes.Get("mass") > 0)
                .Select(s => new ShipAppearance(s).Length)
                .OrderBy(l => l)
                .ToList();

            Assert.Greater(lengths.Count, 800);

            var masses = data.Ships.Values
                .Select(sh => sh.Attributes.Get("mass"))
                .Where(m => m > 0)
                .OrderBy(m => m)
                .ToList();

            double massRatio = masses.Last() / masses.First();
            double lengthRatio = lengths.Last() / lengths.First();

            TestContext.WriteLine(
                $"mass {masses.First():F0} to {masses.Last():F0} ({massRatio:F0}x) " +
                $"becomes length {lengths.First():F1}u to {lengths.Last():F1}u ({lengthRatio:F1}x), " +
                $"median {lengths[lengths.Count / 2]:F1}u");

            // The point of the cube root is compression: a ~6700x mass range has to
            // become a spread a single frame can hold. Asserting the RELATIONSHIP
            // rather than a magic number, since the fleet grows over time.
            Assert.AreEqual(System.Math.Cbrt(massRatio), lengthRatio, lengthRatio * 0.01,
                "length ratio should be the cube root of the mass ratio");
            Assert.Less(lengthRatio, massRatio / 100.0,
                "the spread must be compressed by two orders of magnitude to be drawable");
        }

        [Test]
        public void EveryHullHasAPositiveLengthAndBeam()
        {
            GameData data = UpstreamData.Instance;

            foreach (ShipDefinition definition in data.Ships.Values)
            {
                if (definition.Attributes.Get("mass") <= 0)
                    continue;

                var appearance = new ShipAppearance(definition);
                Assert.Greater(appearance.Length, 0.0, definition.DisplayName);
                Assert.Greater(appearance.Beam, 0.0, definition.DisplayName);
                Assert.Less(appearance.Beam, appearance.Length, "hulls are longer than they are wide");
            }
        }

        // --- Classification -------------------------------------------------------

        [Test]
        public void CategoryIsAuthoritativeOverMass()
        {
            // A Utility hull can outweigh a warship without being one, so the declared
            // category has to win where the data provides it.
            Assert.AreEqual(HullClass.Fighter,
                ShipAppearance.Classify("Fighter", 40));
            Assert.AreEqual(HullClass.Heavy,
                ShipAppearance.Classify("Heavy Warship", 1910));
            Assert.AreEqual(HullClass.Drone,
                ShipAppearance.Classify("Drone", 24));
        }

        [Test]
        public void UncategorisedHullsFallBackToMass()
        {
            Assert.AreEqual(HullClass.Drone, ShipAppearance.Classify(null, 20));
            Assert.AreEqual(HullClass.Capital, ShipAppearance.Classify("Unclassified", 30000));
        }

        [Test]
        public void EveryRealShipClassifies()
        {
            GameData data = UpstreamData.Instance;
            var byClass = new Dictionary<HullClass, int>();

            foreach (ShipDefinition definition in data.Ships.Values)
            {
                if (definition.Attributes.Get("mass") <= 0)
                    continue;

                HullClass hull = new ShipAppearance(definition).Class;
                byClass.TryGetValue(hull, out int seen);
                byClass[hull] = seen + 1;
            }

            TestContext.WriteLine(string.Join(", ",
                byClass.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));

            // Every band should be populated, or the classification is collapsing.
            foreach (HullClass hull in System.Enum.GetValues<HullClass>())
                Assert.Greater(byClass.GetValueOrDefault(hull), 0, hull.ToString());
        }

        // --- Polygon budget -------------------------------------------------------

        [Test]
        public void PolygonBudgetRisesWithHullClass()
        {
            // Ties density to on-screen size: a swarm of drones must not cost more
            // than the capital ship they are attacking.
            var drone = new ShipAppearance(MakeDefinition("Drone", 24));
            var fighter = new ShipAppearance(MakeDefinition("Fighter", 40));
            var heavy = new ShipAppearance(MakeDefinition("Heavy Warship", 1910));

            Assert.Less(drone.TriangleBudget, fighter.TriangleBudget);
            Assert.Less(fighter.TriangleBudget, heavy.TriangleBudget);
        }

        // --- Mounts ---------------------------------------------------------------

        [Test]
        public void MountsAreReportedAtTrueScaleNotSpriteScale()
        {
            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(
                "ship \"Test\"\n\tattributes\n\t\t\"mass\" 630\n" +
                "\tengine -9.5 38\n\tgun 0 -40\n\tturret 0 -18\n", "test.txt").Nodes[0]);

            var appearance = new ShipAppearance(definition);

            Assert.AreEqual(3, appearance.Mounts.Count);

            MountPlacement engine = appearance.Mounts.First(m => m.Kind == MountKind.Engine);
            Assert.AreEqual(-4.75, engine.Offset.X, 1e-9, "sprite coordinates are double scale");
            Assert.AreEqual(19.0, engine.Offset.Y, 1e-9);

            Assert.AreEqual(1, appearance.Mounts.Count(m => m.Kind == MountKind.Gun));
            Assert.AreEqual(1, appearance.Mounts.Count(m => m.Kind == MountKind.Turret));
        }

        [Test]
        public void MountsCarryTheirDefaultOutfitWhereTheDataNamesOne()
        {
            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(
                "ship \"Test\"\n\tattributes\n\t\t\"mass\" 630\n" +
                "\tturret 0 -18 \"Anti-Missile Turret\"\n", "test.txt").Nodes[0]);

            MountPlacement turret = new ShipAppearance(definition).Mounts.Single();
            Assert.AreEqual("Anti-Missile Turret", turret.OutfitName);
        }

        // --- Windows and emissives ------------------------------------------------

        [Test]
        public void AutomataAndUncrewedHullsShowNoLitPorts()
        {
            // A drone reading as crewed is the single most obvious way a low-poly
            // fleet loses its internal logic.
            var drone = new ShipAppearance(MakeDefinition("Drone", 24,
                "\t\t\"automaton\" 1", "\t\t\"bunks\" 0"));
            Assert.AreEqual(0, drone.WindowCount);

            var liner = new ShipAppearance(MakeDefinition("Space Liner", 525, "\t\t\"bunks\" 80"));
            Assert.Greater(liner.WindowCount, 0);
        }

        [Test]
        public void WindowCountIsCappedSoALinerDoesNotBecomeAGrid()
        {
            var huge = new ShipAppearance(MakeDefinition("Space Liner", 5562, "\t\t\"bunks\" 2000"));
            Assert.LessOrEqual(huge.WindowCount, 40);
        }

        [Test]
        public void EngineGlowTracksThrustPerUnitMass()
        {
            var sluggish = new ShipAppearance(MakeDefinition("Heavy Freighter", 4000, "\t\t\"thrust\" 20"));
            var nimble = new ShipAppearance(MakeDefinition("Interceptor", 124, "\t\t\"thrust\" 20"));

            Assert.Less(sluggish.EngineGlow, nimble.EngineGlow,
                "the same engine on a lighter hull should visibly burn harder");
        }

        [Test]
        public void AnEnginelessHullDoesNotGlow()
        {
            Assert.AreEqual(0.0, new ShipAppearance(MakeDefinition("Drone", 24)).EngineGlow, 1e-9);
        }

        // --- Damage states --------------------------------------------------------

        [Test]
        public void DamageStatesStepDownAsHullIsLost()
        {
            Assert.AreEqual(0, ShipAppearance.DamageState(100, 100));
            Assert.AreEqual(1, ShipAppearance.DamageState(60, 100));
            Assert.AreEqual(2, ShipAppearance.DamageState(30, 100));
            Assert.AreEqual(3, ShipAppearance.DamageState(5, 100));
        }

        [Test]
        public void DamageStateIsSafeOnAHullWithNoMaximum()
        {
            Assert.AreEqual(0, ShipAppearance.DamageState(0, 0));
        }
    }
}
