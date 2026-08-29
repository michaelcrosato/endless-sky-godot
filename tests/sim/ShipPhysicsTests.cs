using System;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Flight-model parity tests.
    ///
    /// The ship below is deliberately given round numbers so every expected value can be
    /// derived by hand from upstream's formulas rather than recorded from our own output
    /// (which would only prove the code agrees with itself):
    ///
    ///   mass 100, drag 10, thrust 20, turn 3000
    ///   acceleration = thrust / mass      = 0.20 units/frame^2
    ///   turn rate    = turn / mass        = 30 degrees/frame
    ///   drag force   = drag / mass        = 0.10 per frame
    ///   max velocity = thrust / drag      = 2.00 units/frame
    /// </summary>
    public class ShipPhysicsTests
    {
        private const string TestData =
            "ship \"Test Ship\"\n" +
            "\tattributes\n" +
            "\t\t\"mass\" 100\n" +
            "\t\t\"drag\" 10\n" +
            // A hull, so that "disabled" is a state this ship can actually be in.
            // The disabled flag is derived from hull against the minimum rather than
            // set, so a ship with no hull at all can never be crippled.
            "\t\t\"hull\" 1000\n" +
            "\toutfits\n" +
            "\t\t\"Test Engine\"\n" +
            "\n" +
            "outfit \"Test Engine\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 20\n" +
            "\t\"turn\" 3000\n";

        private static Ship MakeShip()
        {
            var data = new GameData();
            data.LoadText(TestData, "physics-fixture");
            return data.BuildShip("Test Ship");
        }

        [Test]
        public void DerivedValuesMatchUpstreamFormulas()
        {
            Ship ship = MakeShip();

            Assert.AreEqual(100.0, ship.Mass, 1e-9, "hull mass plus outfit mass");
            Assert.AreEqual(20.0, ship.Thrust, 1e-9);
            Assert.AreEqual(0.2, ship.Acceleration, 1e-9);
            Assert.AreEqual(30.0, ship.TurnRate, 1e-9);
            Assert.AreEqual(0.1, ship.DragForce, 1e-9);
            Assert.AreEqual(2.0, ship.MaxVelocity, 1e-9);
        }

        [Test]
        public void DragIsClampedToMassSoShipsNeverReverseUnderDrag()
        {
            // A ship whose drag exceeds its mass would otherwise get a drag force > 1,
            // which would flip its velocity every frame.
            var data = new GameData();
            data.LoadText(
                "ship Draggy\n\tattributes\n\t\t\"mass\" 10\n\t\t\"drag\" 999\n",
                "drag-fixture");
            Ship ship = data.BuildShip("Draggy");

            Assert.AreEqual(1.0, ship.DragForce, 1e-9);
            Assert.AreEqual(10.0, ship.Drag, 1e-9, "drag is clamped to mass");
        }

        [Test]
        public void FirstThrustFrameAppliesFullAcceleration()
        {
            Ship ship = MakeShip();

            ship.Step(new Command { Forward = true });

            // Facing 0 is "up the screen", i.e. (0, -1) in simulation coordinates.
            Assert.AreEqual(0.0, ship.Velocity.X, 1e-12);
            Assert.AreEqual(-0.2, ship.Velocity.Y, 1e-12);
            Assert.AreEqual(-0.2, ship.Position.Y, 1e-12, "position advances by the new velocity");
        }

        [Test]
        public void SecondThrustFrameHasDragSubtracted()
        {
            Ship ship = MakeShip();

            ship.Step(new Command { Forward = true });
            ship.Step(new Command { Forward = true });

            // v2 = v1 + a - v1 * dragForce = 0.2 + 0.2 - 0.02 = 0.38
            Assert.AreEqual(-0.38, ship.Velocity.Y, 1e-12);
        }

        [Test]
        public void SustainedThrustConvergesOnMaxVelocity()
        {
            Ship ship = MakeShip();

            for (int i = 0; i < 2000; i++)
            {
                ship.Step(new Command { Forward = true });
            }

            Assert.AreEqual(ship.MaxVelocity, ship.Velocity.Length, 1e-9,
                "terminal speed must equal thrust / drag");
        }

        [Test]
        public void CoastingShipKeepsItsVelocityForever()
        {
            // This looks like a bug but is upstream behaviour: drag is applied only inside
            // the acceleration block, so a ship under no thrust never slows down.
            Ship ship = MakeShip();
            for (int i = 0; i < 100; i++)
            {
                ship.Step(new Command { Forward = true });
            }

            Point coastingVelocity = ship.Velocity;

            for (int i = 0; i < 500; i++)
            {
                ship.Step(Command.None);
            }

            Assert.AreEqual(coastingVelocity.X, ship.Velocity.X, 1e-12);
            Assert.AreEqual(coastingVelocity.Y, ship.Velocity.Y, 1e-12);
        }

        [Test]
        public void CoastingShipStillMoves()
        {
            Ship ship = MakeShip();
            ship.Step(new Command { Forward = true });
            Point afterThrust = ship.Position;

            ship.Step(Command.None);

            Assert.AreNotEqual(afterThrust.Y, ship.Position.Y, "position advances while coasting");
        }

        [Test]
        public void DisabledShipDriftsToAStop()
        {
            Ship ship = MakeShip();
            for (int i = 0; i < 200; i++)
            {
                ship.Step(new Command { Forward = true });
            }

            ship.Disable();
            for (int i = 0; i < 400; i++)
            {
                ship.Step(Command.None);
            }

            Assert.Less(ship.Velocity.Length, 1e-6, "a disabled ship coasts to a halt under drag");
        }

        [Test]
        public void TurningIsQuantizedToUpstreamAngleSteps()
        {
            // Upstream stores angles as one of 65536 steps. 30 degrees is
            // llround(30 * 65536 / 360) = 5461 steps, which is very slightly under
            // 30 degrees, so twelve full-rate turn frames land just short of 360.
            Ship ship = MakeShip();

            for (int i = 0; i < 12; i++)
            {
                ship.Step(new Command { Turn = 1.0 });
            }

            Assert.AreEqual((12 * 5461) & 0xFFFF, ship.Facing.Step,
                "turning must accumulate in upstream's fixed-point steps");
            Assert.AreNotEqual(0, ship.Facing.Step, "quantization means this does not close exactly");
        }

        [Test]
        public void TurningLeftAndRightAreSymmetric()
        {
            Ship left = MakeShip();
            Ship right = MakeShip();

            for (int i = 0; i < 5; i++)
            {
                left.Step(new Command { Turn = -1.0 });
                right.Step(new Command { Turn = 1.0 });
            }

            Assert.AreEqual(right.Facing.Degrees, -left.Facing.Degrees, 1e-9);
        }

        [Test]
        public void ThrustFollowsFacingAfterTurning()
        {
            Ship ship = MakeShip();

            // Three frames at 30 degrees per frame puts the nose at 90 degrees, which in
            // upstream's clock-angle convention points along +X.
            for (int i = 0; i < 3; i++)
            {
                ship.Step(new Command { Turn = 1.0 });
            }

            Assert.AreEqual(90.0, ship.Facing.Degrees, 0.01);

            ship.Step(new Command { Forward = true });

            Assert.Greater(ship.Velocity.X, 0.0, "thrust should push along +X when facing 90 degrees");
            Assert.AreEqual(0.0, ship.Velocity.Y, 1e-3);
        }

        [Test]
        public void ReverseThrustIsIgnoredWithoutAReverseThruster()
        {
            Ship ship = MakeShip();
            for (int i = 0; i < 50; i++)
            {
                ship.Step(new Command { Forward = true });
            }

            Point before = ship.Velocity;
            ship.Step(new Command { Back = true });

            Assert.AreEqual(before.Y, ship.Velocity.Y, 1e-12,
                "a ship with no reverse thrust does not even slow under drag");
        }

        [Test]
        public void CargoMassReducesAcceleration()
        {
            Ship empty = MakeShip();
            Ship loaded = MakeShip();
            loaded.CargoMass = 100.0;

            Assert.AreEqual(200.0, loaded.Mass, 1e-9);
            Assert.AreEqual(empty.Acceleration / 2.0, loaded.Acceleration, 1e-9);
            Assert.AreEqual(empty.TurnRate / 2.0, loaded.TurnRate, 1e-9);
        }

        [Test]
        public void CargoDoesNotChangeTopSpeed()
        {
            // Drag and thrust both scale out of the terminal-velocity expression, so a
            // fully loaded ship accelerates slower but tops out at the same speed.
            Ship empty = MakeShip();
            Ship loaded = MakeShip();
            loaded.CargoMass = 100.0;

            Assert.AreEqual(empty.MaxVelocity, loaded.MaxVelocity, 1e-9);
        }

        [Test]
        public void StopCommandCancelsVelocityExactlyWhenItCanStopThisFrame()
        {
            // The "stop cheat": when thrust opposes motion and is strong enough to kill
            // all remaining speed this frame, upstream snaps velocity to zero rather than
            // applying the thrust, which is what stops the autopilot oscillating.
            Ship ship = MakeShip();
            ship.Facing = new Angle(180.0);
            ship.Velocity = new Point(0.0, -0.1);

            ship.Step(new Command { Forward = true, Stop = true });

            Assert.AreEqual(0.0, ship.Velocity.Length, 1e-9,
                "a ship slow enough to stop should land exactly on zero");
        }

        [Test]
        public void StopCommandDoesNotCheatWhenTooFastToStopThisFrame()
        {
            // Above one frame's worth of acceleration the cheat must not engage, or ships
            // would be able to halt from any speed instantly.
            Ship ship = MakeShip();
            ship.Facing = new Angle(180.0);
            ship.Velocity = new Point(0.0, -1.5);

            ship.Step(new Command { Forward = true, Stop = true });

            Assert.Greater(ship.Velocity.Length, 1.0, "it should only have decelerated normally");
        }
    }
}
