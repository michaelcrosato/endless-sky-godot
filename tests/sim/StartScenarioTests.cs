using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Where a new pilot begins. Port checks against upstream
    /// <c>StartConditions</c>.
    /// </summary>
    [TestFixture]
    public class StartScenarioTests
    {
        private const string Universe =
            "planet \"New Boston\"\n\tgovernment \"Republic\"\n\tspaceport `Busy.`\n" +
            "system \"Rutilicus\"\n\tpos 0 0\n" +
            "\tobject \"New Boston\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n" +
            "start \"default\"\n" +
            "\tname \"Endless Sky\"\n" +
            "\tdescription `You grew up on New Boston.`\n" +
            "\tdescription `This is the classic experience.`\n" +
            "\tdate 16 11 3013\n" +
            "\tsystem \"Rutilicus\"\n" +
            "\tplanet \"New Boston\"\n" +
            "\tconversation \"default intro\"\n" +
            "\taccount\n\t\tcredits 480000\n\t\tscore 400\n" +
            "\t\tmortgage Mortgage\n\t\t\tprincipal 480000\n\t\t\tinterest 0.004\n\t\t\tterm 365\n" +
            "\tset \"license: Pilot's\"\n" +
            "\tset \"species: human\"\n" +
            "start \"other\"\n\tname \"Elsewhere\"\n\tsystem \"Rutilicus\"\n";

        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(Universe);
            return data;
        }

        // --- Parsing --------------------------------------------------------------

        [Test]
        public void AStartCarriesItsPlaceDateAndMoney()
        {
            StartScenario start = Load().Starts["default"];

            Assert.AreEqual("Endless Sky", start.DisplayName);
            Assert.AreEqual(new DateTime(3013, 11, 16), start.Date);
            Assert.AreEqual("Rutilicus", start.SystemName);
            Assert.AreEqual("New Boston", start.PlanetName);
            Assert.AreEqual(480_000, start.Credits);
            Assert.AreEqual("default intro", start.Conversation);
        }

        [Test]
        public void DescriptionParagraphsAreKeptSeparate()
        {
            StartScenario start = Load().Starts["default"];

            Assert.AreEqual(2, start.Description.Count);
            StringAssert.Contains("New Boston", start.Description[0]);
        }

        [Test]
        public void TheMortgageIsRecorded()
        {
            // The classic start begins in debt; that is the whole premise of it.
            Assert.AreEqual(480_000, Load().Starts["default"].MortgagePrincipal);
        }

        [Test]
        public void UnrecognisedChildrenBecomeConditionAssignments()
        {
            // Same fallthrough upstream uses for events, which is how "set" works here
            // without being enumerated.
            var conditions = new Conditions();
            Load().Starts["default"].Conditions.Apply(conditions);

            Assert.AreEqual(1, conditions.Get("license: Pilot's"));
            Assert.AreEqual(1, conditions.Get("species: human"));
        }

        [Test]
        public void TheDefaultStartIsTheOneNamedDefault()
        {
            GameData data = Load();

            Assert.AreEqual(2, data.Starts.Count);
            Assert.AreEqual("default", data.DefaultStart!.Name);
        }

        [Test]
        public void WithNoDefaultTheFirstStartIsUsed()
        {
            var data = new GameData();
            data.LoadText("start \"only one\"\n\tname \"Only\"\n");

            Assert.AreEqual("only one", data.DefaultStart!.Name);
        }

        [Test]
        public void NoStartsMeansNoDefault()
        {
            var data = new GameData();
            data.LoadText("system \"Nowhere\"\n\tpos 0 0\n");

            Assert.IsNull(data.DefaultStart);
        }

        // --- Applying -------------------------------------------------------------

        [Test]
        public void ApplyingAStartPlacesThePlayer()
        {
            GameData data = Load();
            var player = new PlayerState(data);

            data.DefaultStart!.ApplyTo(player, data);

            Assert.AreEqual(new DateTime(3013, 11, 16), player.Date);
            Assert.AreEqual(new DateTime(3013, 11, 16), player.StartDate);
            Assert.AreEqual(480_000, player.Credits);
            Assert.AreEqual("Rutilicus", player.CurrentSystem!.Name);
            Assert.AreEqual("New Boston", player.CurrentPlanet!.Name);
        }

        [Test]
        public void ApplyingAStartSetsTheConditionsContentGatesOn()
        {
            // The conditions matter as much as the money: content checks for the
            // licence and the species, so a player placed by hand never sees the
            // campaign that checks for them.
            GameData data = Load();
            var player = new PlayerState(data);

            data.DefaultStart!.ApplyTo(player, data);

            Assert.AreEqual(1, player.Conditions.Get("license: Pilot's"));
            Assert.AreEqual(1, player.Conditions.Get("species: human"));
            Assert.AreEqual(1, player.Conditions.Get("flagship landed"),
                "and the player is standing on the ground");
        }

        [Test]
        public void DaysSinceStartCountsFromTheStartDate()
        {
            GameData data = Load();
            var player = new PlayerState(data);
            data.DefaultStart!.ApplyTo(player, data);

            player.AdvanceDays(30);

            Assert.AreEqual(30, player.Conditions.Get("days since start"));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetsStartIsWhatTheGameUsedToHardCode()
        {
            // Milestone 7's rule is not to hard-code content that can be loaded from
            // the source data. Every one of these was a constant in the flight scene.
            GameData data = UpstreamData.Instance;

            StartScenario? start = data.DefaultStart;
            Assert.IsNotNull(start, "the dataset defines a starting scenario");

            TestContext.WriteLine($"{data.Starts.Count} starts; default is {start}");

            Assert.AreEqual("Rutilicus", start!.SystemName);
            Assert.AreEqual("New Boston", start.PlanetName);
            Assert.AreEqual(new DateTime(3013, 11, 16), start.Date);
            Assert.AreEqual(480_000, start.Credits);
        }

        [Test]
        public void ARealPlayerCanBePlacedFromTheData()
        {
            GameData data = UpstreamData.Instance;
            var player = new PlayerState(data);

            data.DefaultStart!.ApplyTo(player, data);

            Assert.AreEqual("New Boston", player.CurrentPlanet!.Name);
            Assert.Greater(player.Conditions.Values.Count, 0, "starting conditions are set");

            TestContext.WriteLine("conditions: " + string.Join(", ",
                player.Conditions.Values.Select(v => $"{v.Key}={v.Value}")));
        }
    }
}
