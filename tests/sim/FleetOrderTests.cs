using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Fleet commands: what the player's escorts do when told. Port checks against
    /// upstream <c>AI::MoveTo</c> and the escort branch of <c>AI::Step</c>.
    /// </summary>
    [TestFixture]
    public class FleetOrderTests
    {
        private static Ship Make(string name = "Escort")
        {
            var definition = new ShipDefinition(name);
            definition.Load(new DataFile(
                $"ship \"{name}\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"drag\" 2\n" +
                "\t\t\"hull\" 500\n\t\t\"thrust\" 25\n\t\t\"turn\" 300\n" +
                "\t\t\"energy capacity\" 1000\n\t\t\"energy generation\" 5\n", "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();
            ship.SetLevels(hull: ship.MaxHull, energy: ship.MaxEnergy);
            return ship;
        }

        private static PlayerFleet FleetOf(out Ship flagship, out Ship escort)
        {
            var fleet = new PlayerFleet();
            flagship = Make("Flagship");
            escort = Make();
            fleet.Add(flagship);
            fleet.Add(escort);
            fleet.SetFlagship(flagship);
            return fleet;
        }

        // --- Escorting ------------------------------------------------------------

        [Test]
        public void AnEscortClosesOnItsFlagship()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            flagship.Position = new Point(0.0, 0.0);
            escort.Position = new Point(0.0, 2000.0);

            double before = (flagship.Position - escort.Position).Length;

            for (int frame = 0; frame < 2000; frame++)
                FleetOrders.Execute(fleet, FleetOrder.Escort);

            double after = (flagship.Position - escort.Position).Length;
            TestContext.WriteLine($"escort closed {before:F0} -> {after:F0}");

            Assert.Less(after, before, "an escort should follow");
            Assert.Less(after, 400.0, "and end up somewhere near its flagship");
        }

        [Test]
        public void AnEscortAlreadyOnStationStopsCorrecting()
        {
            // The arrival test is on RELATIVE velocity. An escort matching a moving
            // flagship is at rest with respect to it, and testing absolute speed would
            // have it endlessly correcting a formation it was already holding.
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            flagship.Position = new Point(0.0, 0.0);
            flagship.Velocity = new Point(5.0, 0.0);
            escort.Position = new Point(10.0, 0.0);
            escort.Velocity = new Point(5.0, 0.0);

            Command command = FleetOrders.For(FleetOrder.Escort, escort, flagship);

            Assert.IsFalse(command.Forward, "no thrust needed when already on station");
            Assert.AreEqual(0.0, command.Turn, 1e-9);
        }

        [Test]
        public void AFlagshipIsNotItsOwnEscort()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out _);

            Assert.AreEqual(Command.None, FleetOrders.For(FleetOrder.Escort, flagship, flagship));
            CollectionAssert.DoesNotContain(fleet.Escorts.ToList(), flagship);
        }

        // --- Holding --------------------------------------------------------------

        [Test]
        public void HoldBringsAMovingEscortToRest()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            escort.Facing = new Angle(0.0);
            for (int i = 0; i < 200; i++)
                escort.Step(new Command { Forward = true });

            double cruising = escort.Velocity.Length;
            Assert.Greater(cruising, 0.0);

            for (int frame = 0; frame < 3000; frame++)
                FleetOrders.Execute(fleet, FleetOrder.Hold);

            TestContext.WriteLine($"hold slowed {cruising:F2} -> {escort.Velocity.Length:F2}");
            Assert.Less(escort.Velocity.Length, cruising * 0.5, "hold should actually stop it");
        }

        [Test]
        public void AStationaryEscortToldToHoldDoesNothing()
        {
            PlayerFleet fleet = FleetOf(out _, out Ship escort);
            escort.Velocity = Point.Zero;

            Assert.AreEqual(Command.None, FleetOrders.For(FleetOrder.Hold, escort, null));
        }

        // --- Attacking ------------------------------------------------------------

        [Test]
        public void AttackTargetSendsEscortsAtTheFlagshipsTarget()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            Ship enemy = Make("Enemy");
            enemy.Position = new Point(0.0, 3000.0);
            escort.Position = new Point(0.0, 0.0);

            double before = (enemy.Position - escort.Position).Length;

            for (int frame = 0; frame < 2000; frame++)
                FleetOrders.Execute(fleet, FleetOrder.AttackTarget, enemy);

            Assert.Less((enemy.Position - escort.Position).Length, before,
                "escorts should close on the ordered target");
        }

        [Test]
        public void AttackWithNoTargetFallsBackToEscorting()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            flagship.Position = new Point(0.0, 0.0);
            escort.Position = new Point(0.0, 900.0);

            Command command = FleetOrders.For(FleetOrder.AttackTarget, escort, flagship, null);

            Assert.IsTrue(command.Forward || command.Turn != 0.0,
                "with nothing to attack it should still keep formation");
        }

        [Test]
        public void ADisabledEscortObeysNothing()
        {
            PlayerFleet fleet = FleetOf(out Ship flagship, out Ship escort);
            escort.SetLevels(hull: 0.0);
            Assert.IsTrue(escort.IsDisabled);

            Assert.AreEqual(Command.None, FleetOrders.For(FleetOrder.Escort, escort, flagship));
        }

        // --- Fleet-wide -----------------------------------------------------------

        [Test]
        public void OrdersReachEveryEscortButNotTheFlagship()
        {
            var fleet = new PlayerFleet();
            Ship flagship = Make("Flagship");
            fleet.Add(flagship);
            fleet.SetFlagship(flagship);

            for (int i = 0; i < 3; i++)
            {
                Ship escort = Make($"Escort {i}");
                escort.Position = new Point(1000.0 * (i + 1), 0.0);
                fleet.Add(escort);
            }

            Point flagshipBefore = flagship.Position;
            int moved = FleetOrders.Execute(fleet, FleetOrder.Gather);

            Assert.AreEqual(3, moved, "every escort should have been given an order");
            Assert.AreEqual(flagshipBefore, flagship.Position,
                "the flagship takes its orders from the player, not from the fleet command");
        }

        [Test]
        public void ParkedShipsAreNotEscorts()
        {
            // Ship management: a parked ship is left behind and should not be flying in
            // formation.
            PlayerFleet fleet = FleetOf(out _, out Ship escort);
            Assert.AreEqual(1, fleet.Escorts.Count());

            escort.IsParked = true;

            Assert.IsEmpty(fleet.Escorts.ToList());
            Assert.AreEqual(0, FleetOrders.Execute(fleet, FleetOrder.Gather));
        }
    }
}
