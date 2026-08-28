using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// NPC engagement behaviour, against upstream AI::FindTarget / Attack / AutoFire.
    /// Engine-free.
    /// </summary>
    [TestFixture]
    public class ShipAiTests
    {
        private static Government Republic => new Government("Republic");

        private static Ship MakeShip(string name, Point position, Government government = null,
                                     int gunMounts = 1)
        {
            var lines = new List<string>
            {
                "ship \"" + name + "\"",
                "\tattributes",
                "\t\t\"hull\" 1000",
                "\t\t\"shields\" 1000",
                "\t\t\"mass\" 100",
                "\t\t\"drag\" 2",
                "\t\t\"thrust\" 20",
                "\t\t\"turn\" 400",
                "\t\t\"energy capacity\" 1000",
                "\t\t\"heat capacity\" 10",
            };
            for (int i = 0; i < gunMounts; i++)
                lines.Add("\tgun 0 -10");

            var definition = new ShipDefinition(name);
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

            var ship = new Ship(definition) { Position = position, Government = government };
            ship.BuildMounts();
            return ship;
        }

        private static Outfit MakeGun(string name, params string[] weaponLines)
        {
            var lines = new List<string> { "outfit \"" + name + "\"", "\tweapon" };
            foreach (string line in weaponLines)
                lines.Add("\t\t" + line);

            var outfit = new Outfit(name);
            outfit.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return outfit;
        }

        /// <summary>Two governments that consider each other enemies.</summary>
        private static (Government Mine, Government Theirs) HostilePair()
        {
            var mine = new Government("Republic");
            var theirs = new Government("Pirate");
            mine.Enemies.Add("Pirate");
            theirs.Enemies.Add("Republic");
            return (mine, theirs);
        }

        // --- Target selection -----------------------------------------------------

        [Test]
        public void TheNearestHostileShipIsChosen()
        {
            (Government mine, Government theirs) = HostilePair();

            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship far = MakeShip("Far", new Point(1000.0, 0.0), theirs);
            Ship near = MakeShip("Near", new Point(200.0, 0.0), theirs);

            Ship chosen = ShipAi.FindTarget(self, new[] { far, near });

            Assert.AreSame(near, chosen);
        }

        [Test]
        public void FriendlyShipsAreNotTargeted()
        {
            var navy = Republic;
            Ship self = MakeShip("Self", Point.Zero, navy);
            Ship friend = MakeShip("Friend", new Point(100.0, 0.0), navy);

            Assert.IsNull(ShipAi.FindTarget(self, new[] { friend }));
        }

        [Test]
        public void ShipsWithoutAGovernmentAreNeutralRatherThanUniversallyHostile()
        {
            // An unconfigured scene should not turn into a free-for-all.
            Ship self = MakeShip("Self", Point.Zero);
            Ship other = MakeShip("Other", new Point(100.0, 0.0));

            Assert.IsFalse(ShipAi.IsHostile(self, other));
            Assert.IsNull(ShipAi.FindTarget(self, new[] { other }));
        }

        [Test]
        public void WreckageIsNotTargetedButDisabledShipsStillAre()
        {
            (Government mine, Government theirs) = HostilePair();

            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship disabled = MakeShip("Disabled", new Point(150.0, 0.0), theirs);
            disabled.SetLevels(shields: 0.0, hull: 1.0);
            Ship wreck = MakeShip("Wreck", new Point(50.0, 0.0), theirs);
            wreck.SetLevels(hull: -1.0);

            Assert.IsTrue(disabled.IsDisabled);
            Assert.IsTrue(wreck.IsDestroyed);

            // The wreck is closer, but upstream keeps attacking disabled ships and
            // ignores destroyed ones.
            Assert.AreSame(disabled, ShipAi.FindTarget(self, new[] { wreck, disabled }));
        }

        [Test]
        public void ProvocationMakesAPreviouslyNeutralShipHostile()
        {
            var navy = new Government("Republic");
            var trader = new Government("Merchant");

            Ship self = MakeShip("Self", Point.Zero, navy);
            Ship other = MakeShip("Other", new Point(100.0, 0.0), trader);

            Assert.IsNull(ShipAi.FindTarget(self, new[] { other }));

            navy.Provoke(trader);

            Assert.AreSame(other, ShipAi.FindTarget(self, new[] { other }));
        }

        // --- Pursuit --------------------------------------------------------------

        [Test]
        public void AnAttackerTurnsTowardItsTarget()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            // Far enough that it cannot arrive during the test: this is about the turn,
            // not the intercept. A nearby target gets overshot and the ship is then
            // legitimately mid-turnaround, which says nothing about its steering.
            Ship target = MakeShip("Target", new Point(0.0, -20000.0), theirs);

            self.Facing = new Angle(90.0);            // pointing +X, target is at -Y

            Command command = ShipAi.Attack(self, target);
            Assert.AreNotEqual(0.0, command.Turn, "it must steer toward the target");

            // Turn rate is turn/mass = 4 degrees per frame, so 90 degrees takes ~23.
            for (int frame = 0; frame < 90; frame++)
                self.Step(ShipAi.Attack(self, target));

            double alignment = self.Facing.Unit().Dot((target.Position - self.Position).Unit());
            Assert.Greater(alignment, 0.99, "it should have settled onto its target");
        }

        [Test]
        public void AnAttackerDoesNotThrustWhileFacingAway()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(-500.0, 0.0), theirs);

            self.Facing = new Angle(90.0);            // pointing +X, target is behind

            Command command = ShipAi.Attack(self, target);

            Assert.IsFalse(command.Forward, "thrusting now would only widen the gap");
        }

        [Test]
        public void AnAttackerClosesTheDistance()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(3000.0, 0.0), theirs);

            double before = (target.Position - self.Position).Length;
            for (int frame = 0; frame < 300; frame++)
                self.Step(ShipAi.Attack(self, target));

            double after = (target.Position - self.Position).Length;
            Assert.Less(after, before * 0.5, "it should have closed most of the gap");
        }

        // --- Firing decisions -----------------------------------------------------

        [Test]
        public void AWeaponOutOfRangeIsHeld()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(5000.0, 0.0), theirs);
            self.Facing = new Angle(90.0);

            // Range is velocity * lifetime = 10 * 48 = 480 units.
            Weapon gun = MakeGun("Blaster", "\"velocity\" 10", "\"lifetime\" 48", "\"reload\" 1").Weapon;

            Assert.AreEqual(480.0, gun.Range, 1e-9);
            Assert.IsFalse(ShipAi.ShouldFire(self, target, gun));

            target.Position = new Point(300.0, 0.0);
            Assert.IsTrue(ShipAi.ShouldFire(self, target, gun));
        }

        [Test]
        public void AGunPointedAwayIsHeldButAMissileIsNot()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(0.0, -300.0), theirs);
            self.Facing = new Angle(90.0);            // pointing +X; target is 90 degrees off

            Weapon gun = MakeGun("Blaster", "\"velocity\" 10", "\"lifetime\" 48", "\"reload\" 1").Weapon;
            Assert.IsFalse(ShipAi.ShouldFire(self, target, gun), "a dumb shot would miss");

            // A homing weapon steers itself, so upstream fires it regardless of facing.
            Weapon missile = MakeGun("Missile",
                "\"homing\"", "\"velocity\" 10", "\"lifetime\" 48", "\"turn\" 4", "\"reload\" 1").Weapon;
            Assert.IsTrue(ShipAi.ShouldFire(self, target, missile));
        }

        [Test]
        public void TheFiringConeWidensForCloseTargets()
        {
            // A large ship at point-blank range subtends a wide angle, so a shot that
            // is off-axis still connects.
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(20.0, -12.0), theirs);
            target.CollisionRadius = 30.0;
            self.Facing = new Angle(90.0);

            Weapon gun = MakeGun("Blaster", "\"velocity\" 10", "\"lifetime\" 48", "\"reload\" 1").Weapon;

            Assert.IsTrue(ShipAi.ShouldFire(self, target, gun));
        }

        [Test]
        public void AutoFireShootsWhenLinedUpAndFeedsRealProjectiles()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(200.0, 0.0), theirs);
            self.Facing = new Angle(90.0);

            Outfit gun = MakeGun("Blaster", "\"velocity\" 10", "\"lifetime\" 48", "\"reload\" 5");
            self.InstallWeapon(gun);

            List<Projectile> shots = ShipAi.AutoFire(self, target);

            Assert.AreEqual(1, shots.Count);
            Assert.AreSame(mine, shots[0].Government, "shots carry the firing government");
            Assert.IsEmpty(ShipAi.AutoFire(self, target), "and then it reloads");
        }

        [Test]
        public void AutoFireHoldsWhenTheTargetIsOutOfRange()
        {
            (Government mine, Government theirs) = HostilePair();
            Ship self = MakeShip("Self", Point.Zero, mine);
            Ship target = MakeShip("Target", new Point(9000.0, 0.0), theirs);
            self.Facing = new Angle(90.0);

            self.InstallWeapon(MakeGun("Blaster", "\"velocity\" 10", "\"lifetime\" 48", "\"reload\" 5"));

            Assert.IsEmpty(ShipAi.AutoFire(self, target), "no ammunition wasted at nine thousand units");
        }

        // --- End to end -----------------------------------------------------------

        [Test]
        public void AnArmedAttackerEventuallyDisablesItsTarget()
        {
            // The whole M2 sim loop: choose a target, close, shoot, resolve impacts.
            (Government mine, Government theirs) = HostilePair();

            Ship attacker = MakeShip("Attacker", Point.Zero, mine, gunMounts: 2);
            Ship victim = MakeShip("Victim", new Point(600.0, 0.0), theirs);
            victim.CollisionRadius = 20.0;

            Outfit gun = MakeGun("Heavy Blaster",
                "\"velocity\" 15", "\"lifetime\" 60", "\"reload\" 3",
                "\"shield damage\" 40", "\"hull damage\" 40");
            attacker.InstallWeapon(gun);
            attacker.InstallWeapon(gun);

            var field = new CombatField();
            field.Add(attacker);
            field.Add(victim);

            for (int frame = 0; frame < 3000 && !victim.IsDisabled; frame++)
            {
                attacker.StepArmament();

                Ship chosen = ShipAi.FindTarget(attacker, new[] { victim });
                Assert.AreSame(victim, chosen);

                attacker.Step(ShipAi.Attack(attacker, chosen));
                field.Add(ShipAi.AutoFire(attacker, chosen));
                field.Step();
            }

            Assert.IsTrue(victim.IsDisabled, "the attacker should have worn its target down");
            Assert.Less(victim.Shields, 1.0, "shields go first");
        }
    }
}
