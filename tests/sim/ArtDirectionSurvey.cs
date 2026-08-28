using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class ArtDirectionSurvey
    {
        [Test, Explicit]
        public void SurveyTheFleet()
        {
            GameData data = UpstreamData.Instance;
            var ships = data.Ships.Values.Where(s => s.Attributes.Get("mass") > 0).ToList();

            var masses = ships.Select(s => s.Attributes.Get("mass")).OrderBy(m => m).ToList();
            double P(double q) => masses[(int)Math.Min(masses.Count - 1, q * masses.Count)];
            TestContext.WriteLine($"ships={ships.Count} mass p05={P(.05):F0} p25={P(.25):F0} p50={P(.50):F0} p75={P(.75):F0} p95={P(.95):F0} max={masses.Last():F0}");

            var guns = ships.Select(s => s.Guns.Count).ToList();
            var turrets = ships.Select(s => s.Turrets.Count).ToList();
            var engines = ships.Select(s => s.Engines.Count).ToList();
            TestContext.WriteLine($"guns max={guns.Max()} mean={guns.Average():F1} | turrets max={turrets.Max()} mean={turrets.Average():F1} | engines max={engines.Max()} mean={engines.Average():F1}");

            var cats = ships.GroupBy(s => s.Category).OrderByDescending(g => g.Count());
            TestContext.WriteLine("categories: " + string.Join(", ", cats.Select(g => $"{g.Key}({g.Count()})")));

            foreach (var g in cats.Take(12))
            {
                var m = g.Select(s => s.Attributes.Get("mass")).OrderBy(x => x).ToList();
                TestContext.WriteLine($"  {g.Key,-22} n={g.Count(),-4} mass median={m[m.Count/2],-7:F0} min={m.First():F0} max={m.Last():F0}");
            }
        }
    }
}
