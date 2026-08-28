using System;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Saving and restoring a game. Partial port of upstream <c>PlayerInfo::Save</c>
    /// and <c>PlayerInfo::Load</c>.
    /// </summary>
    [TestFixture]
    public class SaveGameTests
    {
        private const string Universe =
            "ship \"Shuttle\"\n\tattributes\n\t\t\"mass\" 80\n\t\t\"hull\" 600\n" +
            "\t\t\"cargo space\" 40\n\t\t\"outfit space\" 120\n\t\t\"gun ports\" 0\n" +
            "\t\t\"energy capacity\" 100\n\t\t\"cost\" 180000\n" +
            "ship \"Freighter\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 900\n" +
            "\t\t\"cargo space\" 70\n\t\t\"outfit space\" 200\n\t\t\"energy capacity\" 100\n" +
            "outfit \"Scanner\"\n\tcost 3000\n\t\"mass\" 2\n\t\"outfit space\" -2\n" +
            "government \"Republic\"\n" +
            "planet \"Home\"\n\tgovernment \"Republic\"\n\tspaceport `Busy.`\n" +
            "planet \"Away\"\n\tgovernment \"Republic\"\n\tspaceport `Quiet.`\n" +
            "system \"Sol\"\n\tpos 0 0\n\tlink \"Vega\"\n" +
            "\tobject \"Home\"\n\t\tsprite planet/earth\n\t\tdistance 500\n\t\tperiod 300\n" +
            "system \"Vega\"\n\tpos 100 0\n\tlink \"Sol\"\n" +
            "\tobject \"Away\"\n\t\tsprite planet/desert\n\t\tdistance 700\n\t\tperiod 200\n" +
            "mission \"Deliver Grain\"\n\tdestination \"Away\"\n\tcargo \"Grain\" 20\n" +
            "\tdeadline 30\n\tto offer\n\t\thas \"flagship landed\"\n" +
            "\ton complete\n\t\tpayment 50000\n";

        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(Universe);
            return data;
        }

        private static PlayerState Populated(GameData data)
        {
            var player = new PlayerState(data);
            player.SetDate(new DateTime(3014, 3, 21));
            player.SetCredits(1_234_567);

            Ship shuttle = data.BuildShip("Shuttle");
            shuttle.BuildMounts();
            shuttle.AddOutfit(data.Outfits["Scanner"], 2);
            player.Fleet.Add(shuttle);
            player.Fleet.SetFlagship(shuttle);

            Ship freighter = data.BuildShip("Freighter");
            freighter.BuildMounts();
            freighter.IsParked = true;
            player.Fleet.Add(freighter);

            player.EnterSystem(data.Systems["Sol"]);
            player.MarkVisited(data.Systems["Vega"]);
            player.Land(data.Planets["Home"]);
            player.Fleet.LoadCargo("Grain", 15);

            player.Conditions.Set("chapter", 3);
            player.Conditions.Set("met the captain", 1);
            return player;
        }

        // --- Round trip -----------------------------------------------------------

        [Test]
        public void MoneyAndTheCalendarSurvive()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(SaveGame.Write(Populated(data)), data);

            Assert.AreEqual(1_234_567, restored.Credits);
            Assert.AreEqual(new DateTime(3014, 3, 21), restored.Date);
        }

        [Test]
        public void WhereThePlayerWasStandingSurvives()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(SaveGame.Write(Populated(data)), data);

            Assert.AreEqual("Sol", restored.CurrentSystem!.Name);
            Assert.AreEqual("Home", restored.CurrentPlanet!.Name,
                "the landed planet must survive, not be cleared by entering the system");
            Assert.AreEqual(1, restored.Conditions.Get("flagship landed"));
        }

        [Test]
        public void TheFleetComesBackWithItsOutfitsAndItsFlagship()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(SaveGame.Write(Populated(data)), data);

            Assert.AreEqual(2, restored.Fleet.Ships.Count);
            Assert.AreEqual("Shuttle", restored.Fleet.Flagship!.Definition.DisplayName);
            Assert.AreEqual(2, restored.Fleet.Flagship.Outfits.Count(o => o.Name == "Scanner"));

            Ship freighter = restored.Fleet.Ships.First(s => s.Definition.DisplayName == "Freighter");
            Assert.IsTrue(freighter.IsParked, "a parked ship stays parked");
            Assert.AreEqual(1, restored.Fleet.ActiveShips.Count());
        }

        [Test]
        public void ARestoredShipDoesNotGetItsStockLoadoutOnTop()
        {
            // Rebuilding through BuildShip would install the factory outfits over the
            // ones the save records, quietly handing the player free equipment on every
            // load.
            GameData data = Load();
            var player = new PlayerState(data);

            Ship stripped = new Ship(data.Ships["Shuttle"]);
            stripped.BuildMounts();
            player.Fleet.Add(stripped);
            player.Fleet.SetFlagship(stripped);

            PlayerState restored = SaveGame.Read(SaveGame.Write(player), data);

            Assert.IsEmpty(restored.Fleet.Flagship!.Outfits.ToList(),
                "a stripped hull must come back stripped");
        }

        [Test]
        public void CargoInTheHoldSurvives()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(SaveGame.Write(Populated(data)), data);

            Assert.AreEqual(15, restored.Fleet.CargoCount("Grain"));
        }

        [Test]
        public void StoredConditionsSurviveAndDerivedOnesAreRecomputed()
        {
            GameData data = Load();
            string save = SaveGame.Write(Populated(data));

            Assert.IsTrue(save.Contains("chapter 3"), "a story flag is written out");
            Assert.IsFalse(save.Contains("\ncredits 1234567"),
                "an autocondition must not be written as a stored value");

            PlayerState restored = SaveGame.Read(save, data);

            Assert.AreEqual(3, restored.Conditions.Get("chapter"));
            Assert.AreEqual(1, restored.Conditions.Get("met the captain"));
            Assert.AreEqual(1_234_567, restored.Conditions.Get("credits"),
                "credits comes back from the account, not from a stale copy");
        }

        [Test]
        public void TravelHistorySurvives()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(SaveGame.Write(Populated(data)), data);

            Assert.AreEqual(1, restored.Conditions.Get("visited system: Sol"));
            Assert.AreEqual(1, restored.Conditions.Get("visited system: Vega"));
            Assert.AreEqual(1, restored.Conditions.Get("visited planet: Home"));
        }

        [Test]
        public void MultiWordKeysRoundTripAsSingleTokens()
        {
            // "visited planet" and "start date" have to be quoted on the way out, or
            // they come back as two tokens and silently fail to match on the way in.
            GameData data = Load();
            string save = SaveGame.Write(Populated(data));

            StringAssert.Contains("\"visited planet\"", save);
            StringAssert.Contains("\"start date\"", save);
        }

        // --- Missions -------------------------------------------------------------

        [Test]
        public void AnActiveMissionSurvivesWithItsDeadline()
        {
            GameData data = Load();
            PlayerState player = Populated(data);
            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;

            string save = SaveGame.Write(player, log);

            var restoredPlayer = new PlayerState(data);
            var restoredLog = new MissionLog(restoredPlayer);
            restoredPlayer = SaveGame.Read(save, data, restoredLog);

            Assert.AreEqual(1, restoredLog.Active.Count);
            ActiveMission back = restoredLog.Active[0];

            Assert.AreEqual("Deliver Grain", back.Mission.Name);
            Assert.AreEqual(taken.Deadline, back.Deadline);
            Assert.AreEqual(taken.CargoLoaded, back.CargoLoaded);
        }

        [Test]
        public void RestoringAMissionDoesNotPayItsAdvanceTwice()
        {
            GameData data = Load();
            PlayerState player = Populated(data);
            var log = new MissionLog(player);
            log.Accept(data.Missions["Deliver Grain"]);

            long credits = player.Credits;
            string save = SaveGame.Write(player, log);

            var restoredLog = new MissionLog(new PlayerState(data));
            PlayerState restored = SaveGame.Read(save, data, restoredLog);

            Assert.AreEqual(credits, restored.Credits,
                "reloading is not a way to farm mission advances");
        }

        // --- Robustness -----------------------------------------------------------

        [Test]
        public void AnEmptySaveLoadsIntoAnEmptyGame()
        {
            GameData data = Load();
            PlayerState restored = SaveGame.Read(string.Empty, data);

            Assert.AreEqual(0, restored.Credits);
            Assert.IsEmpty(restored.Fleet.Ships);
        }

        [Test]
        public void ASaveNamingThingsTheUniverseDoesNotHaveIsSkipped()
        {
            // A save made with a plugin that is no longer installed must not throw.
            GameData data = Load();

            Assert.DoesNotThrow(() => SaveGame.Read(
                "date 1 1 3014\nsystem \"Nowhere\"\nplanet \"Nothing\"\n" +
                "ship \"Imaginary\"\nvisited \"Nowhere\"\n", data));
        }

        [Test]
        public void SavingTwiceProducesTheSameFile()
        {
            // Ordering has to be stable or every save looks like a change.
            GameData data = Load();
            PlayerState player = Populated(data);

            Assert.AreEqual(SaveGame.Write(player), SaveGame.Write(player));
        }

        [Test]
        public void ASaveSurvivesRepeatedRoundTrips()
        {
            GameData data = Load();
            string first = SaveGame.Write(Populated(data));
            string second = SaveGame.Write(SaveGame.Read(first, data));
            string third = SaveGame.Write(SaveGame.Read(second, data));

            Assert.AreEqual(second, third,
                "a save that drifts on every reload corrupts a campaign slowly");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void ARealGameSavesAndReloads()
        {
            GameData data = UpstreamData.Instance;

            var player = new PlayerState(data);
            player.SetCredits(500_000);
            Ship ship = data.BuildShip("Star Barge");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            Planet start = data.Planets.Values.First(p => p.HasSpaceport && p.IsInhabited);
            player.EnterSystem(data.Systems.Values
                .First(s => s.AllObjects().Any(o => o.PlanetName == start.Name)));
            player.Land(start);

            string save = SaveGame.Write(player);
            PlayerState restored = SaveGame.Read(save, data);

            TestContext.WriteLine($"save is {save.Length} bytes for a one-ship game");

            Assert.AreEqual(500_000, restored.Credits);
            Assert.AreEqual(start.Name, restored.CurrentPlanet!.Name);
            Assert.AreEqual("Star Barge", restored.Fleet.Flagship!.Definition.DisplayName);
            Assert.IsNotNull(restored.Fleet.Flagship.Government, "and it still flies a flag");
        }
    }
}
