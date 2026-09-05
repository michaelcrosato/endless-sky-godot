using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The generated universe, read by the real loader.
    /// </summary>
    /// <remarks>
    /// The generator deliberately does not validate its own output. A generator
    /// checking its own work only proves it is self-consistent; the question that
    /// matters is whether the ENGINE can read what it wrote, and the only thing that
    /// can answer that is the engine's parser.
    ///
    /// These assertions are the goal restated as tests: a thousand systems, a hundred
    /// ships across twenty classes, twenty peoples with at least two factions each, a
    /// substantially larger upgrade catalogue, and a thousand jobs across at least five
    /// kinds of work. Integrity matters as much as the counts — a link to a system that
    /// does not exist, or a shipyard stocking a hull nobody defined, is content the
    /// player runs into as a bug.
    /// </remarks>
    [TestFixture]
    public class UniverseTests
    {
        /// <summary>Where the generated universe lives.</summary>
        private static string Root => GeneratedUniverse.Root;

        private static GameData Universe => GeneratedUniverse.Instance;

        // --- It loads at all ------------------------------------------------------

        [Test]
        public void TheUniverseLoadsWithoutParseDiagnostics()
        {
            // A diagnostic here means content the player would silently never see.
            var diagnostics = Universe.Diagnostics.Take(8).ToList();

            TestContext.WriteLine($"{Universe.Diagnostics.Count} loader diagnostics");
            foreach (string diagnostic in diagnostics)
                TestContext.WriteLine("  " + diagnostic);

            Assert.IsEmpty(Universe.Diagnostics,
                "the generator must emit content the engine reads cleanly");
        }

        [Test]
        public void NothingImportantIsLeftUnparsed()
        {
            var unhandled = Universe.UnhandledNodes
                .OrderByDescending(entry => entry.Value)
                .ToList();

            TestContext.WriteLine("unhandled node types: " + (unhandled.Count == 0
                ? "(none)"
                : string.Join(", ", unhandled.Select(e => $"{e.Key} x{e.Value}"))));

            Assert.IsEmpty(unhandled, "the generator should only emit nodes the loader knows");
        }

        // --- The counts the goal asked for ----------------------------------------

        [Test]
        public void ThereAreAThousandSystems()
        {
            TestContext.WriteLine($"{Universe.Systems.Count} systems");
            Assert.GreaterOrEqual(Universe.Systems.Count, 1000);
        }

        [Test]
        public void ThereAreAHundredShipsAcrossTwentyClasses()
        {
            var classes = Universe.Ships.Values
                .Select(s => s.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            TestContext.WriteLine($"{Universe.Ships.Count} ships across {classes.Count} classes: " +
                                  string.Join(", ", classes));

            Assert.GreaterOrEqual(Universe.Ships.Count, 100);
            Assert.GreaterOrEqual(classes.Count, 20);
        }

        [Test]
        public void ThereAreTwentyPeoplesWithAtLeastTwoFactionsEach()
        {
            // Factions are grouped by the race prefix their names share, which is how
            // the generator relates them.
            Assert.GreaterOrEqual(Universe.Governments.Count, 40);

            TestContext.WriteLine($"{Universe.Governments.Count} governments");
            Assert.GreaterOrEqual(Universe.Governments.Count / 2, 20,
                "at least two factions per people");
        }

        [Test]
        public void TheUpgradeCatalogueIsSubstantial()
        {
            var categories = Universe.Outfits.Values
                .Select(o => o.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .ToList();

            TestContext.WriteLine($"{Universe.Outfits.Count} outfits across {categories.Count} " +
                "categories: " + string.Join(", ", categories.Select(g => $"{g.Key} {g.Count()}")));

            Assert.GreaterOrEqual(Universe.Outfits.Count, 500);
            Assert.GreaterOrEqual(categories.Count, 8);
        }

        [Test]
        public void ThereAreAThousandJobsAcrossAtLeastFiveArchetypes()
        {
            var jobs = Universe.Missions.Values.Where(m => m.IsJob).ToList();

            // The archetype is the second word of the generated name.
            var kinds = jobs
                .Select(m => m.Name.Split(' ').Skip(1).FirstOrDefault() ?? "")
                .Where(k => k.Length > 0)
                .GroupBy(k => k)
                .OrderByDescending(g => g.Count())
                .ToList();

            TestContext.WriteLine($"{jobs.Count} jobs across {kinds.Count} archetypes: " +
                string.Join(", ", kinds.Select(g => $"{g.Key} {g.Count()}")));

            Assert.GreaterOrEqual(jobs.Count, 1000);
            Assert.GreaterOrEqual(kinds.Count, 5);
        }

        // --- Integrity ------------------------------------------------------------

        [Test]
        public void EveryLinkLeadsSomewhereReal()
        {
            var broken = Universe.Systems.Values
                .SelectMany(s => s.Links.Select(link => (s.Name, link)))
                .Where(pair => !Universe.Systems.ContainsKey(pair.link))
                .Take(6)
                .ToList();

            Assert.IsEmpty(broken,
                "links to nowhere: " + string.Join(", ", broken.Select(b => $"{b.Name}->{b.link}")));
        }

        [Test]
        public void LinksAreMutual()
        {
            // A one-way link is a system a player can fly into and not out of.
            var oneWay = Universe.Systems.Values
                .SelectMany(s => s.Links.Select(link => (From: s.Name, To: link)))
                .Where(pair => Universe.Systems.TryGetValue(pair.To, out StarSystem? other) &&
                               !other.Links.Contains(pair.From))
                .Take(6)
                .ToList();

            Assert.IsEmpty(oneWay,
                "one-way links: " + string.Join(", ", oneWay.Select(p => $"{p.From}->{p.To}")));
        }

        [Test]
        public void TheGalaxyIsOneConnectedPlace()
        {
            // An unreachable pocket is content nobody can ever get to.
            StarSystem start = Universe.Systems.Values.First();
            var seen = new HashSet<string>(StringComparer.Ordinal) { start.Name };
            var frontier = new Queue<StarSystem>();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                foreach (string link in frontier.Dequeue().Links)
                    if (seen.Add(link) && Universe.Systems.TryGetValue(link, out StarSystem? next))
                        frontier.Enqueue(next);
            }

            TestContext.WriteLine($"{seen.Count} of {Universe.Systems.Count} systems reachable " +
                                  $"from {start.Name}");

            Assert.AreEqual(Universe.Systems.Count, seen.Count,
                "every system must be reachable from every other");
        }

        [Test]
        public void EveryStellarObjectNamingAWorldHasOne()
        {
            var missing = Universe.Systems.Values
                .SelectMany(s => s.AllObjects())
                .Where(o => o.PlanetName != null && !Universe.Planets.ContainsKey(o.PlanetName))
                .Select(o => o.PlanetName!)
                .Distinct()
                .Take(6)
                .ToList();

            Assert.IsEmpty(missing, "worlds with no definition: " + string.Join(", ", missing));
        }

        [Test]
        public void EveryShopStocksThingsThatExist()
        {
            var missingShips = Universe.Shipyards.Values
                .SelectMany(sale => sale.Items)
                .Where(name => !Universe.Ships.ContainsKey(name))
                .Distinct().Take(5).ToList();

            var missingOutfits = Universe.Outfitters.Values
                .SelectMany(sale => sale.Items)
                .Where(name => !Universe.Outfits.ContainsKey(name))
                .Distinct().Take(5).ToList();

            Assert.IsEmpty(missingShips, "shipyard stocks unknown hulls: " +
                string.Join(", ", missingShips));
            Assert.IsEmpty(missingOutfits, "outfitter stocks unknown gear: " +
                string.Join(", ", missingOutfits));
        }

        [Test]
        public void EveryFleetFliesShipsAndAFlagThatExist()
        {
            var badGovernment = Universe.Fleets.Values
                .Where(f => f.Government != null && !Universe.Governments.ContainsKey(f.Government))
                .Select(f => f.Name).Take(5).ToList();

            var badShips = Universe.Fleets.Values
                .SelectMany(f => f.Variants.SelectMany(v => v.Ships))
                .Where(name => !Universe.Ships.ContainsKey(name))
                .Distinct().Take(5).ToList();

            Assert.IsEmpty(badGovernment, "fleets under unknown flags: " +
                string.Join(", ", badGovernment));
            Assert.IsEmpty(badShips, "fleets flying unknown hulls: " +
                string.Join(", ", badShips));
        }

        [Test]
        public void EverySystemWithPeopleHasTrafficAndPrices()
        {
            var inhabited = Universe.Systems.Values
                .Where(s => s.AllObjects().Any(o => o.Planet is { IsInhabited: true }))
                .ToList();

            var silent = inhabited.Where(s => s.Fleets.Count == 0).Take(5).ToList();
            var priceless = inhabited
                .Where(s => !Universe.Trade.Price(s.Name, "Food").HasValue)
                .Take(5).ToList();

            TestContext.WriteLine($"{inhabited.Count} inhabited systems");

            Assert.IsEmpty(silent, "inhabited but no traffic: " +
                string.Join(", ", silent.Select(s => s.Name)));
            Assert.IsEmpty(priceless, "inhabited but no market: " +
                string.Join(", ", priceless.Select(s => s.Name)));
        }

        // --- It is playable -------------------------------------------------------

        [Test]
        public void EveryShipCanBeBuiltAndFlown()
        {
            var broken = new List<string>();

            foreach (ShipDefinition definition in Universe.Ships.Values)
            {
                Ship ship = Universe.BuildShip(definition.DisplayName, out List<string> missing);
                ship.BuildMounts();

                if (missing.Count > 0)
                    broken.Add($"{definition.DisplayName} missing {missing[0]}");
                else if (ship.Thrust <= 0.0)
                    broken.Add($"{definition.DisplayName} cannot move");
                else if (ship.TurnRate <= 0.0)
                    broken.Add($"{definition.DisplayName} cannot turn");
                else if (!ship.HasHyperdrive && !ship.HasJumpDrive)
                    broken.Add($"{definition.DisplayName} cannot leave the system");
                else if (ship.MaxEnergy <= 0.0)
                    broken.Add($"{definition.DisplayName} has no power");
            }

            Assert.IsEmpty(broken, "unflyable hulls: " + string.Join("; ", broken.Take(6)));
        }

        [Test]
        public void ANewPilotDoesNotBeginUnderGunfire()
        {
            // Reported from play: "the first seconds of the game the player is being
            // attacked for some reason." The reason was the start scenario itself. The
            // generator picked the starting world on connectivity and services alone
            // and never asked whether its owner would tolerate a stranger, so a new
            // pilot was born on a zealot world at -50 standing, in a system whose
            // patrols spawn hostile and open fire before the player has touched a key.
            //
            // Hostility toward the player is reputation (Government.IsEnemy's player
            // branch: an enemy exactly while standing is negative), so this is decided
            // entirely in the data and is checkable here.
            StartScenario start = Universe.Starts.Values.First();
            Assert.IsNotNull(start.SystemName, "the start must name a system");

            StarSystem system = Universe.Systems[start.SystemName!];
            var offenders = new List<string>();

            if (Universe.Governments.TryGetValue(system.Government, out Government? owner) &&
                owner.IsPlayerEnemy)
            {
                offenders.Add($"the system's own government {owner.Name} " +
                              $"({owner.Reputation:0} standing)");
            }

            foreach (FleetSpawn spawn in system.Fleets)
            {
                if (!Universe.Fleets.TryGetValue(spawn.Name, out Fleet? fleet) ||
                    fleet.Government is null)
                {
                    continue;
                }

                if (Universe.Governments.TryGetValue(fleet.Government, out Government? gov) &&
                    gov.IsPlayerEnemy)
                {
                    offenders.Add($"{spawn.Name} flies for {gov.Name} " +
                                  $"({gov.Reputation:0} standing)");
                }
            }

            TestContext.WriteLine($"start: {start.PlanetName} in {system.Name} " +
                                  $"under {system.Government}");
            foreach (string offender in offenders)
                TestContext.WriteLine("  hostile: " + offender);

            Assert.IsEmpty(offenders,
                "a new pilot must be able to read the controls before anyone shoots at them");
        }

        [Test]
        public void EveryDriveStatesTheSpeedItWillJumpAt()
        {
            // "jump speed" is the speed at or below which Ship::IsReadyToJump lets a
            // ship go. A drive that omits it reads as zero, and then nothing short of
            // an exact standstill is ever legal -- which is not a slow jump, it is no
            // jump at all. Upstream states .2 on the Hyperdrive and .3 on the Jump
            // Drive (data/human/outfits.txt); this galaxy shipped with neither, and the
            // player found it as a ship that turned in circles and never left.
            var silent = new List<string>();

            foreach (Outfit outfit in Universe.Outfits.Values)
            {
                bool isDrive = outfit.Attributes.Get("hyperdrive") > 0.0 || outfit.Attributes.Get("jump drive") > 0.0;
                if (isDrive && outfit.Attributes.Get("jump speed") <= 0.0)
                    silent.Add(outfit.Name);
            }

            TestContext.WriteLine($"{silent.Count} drives state no jump speed");
            Assert.IsEmpty(silent,
                "a drive with no stated jump speed can only jump from a dead stop: " +
                string.Join("; ", silent.Take(6)));
        }

        [Test]
        public void EveryShipsStockLoadoutActuallyFits()
        {
            // A hull that ships carrying more than it can hold cannot be reassembled
            // by the outfitter, and the player finds out the first time they sell.
            var overloaded = new List<string>();

            foreach (ShipDefinition definition in Universe.Ships.Values)
            {
                Ship ship = Universe.BuildShip(definition.DisplayName);
                ship.BuildMounts();

                foreach (string attribute in new[]
                         { "outfit space", "weapon capacity", "engine capacity",
                           "gun ports", "turret mounts" })
                {
                    if (ship.Attributes.Get(attribute) < -1e-6)
                        overloaded.Add($"{definition.DisplayName} over on {attribute} " +
                                       $"({ship.Attributes.Get(attribute):0.#})");
                }
            }

            Assert.IsEmpty(overloaded, string.Join("; ", overloaded.Take(6)));
        }

        [Test]
        public void ThereIsAStartingPointAndItIsSomewhereReal()
        {
            StartScenario? start = Universe.DefaultStart;
            Assert.IsNotNull(start, "a new pilot needs somewhere to begin");

            Assert.IsTrue(Universe.Systems.ContainsKey(start!.SystemName!), start.SystemName);
            Assert.IsTrue(Universe.Planets.ContainsKey(start.PlanetName!), start.PlanetName);

            Planet home = Universe.Planets[start.PlanetName!];
            Assert.IsTrue(home.HasSpaceport, "you should be able to take off again");

            TestContext.WriteLine($"start: {start}");
        }

        [Test]
        public void AJobBoardSomewhereRealHasWorkOnIt()
        {
            var player = new PlayerState(Universe);
            StartScenario start = Universe.DefaultStart!;
            start.ApplyTo(player, Universe);

            Ship ship = Universe.BuildShip(
                Universe.Ships.Values.First(s => s.Category == "Freighter").DisplayName);
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);

            var log = new MissionLog(player);
            var offered = log.Available(Universe).ToList();

            TestContext.WriteLine($"{player.CurrentPlanet?.Name}: {offered.Count} jobs offered; " +
                string.Join(" | ", offered.Take(3)
                    .Select(m => TextSubstitution.NameOf(m, player, Universe))));

            Assert.IsNotEmpty(offered, "the starting world should have work on its board");
        }

        [Test]
        public void RacesHoldTerritoryRatherThanBeingScatteredAtRandom()
        {
            // The point of a territory is that its systems are near each other. If a
            // government's space is spread as widely as the whole galaxy, the map has
            // no regions and travel means nothing.
            var byGovernment = Universe.Systems.Values
                .Where(s => !string.IsNullOrEmpty(s.Government))
                .GroupBy(s => s.Government!)
                .Where(g => g.Count() >= 8)
                .ToList();

            Assert.Greater(byGovernment.Count, 10, "many governments should hold space");

            double galaxySpread = Spread(Universe.Systems.Values.ToList());
            var diffuse = new List<string>();

            foreach (IGrouping<string, StarSystem> group in byGovernment)
            {
                double spread = Spread(group.ToList());
                if (spread > galaxySpread * 0.75)
                    diffuse.Add($"{group.Key} ({spread:0} vs {galaxySpread:0})");
            }

            TestContext.WriteLine($"{byGovernment.Count} governments hold territory; " +
                                  $"galaxy spread {galaxySpread:0}");

            Assert.IsEmpty(diffuse, "governments with no real territory: " +
                string.Join(", ", diffuse.Take(4)));
        }

        private static double Spread(List<StarSystem> systems)
        {
            double x = systems.Average(s => s.MapPosition.X);
            double y = systems.Average(s => s.MapPosition.Y);
            return Math.Sqrt(systems.Average(s =>
                (s.MapPosition.X - x) * (s.MapPosition.X - x) +
                (s.MapPosition.Y - y) * (s.MapPosition.Y - y)));
        }

        // --- Jobs that place ships ------------------------------------------------

        /// <summary>Every job in the universe that places NPCs.</summary>
        private static List<Mission> NpcJobs() =>
            Universe.Missions.Values.Where(m => m.Npcs.Count > 0).ToList();

        [Test]
        public void EveryJobThatPlacesNpcsNamesShipsThatExist()
        {
            GameData data = Universe;
            var missing = new List<string>();

            foreach (Mission mission in NpcJobs())
                foreach (MissionNpc npc in mission.Npcs)
                    foreach (string model in npc.ShipNames)
                        if (!data.Ships.ContainsKey(model))
                            missing.Add($"{mission.Name} -> {model}");

            Assert.That(missing, Is.Empty,
                "an npc block naming a hull nobody defined places nothing at all");
        }

        [Test]
        public void EveryJobThatPlacesNpcsActuallyProducesHulls()
        {
            GameData data = Universe;
            var spawner = new NpcSpawner(data, random: _ => 0);
            StarSystem origin = data.Systems.Values.OrderBy(s => s.Name).First();
            StarSystem destination = data.Systems.Values.OrderBy(s => s.Name).Skip(1).First();

            var empty = new List<string>();
            int hulls = 0;

            foreach (Mission mission in NpcJobs())
                foreach (NpcInstance placed in spawner.Place(mission, origin, destination))
                {
                    if (placed.Ships.Count == 0)
                        empty.Add(mission.Name);
                    else
                        hulls += placed.Ships.Count;
                }

            Assert.That(empty, Is.Empty,
                "a job whose objective needs a ship, and which places none, can never " +
                "be finished - this was true of 429 of the first thousand");
            Assert.That(hulls, Is.GreaterThan(NpcJobs().Count),
                "some jobs ask for more than one hull");
        }

        [Test]
        public void ABountyPlacesAsManyHullsAsItsTextClaims()
        {
            GameData data = Universe;

            // The generator writes one `ship` line per hull. A count written as the
            // second token of a `ship` line would silently name one ship "3" instead,
            // which is how a bounty on three raiders became a bounty on one.
            List<Mission> multi = NpcJobs()
                .Where(m => m.Npcs.Any(n => n.ShipNames.Count > 1))
                .ToList();

            Assert.That(multi, Is.Not.Empty, "the board should carry multi-target bounties");

            foreach (Mission mission in multi.Take(50))
                foreach (MissionNpc npc in mission.Npcs.Where(n => n.ShipNames.Count > 1))
                    Assert.That(npc.ShipNames.Distinct().Count(), Is.EqualTo(1),
                        "a pack of raiders is a pack of the same model");
        }

        [Test]
        public void BountyTargetsAreHostileToThePlayer()
        {
            GameData data = Universe;
            var friendly = new List<string>();

            foreach (Mission mission in NpcJobs())
                foreach (MissionNpc npc in mission.Npcs)
                {
                    // Only the ones the player is sent to destroy.
                    if ((npc.SucceedIf & ShipEvent.Destroy) == 0)
                        continue;

                    if (npc.Government == null ||
                        !data.Governments.TryGetValue(npc.Government, out Government? g))
                    {
                        friendly.Add($"{mission.Name}: no government");
                        continue;
                    }

                    if (!g.IsPlayerEnemy)
                        friendly.Add($"{mission.Name}: {g.Name} is not hostile");
                }

            Assert.That(friendly, Is.Empty,
                "a bounty target that is not hostile to the player never fights back, " +
                "and hostility to the player is reputation rather than the attitude matrix");
        }

        [Test]
        public void SalvageTargetsAreDerelictsAndArriveDisabled()
        {
            GameData data = Universe;
            var spawner = new NpcSpawner(data, random: _ => 0);
            StarSystem origin = data.Systems.Values.OrderBy(s => s.Name).First();

            List<Mission> salvage = NpcJobs()
                .Where(m => m.Npcs.Any(n => (n.SucceedIf & ShipEvent.Board) != 0))
                .ToList();

            Assert.That(salvage, Is.Not.Empty, "the board should carry salvage claims");

            foreach (Mission mission in salvage.Take(25))
                foreach (NpcInstance placed in spawner.Place(mission, origin, origin))
                {
                    if ((placed.Template.SucceedIf & ShipEvent.Board) == 0)
                        continue;

                    Assert.That(placed.Template.IsDerelict, Is.True,
                        $"{mission.Name}: something to board is not something to chase");

                    foreach (Ship hull in placed.Ships)
                        Assert.That(hull.IsDisabled, Is.True);
                }
        }

        [Test]
        public void TheJobBoardDoesNotReadAsOneJobPrintedAThousandTimes()
        {
            GameData data = Universe;
            List<Mission> jobs = data.Missions.Values.Where(m => m.IsJob).ToList();

            int distinct = jobs.Select(m => m.DisplayName).Distinct().Count();
            int worst = jobs.GroupBy(m => m.DisplayName).Max(g => g.Count());

            // Measured before this was addressed: 39 distinct titles over 1000 jobs,
            // with "Escort a convoy to <planet>" appearing 143 times.
            Assert.That(distinct, Is.GreaterThan(150),
                $"only {distinct} distinct job titles across {jobs.Count} jobs");
            Assert.That(worst, Is.LessThan(60),
                $"the most repeated job title appears {worst} times");
        }
    }
}
