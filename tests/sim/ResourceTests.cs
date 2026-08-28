using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Power, heat and repair: what a ship spends to manoeuvre and what it makes back.
    /// Partial port of upstream <c>Ship::DoGeneration</c> and the movement costs in
    /// <c>Ship::Move</c>.
    /// </summary>
    [TestFixture]
    public class ResourceTests
    {
        private static Ship Make(params string[] attributes)
        {
            var text = new System.Text.StringBuilder("ship \"Test\"\n\tattributes\n");
            text.Append("\t\t\"mass\" 100\n\t\t\"drag\" 2\n\t\t\"hull\" 1000\n");
            foreach (string attribute in attributes)
                text.Append($"\t\t{attribute}\n");

            var definition = new ShipDefinition("Test");
            definition.Load(new DataFile(text.ToString(), "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();
            return ship;
        }

        // --- Movement costs -------------------------------------------------------

        [Test]
        public void ThrustingSpendsEnergy()
        {
            Ship ship = Make("\"thrust\" 20", "\"thrusting energy\" 5", "\"energy capacity\" 100");
            ship.SetLevels(energy: 100);

            ship.Step(new Command { Forward = true });

            Assert.AreEqual(95.0, ship.Energy, 1e-9);
            Assert.IsTrue(ship.IsThrusting);
        }

        [Test]
        public void TurningSpendsEnergy()
        {
            Ship ship = Make("\"turn\" 200", "\"turning energy\" 4", "\"energy capacity\" 100");
            ship.SetLevels(energy: 100);

            ship.Step(new Command { Turn = 1.0 });

            Assert.AreEqual(96.0, ship.Energy, 1e-9);
        }

        [Test]
        public void AShipShortOfPowerManoeuvresWeaklyRatherThanNotAtAll()
        {
            // Upstream's FractionalUsage scales the command by what can be afforded, so
            // a browning-out ship stays controllable instead of locking solid.
            Ship ship = Make("\"turn\" 200", "\"turning energy\" 10", "\"energy capacity\" 100");
            ship.SetLevels(energy: 2.5);   // a quarter of one full turn

            double before = ship.Facing.AbsDegrees;
            ship.Step(new Command { Turn = 1.0 });
            double turned = System.Math.Abs(ship.Facing.AbsDegrees - before);

            Assert.Greater(turned, 0.0, "it must still turn");
            // Facing is quantised: upstream stores an Angle in fixed steps, so a small
            // turn lands on the nearest step rather than exactly on the figure.
            Assert.AreEqual(ship.TurnRate * 0.25, turned, 0.01, "at a quarter rate");
            Assert.AreEqual(0.0, ship.Energy, 1e-9, "spending exactly what it had");
        }

        [Test]
        public void AShipWithNoPowerCannotThrust()
        {
            Ship ship = Make("\"thrust\" 20", "\"thrusting energy\" 5", "\"energy capacity\" 100");
            ship.SetLevels(energy: 0);

            ship.Step(new Command { Forward = true });

            Assert.IsFalse(ship.IsThrusting);
            Assert.AreEqual(0.0, ship.Velocity.Length, 1e-9);
        }

        [Test]
        public void FreeManoeuvringStaysFree()
        {
            // Most small hulls state no thrusting energy at all; those must not be
            // grounded by a cost they never declared.
            Ship ship = Make("\"thrust\" 20");
            ship.SetLevels(energy: 0);

            ship.Step(new Command { Forward = true });

            Assert.IsTrue(ship.IsThrusting);
            Assert.Greater(ship.Velocity.Length, 0.0);
        }

        // --- Generation -----------------------------------------------------------

        [Test]
        public void EnergyRegeneratesAndIsCappedAtCapacity()
        {
            Ship ship = Make("\"energy generation\" 3", "\"energy capacity\" 10");
            ship.SetLevels(energy: 0);

            ship.Step(Command.None);
            Assert.AreEqual(3.0, ship.Energy, 1e-9);

            for (int i = 0; i < 20; i++)
                ship.Step(Command.None);

            Assert.AreEqual(10.0, ship.Energy, 1e-9, "capacity is the ceiling");
        }

        [Test]
        public void IdleConsumptionOffsetsGeneration()
        {
            Ship ship = Make("\"energy generation\" 5", "\"energy consumption\" 2",
                             "\"energy capacity\" 100");
            ship.SetLevels(energy: 0);

            ship.Step(Command.None);

            Assert.AreEqual(3.0, ship.Energy, 1e-9);
        }

        [Test]
        public void AShipCanFlyIndefinitelyWhenItGeneratesMoreThanItSpends()
        {
            // The regression this whole file exists for: adding a cost to manoeuvring
            // without adding generation browns out every ship in the game permanently.
            Ship ship = Make("\"thrust\" 20", "\"thrusting energy\" 2",
                             "\"energy generation\" 3", "\"energy capacity\" 50");
            ship.SetLevels(energy: 50);

            for (int i = 0; i < 5000; i++)
                ship.Step(new Command { Forward = true });

            Assert.Greater(ship.Energy, 0.0, "a ship with a surplus must never run dry");
            Assert.IsTrue(ship.IsThrusting, "and must still be under power after a long burn");
        }

        [Test]
        public void ShieldsRegenerateUpToTheirMaximum()
        {
            Ship ship = Make("\"shields\" 100", "\"shield generation\" 2",
                             "\"energy capacity\" 100");
            ship.SetLevels(shields: 0, energy: 100);

            ship.Step(Command.None);
            Assert.AreEqual(2.0, ship.Shields, 1e-9);

            for (int i = 0; i < 200; i++)
                ship.Step(Command.None);

            Assert.AreEqual(100.0, ship.Shields, 1e-9);
        }

        [Test]
        public void ShieldRegenerationIsLimitedByAvailableEnergy()
        {
            // Costs are per point regenerated, so a ship short of power mends
            // proportionally rather than for free.
            Ship ship = Make("\"shields\" 100", "\"shield generation\" 10",
                             "\"shield energy\" 4", "\"energy capacity\" 100");
            ship.SetLevels(shields: 0, energy: 20);   // enough for 5 points, not 10

            ship.Step(Command.None);

            Assert.AreEqual(5.0, ship.Shields, 1e-9);
            Assert.AreEqual(0.0, ship.Energy, 1e-9);
        }

        [Test]
        public void HullRepairsWhenTheShipHasARepairRate()
        {
            Ship ship = Make("\"hull repair rate\" 5", "\"energy capacity\" 100");
            ship.SetLevels(hull: 500, energy: 100);

            ship.Step(Command.None);

            Assert.AreEqual(505.0, ship.Hull, 1e-9);
        }

        [Test]
        public void RepairNeverOvershootsTheMaximum()
        {
            Ship ship = Make("\"hull repair rate\" 50", "\"energy capacity\" 100");
            ship.SetLevels(hull: 990, energy: 100);

            ship.Step(Command.None);

            Assert.AreEqual(1000.0, ship.Hull, 1e-9);
        }

        // --- Heat -----------------------------------------------------------------

        [Test]
        public void HeatIsGeneratedAndDissipated()
        {
            Ship ship = Make("\"heat generation\" 10", "\"heat dissipation\" 1",
                             "\"energy capacity\" 100");

            ship.Step(Command.None);
            Assert.Greater(ship.Heat, 0.0, "a running hull makes heat");

            double peak = ship.Heat;
            for (int i = 0; i < 2000; i++)
                ship.Step(Command.None);

            // Dissipation is a fraction of current heat, so heat approaches an
            // equilibrium rather than climbing without limit.
            Assert.Greater(ship.Heat, peak);
            Assert.Less(ship.Heat, 10.0 / ship.HeatDissipation + 1.0,
                "heat must settle at generation over dissipation, not run away");
        }

        [Test]
        public void ACoolHullSheddingNoHeatStaysAtZero()
        {
            Ship ship = Make("\"heat dissipation\" 1", "\"energy capacity\" 100");

            for (int i = 0; i < 100; i++)
                ship.Step(Command.None);

            Assert.AreEqual(0.0, ship.Heat, 1e-9, "heat must never go negative");
        }

        [Test]
        public void ThrustingMakesHeat()
        {
            Ship ship = Make("\"thrust\" 20", "\"thrusting heat\" 6", "\"energy capacity\" 100");

            ship.Step(new Command { Forward = true });

            Assert.Greater(ship.Heat, 0.0);
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void RealShipsSustainAContinuousBurn()
        {
            // End to end on real content: fly a stock hull flat out for a simulated
            // minute and check it is still under power at the end.
            GameData data = UpstreamData.Instance;

            foreach (string model in new[] { "Shuttle", "Sparrow", "Star Barge", "Falcon" })
            {
                if (!data.Ships.ContainsKey(model))
                    continue;

                Ship ship = data.BuildShip(model);
                ship.BuildMounts();
                ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                               energy: ship.MaxEnergy, fuel: ship.MaxFuel);

                for (int frame = 0; frame < 3600; frame++)
                    ship.Step(new Command { Forward = true });

                TestContext.WriteLine(
                    $"{model}: after a minute at full thrust, energy {ship.Energy:F0}/{ship.MaxEnergy:F0}, " +
                    $"heat {ship.Heat:F0}/{ship.MaxHeat:F0}, speed {ship.Velocity.Length:F1}");

                Assert.IsTrue(ship.IsThrusting,
                    $"a stock {model} should still be under power after a sustained burn");
                Assert.Greater(ship.Velocity.Length, 0.0, model);
            }
        }
    }
}
