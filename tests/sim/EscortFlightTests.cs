using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    [TestFixture]
    public class EscortFlightTests
    {
        private static PlayerState Pilot(out GameData data, out Ship escort, bool jumpDrive = false)
        {
            data = new GameData();
            data.LoadText("ship Courier\n\tattributes\n\t\tmass 100\n\t\tdrag 2\n\t\thull 500\n" +
                "\t\tthrust 50\n\t\tturn 600\n\t\t\"cargo space\" 20\n\t\t\"fuel capacity\" 500\n" +
                (jumpDrive ? "\t\t\"jump drive\" 1\n" : "\t\thyperdrive 1\n") +
                "\t\t\"jump speed\" 1\n\t\t\"energy capacity\" 100\n\t\t\"energy generation\" 1\n" +
                "planet Home\n\tspaceport Busy\nplanet Away\n\tspaceport Busy\n" +
                "system A\n\tpos 0 0\n\tlink B\n\tobject Home\n" +
                "system B\n\tpos 100 0\n\tlink A\n\tlink C\n\tobject Away\n" +
                "system C\n\tpos 200 0\n\tlink B\n" +
                "system Island\n\tpos 5000 0\n" +
                "mission Freight\n\tcargo documents 30\n\tdestination Away\n\ton complete\n\t\tpayment 1000\n");
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["A"]);
            for (int i = 0; i < 2; i++)
            {
                Ship ship = data.BuildShip("Courier");
                ship.CurrentSystem = player.CurrentSystem;
                player.Fleet.Add(ship);
            }
            escort = player.Fleet.Ships[1];
            return player;
        }

        [Test]
        public void FlightStepsMoveAnEscortWithoutSteppingTheFlagshipTwice()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            escort.Position = new Point(0, 1500);
            Point flagshipPosition = player.Flagship!.Position;
            for (int i = 0; i < 1500; i++) player.Fleet.StepEscorts(data);
            Assert.Less((escort.Position - player.Flagship.Position).Length, 100);
            Assert.AreEqual(flagshipPosition, player.Flagship.Position);
        }

        [Test]
        public void AnEscortWaitsForTheFlagshipThenPaysForItsOwnJump()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            player.Flagship!.TargetSystem = data.Systems["B"];
            player.Flagship.Velocity = new Point(20, 0);
            escort.Facing = new Angle(90);
            player.Fleet.StepEscorts(data, flagshipJumping: true);
            Assert.IsFalse(escort.IsEnteringHyperspace);
            Assert.AreEqual(500, escort.Fuel);
            player.Flagship.Velocity = Point.Zero;
            player.Flagship.Facing = new Angle(90);
            player.Fleet.StepEscorts(data, flagshipJumping: true);
            Assert.IsTrue(escort.IsEnteringHyperspace);
            Assert.AreEqual(500, escort.Fuel, "the commit frame does not charge the whole jump");
            for (int i = 0; i < 100; i++) player.Fleet.StepEscorts(data, flagshipJumping: true);
            Assert.AreSame(data.Systems["B"], escort.CurrentSystem);
            Assert.AreSame(data.Systems["A"], player.Flagship.CurrentSystem);
            Assert.AreEqual(400, escort.Fuel, 1e-8);
        }

        [Test]
        public void ASeparatedEscortRoutesThroughIntermediateSystems()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            player.Flagship!.CurrentSystem = data.Systems["C"];
            player.EnterSystem(data.Systems["C"]);
            bool passedB = false;
            for (int i = 0; i < 4000 && (escort.CurrentSystem != player.CurrentSystem || escort.IsHyperspacing); i++)
            {
                player.Fleet.StepEscorts(data);
                passedB |= ReferenceEquals(escort.CurrentSystem, data.Systems["B"]);
            }
            Assert.IsTrue(passedB);
            Assert.AreSame(data.Systems["C"], escort.CurrentSystem);
            Assert.AreEqual(300, escort.Fuel, 1e-8);
        }

        [TestCase("parked")]
        [TestCase("disabled")]
        [TestCase("destroyed")]
        [TestCase("fuel")]
        [TestCase("drive")]
        [TestCase("unreachable")]
        public void IneligibleEscortsAreNeverTeleportedToTheFlagship(string reason)
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            player.Flagship!.CurrentSystem = data.Systems[reason == "unreachable" ? "Island" : "B"];
            if (reason == "parked") escort.IsParked = true;
            if (reason == "disabled") escort.SetLevels(hull: escort.MinimumHull - 1);
            if (reason == "destroyed") escort.SetLevels(hull: -1);
            if (reason == "fuel") escort.SetLevels(fuel: 0);
            if (reason == "drive") escort.Attributes.Set("hyperdrive", 0);
            for (int i = 0; i < 400; i++) player.Fleet.StepEscorts(data);
            Assert.AreSame(data.Systems["A"], escort.CurrentSystem);
            Assert.IsFalse(escort.IsEnteringHyperspace);
        }

        [Test]
        public void HoldPreventsFollowingUntilTheOrderIsReleased()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            player.Flagship!.CurrentSystem = data.Systems["B"];
            player.Fleet.IssueOrder(FleetOrder.Hold);
            for (int i = 0; i < 300; i++) player.Fleet.StepEscorts(data);
            Assert.AreSame(data.Systems["A"], escort.CurrentSystem);
            Assert.AreEqual(500, escort.Fuel);
            player.Fleet.IssueOrder(FleetOrder.Escort);
            for (int i = 0; i < 1000 && escort.CurrentSystem != player.Flagship.CurrentSystem; i++)
                player.Fleet.StepEscorts(data);
            Assert.AreSame(player.Flagship.CurrentSystem, escort.CurrentSystem);
        }

        [Test]
        public void FreightOnAnEscortCanCatchUpAndBeDeliveredIntact()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            player.Land(data.Planets["Home"]);
            var log = new MissionLog(player);
            ActiveMission job = log.Accept(data.Missions["Freight"])!;
            Assert.IsTrue(player.TakeOff(log));
            Assert.AreEqual(10, escort.Cargo.MissionCargo[job.Id]);
            player.Flagship!.TargetSystem = data.Systems["B"];
            bool leaving = true;
            for (int i = 0; i < 4000; i++)
            {
                if (!player.Flagship.StepHyperspace())
                {
                    player.Flagship.Step(leaving ? ShipAi.PrepareForHyperspace(player.Flagship) : Command.None);
                    if (leaving && player.Flagship.TryCommitJump()) leaving = false;
                }
                player.Fleet.StepEscorts(data, leaving || player.Flagship.IsEnteringHyperspace);
                if (player.Flagship.CurrentSystem == data.Systems["B"] && escort.CurrentSystem == data.Systems["B"]
                    && !escort.IsHyperspacing && !player.Flagship.IsHyperspacing) break;
            }
            player.EnterSystem(player.Flagship.CurrentSystem);
            player.Land(data.Planets["Away"]);
            Assert.AreSame(data.Systems["B"], escort.CurrentSystem);
            Assert.IsTrue(log.Complete(job));
            Assert.AreEqual(1000, player.Credits);
            Assert.AreEqual(0, player.Fleet.CargoUsed());
        }

        [Test]
        public void LaunchPlacesLocalEscortsAtThePortAndLeavesRemoteShipsAlone()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            Ship remote = data.BuildShip("Courier");
            remote.CurrentSystem = data.Systems["C"];
            remote.Position = new Point(2500, 4000);
            player.Fleet.Add(remote);
            player.Land(data.Planets["Home"]);
            player.Fleet.IssueOrder(FleetOrder.Hold);
            Point before = remote.Position;
            Assert.IsTrue(player.TakeOff());
            StellarObject port = data.Systems["A"].AllObjects().Single();
            Assert.LessOrEqual((escort.Position - port.Position).Length, port.LandingRadius);
            Assert.AreEqual(1, escort.Velocity.Length, 1e-8);
            Assert.AreEqual(before, remote.Position);
            Assert.AreEqual(FleetOrder.Escort, player.Fleet.Order);
        }

        [Test]
        public void ADisabledEscortStillDriftsAndCoolsWithoutGeneratingPower()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            escort.Attributes.Set("heat dissipation", 10);
            escort.SetLevels(hull: escort.MinimumHull - 1, energy: 0, heat: 200);
            escort.Velocity = new Point(3, 0);
            player.Fleet.StepEscorts(data);
            Assert.Greater(escort.Position.X, 0);
            Assert.AreEqual(0, escort.Energy);
            Assert.Less(escort.Heat, 200);
            Assert.IsTrue(escort.IsDisabled);
        }

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 69)]
        [TestCase(false, 99)]
        [TestCase(false, 100)]
        [TestCase(false, 120)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 99)]
        [TestCase(true, 100)]
        public void AnEscortsJumpResumesAfterReloadWithoutChargingFuelAgain(bool jumpDrive, int frames)
        {
            PlayerState player = Pilot(out GameData data, out Ship escort, jumpDrive);
            escort.TargetSystem = data.Systems["B"];
            escort.Facing = new Angle(90);
            escort.RandomSource = () => 0.25;
            Assert.IsTrue(escort.TryCommitJump());
            for (int i = 0; i < frames; i++) escort.StepHyperspace();
            string saved = SaveGame.Write(player);
            PlayerState restored = SaveGame.Read(saved, data);
            Ship back = restored.Fleet.Ships[1];
            back.RandomSource = () => 0.25;
            Assert.AreEqual(saved, SaveGame.Write(restored));
            Assert.AreEqual(escort.HyperspaceCount, back.HyperspaceCount);
            Assert.AreSame(escort.HyperspaceSystem, back.HyperspaceSystem);
            Assert.AreEqual(escort.IsUsingJumpDrive, back.IsUsingJumpDrive);
            for (int i = 0; i < 220; i++)
            {
                Assert.AreEqual(escort.StepHyperspace(), back.StepHyperspace());
                Assert.AreEqual(escort.Fuel, back.Fuel, 1e-8);
            }
            Assert.AreSame(data.Systems["B"], back.CurrentSystem);
            Assert.AreEqual(jumpDrive ? 300 : 400, back.Fuel, 1e-8);
            Assert.Less((escort.Position - back.Position).Length, 1e-4);
        }

        [Test]
        public void AnImpossibleOutboundSavePhaseCannotTrapAnEscortInHyperspace()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            escort.TargetSystem = data.Systems["B"];
            escort.Facing = new Angle(90);
            Assert.IsTrue(escort.TryCommitJump());
            string saved = SaveGame.Write(player);
            StringAssert.Contains("hyperspace 0 ", saved);
            // Frame 100 has already arrived and cannot still have an outbound
            // destination. Advancing such a phase would miss arrival forever.
            PlayerState restored = SaveGame.Read(saved.Replace("hyperspace 0 ", "hyperspace 100 "), data);
            Ship back = restored.Fleet.Ships[1];
            Assert.IsFalse(back.StepHyperspace());
            Assert.AreEqual(500, back.Fuel);
            back.TargetSystem = data.Systems["B"];
            Assert.IsTrue(back.TryCommitJump());
        }

        [TestCase(69, true)]
        [TestCase(70, false)]
        [TestCase(100, false)]
        [TestCase(131, true)]
        public void HyperspaceTargetabilityChangesAtTheUpstreamThreshold(int frames, bool targetable)
        {
            Pilot(out GameData data, out Ship escort);
            escort.TargetSystem = data.Systems["B"];
            escort.Facing = new Angle(90);
            Assert.IsTrue(escort.TryCommitJump());
            for (int i = 0; i < frames; i++) escort.StepHyperspace();
            Assert.AreEqual(targetable, escort.IsTargetable);
        }

        [Test]
        public void AnActiveEscortGeneratesPowerOncePerFrame()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            escort.SetLevels(energy: 0);
            player.Fleet.StepEscorts(data);
            Assert.AreEqual(1, escort.Energy);
        }

        private static void Arm(GameData data, Ship ship)
        {
            data.LoadText("outfit \"Escort test gun\"\n\t\"gun ports\" -1\n\tweapon\n" +
                "\t\tvelocity 100\n\t\tlifetime 10\n\t\t\"hull damage\" 10\n" +
                "ship Courier\n\tgun 0 0\n");
            ship.AddOutfit(data.Outfits["Escort test gun"]);
            ship.BuildMounts();
            Assert.IsTrue(ShipAi.IsArmed(ship));
        }

        [TestCase(FleetOrder.Escort, false, 300, false)]
        [TestCase(FleetOrder.Escort, true, 300, true)]
        [TestCase(FleetOrder.Escort, false, 2500, true)]
        [TestCase(FleetOrder.Gather, false, 300, true)]
        [TestCase(FleetOrder.AttackTarget, true, 2500, false)]
        public void EscortOrdersAndDamageDetermineWhetherToPursueOrRegroup(
            FleetOrder order, bool damaged, int distance, bool turnsBackToFlagship)
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            Arm(data, escort);
            player.Flagship!.Position = new Point(0, 1000);
            Ship enemy = data.BuildShip("Courier");
            enemy.CurrentSystem = player.CurrentSystem;
            enemy.Position = new Point(0, -distance);
            enemy.Government = new Government("Enemy");
            enemy.Government.SetReputation(-100);
            if (damaged) escort.SetLevels(hull: escort.MinimumHull + (escort.MaxHull - escort.MinimumHull) * 0.1);
            player.Fleet.IssueOrder(order, enemy);
            player.Fleet.StepEscorts(data, candidates: new[] { enemy });
            Assert.AreEqual(turnsBackToFlagship, escort.Facing != new Angle(0));
        }

        [Test]
        public void HoldingPositionStillAllowsAnEscortToFireAtAHostile()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            Arm(data, escort);
            Ship enemy = data.BuildShip("Courier");
            enemy.CurrentSystem = player.CurrentSystem;
            enemy.Position = new Point(0, -100);
            enemy.Government = new Government("Enemy");
            enemy.Government.SetReputation(-100);
            player.Fleet.IssueOrder(FleetOrder.Hold);
            Assert.IsNotEmpty(player.Fleet.StepEscorts(data, candidates: new[] { enemy }));
            Assert.AreEqual(Point.Zero, escort.Position);
        }

        [Test]
        public void AShipDeepInHyperspaceCannotBeSelectedOrAutofiredUpon()
        {
            PlayerState player = Pilot(out GameData data, out Ship escort);
            Arm(data, escort);
            Ship enemy = data.BuildShip("Courier");
            enemy.CurrentSystem = player.CurrentSystem;
            enemy.TargetSystem = data.Systems["B"];
            enemy.Facing = new Angle(90);
            enemy.Government = new Government("Enemy");
            enemy.Government.SetReputation(-100);
            Assert.IsTrue(enemy.TryCommitJump());
            for (int i = 0; i < 70; i++) enemy.StepHyperspace();
            enemy.Position = new Point(0, -100);
            Assert.IsNull(ShipAi.FindTarget(escort, new[] { enemy }));
            Assert.IsEmpty(ShipAi.AutoFire(escort, enemy));
        }

        [Test]
        public void RegisteringAnOwnedShipTwiceDoesNotCreateDuplicateCombatTargets()
        {
            PlayerState player = Pilot(out _, out Ship escort);
            var field = new CombatField();
            field.Add(escort);
            field.Add(escort);
            Assert.AreEqual(1, field.Ships.Count);
            Assert.IsTrue(field.Remove(escort));
            Assert.IsEmpty(field.Ships);
        }
    }
}
