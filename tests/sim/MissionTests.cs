using System;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Mission definitions and their condition gates. Engine-free.
    /// </summary>
    [TestFixture]
    public class MissionTests
    {
        private static Mission LoadMission(string text)
        {
            DataNode node = new DataFile(text, "test.txt").Nodes[0];
            var mission = new Mission(node.Token(1));
            mission.Load(node);
            return mission;
        }

        [Test]
        public void AMissionReadsItsIdentityAndFlags()
        {
            Mission mission = LoadMission(
                "mission \"Assisting Merchant (Large)\"\n" +
                "\tassisting\n" +
                "\trepeat\n" +
                "\tminor\n" +
                "\tdestination \"Earth\"\n");

            Assert.AreEqual("Assisting Merchant (Large)", mission.Name);
            Assert.IsTrue(mission.IsAssisting);
            Assert.IsTrue(mission.IsRepeating);
            Assert.IsTrue(mission.IsMinor);
            Assert.IsFalse(mission.IsJob);
            Assert.AreEqual("Earth", mission.Destination);
        }

        [Test]
        public void DisplayNameFallsBackToTheIdentifier()
        {
            Mission plain = LoadMission("mission \"internal id\"\n");
            Assert.AreEqual("internal id", plain.DisplayName);

            Mission named = LoadMission("mission \"internal id\"\n\tname \"Deliver the Package\"\n");
            Assert.AreEqual("Deliver the Package", named.DisplayName);
            Assert.AreEqual("internal id", named.Name, "the identifier is unchanged");
        }

        [Test]
        public void CargoAndPassengersAreRead()
        {
            Mission mission = LoadMission(
                "mission \"Run\"\n\tcargo \"Food\" 25\n\tpassengers 4\n\tdeadline 10\n");

            Assert.AreEqual("Food", mission.CargoType);
            Assert.AreEqual(25, mission.CargoTons);
            Assert.AreEqual(4, mission.Passengers);
            Assert.AreEqual(10, mission.DeadlineDays);
        }

        [Test]
        public void OfferGatesAreEvaluatedAgainstPlayerConditions()
        {
            Mission mission = LoadMission(
                "mission \"Second Chapter\"\n" +
                "\tto offer\n" +
                "\t\thas \"chapter one done\"\n" +
                "\t\tnot \"chapter two done\"\n");

            var conditions = new Conditions();
            Assert.IsFalse(mission.CanOffer(conditions));

            conditions.Set("chapter one done", 1);
            Assert.IsTrue(mission.CanOffer(conditions));

            conditions.Set("chapter two done", 1);
            Assert.IsFalse(mission.CanOffer(conditions), "already done, do not offer again");
        }

        [Test]
        public void AMissionWithNoOfferGateIsAlwaysAvailable()
        {
            Mission mission = LoadMission("mission \"Always\"\n\tjob\n");

            Assert.IsTrue(mission.ToOffer.IsEmpty);
            Assert.IsTrue(mission.CanOffer(new Conditions()));
        }

        [Test]
        public void CompletionAndFailureAreSeparateGates()
        {
            Mission mission = LoadMission(
                "mission \"Delivery\"\n" +
                "\tto complete\n" +
                "\t\thas \"package delivered\"\n" +
                "\tto fail\n" +
                "\t\thas \"package destroyed\"\n");

            var conditions = new Conditions();
            Assert.IsFalse(mission.CanComplete(conditions));
            Assert.IsFalse(mission.HasFailed(conditions));

            conditions.Set("package destroyed", 1);
            Assert.IsTrue(mission.HasFailed(conditions));
            Assert.IsFalse(mission.CanComplete(conditions));
        }

        [Test]
        public void AMissionWithoutAFailGateNeverSpontaneouslyFails()
        {
            // An empty ConditionSet passes, so treating "to fail" as a plain test would
            // fail every mission that does not define one.
            Mission mission = LoadMission("mission \"Simple\"\n\tto complete\n\t\thas \"done\"\n");

            Assert.IsTrue(mission.ToFail.IsEmpty);
            Assert.IsFalse(mission.HasFailed(new Conditions()));
        }

        [Test]
        public void TriggersApplyConditionChangesAndReturnPayment()
        {
            Mission mission = LoadMission(
                "mission \"Courier\"\n" +
                "\ton offer\n" +
                "\t\tset \"courier offered\"\n" +
                "\ton complete\n" +
                "\t\tpayment 30000\n" +
                "\t\tset \"courier done\"\n" +
                "\t\t\"reputation: Merchant\" += 5\n");

            var conditions = new Conditions();

            Assert.AreEqual(0L, mission.Fire(MissionTrigger.Offer, conditions));
            Assert.AreEqual(1L, conditions.Get("courier offered"));

            long paid = mission.Fire(MissionTrigger.Complete, conditions);

            Assert.AreEqual(30000L, paid);
            Assert.AreEqual(1L, conditions.Get("courier done"));
            Assert.AreEqual(5L, conditions.Get("reputation: Merchant"));
        }

        [Test]
        public void FiringAnAbsentTriggerIsANoOp()
        {
            Mission mission = LoadMission("mission \"Bare\"\n");

            Assert.AreEqual(0L, mission.Fire(MissionTrigger.Complete, new Conditions()));
            Assert.IsNull(mission.Action(MissionTrigger.Complete));
        }

        [Test]
        public void ActionsCarryConversationAndDialogReferences()
        {
            Mission mission = LoadMission(
                "mission \"Talky\"\n" +
                "\ton offer\n" +
                "\t\tconversation \"assisting merchant\"\n" +
                "\ton complete\n" +
                "\t\tdialog \"Thanks for the help.\"\n");

            Assert.AreEqual("assisting merchant", mission.Action(MissionTrigger.Offer)!.Conversation);
            Assert.AreEqual("Thanks for the help.", mission.Action(MissionTrigger.Complete)!.Dialog);
        }

        [Test]
        public void AFullMissionLifecycleRunsThroughItsGates()
        {
            Mission mission = LoadMission(
                "mission \"Package Run\"\n" +
                "\tto offer\n" +
                "\t\tnot \"package run done\"\n" +
                "\tto complete\n" +
                "\t\thas \"package delivered\"\n" +
                "\ton complete\n" +
                "\t\tpayment 12000\n" +
                "\t\tset \"package run done\"\n");

            var conditions = new Conditions();
            long credits = 0;

            Assert.IsTrue(mission.CanOffer(conditions), "offered first time");
            Assert.IsFalse(mission.CanComplete(conditions));

            conditions.Set("package delivered", 1);
            Assert.IsTrue(mission.CanComplete(conditions));

            credits += mission.Fire(MissionTrigger.Complete, conditions);

            Assert.AreEqual(12000L, credits);
            Assert.IsFalse(mission.CanOffer(conditions),
                "completing it sets the flag that gates it out");
        }

        // --- Against real upstream content ---------------------------------------

        [Test]
        public void RealUpstreamMissionsParseWithoutLosingTheirGates()
        {
            string dataPath = UpstreamData.RequiredPath;
            Assert.IsNotNull(dataPath, "upstream data required");

            var missions = new System.Collections.Generic.List<Mission>();
            foreach (string path in System.IO.Directory.EnumerateFiles(
                         System.IO.Path.Combine(dataPath, "human"), "*.txt"))
            {
                foreach (DataNode node in DataFile.FromPath(path).Nodes)
                {
                    if (node.Token(0) != "mission" || node.Size < 2)
                        continue;

                    var mission = new Mission(node.Token(1));
                    mission.Load(node);
                    missions.Add(mission);
                }
            }

            Assert.Greater(missions.Count, 50, "the human campaign defines many missions");

            // Most missions gate on something; if none did, the parser is dropping
            // "to offer" blocks silently.
            int gated = missions.Count(m => !m.ToOffer.IsEmpty);
            Assert.Greater(gated, 10, $"only {gated} of {missions.Count} missions had offer gates");

            // And a good number pay or set conditions on completion.
            int withCompletion = missions.Count(m => m.Action(MissionTrigger.Complete) is not null);
            Assert.Greater(withCompletion, 10);

            TestContext.WriteLine(
                $"parsed {missions.Count} human missions, {gated} gated, {withCompletion} with completion actions");
        }

        // --- Deadlines have four shapes -------------------------------------------

        private static Mission Parse(params string[] lines)
        {
            var data = new GameData();
            data.LoadText(string.Join("\n", lines) + "\n");
            return data.Missions.Values.First();
        }

        [Test]
        public void ABareDeadlineIsPerJumpNotNoDeadlineAtAll()
        {
            // Mission.cpp:165-172 splits deadline into a base and a per-jump
            // multiplier: bare `deadline` adds 2 to the multiplier. Reading only a
            // numeric token 1 meant 162 upstream missions parsed to no deadline
            // whatever, so the clock the content is built around never ran.
            Mission job = Parse("mission \"Bare\"", "\tdeadline");

            Assert.AreEqual(0, job.DeadlineBase);
            Assert.AreEqual(2, job.DeadlineMultiplier, "two days per jump");
        }

        [Test]
        public void ANumericDeadlineIsABase()
        {
            Mission job = Parse("mission \"Fixed\"", "\tdeadline 30");

            Assert.AreEqual(30, job.DeadlineBase);
            Assert.AreEqual(0, job.DeadlineMultiplier);
        }

        [Test]
        public void ATwoNumberDeadlineSetsBothHalves()
        {
            Mission job = Parse("mission \"Both\"", "\tdeadline 10 3");

            Assert.AreEqual(10, job.DeadlineBase);
            Assert.AreEqual(3, job.DeadlineMultiplier);
        }

        [Test]
        public void AThreeNumberDeadlineIsAnAbsoluteDate()
        {
            // `deadline 16 11 3013` is a DATE, not "sixteen days". Reading token 1 as
            // a day count turned every dated deadline into a sixteen-day one.
            Mission job = Parse("mission \"Dated\"", "\tdeadline 16 11 3013");

            Assert.AreEqual(new DateTime(3013, 11, 16), job.AbsoluteDeadline);
            Assert.AreEqual(0, job.DeadlineBase);
        }

        // --- Where a mission is offered from --------------------------------------

        [Test]
        public void AMissionKnowsWhichCounterOffersIt()
        {
            // Mission.h:108 lists ten offer locations and Mission.cpp:223-244 parses
            // nine keywords for them. Reading only "job" left every other mission at the
            // default, so a job board showed boarding missions, shipyard missions and
            // the ones that fire on entering a system all mixed together.
            Assert.AreEqual(MissionLocation.Job,
                Parse("mission \"A\"", "\tjob").Offered);
            Assert.AreEqual(MissionLocation.Landing,
                Parse("mission \"B\"", "\tlanding").Offered);
            Assert.AreEqual(MissionLocation.Shipyard,
                Parse("mission \"C\"", "\tshipyard").Offered);
            Assert.AreEqual(MissionLocation.Outfitter,
                Parse("mission \"D\"", "\toutfitter").Offered);
            Assert.AreEqual(MissionLocation.JobBoard,
                // Quoted, because "job board" is one key with a space in it — written
                // bare it tokenises as "job" plus "board" and matches the job case.
                Parse("mission \"E\"", "\t\"job board\"").Offered);
            Assert.AreEqual(MissionLocation.Entering,
                Parse("mission \"F\"", "\tentering").Offered);
            Assert.AreEqual(MissionLocation.Boarding,
                Parse("mission \"G\"", "\tboarding").Offered);
            Assert.AreEqual(MissionLocation.Assisting,
                Parse("mission \"H\"", "\tassisting").Offered);
        }

        [Test]
        public void AMissionThatSaysNothingIsOfferedInTheSpaceport()
        {
            // SPACEPORT is upstream's default, and the reason a plain mission shows up
            // when the player walks into a port rather than never at all.
            Assert.AreEqual(MissionLocation.Spaceport, Parse("mission \"Plain\"").Offered);
        }

        [Test]
        public void TheJobBoardShowsJobsAndNotBoardingMissions()
        {
            // The player-visible consequence: a board full of missions that cannot be
            // taken from a board.
            Mission job = Parse("mission \"Work\"", "\tjob");
            Mission boarding = Parse("mission \"Salvage\"", "\tboarding");

            Assert.IsTrue(job.IsOfferedFrom(MissionLocation.Job));
            Assert.IsFalse(boarding.IsOfferedFrom(MissionLocation.Job));
            Assert.IsTrue(boarding.IsOfferedFrom(MissionLocation.Boarding));
        }
    }
}
