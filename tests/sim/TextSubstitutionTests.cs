using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Filling the angle-bracket placeholders mission text is written with. Port
    /// checks against the substitution map upstream builds in
    /// <c>Mission::Instantiate</c>.
    /// </summary>
    [TestFixture]
    public class TextSubstitutionTests
    {
        private const string Universe =
            "ship \"Hauler\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 900\n" +
            "\t\t\"cargo space\" 50\n\t\t\"energy capacity\" 100\n" +
            "planet \"New Boston\"\n\tgovernment \"Republic\"\n\tspaceport `Busy.`\n" +
            "planet \"Delve\"\n\tgovernment \"Republic\"\n\tspaceport `Quiet.`\n" +
            "system \"Rutilicus\"\n\tpos 0 0\n" +
            "\tobject \"New Boston\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n" +
            "system \"Alnitak\"\n\tpos 100 0\n" +
            "\tobject \"Delve\"\n\t\tsprite planet/desert\n\t\tdistance 700\n\t\tperiod 200\n" +
            "mission \"Haul\"\n\tname `<origin> Emigration`\n\tdestination \"Delve\"\n" +
            "\tcargo \"Grain\" 20\n\tpassengers 3\n" +
            "\tdescription `Carry <cargo> to <destination> for <payment>.`\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n" +
            "\ton complete\n\t\tpayment 50000\n";

        private static (Mission Mission, PlayerState Player, GameData Data) Setup()
        {
            var data = new GameData();
            data.LoadText(Universe);

            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Hauler");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Rutilicus"]);
            player.Land(data.Planets["New Boston"]);

            return (data.Missions["Haul"], player, data);
        }

        // --- The keys -------------------------------------------------------------

        [Test]
        public void OriginIsWhereThePlayerIsStanding()
        {
            (Mission mission, PlayerState player, GameData data) = Setup();

            Assert.AreEqual("New Boston Emigration",
                TextSubstitution.NameOf(mission, player, data));
        }

        [Test]
        public void DestinationNamesThePlanetAndItsSystem()
        {
            (Mission mission, PlayerState player, GameData data) = Setup();
            var subs = TextSubstitution.For(mission, player, data);

            Assert.AreEqual("Delve", subs["<planet>"]);
            Assert.AreEqual("Alnitak", subs["<system>"]);
            Assert.AreEqual("Delve in the Alnitak system", subs["<destination>"]);
        }

        [Test]
        public void CargoAndPassengersReadAsProse()
        {
            (Mission mission, PlayerState player, GameData data) = Setup();
            var subs = TextSubstitution.For(mission, player, data);

            Assert.AreEqual("Grain", subs["<commodity>"]);
            Assert.AreEqual("20 tons", subs["<tons>"]);
            Assert.AreEqual("20 tons of Grain", subs["<cargo>"]);
            Assert.AreEqual("3", subs["<bunks>"]);
            Assert.AreEqual("passengers", subs["<passengers>"]);
        }

        [Test]
        public void OneOfSomethingIsSingular()
        {
            var data = new GameData();
            data.LoadText(Universe.Replace("cargo \"Grain\" 20", "cargo \"Grain\" 1")
                                  .Replace("passengers 3", "passengers 1"));

            var subs = TextSubstitution.For(data.Missions["Haul"], null, data);

            Assert.AreEqual("1 ton", subs["<tons>"]);
            Assert.AreEqual("passenger", subs["<passengers>"]);
            Assert.AreEqual("a passenger", subs["<fare>"]);
        }

        [Test]
        public void PaymentComesFromTheCompletionAction()
        {
            (Mission mission, PlayerState player, GameData data) = Setup();

            Assert.AreEqual("50,000 credits",
                TextSubstitution.For(mission, player, data)["<payment>"]);
        }

        [Test]
        public void AWholeDescriptionIsFilledIn()
        {
            (Mission mission, PlayerState player, GameData data) = Setup();

            Assert.AreEqual(
                "Carry 20 tons of Grain to Delve in the Alnitak system for 50,000 credits.",
                TextSubstitution.DescriptionOf(mission, player, data));
        }

        // --- Replacement rules ----------------------------------------------------

        [Test]
        public void ReplacementIsSinglePassSoValuesAreNotRescanned()
        {
            // A value containing brackets must not be substituted again, or content
            // could inject a placeholder into its own output.
            var subs = new Dictionary<string, string>
            {
                ["<a>"] = "<b>",
                ["<b>"] = "should not appear",
            };

            Assert.AreEqual("<b>", TextSubstitution.Apply("<a>", subs));
        }

        [Test]
        public void AnUnknownPlaceholderIsLeftAlone()
        {
            // Content uses angle brackets for its own purposes; blanking anything
            // bracketed would silently eat text nobody meant as a placeholder.
            var subs = new Dictionary<string, string> { ["<planet>"] = "Delve" };

            Assert.AreEqual("Go to Delve, <someday>",
                TextSubstitution.Apply("Go to <planet>, <someday>", subs));
        }

        [Test]
        public void AnUnclosedBracketIsNotAnError()
        {
            var subs = new Dictionary<string, string> { ["<planet>"] = "Delve" };

            Assert.AreEqual("Go to <planet, and then",
                TextSubstitution.Apply("Go to <planet, and then", subs));
        }

        [Test]
        public void TextWithNoPlaceholdersIsUnchanged()
        {
            var subs = new Dictionary<string, string> { ["<planet>"] = "Delve" };

            Assert.AreEqual("Nothing to do here",
                TextSubstitution.Apply("Nothing to do here", subs));
            Assert.AreEqual("", TextSubstitution.Apply("", subs));
            Assert.AreEqual("", TextSubstitution.Apply(null, subs));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void RealJobsStopShowingTheirTemplates()
        {
            // The defect this closes: a job board rendering raw names showed
            // "<planet> business convention", which reads as broken content rather
            // than a missing feature.
            GameData data = UpstreamData.Instance;

            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Star Barge");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            Planet start = data.Planets.Values.First(p => p.HasSpaceport && p.IsInhabited);
            player.EnterSystem(data.Systems.Values
                .First(s => s.AllObjects().Any(o => o.PlanetName == start.Name)));
            player.Land(start);

            var log = new MissionLog(player);
            var offers = log.Available(data).ToList();
            Assert.IsNotEmpty(offers);

            var templated = offers
                .Select(m => TextSubstitution.NameOf(m, player, data))
                .Where(name => name.Contains("<planet>") || name.Contains("<origin>") ||
                               name.Contains("<system>") || name.Contains("<destination>"))
                .ToList();

            TestContext.WriteLine($"{offers.Count} jobs offered; " +
                                  $"{templated.Count} still show a standard placeholder");

            Assert.IsEmpty(templated,
                "still templated: " + string.Join(", ", templated.Take(4)));
        }
    }
}
