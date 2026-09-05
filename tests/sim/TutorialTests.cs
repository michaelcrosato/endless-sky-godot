using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The opening tutorial: land, take a job, jump, deliver.
    /// </summary>
    /// <remarks>
    /// The state machine is driven by POLLING observed facts rather than by the view
    /// announcing what happened, and these tests are why that is worth its slight
    /// awkwardness: every transition below is provable with no engine running. An
    /// event-fed design would put the interesting half — did the right call site fire?
    /// — on the view side of the architecture boundary, which is exactly where this
    /// project keeps losing defects.
    /// </remarks>
    public class TutorialTests
    {
        private static TutorialState State(
            bool landed = false,
            bool hasJob = false,
            bool delivered = false,
            int jobsOnOffer = 3,
            string system = "Home",
            string? destination = "Away") =>
            new TutorialState
            {
                IsLanded = landed,
                HasJob = hasJob,
                DeliveredAJob = delivered,
                JobsOnOffer = jobsOnOffer,
                CurrentSystem = system,
                DestinationSystem = destination,
            };

        // --- The sequence ---------------------------------------------------------

        [Test]
        public void ItOpensByAskingForTheThingTheKeyboardCannotExplain()
        {
            var tutorial = new Tutorial();

            Assert.AreEqual(TutorialStep.Land, tutorial.Step);
            StringAssert.Contains("L", tutorial.Prompt, "the prompt has to name the key");
            Assert.IsFalse(tutorial.IsComplete);
        }

        [Test]
        public void EachStepWaitsForItsOwnEventAndNotForAnyOther()
        {
            var tutorial = new Tutorial();

            // Flying about and jumping are not landing.
            tutorial.Observe(State(system: "Elsewhere"));
            Assert.AreEqual(TutorialStep.Land, tutorial.Step);

            tutorial.Observe(State(landed: true));
            Assert.AreEqual(TutorialStep.TakeJob, tutorial.Step);

            // Landing again does not stand in for accepting work.
            tutorial.Observe(State(landed: true));
            Assert.AreEqual(TutorialStep.TakeJob, tutorial.Step);

            tutorial.Observe(State(landed: true, hasJob: true));
            Assert.AreEqual(TutorialStep.Jump, tutorial.Step);
            StringAssert.Contains("Away", tutorial.Prompt, "the prompt names where the job goes");

            // Still in the origin system: not yet.
            tutorial.Observe(State(hasJob: true, system: "Home"));
            Assert.AreEqual(TutorialStep.Jump, tutorial.Step);

            tutorial.Observe(State(hasJob: true, system: "Away"));
            Assert.AreEqual(TutorialStep.Deliver, tutorial.Step);

            // Arriving is not delivering.
            tutorial.Observe(State(landed: true, hasJob: true, system: "Away"));
            Assert.AreEqual(TutorialStep.Deliver, tutorial.Step);

            tutorial.Observe(State(landed: true, delivered: true, system: "Away"));
            Assert.AreEqual(TutorialStep.Done, tutorial.Step);
            Assert.IsTrue(tutorial.IsComplete);
        }

        [Test]
        public void FinishingAStepSaysSoOnceRatherThanEveryFrame()
        {
            var tutorial = new Tutorial();

            Assert.IsNull(tutorial.Observe(State()), "nothing happened, so nothing to say");

            Assert.IsNotNull(tutorial.Observe(State(landed: true)),
                             "the step it just finished is worth confirming");

            Assert.IsNull(tutorial.Observe(State(landed: true)),
                          "and confirming once, not on every frame the player stays landed");
        }

        // --- Not getting in the way -----------------------------------------------

        [Test]
        public void AWorldWithNoWorkDoesNotStrandThePlayerOnStepTwo()
        {
            // The tutorial must never ask for something the galaxy cannot supply. A
            // start world with an empty counter would otherwise hold the player on
            // "take a job" forever, and the instruction they cannot follow would be the
            // last thing the game ever said to them.
            var tutorial = new Tutorial();
            tutorial.Observe(State(landed: true));
            Assert.AreEqual(TutorialStep.TakeJob, tutorial.Step);

            tutorial.Observe(State(landed: true, jobsOnOffer: 0));

            Assert.IsTrue(tutorial.IsComplete,
                          "with no work on the board the tutorial gets out of the way");
        }

        [Test]
        public void AJobThatNamesNoDestinationDoesNotHoldThePlayerOnJump()
        {
            // Not every job sends you somewhere. Asking a player to jump to a system
            // their mission never named is an instruction with no correct action.
            var tutorial = new Tutorial();
            tutorial.Observe(State(landed: true));
            tutorial.Observe(State(landed: true, hasJob: true, destination: null));
            // Observe advances at most one step per call, so the jump step is entered
            // and then immediately skipped on the following look.
            tutorial.Observe(State(landed: true, hasJob: true, destination: null));

            Assert.AreEqual(TutorialStep.Deliver, tutorial.Step,
                            "with nowhere to jump to, the jump step is not a step");
        }

        [Test]
        public void ItCanBeDismissedAndStaysDismissed()
        {
            var tutorial = new Tutorial();
            tutorial.Dismiss();

            Assert.IsTrue(tutorial.IsDismissed);
            Assert.IsTrue(tutorial.IsComplete, "a dismissed tutorial shows nothing further");

            tutorial.Observe(State(landed: true));
            Assert.IsTrue(tutorial.IsDismissed, "and observing the world does not revive it");
            Assert.AreEqual(TutorialStep.Land, tutorial.Step, "nor does it advance behind the scenes");
        }

        [Test]
        public void APlayerWhoAlreadyKnowsHowIsNotMadeToRepeatTheSteps()
        {
            // Landing with a job already accepted skips both steps rather than
            // insisting on the ceremony. Each Observe advances at most one step, so
            // this also pins that the machine keeps up with a player moving faster
            // than it does.
            var tutorial = new Tutorial();

            for (int frame = 0; frame < 4; frame++)
                tutorial.Observe(State(landed: true, hasJob: true, system: "Away"));

            Assert.AreEqual(TutorialStep.Deliver, tutorial.Step);
        }

        // --- Against the galaxy the game actually plays ---------------------------

        [Test]
        public void TheRealStartingWorldCanSatisfyEveryStepTheTutorialAsksFor()
        {
            // A tutorial is a promise that its steps are possible where the player is
            // standing. This is the test that the promise holds in the shipped galaxy:
            // somewhere to land, work on the counter when they get there, and a
            // destination they can actually be sent to.
            GameData universe = GeneratedUniverse.Instance;
            StartScenario start = universe.Starts.Values.First();
            StarSystem system = universe.Systems[start.SystemName!];
            system.SetDate(0.0);

            Assert.IsTrue(system.AllObjects().Any(o => o.Planet is not null),
                          "step 1 asks the player to land, so there must be somewhere to land");

            // The galaxy has to be handed in: a mission's destination is a FILTER,
            // and resolving it at accept time needs the data to resolve against.
            var player = new PlayerState(universe);
            start.ApplyTo(player, universe);
            player.Land(universe.Planets[start.PlanetName!]);

            var missions = new MissionLog(player);
            List<Mission> board = missions.Available(universe, MissionLocation.Job).ToList();

            TestContext.WriteLine($"{start.PlanetName} ({system.Name}, {system.Government}): " +
                                  $"{board.Count} jobs on the board");

            Assert.IsNotEmpty(board, "step 2 asks the player to take a job, so one must be offered");

            ActiveMission? taken = missions.Accept(board[0]);
            Assert.IsNotNull(taken, "and it must be acceptable");

            string? destination = taken!.Destination;
            TestContext.WriteLine($"  accepted: {taken.Mission.DisplayName} → {destination ?? "(nowhere)"}");

            Assert.IsNotNull(destination,
                             "step 4 asks the player to deliver, so the job must fix a destination");
            Assert.IsTrue(universe.Planets.ContainsKey(destination!),
                          "and that destination must be a world that exists");
            Assert.AreNotEqual(start.PlanetName, destination,
                               "a job that ends where it started teaches nothing about travelling");

            // And the last step has to be finishable. The tutorial ends by handing the
            // job in, so the data has to permit that once the player is standing in the
            // right place: the cargo still aboard, the conditions satisfiable, the
            // deadline not already past on arrival.
            player.Land(universe.Planets[destination!]);

            long before = player.Credits;
            Assert.IsTrue(missions.CanComplete(taken), "the job must be completable at its destination");
            Assert.IsTrue(missions.Complete(taken), "and completing it must succeed");

            TestContext.WriteLine($"  handed in at {destination} for {player.Credits - before:n0} credits");
            Assert.Greater(player.Credits, before, "a delivered job pays");
            Assert.AreEqual(MissionOutcome.Completed, taken.Outcome);
        }
    }
}
