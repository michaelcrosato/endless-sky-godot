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
            shuttle.CurrentSystem = player.CurrentSystem;
            freighter.CurrentSystem = player.CurrentSystem;
            player.MarkVisited(data.Systems["Vega"]);
            player.Land(data.Planets["Home"]);
            player.Fleet.LoadCargo("Grain", 15);

            player.Conditions.Set("chapter", 3);
            player.Conditions.Set("met the captain", 1);
            return player;
        }

        [Test]
        public void PurchaseAgesSurviveASave()
        {
            // Without this, loading a game re-values everything the player owns at the
            // no-record default -- the 0.25 floor -- so saving and loading silently
            // wiped three quarters off the resale value of their whole fleet.
            GameData data = Load();
            PlayerState original = Populated(data);
            original.Purchases.Record(PurchaseLog.OutfitKey("Blaster"), original.Date);

            PlayerState restored = SaveGame.Read(SaveGame.Write(original), data);

            Assert.AreEqual(0, restored.Purchases.TakeAge(PurchaseLog.OutfitKey("Blaster"),
                                                          restored.Date),
                "bought on the day the save was taken, so still brand new");
        }

        [Test]
        public void ScheduledEventsSurviveASave()
        {
            // A load that forgets the queue loses every consequence the player has set
            // in motion but not yet seen.
            GameData data = Load();
            PlayerState original = Populated(data);
            original.ScheduleEvent("something later", original.Date.AddDays(12));

            PlayerState restored = SaveGame.Read(SaveGame.Write(original), data);

            Assert.AreEqual(1, restored.ScheduledEvents.Count);
            Assert.AreEqual("something later", restored.ScheduledEvents[0].Name);
            Assert.AreEqual(original.Date.AddDays(12), restored.ScheduledEvents[0].When);
        }

        // --- Restoring the mission log --------------------------------------------

        [Test]
        public void ActiveMissionsComeBackAttachedToTheRestoredPlayer()
        {
            // The log needs a player and the player is what Read produces, so a caller
            // could not hand in a log without building it against somebody else. The
            // factory overload closes that circle: Read builds the player, then asks
            // for the log that belongs to it. Without this the game had no way to load
            // a save with a mission in progress at all.
            GameData data = Load();
            PlayerState original = Populated(data);
            var log = new MissionLog(original);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"]);
            Assert.IsNotNull(taken);

            string text = SaveGame.Write(original, log);

            MissionLog? restoredLog = null;
            PlayerState restored = SaveGame.Read(text, data, p => restoredLog = new MissionLog(p));

            Assert.IsNotNull(restoredLog);
            Assert.AreEqual(1, restoredLog!.Active.Count, "the mission in progress came back");
            Assert.AreEqual("Deliver Grain", restoredLog.Active[0].Mission.Name);
        }

        [Test]
        public void AMissionKeepsTheDestinationItWasGivenWhenItWasAccepted()
        {
            // A described destination is chosen once, when the job is taken. Losing it
            // over a save would send the player somewhere else on reload -- or, for a
            // job whose filter matches several worlds, somewhere they were never told
            // about.
            GameData data = Load();
            PlayerState original = Populated(data);
            var log = new MissionLog(original);
            ActiveMission taken = log.Accept(data.Missions["Deliver Grain"])!;
            Assert.IsNotNull(taken.Destination, "the job resolved a destination when taken");

            MissionLog? restoredLog = null;
            SaveGame.Read(SaveGame.Write(original, log), data, p => restoredLog = new MissionLog(p));

            Assert.AreEqual(taken.Destination, restoredLog!.Active[0].Destination);
        }

        // --- Round trip -----------------------------------------------------------

        [TestCase(9_007_199_254_740_993L)]
        [TestCase(-9_007_199_254_740_993L)]
        [TestCase(long.MaxValue)]
        [TestCase(long.MinValue)]
        public void IntegerBalancesAndConditionsKeepAllSixtyFourBits(long value)
        {
            GameData data = Load();
            PlayerState original = Populated(data);
            original.SetCredits(value);
            original.Conditions.Set("large counter", value);

            PlayerState restored = SaveGame.Read(SaveGame.Write(original), data);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(value, restored.Credits);
                Assert.AreEqual(value, restored.Conditions.Get("large counter"));
            });
        }

        [Test]
        public void LegacyExponentNotationStillLoadsForIntegerFields()
        {
            PlayerState restored = SaveGame.Read(
                "account\n\tcredits 1.25e6\nconditions\n\tcounter -2.5e2\n", Load());
            Assert.AreEqual(1_250_000, restored.Credits);
            Assert.AreEqual(-250, restored.Conditions.Get("counter"));
        }

        [Test]
        public void ShipsKeepTheirOwnConditionIdentityAndCargo()
        {
            GameData data = Load();
            data.Ships["Shuttle"].Attributes.Set("shields", 300);
            data.Ships["Shuttle"].Attributes.Set("fuel capacity", 500);
            data.Ships["Shuttle"].Attributes.Set("bunks", 10);
            PlayerState original = Populated(data);
            Ship flagship = original.Fleet.Flagship!;
            Ship parked = original.Fleet.Ships[1];
            flagship.GivenName = "Homeward Bound";
            flagship.Crew = 7;
            flagship.CurrentSystem = data.Systems["Sol"];
            flagship.Position = new Point(320.5, -185.25);
            flagship.Velocity = new Point(1.25, -0.5);
            flagship.Facing = new Angle(93.0);
            flagship.SetLevels(shields: 21.5, hull: 300, energy: 9.5, heat: 700, fuel: 45.25);

            parked.GivenName = "Waiting Here";
            parked.CurrentSystem = data.Systems["Vega"];
            parked.LoadCargo("Metal", 19);
            parked.SetLevels(hull: 12, energy: 0, heat: 300);

            PlayerState restored = SaveGame.Read(SaveGame.Write(original), data);
            Ship back = restored.Fleet.Flagship!;
            Ship parkedBack = restored.Fleet.Ships[1];
            Assert.Multiple(() =>
            {
                Assert.AreEqual("Homeward Bound", back.GivenName);
                Assert.AreEqual(7, back.Crew);
                Assert.AreEqual(21.5, back.Shields);
                Assert.AreEqual(300, back.Hull);
                Assert.AreEqual(9.5, back.Energy);
                Assert.AreEqual(700, back.Heat);
                Assert.AreEqual(45.25, back.Fuel);
                Assert.AreEqual(flagship.Position, back.Position);
                Assert.AreEqual(flagship.Velocity, back.Velocity);
                Assert.AreEqual(flagship.Facing, back.Facing);
                Assert.AreSame(data.Systems["Sol"], back.CurrentSystem);
                Assert.AreEqual(15, back.Cargo.Count("Grain"));
                Assert.AreEqual("Waiting Here", parkedBack.GivenName);
                Assert.IsTrue(parkedBack.IsParked);
                Assert.IsTrue(parkedBack.IsDisabled);
                Assert.AreEqual(12, parkedBack.Hull);
                Assert.AreEqual(0, parkedBack.Energy);
                Assert.AreEqual(19, parkedBack.Cargo.Count("Metal"));
                Assert.AreEqual(19, parkedBack.CargoMass);
                Assert.AreSame(data.Systems["Vega"], parkedBack.CurrentSystem);
            });
        }

        [Test]
        public void OverheatingHysteresisSurvivesAReload()
        {
            GameData data = Load();
            PlayerState original = Populated(data);
            Ship ship = original.Fleet.Flagship!;
            ship.SetLevels(heat: ship.MaxHeat * 1.1);
            ship.StepResources();
            ship.SetLevels(heat: ship.MaxHeat * 0.95);
            Assert.IsTrue(ship.IsOverheated);

            Ship restored = SaveGame.Read(SaveGame.Write(original), data).Fleet.Flagship!;
            Assert.IsTrue(restored.IsOverheated);
            Assert.IsTrue(restored.IsDisabled);
            restored.StepResources();
            Assert.IsTrue(restored.IsDisabled, "loading must not bypass the 90% cooling threshold");
        }

        [Test]
        public void LegacySavesStillLoadFleetCargoAndFullDefaultLevels()
        {
            PlayerState restored = SaveGame.Read(
                "ship Shuttle\n\tflagship\nship Freighter\n\tparked\n" +
                "cargo\n\tcommodity Grain 12\nsystem Sol\n", Load());
            Assert.AreEqual(12, restored.Fleet.CargoCount("Grain"));
            Assert.AreEqual(600, restored.Fleet.Flagship!.Hull);
            Assert.AreEqual(100, restored.Fleet.Flagship.Energy);
            Assert.AreSame(restored.CurrentSystem, restored.Fleet.Flagship.CurrentSystem);
        }

        [Test]
        public void SavedLevelsUseInstalledCapacityRegardlessOfFieldOrder()
        {
            GameData data = Load();
            data.LoadText("outfit Tank\n\t\"fuel capacity\" 500\n");
            PlayerState restored = SaveGame.Read(
                "ship Shuttle\n\tfuel 350\n\toutfit Tank\n\thull -1\n", data);
            Assert.AreEqual(350, restored.Fleet.Flagship!.Fuel);
            Assert.IsTrue(restored.Fleet.Flagship.IsDestroyed,
                "loading must not resurrect a destroyed escort");
        }

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

            MissionLog? restoredLog = null;
            SaveGame.Read(save, data, p => restoredLog = new MissionLog(p));

            Assert.AreEqual(1, restoredLog!.Active.Count);
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

            PlayerState restored = SaveGame.Read(save, data, p => new MissionLog(p));

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
            // Loading restores shared market state, so this test owns its universe.
            var data = new GameData();
            data.LoadDirectory(UpstreamData.RequiredPath);

            var player = new PlayerState(data);
            player.SetCredits(500_000);
            Ship ship = data.BuildShip("Star Barge");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            Planet start = data.Planets.Values.First(p => p.HasSpaceport && p.IsInhabited);
            player.EnterSystem(data.Systems.Values
                .First(s => s.AllObjects().Any(o => o.PlanetName == start.Name)));
            ship.CurrentSystem = player.CurrentSystem;
            player.Land(start);
            data.StepEconomy(new Random(7));

            string save = SaveGame.Write(player);
            var fresh = new GameData();
            fresh.LoadDirectory(UpstreamData.RequiredPath);
            PlayerState restored = SaveGame.Read(save, fresh);

            TestContext.WriteLine($"save is {save.Length} bytes for a one-ship game");

            Assert.AreEqual(500_000, restored.Credits);
            Assert.AreEqual(start.Name, restored.CurrentPlanet!.Name);
            Assert.AreEqual("Star Barge", restored.Fleet.Flagship!.Definition.DisplayName);
            Assert.IsNotNull(restored.Fleet.Flagship.Government, "and it still flies a flag");
            Assert.AreEqual(save, SaveGame.Write(restored), "a fresh universe retains the saved pilot and markets");
        }
    }
}
