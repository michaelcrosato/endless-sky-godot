using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Loads the real Endless Sky dataset and checks that our loader survives it and
    /// reproduces known values. This is the test that actually proves data compatibility;
    /// the hand-written fixtures only prove the syntax rules.
    ///
    /// The dataset is not vendored into this repository. Point <c>ENDLESS_SKY_DATA</c> at
    /// an upstream checkout's <c>data</c> directory, or place the checkout beside this
    /// project as <c>../es-upstream</c>. Missing required content fails these tests.
    /// </summary>
    public class UpstreamContentTests
    {
        // Dataset location + caching live in the shared UpstreamData loader.
        private static GameData Data() => UpstreamData.Instance;

        [Test]
        public void DatasetLoadsWithoutParseErrors()
        {
            GameData data = Data();

            // Upstream's own files are well-formed, so any diagnostic means our parser
            // disagrees with upstream about the syntax.
            Assert.IsEmpty(data.Diagnostics,
                "parser reported problems in upstream content:\n" +
                string.Join("\n", data.Diagnostics.Take(20)));
        }

        [Test]
        public void DatasetContainsTheExpectedBulkOfContent()
        {
            GameData data = Data();

            // Lower bounds, not exact counts: upstream adds content over time and the
            // test should not break every time it does.
            Assert.Greater(data.Ships.Count, 200, "ship definitions");
            Assert.Greater(data.Outfits.Count, 400, "outfit definitions");
            Assert.Greater(data.Systems.Count, 200, "star systems");

            TestContext.WriteLine(
                $"Loaded {data.Ships.Count} ships, {data.Outfits.Count} outfits, " +
                $"{data.Systems.Count} systems from {UpstreamData.Path}");
        }

        [Test]
        public void StarBargeMatchesItsAuthoredAttributes()
        {
            GameData data = Data();

            Assert.IsTrue(data.Ships.ContainsKey("Star Barge"), "Star Barge should be defined");
            ShipDefinition definition = data.Ships["Star Barge"];

            Assert.AreEqual(80.0, definition.Attributes.Get("mass"), 1e-9);
            Assert.AreEqual(2.2, definition.Attributes.Get("drag"), 1e-9);
            Assert.AreEqual(1000.0, definition.Attributes.Get("hull"), 1e-9);
            Assert.AreEqual(600.0, definition.Attributes.Get("shields"), 1e-9);
            Assert.AreEqual("Light Freighter", definition.Category);
            Assert.AreEqual(2, definition.Engines.Count, "Star Barge has two engine mounts");
        }

        [Test]
        public void StarBargeBuildsWithEveryDefaultOutfitPresent()
        {
            GameData data = Data();

            Ship ship = data.BuildShip("Star Barge", out List<string> missing);

            Assert.IsEmpty(missing,
                "every outfit a stock ship ships with must resolve: " + string.Join(", ", missing));

            // Its engines came from outfits, so a working build has non-zero thrust and turn.
            Assert.Greater(ship.Thrust, 0.0, "thrust comes from the installed thruster");
            Assert.Greater(ship.TurnTorque, 0.0, "turn comes from the installed steering");
            Assert.Greater(ship.Mass, 80.0, "outfits add mass on top of the hull");
            Assert.Greater(ship.MaxVelocity, 0.0);

            TestContext.WriteLine(
                $"Star Barge: mass={ship.Mass} thrust={ship.Thrust} turn={ship.TurnTorque} " +
                $"accel={ship.Acceleration:F4}/frame maxV={ship.MaxVelocity:F3}/frame " +
                $"({ship.MaxVelocity * Ship.FramesPerSecond:F1}/s), turnRate={ship.TurnRate:F3}deg/frame");
        }

        [Test]
        public void EveryStockShipBuildsWithResolvableOutfits()
        {
            GameData data = Data();

            var broken = new List<string>();
            foreach (string name in data.Ships.Keys)
            {
                if (data.Ships[name].OutfitNames.Count == 0)
                {
                    continue;
                }

                data.BuildShip(name, out List<string> missing);
                if (missing.Count > 0)
                {
                    broken.Add($"{name}: {string.Join(", ", missing.Distinct())}");
                }
            }

            Assert.IsEmpty(broken,
                "ships referencing outfits our loader did not parse:\n" +
                string.Join("\n", broken.Take(15)));
        }

        [Test]
        public void FlyableShipsHaveSaneHandlingNumbers()
        {
            GameData data = Data();

            int checkedCount = 0;
            foreach (string name in data.Ships.Keys)
            {
                Ship ship = data.BuildShip(name, out _);
                if (ship.Thrust <= 0.0 || ship.Mass <= 0.0)
                {
                    continue;
                }

                checkedCount++;
                Assert.Greater(ship.MaxVelocity, 0.0, $"{name} should have a positive top speed");
                Assert.IsFalse(double.IsNaN(ship.Acceleration), $"{name} acceleration is NaN");
                Assert.IsFalse(double.IsInfinity(ship.MaxVelocity), $"{name} has infinite top speed (zero drag?)");
            }

            Assert.Greater(checkedCount, 100, "should have found plenty of powered ships");
        }

        [Test]
        public void SystemsHaveOrbitingObjectsThatMoveOverTime()
        {
            GameData data = Data();

            StarSystem system = data.Systems.Values.First(s => s.Objects.Count > 0);
            List<StellarObject> orbiting = system.AllObjects().Where(o => o.Distance > 0.0).ToList();
            Assert.IsNotEmpty(orbiting, "expected at least one object away from the system centre");

            system.SetDate(0.0);
            Point start = orbiting[0].Position;
            system.SetDate(30.0);
            Point later = orbiting[0].Position;

            Assert.AreNotEqual(start, later, "orbiting objects should move as the date advances");
            Assert.AreEqual(orbiting[0].Distance, start.Length, 1e-6,
                "a circular orbit keeps a constant radius");
        }

        [Test]
        public void UnhandledContentDoesNotGrowBeyondTheReviewedBaseline()
        {
            GameData data = Data();

            // Known incomplete content at tools/upstream-ref.txt. Improvements may
            // reduce these counts. New kinds or growth need review when changing the
            // loader or updating the pinned dataset; they must not silently pass.
            var baseline = new Dictionary<string, int>
            {
                ["phrase"] = 867, ["tip"] = 560, ["effect"] = 309,
                ["news"] = 219, ["color"] = 182, ["interface"] = 42,
                ["help"] = 40, ["swizzle"] = 31, ["hazard"] = 30,
                ["message"] = 27, ["galaxy"] = 26, ["person"] = 16,
                ["formation"] = 14, ["message category"] = 9,
                ["landing message"] = 8, ["category"] = 4, ["rating"] = 3,
                ["substitutions"] = 3, ["gamerules preset"] = 1,
            };

            TestContext.WriteLine("Top-level node kinds not yet modelled (count):");
            foreach (KeyValuePair<string, int> pair in data.UnhandledNodes.OrderByDescending(p => p.Value))
            {
                TestContext.WriteLine($"  {pair.Key,-24} {pair.Value}");
            }

            var regressions = data.UnhandledNodes.Where(pair =>
                !baseline.TryGetValue(pair.Key, out int maximum) || pair.Value > maximum);
            Assert.IsEmpty(regressions,
                "unhandled upstream content grew beyond the reviewed baseline");
        }
    }
}
