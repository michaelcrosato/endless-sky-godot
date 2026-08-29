using System;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Hyperspace jump protocol (Ship.Travel.cs) against upstream
    /// Ship::IsReadyToJump / DoHyperspaceLogic, hyperdrive path. The fixture
    /// ship carries the stock Shuttle's derived constants plus a stock
    /// Hyperdrive ("jump fuel" 100, "jump speed" .2) and 400 fuel = 4 jumps.
    /// Systems: Home and Away, linked, 500 map units apart along +X.
    /// </summary>
    public class ShipTravelTests
    {
        private const string FixtureData =
            "ship \"Jump Fixture\"\n" +
            "\tattributes\n" +
            "\t\t\"mass\" 192\n" +
            "\t\t\"drag\" 1.8\n" +
            "\t\t\"fuel capacity\" 400\n" +
            // Shields and a reactor, so "does a jump recharge?" is a question this
            // fixture can answer at all.
            "\t\t\"shields\" 400\n" +
            "\t\t\"shield generation\" 2\n" +
            "\t\t\"energy capacity\" 200\n" +
            "\t\t\"energy generation\" 3\n" +
            "\toutfits\n" +
            "\t\t\"Fixture Drive\"\n" +
            "\t\t\"Fixture Hyperdrive\"\n" +
            "\n" +
            "outfit \"Fixture Drive\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 24.075\n" +
            "\t\"turn\" 552\n" +
            "\n" +
            "outfit \"Fixture Hyperdrive\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"hyperdrive\" 1\n" +
            "\t\"jump speed\" .2\n" +
            "\t\"jump fuel\" 100\n" +
            "\n" +
            "system Home\n" +
            "\tpos 0 0\n" +
            "\tlink Away\n" +
            "\n" +
            "system Away\n" +
            "\tpos 500 0\n" +
            "\tlink Home\n";

        private static (Ship ship, StarSystem home, StarSystem away) MakeFixture()
        {
            var data = new GameData();
            data.LoadText(FixtureData, "travel-fixture");
            Ship ship = data.BuildShip("Jump Fixture");
            ship.SetLevels(fuel: 400.0);
            StarSystem home = data.Systems["Home"];
            StarSystem away = data.Systems["Away"];
            ship.CurrentSystem = home;
            ship.TargetSystem = away;
            // Away lies along +X of Home: sim angle 90°.
            ship.Facing = new Angle(90.0);
            return (ship, home, away);
        }

        [Test]
        public void ReadyToJumpRequiresFuelSpeedFacingAndLink()
        {
            (Ship ship, StarSystem home, _) = MakeFixture();
            Assert.IsTrue(ship.IsReadyToJump(), "aligned, fueled, at rest: ready");

            ship.Velocity = new Point(0.3, 0.0);
            Assert.IsFalse(ship.IsReadyToJump(), "above the 0.2 jump speed limit");
            ship.Velocity = Point.Zero;

            ship.Facing = new Angle(0.0);
            Assert.IsFalse(ship.IsReadyToJump(), "90 degrees off target: not within one turn step");
            ship.Facing = new Angle(90.0);

            ship.SetLevels(fuel: 99.0);
            Assert.IsFalse(ship.IsReadyToJump(), "one point short of the 100 jump fuel");
            ship.SetLevels(fuel: 400.0);

            var stranger = new StarSystem("Stranger");
            ship.TargetSystem = stranger;
            Assert.IsFalse(ship.IsReadyToJump(), "hyperdrives demand a linked target");
            _ = home;
        }

        [Test]
        public void FacingWithinOneTurnStepPasses()
        {
            (Ship ship, _, _) = MakeFixture();
            // TurnRate = 552/192 = 2.875°/frame; 2° off crosses over in one step.
            ship.Facing = new Angle(88.0);
            Assert.IsTrue(ship.IsReadyToJump(), "within one turn step of the departure direction");
            ship.Facing = new Angle(85.0);
            Assert.IsFalse(ship.IsReadyToJump(), "five degrees off needs two turn steps");
        }

        [Test]
        public void FullJumpDrainsExactFuelTeleportsAndDeceleratesToExit()
        {
            (Ship ship, _, StarSystem away) = MakeFixture();
            Assert.IsTrue(ship.TryCommitJump());
            Assert.IsTrue(ship.IsEnteringHyperspace);

            // Outbound: exactly 100 frames, fuel drains to 300.
            int frames = 0;
            while (ship.CurrentSystem != away && frames < 200)
            {
                Assert.IsTrue(ship.StepHyperspace(), "hyperspace consumes the frame");
                frames++;
            }

            Assert.AreEqual(100, frames, "outbound phase is exactly HYPER_C frames");
            Assert.AreEqual(300.0, ship.Fuel, 1e-6, "one stock hyperdrive jump costs 100 fuel");
            Assert.IsNull(ship.TargetSystem, "target clears on arrival");

            // Away has no planets, so arrival aims at the system center:
            // 11,000 px behind it along the preserved facing (90° = +X).
            // From rest, frames 1–99 each add +2 (the 100th frame IS the
            // arrival), so the snap sees 198; the same frame then applies one
            // decel step (−2) and one move before we observe.
            double expectedArrivalX = -11000.0;
            double arrivalSpeed = 99.0 * Ship.HyperspaceAcceleration - Ship.HyperspaceAcceleration;
            Assert.AreEqual(expectedArrivalX + arrivalSpeed, ship.Position.X, 1e-6,
                "teleport to target − 11000·facing, then one decelerated step");
            Assert.AreEqual(0.0, ship.Position.Y, 1e-6);

            // Inbound: decelerate at 2 px/f² until the exit-speed check trips.
            while (ship.IsHyperspacing && frames < 400)
            {
                ship.StepHyperspace();
                frames++;
            }

            Assert.Less(frames, 400, "the jump must terminate");
            double exitSpeed = ship.Velocity.Length;
            Assert.LessOrEqual(exitSpeed, Math.Max(Ship.HyperspaceAcceleration, ship.MaxVelocity) + 1e-9,
                "exit speed obeys the upstream cap");
            Assert.Greater(exitSpeed, 0.0);
            Assert.IsFalse(ship.StepHyperspace(), "after exit the frame is no longer consumed");
        }

        [Test]
        public void FourJumpsExhaustTheTank()
        {
            (Ship ship, StarSystem home, StarSystem away) = MakeFixture();
            for (int jump = 0; jump < 4; jump++)
            {
                ship.Velocity = Point.Zero;
                ship.TargetSystem = ship.CurrentSystem == home ? away : home;
                ship.Facing = Angle.FromPoint(ship.JumpDirection);
                Assert.IsTrue(ship.TryCommitJump(), $"jump {jump + 1} should commit");
                int guard = 0;
                while (ship.StepHyperspace() && guard++ < 400)
                {
                }
            }

            Assert.AreEqual(0.0, ship.Fuel, 1e-6, "four 100-fuel jumps drain a 400 tank");
            ship.TargetSystem = ship.CurrentSystem == home ? away : home;
            ship.Facing = Angle.FromPoint(ship.JumpDirection);
            ship.Velocity = Point.Zero;
            Assert.IsFalse(ship.IsReadyToJump(), "an empty tank grounds the ship");
        }

        [Test]
        public void AShipRechargesWhileItIsInHyperspace()
        {
            // Ship::Move calls DoGeneration (Ship.cpp:1660) BEFORE DoHyperspaceLogic
            // (:1679), so a ship recharges across the ~200 frames of a jump. That is
            // load-bearing pacing: you break off a fight, jump, and arrive with your
            // shields back. StepHyperspace owned the whole frame here and skipped
            // StepResources entirely, so a jump froze every ship in the state it left in.
            (Ship ship, _, _) = MakeFixture();
            ship.SetLevels(shields: 0.0, energy: 0.0);

            Assert.IsTrue(ship.TryCommitJump(), "the jump starts");

            for (int frame = 0; frame < 60; frame++)
            {
                ship.StepHyperspace();
            }

            Assert.Greater(ship.Shields, 0.0, "shields come back during the jump");
            Assert.Greater(ship.Energy, 0.0, "and so does power");
        }
    }
}
