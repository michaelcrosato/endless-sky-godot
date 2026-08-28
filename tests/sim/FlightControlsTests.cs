using System;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The AI::MovePlayer BACK translation (FlightControls): on a ship with no
    /// reverse thruster the reverse key becomes a retrograde turn, exactly as
    /// upstream (AI.cpp:4809-4815). The fixture uses the stock Shuttle's
    /// derived constants (mass 192, drag 1.8, thrust 24.075, turn 552 →
    /// accel 0.12539 px/f², turn 2.875°/f, vmax 13.375 px/f) so expected frame
    /// counts are derived by hand from upstream's formulas.
    /// </summary>
    public class FlightControlsTests
    {
        private const string ShuttleLikeData =
            "ship \"Shuttle Fixture\"\n" +
            "\tattributes\n" +
            "\t\t\"mass\" 192\n" +
            "\t\t\"drag\" 1.8\n" +
            "\toutfits\n" +
            "\t\t\"Fixture Drive\"\n" +
            "\n" +
            "outfit \"Fixture Drive\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 24.075\n" +
            "\t\"turn\" 552\n";

        private static Ship MakeShuttle()
        {
            var data = new GameData();
            data.LoadText(ShuttleLikeData, "flight-controls-fixture");
            return data.BuildShip("Shuttle Fixture");
        }

        [Test]
        public void ReverseKeyAtZeroVelocitySpinsClockwiseLikeUpstream()
        {
            Ship ship = MakeShuttle();
            Command command = FlightControls.BuildPlayerCommand(ship, forward: false, back: true, turnInput: 0.0);
            Assert.IsFalse(command.Back, "no reverse thruster: Back must never be raised");
            Assert.AreEqual(-1.0, command.Turn, 1e-12,
                "upstream edge case: zero velocity yields turn -1, not 0 — do not 'fix' this");
        }

        [Test]
        public void HeldTurnKeyIsNeverOverriddenByTheReverseTranslation()
        {
            Ship ship = MakeShuttle();
            ship.Velocity = new Point(0.0, -5.0);
            Command command = FlightControls.BuildPlayerCommand(ship, forward: false, back: true, turnInput: -1.0);
            Assert.AreEqual(-1.0, command.Turn, 1e-12, "the Left key wins, not TurnToward");
            Assert.IsFalse(command.Back);
        }

        [Test]
        public void ReverseKeyReachesExactRetrogradeOnFrameSixtyThree()
        {
            Ship ship = MakeShuttle();
            // Flying "up" (sim north) at max speed, facing along the velocity.
            ship.Velocity = new Point(0.0, -13.375);
            ship.Facing = new Angle(0.0);

            // 180° / 2.875°-per-frame = 62.6 → 63 frames. The zero-cross tie
            // (velocity dead ahead) breaks to turn -1 like upstream's
            // `left - !left`, so the flip runs counterclockwise and arrives at
            // 180° from above: after 62 full-rate frames the ship sits ~1.9°
            // past (quantized 2.8729°/frame), and frame 63 is TurnToward's
            // fractional final step that lands retrograde exactly.
            for (int frame = 0; frame < 62; frame++)
            {
                ship.Step(FlightControls.BuildPlayerCommand(ship, forward: false, back: true, turnInput: 0.0));
            }

            Assert.Greater(Math.Abs(ship.Facing.AbsDegrees - 180.0), 1.5, "not yet aligned at frame 62");
            ship.Step(FlightControls.BuildPlayerCommand(ship, forward: false, back: true, turnInput: 0.0));
            Assert.AreEqual(180.0, ship.Facing.AbsDegrees, 0.01,
                "frame 63's fractional TurnToward step must land exactly retrograde");
        }

        [Test]
        public void FlipAndBurnFromMaxSpeedBrakesWithinUpstreamFrameBudget()
        {
            Ship ship = MakeShuttle();
            ship.Velocity = new Point(0.0, -13.375);
            ship.Facing = new Angle(0.0);
            Point start = ship.Position;

            // Hand-derived upstream budget: 62.6 flip frames + 73.9 braking
            // frames ≈ 137 (docs/upstream-reference.md). Without the Stop
            // autopilot (deliberately unbound, as upstream ships it) constant
            // thrust cannot settle below one frame's Δv — it overshoots and
            // oscillates — so "stopped" means within one frame's acceleration
            // of zero, the tightest bound the physics guarantees.
            // The maneuver the derivation prices: coast through the 63-frame
            // flip (Down only — coasting is lossless, 63 × 13.375 ≈ 843 px),
            // then thrust against the velocity (Down + W ≈ 438 px more).
            double minSpeed = double.MaxValue;
            int minFrame = 0;
            double distanceAtMin = 0.0;
            for (int frame = 1; frame <= 150; frame++)
            {
                bool thrust = frame > 63;
                ship.Step(FlightControls.BuildPlayerCommand(ship, forward: thrust, back: true, turnInput: 0.0));
                if (ship.Velocity.Length < minSpeed)
                {
                    minSpeed = ship.Velocity.Length;
                    minFrame = frame;
                    distanceAtMin = (ship.Position - start).Length;
                }
            }

            Assert.Less(minSpeed, ship.Acceleration, "must brake to within one frame's Δv of a dead stop");
            Assert.LessOrEqual(minFrame, 140, "the stop must land within the ~137-frame upstream budget");
            Assert.AreEqual(1275.0, distanceAtMin, 90.0,
                "distance covered during the stop should match the hand-derived ~1275 px");
        }
    }
}
