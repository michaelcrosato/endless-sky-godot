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

        [Test]
        public void APartialTurnCommandIsClampedByTheThrottleRatherThanScaledByIt()
        {
            // Ship.cpp:4909 clamps: copysign(min(|turn|, maxTurnThrottle), turn). A
            // ship that can afford 56% of a full-rate turn still executes a commanded
            // 30% turn at the full 30%. Multiplying instead gave 30% x 56% = 17%, so
            // every partial command was quietly weaker than asked for -- and upstream
            // multiplies only for THRUST (Ship.cpp:4928), which is why the two look
            // interchangeable and are not.
            Ship ship = Make("\"turn\" 200", "\"turning energy\" 10", "\"energy capacity\" 100");
            ship.SetLevels(energy: 5.6);   // 56% of one full-rate turn

            double before = ship.Facing.Degrees;
            ship.Step(new Command { Turn = 0.3 });

            Assert.AreEqual(0.3 * ship.TurnRate, ship.Facing.Degrees - before, 0.01,
                "the command is under the throttle, so it goes through in full");
            Assert.AreEqual(5.6 - 3.0, ship.Energy, 1e-9,
                "and it costs the fraction actually turned");
        }

        [Test]
        public void ATurnCommandBeyondTheThrottleIsCutToIt()
        {
            Ship ship = Make("\"turn\" 200", "\"turning energy\" 10", "\"energy capacity\" 100");
            ship.SetLevels(energy: 4.0);   // 40% of one full-rate turn

            double before = ship.Facing.Degrees;
            ship.Step(new Command { Turn = 1.0 });

            Assert.AreEqual(0.4 * ship.TurnRate, ship.Facing.Degrees - before, 0.01,
                "asked for everything, gets what it can pay for");
        }

        [Test]
        public void AShipThatRunsOutOfPowerDriftsToAStop()
        {
            // Ship.cpp:4896 takes the drag-only branch on `isDisabled || NeedsEnergy()`.
            // Testing only IsDisabled let a browned-out hull coast forever at whatever
            // velocity it had — no thrust, no turn, and no drag either, which is the one
            // state that should feel like being adrift.
            // thrust 20 / mass 100 / drag 2 gives a terminal speed of 10, and a
            // 1000-unit reserve at 5 a frame is exactly 200 frames of thrust — so it
            // gets up to speed and arrives at the far end with the tank empty.
            Ship ship = Make("\"thrust\" 20", "\"thrusting energy\" 5",
                             "\"turn\" 200", "\"turning energy\" 5",
                             "\"energy capacity\" 1000");

            for (int frame = 0; frame < 200; frame++)
                ship.Step(new Command { Forward = true });

            Assert.Greater(ship.Velocity.Length, 5.0, "it got moving first");
            Assert.LessOrEqual(ship.Energy, 0.00390625, "and ran the reactor dry doing it");

            // Drag sheds 2% of the remaining speed a frame, so a halt is asymptotic:
            // long enough for 0.98^n to take ten units below a millionth.
            for (int frame = 0; frame < 1500; frame++)
                ship.Step(Command.None);

            Assert.Less(ship.Velocity.Length, 1e-6, "with no power it drifts to a halt");
        }

        [Test]
        public void AShipWithItsOwnReactorNeverCountsAsOutOfPower()
        {
            // NeedsEnergy is false the moment a ship generates more than it draws
            // (Ship.cpp:2879-2882), whatever its current reserve.
            Ship ship = Make("\"thrust\" 20", "\"thrusting energy\" 5",
                             "\"energy capacity\" 100", "\"energy generation\" 6");

            ship.SetLevels(energy: 0.0);
            Assert.IsFalse(ship.NeedsEnergy, "a working reactor is not a dead ship");
        }

        [Test]
        public void AShipThatCostsNoEnergyToMoveNeverNeedsAny()
        {
            // RequiresMovementEnergy (Ship.cpp:5098): a hull whose engines are free
            // does not brown out, however empty its reserve.
            Ship ship = Make("\"thrust\" 20", "\"energy capacity\" 100");

            ship.SetLevels(energy: 0.0);
            Assert.IsFalse(ship.NeedsEnergy);
        }

        // --- Landing and taking off -----------------------------------------------

        [Test]
        public void ASpaceportPutsEveryStatBackToFull()
        {
            // Ship.cpp:2644-2668. This is the ONLY repair path most hulls have: the
            // per-frame regeneration in StepResources only runs for ships carrying a
            // "hull repair rate" or "shield generation" outfit, which most do not. With
            // nothing calling Recharge, damage was permanent for the rest of the game.
            Ship ship = Make("\"shields\" 500", "\"energy capacity\" 200",
                             "\"fuel capacity\" 300", "\"heat dissipation\" 1");

            ship.SetLevels(shields: 10.0, hull: 20.0, energy: 5.0, fuel: 3.0, heat: 900.0);
            ship.Recharge(RechargeType.All);

            Assert.AreEqual(ship.MaxShields, ship.Shields, 1e-9);
            Assert.AreEqual(ship.MaxHull, ship.Hull, 1e-9);
            Assert.AreEqual(ship.MaxEnergy, ship.Energy, 1e-9);
            Assert.AreEqual(ship.MaxFuel, ship.Fuel, 1e-9);
            Assert.AreEqual(0.0, ship.Heat, 1e-9, "a ship on the ground cools off");
        }

        [Test]
        public void AWorldWithNoPortStillRestoresWhatTheShipMakesItself()
        {
            // Upstream ORs the port's services with the ship's own regeneration, so a
            // hull with a shield generator recovers its shields anywhere, and one
            // without recovers nothing at a world with no port.
            Ship generating = Make("\"shields\" 500", "\"shield generation\" 2");
            generating.SetLevels(shields: 0.0);
            generating.Recharge(RechargeType.None);
            Assert.AreEqual(generating.MaxShields, generating.Shields, 1e-9);

            Ship bare = Make("\"shields\" 500");
            bare.SetLevels(shields: 0.0);
            bare.Recharge(RechargeType.None);
            Assert.AreEqual(0.0, bare.Shields, 1e-9, "nothing to recharge it with");
        }

        [Test]
        public void TakingOffRechargesTheLocalFleetButNotParkedOrCrippledShips()
        {
            // PlayerInfo.cpp:1870 skips parked and disabled ships entirely: a hull left
            // in a hangar is not being serviced, and a wreck is not repaired by landing
            // next to one. Recharging only the flagship left every escort in the fleet
            // carrying its damage forever.
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "ship \"Hauler\"",
                "	attributes",
                "		\"mass\" 100",
                "		\"hull\" 400",
                "		\"shields\" 200",
                "planet \"Home\"",
                "	government \"Republic\"",
                "	spaceport `Busy.`",
                "system \"Sol\"",
                "	pos 0 0",
                "	object \"Home\"",
                "		sprite planet/earth",
                "		distance 500",
                "\t\tperiod 300") + "\n");

            var player = new PlayerState(data);
            Ship flagship = data.BuildShip("Hauler");
            Ship escort = data.BuildShip("Hauler");
            Ship parked = data.BuildShip("Hauler");
            Ship wreck = data.BuildShip("Hauler");

            foreach (Ship ship in new[] { flagship, escort, parked, wreck })
            {
                ship.CurrentSystem = data.Systems["Sol"];
                player.Fleet.Add(ship);
            }

            player.Fleet.SetFlagship(flagship);
            parked.IsParked = true;

            flagship.SetLevels(shields: 0.0);
            escort.SetLevels(shields: 0.0);
            parked.SetLevels(shields: 0.0);
            wreck.Disable();

            player.EnterSystem(data.Systems["Sol"]);
            player.Land(data.Planets["Home"]);
            player.TakeOff();

            Assert.AreEqual(flagship.MaxShields, flagship.Shields, 1e-9, "the flagship");
            Assert.AreEqual(escort.MaxShields, escort.Shields, 1e-9, "and every escort with it");
            Assert.AreEqual(0.0, parked.Shields, 1e-9, "a parked hull is not being serviced");
            Assert.IsTrue(wreck.IsDisabled, "and a wreck is not repaired by landing beside one");
        }

        [TestCase("")]
        [TestCase("shield generation")]
        [TestCase("hull repair rate")]
        [TestCase("energy generation")]
        [TestCase("fuel generation")]
        public void TakeOffGivesRemoteShipsOnlyTheirOwnGeneration(string generation)
        {
            var data = new GameData();
            data.LoadText("planet Home\n\tspaceport Busy\nsystem Sol\n\tpos 0 0\n\tobject Home\n" +
                "system Vega\n\tpos 100 0\n");
            var player = new PlayerState(data);
            Ship local = Make("shields 200", "\"energy capacity\" 500", "\"fuel capacity\" 300");
            Ship remote = Make("shields 200", "\"energy capacity\" 500", "\"fuel capacity\" 300",
                generation.Length > 0 ? $"\"{generation}\" 1" : string.Empty);
            local.CurrentSystem = data.Systems["Sol"];
            remote.CurrentSystem = data.Systems["Vega"];
            player.Fleet.Add(local);
            player.Fleet.Add(remote);
            foreach (Ship ship in new[] { local, remote })
                ship.SetLevels(shields: 10, hull: 500, energy: 5, fuel: 7, heat: 50);
            player.EnterSystem(local.CurrentSystem);
            player.Land(data.Planets["Home"]);

            player.TakeOff();

            Assert.Multiple(() =>
            {
                Assert.AreEqual(200, local.Shields);
                Assert.AreEqual(1000, local.Hull);
                Assert.AreEqual(500, local.Energy);
                Assert.AreEqual(300, local.Fuel);
                Assert.AreEqual(generation == "shield generation" ? 200 : 10, remote.Shields);
                Assert.AreEqual(generation == "hull repair rate" ? 1000 : 500, remote.Hull);
                Assert.AreEqual(generation == "energy generation" ? 500 : 5, remote.Energy);
                Assert.AreEqual(generation == "fuel generation" ? 300 : 7, remote.Fuel);
                Assert.AreEqual(0, remote.Heat, "remote ships still receive upstream's no-port recharge");
                Assert.IsNull(player.CurrentPlanet);
            });
        }

        [TestCase("in flight")]
        [TestCase("missing system")]
        [TestCase("missing flagship")]
        public void TakeOffNeedsALandedPilotWithASystemAndFlagship(string state)
        {
            var data = new GameData();
            data.LoadText("planet Home\n\tspaceport Busy\nsystem Sol\n\tpos 0 0\n\tobject Home\n");
            var player = new PlayerState(data);
            Ship ship = Make("\"energy capacity\" 500", "\"energy generation\" 1");
            ship.CurrentSystem = data.Systems["Sol"];
            ship.SetLevels(energy: 5, heat: 50);
            if (state != "missing flagship") player.Fleet.Add(ship);
            player.EnterSystem(ship.CurrentSystem);
            if (state != "in flight") player.Land(data.Planets["Home"]);
            if (state == "missing system") player.CurrentSystem = null;
            Planet? before = player.CurrentPlanet;

            player.TakeOff();

            Assert.Multiple(() =>
            {
                Assert.AreSame(before, player.CurrentPlanet, "a rejected takeoff must not change location");
                Assert.AreEqual(5, ship.Energy, "it must not trigger an instant recharge");
                Assert.AreEqual(50, ship.Heat);
            });
        }

        // --- Disabled and overheated ----------------------------------------------

        [Test]
        public void ADisabledShipRepairsNothingAndGeneratesNoPower()
        {
            // Upstream gates the whole generation block on !isDisabled (Ship.cpp:4331),
            // which is what makes a crippled hull stay crippled until someone boards or
            // repairs it. Running it unguarded let a disabled ship quietly rebuild its
            // shields and hull while the fight went on around it.
            Ship ship = Make(
                "\"shields\" 500", "\"hull repair rate\" 10", "\"shield generation\" 20",
                "\"energy generation\" 30", "\"energy capacity\" 1000");

            ship.SetLevels(shields: 0.0, hull: 1.0, energy: 0.0);
            Assert.IsTrue(ship.IsDisabled, "a hull below the minimum is disabled");

            ship.StepResources();

            Assert.AreEqual(1.0, ship.Hull, 1e-9, "no hull repair while disabled");
            Assert.AreEqual(0.0, ship.Shields, 1e-9, "no shield regeneration while disabled");
            Assert.AreEqual(0.0, ship.Energy, 1e-9, "no energy generation while disabled");
        }

        [Test]
        public void AShipThatOverheatsIsDisabledUntilItCoolsBelowNineTenths()
        {
            // Ship.cpp:4449-4457: crossing MaxHeat sets isOverheated, and only dropping
            // under .9 * MaxHeat clears it -- hysteresis, so a ship on the edge does not
            // flicker in and out of commission. Ship.cpp:4469 folds that into isDisabled.
            // Heat was accumulated and displayed here but had no effect whatever.
            Ship ship = Make("\"heat capacity\" 1", "\"heat dissipation\" 0");

            double max = ship.MaxHeat;
            Assert.Greater(max, 0.0);

            ship.SetLevels(heat: max * 1.01);
            ship.StepResources();
            Assert.IsTrue(ship.IsDisabled, "over its heat capacity, a ship shuts down");

            // Still hot, but under the ceiling: upstream keeps it disabled.
            ship.SetLevels(heat: max * 0.95);
            ship.StepResources();
            Assert.IsTrue(ship.IsDisabled, "between .9 and 1.0 of MaxHeat it stays disabled");

            ship.SetLevels(heat: max * 0.5);
            ship.StepResources();
            Assert.IsFalse(ship.IsDisabled, "cooled well below the ceiling, it comes back");
        }

        [Test]
        public void OverheatingBurnsHullOnlyWhereTheAttributeAsksForIt()
        {
            // overheatDamageRate defaults to 0 (ShipAttributeCache.h:81), so vanilla
            // ships shut down without burning. A hull that declares the attribute takes
            // rate * (heatFraction / (1 + threshold)) per frame.
            Ship plain = Make("\"heat capacity\" 1", "\"heat dissipation\" 0");
            plain.SetLevels(hull: 1000.0, heat: plain.MaxHeat * 2.0);
            plain.StepResources();
            Assert.AreEqual(1000.0, plain.Hull, 1e-9, "no burn without the attribute");

            Ship burner = Make(
                "\"heat capacity\" 1", "\"heat dissipation\" 0", "\"overheat damage rate\" 5");
            burner.SetLevels(hull: 1000.0, heat: burner.MaxHeat * 2.0);
            burner.StepResources();
            Assert.AreEqual(990.0, burner.Hull, 1e-9, "rate 5 at twice the ceiling burns 10");
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
