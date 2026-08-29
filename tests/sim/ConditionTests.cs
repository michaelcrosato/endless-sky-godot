using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The condition system that gates missions, events and conversations.
    /// Engine-free.
    /// </summary>
    [TestFixture]
    public class ConditionTests
    {
        private static DataNode Parse(string text) => new DataFile(text, "test.txt").Nodes[0];

        private static bool Test(string conditionText, Conditions conditions) =>
            ConditionSet.Load(Parse(conditionText)).Test(conditions);

        // --- The store ------------------------------------------------------------

        [Test]
        public void UnsetConditionsReadAsZeroRatherThanFailing()
        {
            // Load-bearing: content routinely asks about conditions nothing has written.
            var conditions = new Conditions();

            Assert.AreEqual(0L, conditions.Get("never heard of it"));
            Assert.IsFalse(conditions.Has("never heard of it"));
        }

        [Test]
        public void SettingAConditionToZeroForgetsItRatherThanStoringAZero()
        {
            var conditions = new Conditions();
            conditions.Set("flag", 1);
            Assert.AreEqual(1, conditions.Values.Count);

            conditions.Set("flag", 0);
            Assert.IsEmpty(conditions.Values, "a save should not accumulate every condition ever asked about");
            Assert.AreEqual(0L, conditions.Get("flag"));
        }

        [Test]
        public void ConditionsAccumulate()
        {
            var conditions = new Conditions();
            conditions.Add("kills", 3);
            conditions.Add("kills", 4);

            Assert.AreEqual(7L, conditions.Get("kills"));
        }

        // --- Tests ----------------------------------------------------------------

        [Test]
        public void HasIsTruthinessAndNotIsEqualityWithZero()
        {
            var conditions = new Conditions();
            conditions.Set("started", 1);
            conditions.Set("count", 5);

            Assert.IsTrue(Test("to offer\n\thas \"started\"\n", conditions));
            Assert.IsTrue(Test("to offer\n\thas \"count\"\n", conditions), "any non-zero counts");
            Assert.IsFalse(Test("to offer\n\thas \"missing\"\n", conditions));

            Assert.IsTrue(Test("to offer\n\tnot \"missing\"\n", conditions));
            Assert.IsFalse(Test("to offer\n\tnot \"started\"\n", conditions));
        }

        [Test]
        public void NeverIsAlwaysFalse()
        {
            // Content uses this to switch something off without deleting it.
            Assert.IsFalse(Test("to offer\n\tnever\n", new Conditions()));
        }

        [Test]
        public void AnEmptyConditionSetPasses()
        {
            // Content with no conditions is always available.
            var set = ConditionSet.Load(Parse("to offer\n"));

            Assert.IsTrue(set.IsEmpty);
            Assert.IsTrue(set.Test(new Conditions()));
        }

        [Test]
        public void APlainListOfChildrenIsAnAnd()
        {
            var conditions = new Conditions();
            conditions.Set("a", 1);

            Assert.IsFalse(Test("to offer\n\thas \"a\"\n\thas \"b\"\n", conditions));

            conditions.Set("b", 1);
            Assert.IsTrue(Test("to offer\n\thas \"a\"\n\thas \"b\"\n", conditions));
        }

        [Test]
        public void OrGroupsPassWhenAnyChildPasses()
        {
            var conditions = new Conditions();
            conditions.Set("shortcut", 1);

            const string text = "to offer\n\tor\n\t\thas \"shortcut\"\n\t\thas \"the long way\"\n";
            Assert.IsTrue(Test(text, conditions));

            conditions.Clear("shortcut");
            Assert.IsFalse(Test(text, conditions));
        }

        [Test]
        public void OrGroupsNestInsideTheImplicitAnd()
        {
            // Required AND (one of two alternatives).
            const string text =
                "to offer\n" +
                "\thas \"licence\"\n" +
                "\tor\n" +
                "\t\thas \"invited\"\n" +
                "\t\t\"reputation\" > 100\n";

            var conditions = new Conditions();
            conditions.Set("licence", 1);
            Assert.IsFalse(Test(text, conditions), "neither alternative met");

            conditions.Set("reputation", 150);
            Assert.IsTrue(Test(text, conditions));

            conditions.Clear("licence");
            Assert.IsFalse(Test(text, conditions), "the required condition still gates it");
        }

        [Test]
        public void ComparisonOperatorsWorkAgainstLiteralsAndOtherConditions()
        {
            var conditions = new Conditions();
            conditions.Set("credits", 500);
            conditions.Set("price", 400);

            Assert.IsTrue(Test("to offer\n\t\"credits\" > 400\n", conditions));
            Assert.IsFalse(Test("to offer\n\t\"credits\" > 500\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"credits\" >= 500\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"credits\" != 400\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"credits\" == 500\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"credits\" > \"price\"\n", conditions),
                "a condition can be compared against another condition");
        }

        [Test]
        public void ArithmeticBindsTighterThanComparison()
        {
            // "a" + 1 > "b"  parses as  ("a" + 1) > "b"
            var conditions = new Conditions();
            conditions.Set("a", 5);
            conditions.Set("b", 5);

            Assert.IsTrue(Test("to offer\n\t\"a\" + 1 > \"b\"\n", conditions));
            Assert.IsFalse(Test("to offer\n\t\"a\" - 1 > \"b\"\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"a\" * 2 == 10\n", conditions));
        }

        [Test]
        public void DivisionByZeroSaturatesAndModuloIsIdentity()
        {
            // It must not fault, but it does NOT yield zero either: upstream saturates
            // division to the largest representable value and leaves modulo as the
            // dividend. Returning zero flips comparisons that content depends on.
            var conditions = new Conditions();
            conditions.Set("n", 10);

            Assert.IsTrue(Test("to offer\n\t\"n\" / \"missing\" > 1000000\n", conditions));
            Assert.IsTrue(Test("to offer\n\t\"n\" % \"missing\" == 10\n", conditions));
        }

        // --- Assignments ----------------------------------------------------------

        [Test]
        public void SetAndClearAreShorthandForOneAndZero()
        {
            var conditions = new Conditions();
            conditions.Set("old", 1);

            ConditionAssignments.Load(Parse("on complete\n\tset \"done\"\n\tclear \"old\"\n"))
                .Apply(conditions);

            Assert.AreEqual(1L, conditions.Get("done"));
            Assert.AreEqual(0L, conditions.Get("old"));
        }

        [Test]
        public void CompoundAssignmentsAccumulate()
        {
            var conditions = new Conditions();
            conditions.Set("reputation: Republic", 10);

            ConditionAssignments.Load(Parse(
                "on complete\n\t\"reputation: Republic\" += 15\n\t\"deliveries\" += 1\n"))
                .Apply(conditions);

            Assert.AreEqual(25L, conditions.Get("reputation: Republic"));
            Assert.AreEqual(1L, conditions.Get("deliveries"));
        }

        [Test]
        public void AssignmentRightHandSidesAreExpressionsOverConditions()
        {
            var conditions = new Conditions();
            conditions.Set("this run", 7);
            conditions.Set("total", 10);

            ConditionAssignments.Load(Parse("on complete\n\t\"total\" += \"this run\"\n"))
                .Apply(conditions);

            Assert.AreEqual(17L, conditions.Get("total"));
        }

        [Test]
        public void IncrementAndDecrementOperatorsWork()
        {
            var conditions = new Conditions();
            conditions.Set("count", 5);

            ConditionAssignments.Load(Parse("on visit\n\t\"count\" ++\n\t\"other\" --\n"))
                .Apply(conditions);

            Assert.AreEqual(6L, conditions.Get("count"));
            Assert.AreEqual(-1L, conditions.Get("other"));
        }

        [Test]
        public void MinAndMaxAssignmentOperatorsClampTowardTheirSide()
        {
            var conditions = new Conditions();
            conditions.Set("floor", 10);
            conditions.Set("ceiling", 10);

            ConditionAssignments.Load(Parse(
                "on complete\n\t\"floor\" >?= 25\n\t\"ceiling\" <?= 4\n")).Apply(conditions);

            Assert.AreEqual(25L, conditions.Get("floor"), ">?= keeps the larger");
            Assert.AreEqual(4L, conditions.Get("ceiling"), "<?= keeps the smaller");

            ConditionAssignments.Load(Parse(
                "on complete\n\t\"floor\" >?= 1\n\t\"ceiling\" <?= 99\n")).Apply(conditions);

            Assert.AreEqual(25L, conditions.Get("floor"), "no change when the current value already wins");
            Assert.AreEqual(4L, conditions.Get("ceiling"));
        }

        [Test]
        public void ABareNameIsNotAnAssignmentAndDoesNotLeakIntoTheStore()
        {
            // Treating a bare token as "increment this" was my invention, and it made
            // every non-assignment key inside an action block - conversation, dialog,
            // payment - appear in the player's condition store.
            var conditions = new Conditions();

            ConditionAssignments.Load(Parse(
                "on offer\n\tconversation \"some talk\"\n\t\"visited New Boston\"\n")).Apply(conditions);

            Assert.IsEmpty(conditions.Values,
                "neither the conversation key nor a bare name should be recorded");
        }

        [Test]
        public void NonIntegerLiteralsAreTruncatedRatherThanReadAsConditionNames()
        {
            var conditions = new Conditions();

            ConditionAssignments.Load(Parse("on complete\n\t\"x\" = 2.7\n")).Apply(conditions);

            Assert.AreEqual(2L, conditions.Get("x"));
        }

        [Test]
        public void AssignmentsApplyInDeclarationOrder()
        {
            var conditions = new Conditions();

            ConditionAssignments.Load(Parse(
                "on complete\n\t\"x\" = 5\n\t\"x\" *= 3\n\t\"x\" -= 1\n"))
                .Apply(conditions);

            Assert.AreEqual(14L, conditions.Get("x"));
        }

        // --- Round trip against real content shapes -------------------------------

        [Test]
        public void ARealisticOfferGateEvaluatesCorrectly()
        {
            const string text =
                "to offer\n" +
                "\thas \"main plot: started\"\n" +
                "\tnot \"main plot: failed\"\n" +
                "\t\"reputation: Republic\" >= 100\n";

            var conditions = new Conditions();
            conditions.Set("main plot: started", 1);
            conditions.Set("reputation: Republic", 250);
            Assert.IsTrue(Test(text, conditions));

            conditions.Set("main plot: failed", 1);
            Assert.IsFalse(Test(text, conditions), "a failure flag locks the mission out");
        }

        // --- The conditions the engine provides ------------------------------------

        [Test]
        public void RandomIsARollNotAMissingCondition()
        {
            // PlayerInfo.cpp:4670. Content gates on `random < 40` to make an outcome
            // happen four times in ten. An unregistered condition reads 0, so
            // `random < N` was ALWAYS true and every such gate fired every time.
            var data = new GameData();
            data.LoadText("government \"Navy\"" + "\n");
            var player = new PlayerState(data);

            var seen = new HashSet<long>();
            for (int roll = 0; roll < 200; roll++)
                seen.Add(player.Conditions.Get("random"));

            Assert.Greater(seen.Count, 1, "a roll that never changes is not a roll");
            Assert.IsTrue(seen.All(v => v >= 0 && v < 100), "upstream's range is [0, 100)");
        }

        [Test]
        public void ReputationReadsAndWritesTheGovernmentItNames()
        {
            // PlayerInfo.cpp:4654-4667: read AND write, because content adjusts
            // standing directly. Unregistered, every `reputation: X` gate read a dead
            // zero however the player had actually behaved.
            var data = new GameData();
            data.LoadText("government \"Navy\"" + "\n");
            var player = new PlayerState(data);

            data.Governments["Navy"].SetReputation(42);
            Assert.AreEqual(42, player.Conditions.Get("reputation: Navy"));

            player.Conditions.Set("reputation: Navy", -7);
            Assert.AreEqual(-7.0, data.Governments["Navy"].Reputation, 1e-9,
                "content that sets standing has to actually set it");
        }

        [Test]
        public void ReputationOfAGovernmentNobodyDefinedIsZero()
        {
            var data = new GameData();
            data.LoadText("government \"Navy\"" + "\n");

            Assert.AreEqual(0, new PlayerState(data).Conditions.Get("reputation: Nobody"));
        }
    }
}
