using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Gauntlet round 2 regressions: parentheses, the never term, the explode
    /// endpoint, cluster fan-out, standoff ceiling and boolean tokens. Engine-free.
    /// </summary>
    [TestFixture]
    public class AuditRegressionTests4
    {
        private static DataNode Parse(string text) => new DataFile(text, "test.txt").Nodes[0];

        private static bool TestGate(string text, Conditions conditions) =>
            ConditionSet.Load(Parse(text)).Test(conditions);

        // --- Parentheses ----------------------------------------------------------

        [Test]
        public void ParenthesesGroupAnExpressionRatherThanReadingAsConditionNames()
        {
            // Unbracketed, arithmetic binds tighter than comparison, so
            // "a" + "b" * 2 is a + (b * 2). Brackets must change that.
            var conditions = new Conditions();
            conditions.Set("a", 2);
            conditions.Set("b", 3);

            Assert.IsTrue(TestGate("to offer\n\t\"a\" + \"b\" * 2 == 8\n", conditions));
            Assert.IsTrue(TestGate("to offer\n\t( \"a\" + \"b\" ) * 2 == 10\n", conditions),
                "brackets must regroup, not evaluate as conditions named ( and )");
        }

        [Test]
        public void NestedParenthesesEvaluateInnermostFirst()
        {
            var conditions = new Conditions();
            conditions.Set("x", 1);

            Assert.IsTrue(TestGate("to offer\n\t( ( \"x\" + 1 ) * 3 ) == 6\n", conditions));
        }

        [Test]
        public void UnbalancedBracketsDoNotThrow()
        {
            // Malformed content must degrade, not crash the load.
            var conditions = new Conditions();
            conditions.Set("x", 1);

            Assert.DoesNotThrow(() => TestGate("to offer\n\t( \"x\" + 1 == 2\n", conditions));
            Assert.DoesNotThrow(() => TestGate("to offer\n\t\"x\" + 1 ) == 2\n", conditions));
        }

        // --- "never" is a term ----------------------------------------------------

        [Test]
        public void NeverInsideAnOrLeavesTheOtherAlternativesAlive()
        {
            // never is a literal-false CHILD upstream, not a flag on the enclosing set.
            // Short-circuiting the whole set silently disables the live alternatives
            // parked beside a disabled one.
            var conditions = new Conditions();
            conditions.Set("x", 1);

            Assert.IsTrue(TestGate("to offer\n\tor\n\t\tnever\n\t\thas \"x\"\n", conditions));
        }

        [Test]
        public void NeverUnderAnAndStillFalsifiesTheWholeGate()
        {
            var conditions = new Conditions();
            conditions.Set("x", 1);

            Assert.IsFalse(TestGate("to offer\n\tnever\n\thas \"x\"\n", conditions));
            Assert.IsFalse(TestGate("to offer\n\tnever\n", conditions));
        }

        // --- Conversation endpoints ----------------------------------------------

        private static Conversation LoadConversation(params string[] lines) =>
            Conversation.Load(new DataFile(
                "conversation \"test\"\n" + string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

        [Test]
        public void ExplodeIsARecognisedEndpoint()
        {
            // Without it a death node falls through into whatever text follows, and
            // the player survives an outcome meant to destroy their flagship.
            var runner = new ConversationRunner(
                LoadConversation("\t`Plasma engulfs your ship.`", "\t\texplode",
                                 "\tlabel after", "\t`Unrelated paragraph.`", "\t\taccept"),
                new Conditions());

            Assert.IsTrue(runner.IsFinished);
            Assert.AreEqual(ConversationOutcome.Explode, runner.Outcome);
            CollectionAssert.DoesNotContain(runner.PendingText, "Unrelated paragraph.");
        }

        [Test]
        public void BranchCanTargetExplodeEvenWhenALabelSharesTheName()
        {
            // Upstream resolves branch targets as endpoints FIRST, which is exactly
            // the collision that matters: avgi content has a label named "explode".
            var conditions = new Conditions();
            conditions.Set("doomed", 1);

            var runner = new ConversationRunner(
                LoadConversation("\tbranch explode", "\t\thas \"doomed\"",
                                 "\t`You survive.`", "\t\taccept",
                                 "\tlabel explode", "\t`Wrong branch.`", "\t\tdecline"),
                conditions);

            Assert.AreEqual(ConversationOutcome.Explode, runner.Outcome);
        }

        // --- Cluster fan-out ------------------------------------------------------

        private static Weapon MakeWeapon(params string[] lines)
        {
            var text = new List<string> { "weapon" };
            foreach (string line in lines)
                text.Add("\t" + line);

            var weapon = new Weapon();
            weapon.Load(new DataFile(string.Join("\n", text) + "\n", "test.txt").Nodes[0]);
            return weapon;
        }

        [Test]
        public void SubmunitionFacingAndOffsetAreParsed()
        {
            var weapon = new Weapon();
            weapon.Load(new DataFile(
                "weapon\n\t\"velocity\" 5\n\t\"lifetime\" 3\n" +
                "\t\"submunition\" \"shard\" 3\n\t\tfacing 30\n\t\toffset 4 -2\n", "test.txt").Nodes[0]);

            Submunition submunition = weapon.Submunitions.Single();
            Assert.AreEqual(30.0, submunition.Facing, 1e-9);
            Assert.AreEqual(4.0, submunition.Offset.X, 1e-9);
            Assert.AreEqual(-2.0, submunition.Offset.Y, 1e-9);
        }

        [Test]
        public void AClusterActuallyFansOutRatherThanFlyingAsOneShot()
        {
            // With every child inheriting the parent's exact heading the whole cluster
            // travels as a single round and the weapon stops being a cluster at all.
            Weapon shard = MakeWeapon("\"velocity\" 5", "\"lifetime\" 40", "\"hull damage\" 5");

            var spread = new Weapon();
            spread.Load(new DataFile(
                "weapon\n\t\"velocity\" 6\n\t\"lifetime\" 2\n" +
                "\t\"submunition\" \"shard\" 1\n\t\tfacing -25\n" +
                "\t\"submunition\" \"shard\" 1\n\t\tfacing 25\n", "test.txt").Nodes[0]);
            spread.ResolveSubmunitions(name => name == "shard" ? shard : null);

            var field = new CombatField { WeaponLookup = name => name == "shard" ? shard : null };
            field.Add(new Projectile(spread, Point.Zero, Point.Zero, new Angle(90.0)));

            field.Step();
            field.Step();

            var children = field.Projectiles.ToList();
            Assert.AreEqual(2, children.Count);
            Assert.AreNotEqual(children[0].Angle.Step, children[1].Angle.Step,
                "the two children must leave on different headings");

            double separation = System.Math.Abs(
                children[0].Angle.AbsDegrees - children[1].Angle.AbsDegrees);
            Assert.AreEqual(50.0, separation, 0.1);
        }

        // --- AI standoff ceiling --------------------------------------------------

        [Test]
        public void StandoffRangeIsCappedSoLongRangedShipsStillClose()
        {
            // Upstream seeds shortestRange at 4000 and only mins into it, so a missile
            // boat closes to 4000 rather than sniping from its full reach.
            var lines = new List<string>
            {
                "ship \"Sniper\"",
                "\tattributes",
                "\t\t\"hull\" 500",
                "\t\t\"mass\" 100",
                "\t\t\"energy capacity\" 1000",
                "\tgun 0 -10",
            };
            var definition = new ShipDefinition("Sniper");
            definition.Load(new DataFile(string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();

            var longGun = new Outfit("Long Gun");
            longGun.Load(new DataFile(
                "outfit \"Long Gun\"\n\tweapon\n\t\t\"reload\" 10\n\t\t\"velocity\" 30\n\t\t\"lifetime\" 400\n",
                "test.txt").Nodes[0]);

            Assert.AreEqual(12000.0, longGun.Weapon.Range, 1e-6, "a genuinely long-ranged weapon");

            ship.InstallWeapon(longGun);

            Assert.AreEqual(ShipAi.MaxEngagementStandoff, ShipAi.ShortestWeaponRange(ship), 1e-9);
        }

        [Test]
        public void AnUnarmedShipHasNoStandoffRange()
        {
            var definition = new ShipDefinition("Hauler");
            definition.Load(new DataFile(
                "ship \"Hauler\"\n\tattributes\n\t\t\"hull\" 500\n\t\t\"mass\" 100\n", "test.txt").Nodes[0]);
            var ship = new Ship(definition);
            ship.BuildMounts();

            Assert.AreEqual(0.0, ShipAi.ShortestWeaponRange(ship), 1e-9);
        }

        // --- Boolean tokens -------------------------------------------------------

        [Test]
        public void BooleanTokensAreTextualNotNumeric()
        {
            // Upstream accepts exactly true/false/1/0. Reading them numerically makes
            // "true" false and any nonzero number true.
            DataNode node = Parse("flags true false 1 0 7 yes\n");

            Assert.IsTrue(node.BoolValue(1), "\"true\" must read as true");
            Assert.IsFalse(node.BoolValue(2));
            Assert.IsTrue(node.BoolValue(3));
            Assert.IsFalse(node.BoolValue(4));
            Assert.IsFalse(node.BoolValue(5), "7 is not a boolean upstream");

            Assert.IsTrue(node.IsBool(1));
            Assert.IsTrue(node.IsBool(2));
            Assert.IsFalse(node.IsBool(5));
            Assert.IsFalse(node.IsBool(6));
        }
    }
}
