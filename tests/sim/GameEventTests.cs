using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Events: the scheduled and triggered changes that move the galaxy.
    /// Port checks against upstream <c>GameEvent</c>.
    /// </summary>
    [TestFixture]
    public class GameEventTests
    {
        private static GameData Load(string text)
        {
            var data = new GameData();
            data.LoadText(text);
            return data;
        }

        private static PlayerState PlayerIn(GameData data) => new PlayerState(data);

        // --- Parsing --------------------------------------------------------------

        [Test]
        public void ADateIsParsedIntoTheDayItFires()
        {
            GameData data = Load("event \"war begins\"\n\tdate 16 11 3014\n");

            GameEvent e = data.Events["war begins"];
            Assert.AreEqual(new DateTime(3014, 11, 16), e.Date);
            Assert.IsTrue(e.IsDue(new DateTime(3014, 11, 16)));
            Assert.IsTrue(e.IsDue(new DateTime(3015, 1, 1)), "an overdue event is still due");
            Assert.IsFalse(e.IsDue(new DateTime(3014, 11, 15)));
        }

        [Test]
        public void AnEventWithNoDateIsNeverDueOnItsOwn()
        {
            // Most events are fired by missions rather than by the calendar.
            GameData data = Load("event \"triggered only\"\n\tset \"flag\"\n");

            Assert.IsNull(data.Events["triggered only"].Date);
            Assert.IsFalse(data.Events["triggered only"].IsDue(new DateTime(9999, 1, 1)));
        }

        [Test]
        public void UnrecognisedChildrenBecomeConditionAssignments()
        {
            // Upstream's fallthrough: anything that is not a date, a visit mark or a
            // definition node is an assignment. This is why "set" and bare arithmetic
            // work inside an event without being listed anywhere.
            GameData data = Load(
                "event \"bookkeeping\"\n\tset \"chapter two\"\n\t\"reputation\" += 5\n");

            var player = PlayerIn(data);
            player.Conditions.Set("reputation", 10);
            data.Events["bookkeeping"].Apply(data, player);

            Assert.AreEqual(1, player.Conditions.Get("chapter two"));
            Assert.AreEqual(15, player.Conditions.Get("reputation"));
        }

        [Test]
        public void FiringAnEventRecordsThatItHappened()
        {
            GameData data = Load("event \"first contact\"\n\tset \"met them\"\n");
            var player = PlayerIn(data);

            Assert.AreEqual(0, player.Conditions.Get("event: first contact"));

            data.Events["first contact"].Apply(data, player);

            Assert.AreEqual(1, player.Conditions.Get("event: first contact"),
                "content gates on \"event: <name>\"");
        }

        // --- Patching the universe ------------------------------------------------

        [Test]
        public void AnEventCanStockAShipyardThatStartedEmpty()
        {
            // Exactly the Kestrel's situation in the real dataset: the shipyard is
            // defined empty and three events put ships in it. Without events the ship
            // is unobtainable however far the player progresses.
            GameData data = Load(
                "ship \"Kestrel\"\n\tattributes\n\t\t\"mass\" 900\n" +
                "shipyard \"Kestrel\"\n" +
                "event \"kestrel available\"\n\tshipyard \"Kestrel\"\n\t\t\"Kestrel\"\n");

            Assert.IsFalse(data.Shipyards["Kestrel"].Contains("Kestrel"), "empty to begin with");

            data.Events["kestrel available"].Apply(data, PlayerIn(data));

            Assert.IsTrue(data.Shipyards["Kestrel"].Contains("Kestrel"),
                "the event should have stocked it");
        }

        [Test]
        public void AnEventCanChangeAPlanetsGovernment()
        {
            GameData data = Load(
                "planet \"Contested\"\n\tgovernment \"Republic\"\n" +
                "event \"occupation\"\n\tplanet \"Contested\"\n\t\tgovernment \"Syndicate\"\n");

            Assert.AreEqual("Republic", data.Planets["Contested"].Government);

            data.Events["occupation"].Apply(data, PlayerIn(data));

            Assert.AreEqual("Syndicate", data.Planets["Contested"].Government);
        }

        [Test]
        public void LinkAndUnlinkOpenAndCloseHyperspaceRoutes()
        {
            // "link" is not a definition node; it modifies two systems at once, and in
            // both directions, or the route would be one-way.
            GameData data = Load(
                "system \"Alpha\"\n\tpos 0 0\n" +
                "system \"Beta\"\n\tpos 100 0\n" +
                "event \"open route\"\n\tlink \"Alpha\" \"Beta\"\n" +
                "event \"close route\"\n\tunlink \"Alpha\" \"Beta\"\n");

            Assert.IsFalse(data.Systems["Alpha"].Links.Contains("Beta"));

            data.Events["open route"].Apply(data, PlayerIn(data));
            Assert.IsTrue(data.Systems["Alpha"].Links.Contains("Beta"));
            Assert.IsTrue(data.Systems["Beta"].Links.Contains("Alpha"),
                "a hyperspace link works both ways");

            data.Events["close route"].Apply(data, PlayerIn(data));
            Assert.IsFalse(data.Systems["Alpha"].Links.Contains("Beta"));
            Assert.IsFalse(data.Systems["Beta"].Links.Contains("Alpha"));
        }

        [Test]
        public void OpeningARouteTwiceDoesNotDuplicateIt()
        {
            GameData data = Load(
                "system \"Alpha\"\n\tpos 0 0\n\tlink \"Beta\"\n" +
                "system \"Beta\"\n\tpos 100 0\n" +
                "event \"open route\"\n\tlink \"Alpha\" \"Beta\"\n");

            data.Events["open route"].Apply(data, PlayerIn(data));

            Assert.AreEqual(1, data.Systems["Alpha"].Links.Count(l => l == "Beta"));
        }

        // --- Visit marks ----------------------------------------------------------

        [Test]
        public void VisitAndUnvisitMarkTheMapWithoutMovingThePlayer()
        {
            GameData data = Load(
                "planet \"Hidden\"\n" +
                "system \"Secret\"\n\tpos 0 0\n" +
                // The two-word keys are QUOTED in real content, which is what makes
                // them a single token: `"unvisit planet" "Varu K'prai"`.
                "event \"reveal\"\n\tvisit \"Secret\"\n\t\"visit planet\" \"Hidden\"\n" +
                "event \"forget\"\n\tunvisit \"Secret\"\n\t\"unvisit planet\" \"Hidden\"\n");

            var player = PlayerIn(data);

            data.Events["reveal"].Apply(data, player);
            Assert.AreEqual(1, player.Conditions.Get("visited system: Secret"));
            Assert.AreEqual(1, player.Conditions.Get("visited planet: Hidden"));
            Assert.IsNull(player.CurrentSystem, "marking the map must not move the player");

            data.Events["forget"].Apply(data, player);
            Assert.AreEqual(0, player.Conditions.Get("visited system: Secret"));
            Assert.AreEqual(0, player.Conditions.Get("visited planet: Hidden"));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetsEventsLoad()
        {
            GameData data = UpstreamData.Instance;

            Assert.Greater(data.Events.Count, 100, "upstream defines hundreds of events");

            int dated = data.Events.Values.Count(e => e.Date.HasValue);
            int patching = data.Events.Values.Count(e => e.Changes.Count > 0);

            TestContext.WriteLine(
                $"{data.Events.Count} events; {dated} scheduled by date, {patching} patch the universe");

            Assert.Greater(patching, 0, "events exist to change the galaxy");
        }

        [Test]
        public void TheKestrelBecomesObtainableOnceItsEventsFire()
        {
            // End to end on real content, and the reason events matter: the Kestrel has
            // no fleet and an empty shipyard, so it is unobtainable until an event
            // stocks it. This is the same ship PlayerStateTests records as having no
            // faction for exactly that reason.
            GameData data = UpstreamData.Instance;

            if (!data.Shipyards.ContainsKey("Kestrel"))
                Assert.Ignore("dataset has no Kestrel shipyard");

            var stocking = data.Events.Values
                .Where(e => e.Changes.Any(c => c.Token(0) == "shipyard" &&
                                               c.Size >= 2 && c.Token(1) == "Kestrel"))
                .ToList();

            TestContext.WriteLine($"{stocking.Count} events stock the Kestrel shipyard: " +
                                  string.Join(", ", stocking.Select(e => e.Name)));

            Assert.IsNotEmpty(stocking, "some event must make the Kestrel available");

            var fresh = new GameData();
            fresh.LoadDirectory(UpstreamData.RequiredPath);
            Assert.IsEmpty(fresh.Shipyards["Kestrel"].Items, "empty before any event fires");

            foreach (GameEvent e in stocking.Select(s => fresh.Events[s.Name]))
                e.Apply(fresh, new PlayerState(fresh));

            Assert.IsNotEmpty(fresh.Shipyards["Kestrel"].Items,
                "and stocked once its events have fired");
            TestContext.WriteLine("stocked with: " +
                                  string.Join(", ", fresh.Shipyards["Kestrel"].Items));
        }

        // --- Events actually firing ------------------------------------------------

        private static (PlayerState player, GameData data) EventFixture()
        {
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "event \"war breaks out\"",
                "\tset \"at war\"",
                "system \"Sol\"",
                "\tpos 0 0") + "\n");

            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["Sol"]);
            return (player, data);
        }

        [Test]
        public void AScheduledEventFiresOnItsDayAndNotBefore()
        {
            // Events were parsed and nothing ever fired them: no queue, no date check.
            // 416 of them sat in the dataset doing nothing, which is most of how the
            // galaxy is supposed to change underneath the player.
            (PlayerState player, GameData data) = EventFixture();

            player.ScheduleEvent("war breaks out", player.Date.AddDays(3));

            for (int day = 0; day < 2; day++)
            {
                player.AdvanceDays(1);
                player.FireDueEvents(data);
            }

            Assert.AreEqual(0, player.Conditions.Get("at war"), "not yet");

            player.AdvanceDays(1);
            player.FireDueEvents(data);

            Assert.AreEqual(1, player.Conditions.Get("at war"), "the day arrives");
        }

        [Test]
        public void AnEventFiresOnceAndIsForgotten()
        {
            (PlayerState player, GameData data) = EventFixture();
            player.ScheduleEvent("war breaks out", player.Date);

            player.FireDueEvents(data);
            player.Conditions.Set("at war", 0);

            player.AdvanceDays(10);
            player.FireDueEvents(data);

            Assert.AreEqual(0, player.Conditions.Get("at war"), "it does not fire again");
        }

        [Test]
        public void AMissionActionCanScheduleAnEvent()
        {
            // GameAction.cpp:217-223: `event "<name>" <min> <max>` schedules it that
            // many days out. Unparsed, a mission that sets the galaxy in motion did
            // nothing at all.
            var data = new GameData();
            data.LoadText(string.Join("\n",
                "event \"war breaks out\"",
                "\tset \"at war\"",
                "mission \"Spark\"",
                "\ton accept",
                "\t\tevent \"war breaks out\" 5 5") + "\n");

            MissionAction accept = data.Missions["Spark"].Action(MissionTrigger.Accept)!;

            Assert.AreEqual(1, accept.Events.Count);
            Assert.AreEqual("war breaks out", accept.Events[0].Name);
            Assert.AreEqual(5, accept.Events[0].MinDays);
            Assert.AreEqual(5, accept.Events[0].MaxDays);
        }
    }
}
