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
        public void DivisionByZeroYieldsZeroRatherThanFaulting()
        {
            // One bad condition in one content file must not take down the game.
            var conditions = new Conditions();
            conditions.Set("n", 10);

            Assert.IsTrue(Test("to offer\n\t\"n\" / \"missing\" == 0\n", conditions));
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
        public void ABareNameIncrementsACounter()
        {
            var conditions = new Conditions();

            var assignments = ConditionAssignments.Load(Parse("on visit\n\t\"visited New Boston\"\n"));
            assignments.Apply(conditions);
            assignments.Apply(conditions);

            Assert.AreEqual(2L, conditions.Get("visited New Boston"));
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
    }
}
