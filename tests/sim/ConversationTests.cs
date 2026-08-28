using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Conversation flow: narration, branching, choices and outcomes. Engine-free.
    /// </summary>
    [TestFixture]
    public class ConversationTests
    {
        private static Conversation Load(params string[] lines) =>
            Conversation.Load(new DataFile(
                "conversation \"test\"\n" + string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);

        private static ConversationRunner Run(Conversation conversation, Conditions conditions = null) =>
            new ConversationRunner(conversation, conditions ?? new Conditions());

        [Test]
        public void NarrationAccumulatesUntilAChoiceIsNeeded()
        {
            Conversation conversation = Load(
                "\t`First line.`",
                "\t`Second line.`",
                "\tchoice",
                "\t\t`Say yes.`",
                "\t\t`Say no.`");

            ConversationRunner runner = Run(conversation);

            Assert.AreEqual(new[] { "First line.", "Second line." }, runner.PendingText.ToArray());
            Assert.IsTrue(runner.IsAwaitingChoice);
            Assert.AreEqual(new[] { "Say yes.", "Say no." }, runner.Choices.ToArray());
            Assert.IsFalse(runner.IsFinished);
        }

        [Test]
        public void AConversationWithNoChoicesRunsStraightToItsEnding()
        {
            Conversation conversation = Load("\t`They wave you off.`", "\t\tdecline");

            ConversationRunner runner = Run(conversation);

            Assert.IsTrue(runner.IsFinished);
            Assert.AreEqual(ConversationOutcome.Decline, runner.Outcome);
        }

        [Test]
        public void ChoosingAnOptionFollowsItsGoto()
        {
            Conversation conversation = Load(
                "\tchoice",
                "\t\t`Take the job.`",
                "\t\t\tgoto accepted",
                "\t\t`Walk away.`",
                "\t\t\tgoto refused",
                "\tlabel accepted",
                "\t`You shake on it.`",
                "\t\taccept",
                "\tlabel refused",
                "\t`You turn and leave.`",
                "\t\tdecline");

            ConversationRunner runner = Run(conversation);
            runner.Choose(0);

            Assert.IsTrue(runner.IsFinished);
            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            Assert.AreEqual(new[] { "You shake on it." }, runner.PendingText.ToArray());
        }

        [Test]
        public void AnOptionWithoutAGotoFallsThroughToWhatFollows()
        {
            Conversation conversation = Load(
                "\tchoice",
                "\t\t`Ask why.`",
                "\t`\"Because I said so.\"`",
                "\t\taccept");

            ConversationRunner runner = Run(conversation);
            runner.Choose(0);

            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            Assert.AreEqual(new[] { "\"Because I said so.\"" }, runner.PendingText.ToArray());
        }

        [Test]
        public void BranchesTakeTheirLabelWhenTheConditionHolds()
        {
            Conversation conversation = Load(
                "\tbranch known",
                "\t\thas \"met before\"",
                "\t`\"I don't believe we've met.\"`",
                "\t\tdecline",
                "\tlabel known",
                "\t`\"Good to see you again.\"`",
                "\t\taccept");

            var stranger = new Conditions();
            ConversationRunner first = Run(conversation, stranger);
            Assert.AreEqual(ConversationOutcome.Decline, first.Outcome);
            Assert.AreEqual("\"I don't believe we've met.\"", first.PendingText[0]);

            var acquaintance = new Conditions();
            acquaintance.Set("met before", 1);
            ConversationRunner second = Run(conversation, acquaintance);
            Assert.AreEqual(ConversationOutcome.Accept, second.Outcome);
            Assert.AreEqual("\"Good to see you again.\"", second.PendingText[0]);
        }

        [Test]
        public void ABranchCanNameAnExplicitElseLabel()
        {
            Conversation conversation = Load(
                "\tbranch rich poor",
                "\t\t\"credits\" > 1000",
                "\tlabel rich",
                "\t`You pay easily.`",
                "\t\taccept",
                "\tlabel poor",
                "\t`You cannot afford it.`",
                "\t\tdecline");

            var broke = new Conditions();
            Assert.AreEqual(ConversationOutcome.Decline, Run(conversation, broke).Outcome);

            var flush = new Conditions();
            flush.Set("credits", 5000);
            Assert.AreEqual(ConversationOutcome.Accept, Run(conversation, flush).Outcome);
        }

        [Test]
        public void ActionsChangeConditionsMidConversation()
        {
            Conversation conversation = Load(
                "\t`You agree to help.`",
                "\taction",
                "\t\tset \"agreed to help\"",
                "\t\t\"reputation: Merchant\" += 5",
                "\t`They thank you.`",
                "\t\taccept");

            var conditions = new Conditions();
            ConversationRunner runner = Run(conversation, conditions);

            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            Assert.AreEqual(1L, conditions.Get("agreed to help"));
            Assert.AreEqual(5L, conditions.Get("reputation: Merchant"));
        }

        [Test]
        public void ActionsOnAnUntakenBranchDoNotFire()
        {
            // Side effects must follow the path actually walked, or a conversation
            // would grant every reward it contains.
            Conversation conversation = Load(
                "\tbranch skip",
                "\t\thas \"shortcut\"",
                "\taction",
                "\t\tset \"took the long way\"",
                "\tlabel skip",
                "\t`Done.`",
                "\t\taccept");

            var conditions = new Conditions();
            conditions.Set("shortcut", 1);
            Run(conversation, conditions);

            Assert.AreEqual(0L, conditions.Get("took the long way"));
        }

        [Test]
        public void EveryTerminalKeywordMapsToItsOutcome()
        {
            var expected = new Dictionary<string, ConversationOutcome>
            {
                ["accept"] = ConversationOutcome.Accept,
                ["decline"] = ConversationOutcome.Decline,
                ["defer"] = ConversationOutcome.Defer,
                ["die"] = ConversationOutcome.Die,
                ["launch"] = ConversationOutcome.Launch,
                ["flee"] = ConversationOutcome.Flee,
                ["depart"] = ConversationOutcome.Depart,
            };

            foreach (KeyValuePair<string, ConversationOutcome> entry in expected)
            {
                Conversation conversation = Load("\t`Text.`", "\t\t" + entry.Key);
                Assert.AreEqual(entry.Value, Run(conversation).Outcome, entry.Key);
            }
        }

        [Test]
        public void RunningOffTheEndIsADecline()
        {
            // Upstream maps any jump landing outside the node list to
            // Endpoint::DECLINE. Reporting "no outcome" instead invites a caller
            // to treat it as acceptance and hand out a mission the player never
            // agreed to.
            Conversation conversation = Load("	`They say nothing more.`");
            ConversationRunner runner = Run(conversation);

            Assert.IsTrue(runner.IsFinished);
            Assert.AreEqual(ConversationOutcome.Decline, runner.Outcome);
        }

        [Test]
        public void BranchCanTargetAnEndpointRatherThanALabel()
        {
            // "branch accept" is an ending, not a search for a label called
            // "accept"; upstream resolves the token as an endpoint first.
            Conversation conversation = Load(
                "	branch accept",
                "		has \"agreed\"",
                "	`You hesitate.`",
                "		decline");

            var agreed = new Conditions();
            agreed.Set("agreed", 1);
            Assert.AreEqual(ConversationOutcome.Accept, Run(conversation, agreed).Outcome);

            Assert.AreEqual(ConversationOutcome.Decline, Run(conversation, new Conditions()).Outcome);
        }

        [Test]
        public void ANamePromptIsNotRenderedAsDialogue()
        {
            // Upstream represents a name-entry field as an empty choice node.
            // Treating it as narration puts a line reading "name" on screen.
            Conversation conversation = Load(
                "	`What should we call you?`",
                "	name",
                "	`Pleased to meet you.`",
                "		accept");

            ConversationRunner runner = Run(conversation);

            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            CollectionAssert.DoesNotContain(runner.PendingText, "name");
        }

        [Test]
        public void ApplyRunsItsAssignmentsLikeAction()
        {
            Conversation conversation = Load(
                "	apply",
                "		set \"applied\"",
                "	`Done.`",
                "		accept");

            var conditions = new Conditions();
            Run(conversation, conditions);

            Assert.AreEqual(1L, conditions.Get("applied"));
        }

        [Test]
        public void ADuplicateLabelResolvesToItsFirstOccurrence()
        {
            Conversation conversation = Load(
                "	goto twice",
                "	label twice",
                "	`First.`",
                "		accept",
                "	label twice",
                "	`Second.`",
                "		decline");

            ConversationRunner runner = Run(conversation);

            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            CollectionAssert.Contains(runner.PendingText, "First.");
        }

        [Test]
        public void ALabelCycleAbortsInsteadOfHangingTheEngine()
        {
            // Content can contain cycles; looping forever inside a frame would hang
            // the game rather than showing a broken conversation.
            Conversation conversation = Load(
                "\tlabel loop",
                "\t`Round and round.`",
                "\t\tgoto loop");

            ConversationRunner runner = Run(conversation);

            Assert.IsTrue(runner.IsFinished);
            Assert.IsTrue(runner.AbortedOnCycle);
        }

        [Test]
        public void AMultiStepConversationWalksThroughSeveralChoices()
        {
            Conversation conversation = Load(
                "\t`An officer blocks your hatch.`",
                "\tchoice",
                "\t\t`(Follow orders.)`",
                "\t\t\tgoto orders",
                "\t\t`(Flee.)`",
                "\t\t\tgoto flee",
                "\tlabel orders",
                "\t`You follow them inside.`",
                "\tchoice",
                "\t\t`(Wait.)`",
                "\t`They question you for an hour.`",
                "\t\taccept",
                "\tlabel flee",
                "\t`A stun gun drops you.`",
                "\t\tdie");

            ConversationRunner runner = Run(conversation);
            Assert.AreEqual(2, runner.Choices.Count);

            runner.Choose(0);
            Assert.AreEqual(new[] { "You follow them inside." }, runner.PendingText.ToArray());
            Assert.AreEqual(new[] { "(Wait.)" }, runner.Choices.ToArray());

            runner.Choose(0);
            Assert.IsTrue(runner.IsFinished);
            Assert.AreEqual(ConversationOutcome.Accept, runner.Outcome);
            Assert.AreEqual(new[] { "They question you for an hour." }, runner.PendingText.ToArray());
        }

        // --- Against real upstream content ---------------------------------------

        [Test]
        public void RealUpstreamConversationsParseAndReachAnEnding()
        {
            string dataPath = UpstreamData.Path;
            Assert.IsNotNull(dataPath, "upstream data required");

            var conversations = new List<Conversation>();
            foreach (string path in System.IO.Directory.EnumerateFiles(
                         System.IO.Path.Combine(dataPath, "human"), "*.txt"))
            {
                foreach (DataNode node in DataFile.FromPath(path).Nodes)
                {
                    if (node.Token(0) == "conversation" && node.Size >= 2)
                        conversations.Add(Conversation.Load(node));
                }
            }

            Assert.Greater(conversations.Count, 20, "the human campaign has many conversations");

            // Walk each one taking the first option every time. None should hang, and
            // most should reach an explicit ending rather than running off the end.
            int ended = 0, cycled = 0;
            foreach (Conversation conversation in conversations)
            {
                var runner = new ConversationRunner(conversation, new Conditions());
                int guard = 0;
                while (!runner.IsFinished && guard++ < 500)
                    runner.Choose(0);

                if (runner.AbortedOnCycle) cycled++;
                if (runner.Outcome != ConversationOutcome.None) ended++;
            }

            Assert.AreEqual(0, cycled, "no upstream conversation should trip the cycle guard");
            Assert.Greater(ended, conversations.Count / 2,
                $"only {ended} of {conversations.Count} reached an explicit ending");

            TestContext.WriteLine($"walked {conversations.Count} conversations, {ended} ended explicitly");
        }
    }
}
