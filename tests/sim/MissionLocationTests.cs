using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Where a mission may be offered and where it sends the player, when content
    /// describes those places rather than naming them. Port checks against upstream
    /// <c>LocationFilter</c> as used by <c>Mission::Instantiate</c>.
    /// </summary>
    [TestFixture]
    public class MissionLocationTests
    {
        private const string Universe =
            "ship \"Hauler\"\n\tattributes\n\t\t\"mass\" 200\n\t\t\"hull\" 900\n" +
            "\t\t\"cargo space\" 50\n\t\t\"energy capacity\" 100\n" +
            "planet \"Farmworld\"\n\tgovernment \"Republic\"\n\tattributes farming\n" +
            "\tspaceport `Quiet.`\n" +
            "planet \"Foundry\"\n\tgovernment \"Republic\"\n\tattributes industrial\n" +
            "\tspaceport `Loud.`\n" +
            "planet \"Freeport\"\n\tgovernment \"Independent\"\n\tattributes farming\n" +
            "\tspaceport `Open.`\n" +
            "system \"Home\"\n\tpos 0 0\n" +
            "\tobject \"Farmworld\"\n\t\tsprite planet/earth\n\t\tdistance 400\n\t\tperiod 100\n" +
            "\tobject \"Foundry\"\n\t\tsprite planet/rock\n\t\tdistance 700\n\t\tperiod 200\n" +
            "\tobject \"Freeport\"\n\t\tsprite planet/desert\n\t\tdistance 900\n\t\tperiod 300\n" +
            // Offered only on Republic worlds; sends you to a farming world.
            "mission \"Republic Job\"\n\tname `Republic work`\n" +
            "\tsource\n\t\tgovernment \"Republic\"\n" +
            "\tdestination\n\t\tattributes farming\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n" +
            // Offered anywhere.
            "mission \"Open Job\"\n\tname `Anyone's work`\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n" +
            // Offered on exactly one named world.
            "mission \"Foundry Job\"\n\tname `Foundry work`\n\tsource \"Foundry\"\n" +
            "\tto offer\n\t\thas \"flagship landed\"\n";

        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(Universe);
            return data;
        }

        private static PlayerState LandedOn(GameData data, string planet)
        {
            var player = new PlayerState(data);
            Ship ship = data.BuildShip("Hauler");
            ship.BuildMounts();
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Home"]);
            player.Land(data.Planets[planet]);
            return player;
        }

        // --- The two shapes of "not" ----------------------------------------------

        [Test]
        public void AnInlineNotExcludesOnlyWhatItNames()
        {
            // Upstream reads `not` two ways (LocationFilter.cpp:184-190): alone on a
            // line it opens a nested block, but with tokens after it the rest of that
            // same line IS the negated term. Taking the block path for both built an
            // exclusion with no terms, and an empty filter matches everything -- so a
            // filter carrying an inline `not` rejected every place in the galaxy. The
            // dataset uses the inline form 676 times against 31 blocks.
            var filter = LocationFilter.Load(new EndlessSky.Data.DataFile(
                string.Join("\n", "destination", "\tnot attributes industrial") + "\n",
                "test.txt").Nodes[0]);

            GameData data = Load();

            Assert.IsTrue(filter.Matches(data.Planets["Farmworld"], "Home", data, "Home"),
                "a farming world is not industrial, so it passes");
            Assert.IsFalse(filter.Matches(data.Planets["Foundry"], "Home", data, "Home"),
                "the industrial world is the one thing excluded");
        }

        [Test]
        public void ANotBlockStillExcludesEverythingInsideIt()
        {
            // The block form has to keep working: `not` alone, terms indented under it.
            var filter = LocationFilter.Load(new EndlessSky.Data.DataFile(
                string.Join("\n", "destination", "\tnot", "\t\tattributes industrial") + "\n",
                "test.txt").Nodes[0]);

            GameData data = Load();

            Assert.IsTrue(filter.Matches(data.Planets["Farmworld"], "Home", data, "Home"));
            Assert.IsFalse(filter.Matches(data.Planets["Foundry"], "Home", data, "Home"));
        }

        // --- A described destination has to decide behaviour, not just text -------

        [Test]
        public void HandingInRequiresBeingAtADescribedDestinationToo()
        {
            // The "be at the destination" gate only looked at Mission.Destination, the
            // literal planet name. Every generated job describes its destination with a
            // filter instead, so Destination is null for all of them and the gate
            // vanished: any job could be handed in on any world, including the one it
            // was taken on, still carrying the cargo it was meant to deliver.
            GameData data = Load();
            Mission job = data.Missions["Republic Job"];
            string destination = job.ResolveDestination(data, "Home")!;
            string elsewhere = destination == "Farmworld" ? "Foundry" : "Farmworld";

            PlayerState player = LandedOn(data, "Foundry");
            var log = new MissionLog(player);
            ActiveMission taken = log.Accept(job)!;

            player.Depart();
            player.Land(data.Planets[elsewhere]);
            Assert.IsFalse(log.CanComplete(taken),
                "the wrong world does not pay out a described destination");

            player.Depart();
            player.Land(data.Planets[destination]);
            Assert.IsTrue(log.CanComplete(taken), "the world the job describes does");
        }

        // --- Where a mission is offered -------------------------------------------

        [Test]
        public void ASourceFilterRestrictsWhichWorldsOfferAMission()
        {
            // Content targets the galaxy by DESCRIPTION far more often than by name.
            GameData data = Load();
            Mission job = data.Missions["Republic Job"];

            Assert.IsTrue(job.IsOfferedAt(data.Planets["Farmworld"]), "a Republic world");
            Assert.IsTrue(job.IsOfferedAt(data.Planets["Foundry"]), "also Republic");
            Assert.IsFalse(job.IsOfferedAt(data.Planets["Freeport"]), "Independent, so not here");
        }

        [Test]
        public void ANamedSourceIsExact()
        {
            GameData data = Load();
            Mission job = data.Missions["Foundry Job"];

            Assert.IsTrue(job.IsOfferedAt(data.Planets["Foundry"]));
            Assert.IsFalse(job.IsOfferedAt(data.Planets["Farmworld"]));
        }

        [Test]
        public void AMissionWithNoSourceIsOfferedAnywhere()
        {
            GameData data = Load();
            Mission job = data.Missions["Open Job"];

            Assert.IsTrue(job.IsOfferedAt(data.Planets["Farmworld"]));
            Assert.IsTrue(job.IsOfferedAt(data.Planets["Freeport"]));
            Assert.IsFalse(job.IsOfferedAt(null), "but not nowhere");
        }

        [Test]
        public void TheJobBoardOnlyShowsWhatThisWorldOffers()
        {
            GameData data = Load();

            var atFreeport = new MissionLog(LandedOn(data, "Freeport"))
                .Available(data).Select(m => m.Name).ToList();
            var atFoundry = new MissionLog(LandedOn(data, "Foundry"))
                .Available(data).Select(m => m.Name).ToList();

            CollectionAssert.DoesNotContain(atFreeport, "Republic Job");
            CollectionAssert.DoesNotContain(atFreeport, "Foundry Job");
            CollectionAssert.Contains(atFreeport, "Open Job");

            CollectionAssert.Contains(atFoundry, "Republic Job");
            CollectionAssert.Contains(atFoundry, "Foundry Job");
        }

        // --- Where a mission sends you --------------------------------------------

        [Test]
        public void ADestinationFilterResolvesToARealPlanet()
        {
            // Without this the mission has no destination at all and its text still
            // reads "Researchers to <planet>".
            GameData data = Load();
            string? destination = data.Missions["Republic Job"].ResolveDestination(data);

            Assert.IsNotNull(destination);
            CollectionAssert.Contains(new[] { "Farmworld", "Freeport" }, destination,
                "both are farming worlds");
        }

        [Test]
        public void ResolutionIsStableAcrossCalls()
        {
            // A job that changed where it was going between two glances at the board
            // would be unplayable.
            GameData data = Load();
            Mission job = data.Missions["Republic Job"];

            string? first = job.ResolveDestination(data);
            Assert.AreEqual(first, job.ResolveDestination(data));
            Assert.AreEqual(first, job.ResolveDestination(data));
        }

        [Test]
        public void ANamedDestinationWins()
        {
            var data = new GameData();
            data.LoadText(Universe +
                "mission \"Pinned\"\n\tdestination \"Foundry\"\n" +
                "\tto offer\n\t\thas \"flagship landed\"\n");

            Assert.AreEqual("Foundry", data.Missions["Pinned"].ResolveDestination(data));
        }

        [Test]
        public void AMissionWithNoDestinationResolvesToNothing()
        {
            GameData data = Load();

            Assert.IsNull(data.Missions["Open Job"].ResolveDestination(data));
        }

        [Test]
        public void TheBoardRendersAResolvedDestinationRatherThanAPlaceholder()
        {
            GameData data = Load();
            PlayerState player = LandedOn(data, "Foundry");

            var subs = TextSubstitution.For(data.Missions["Republic Job"], player, data);

            Assert.IsNotEmpty(subs["<planet>"]);
            StringAssert.Contains("Home", subs["<destination>"], "and names its system");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void RealWorldsOfferAPlausibleNumberOfJobs()
        {
            // The measurement that made this gap obvious: with every mission treated as
            // offerable anywhere, one planet's board carried 424 jobs — most of them
            // belonging to other governments, species and regions entirely.
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

            var offered = new MissionLog(player).Available(data).ToList();
            int gated = data.Missions.Values.Count(m => m.CanOffer(player.Conditions));

            TestContext.WriteLine(
                $"{start.Name}: {gated} missions pass their condition gate, " +
                $"{offered.Count} are actually offered here");

            Assert.Less(offered.Count, gated,
                "a location filter should turn some of them away");
            Assert.Greater(offered.Count, 0, "but not all of them");
        }

        [Test]
        public void RealMissionsResolveADestinationFromAWorldThatOffersThem()
        {
            // Distance terms are measured FROM the world offering the mission, and 520
            // of the 589 filtered destinations carry one. Resolving them all from a
            // single arbitrary system answers the wrong question: an Avgi job correctly
            // finds nothing within three jumps of human space, because there is no link
            // path between them at all.
            GameData data = UpstreamData.Instance;

            var filtered = data.Missions.Values
                .Where(m => m.Destination is null && m.DestinationFilter != null &&
                            !m.DestinationFilter.IsEmpty &&
                            !m.DestinationFilter.HasUnmodelledTerms)
                .ToList();

            // Where each planet sits, so a mission can be resolved from its own source.
            var systemOf = new Dictionary<string, string>();
            foreach (StarSystem system in data.Systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                    if (obj.PlanetName != null)
                        systemOf.TryAdd(obj.PlanetName, system.Name);

            var landable = data.Planets.Values
                .Where(p => p.IsInhabited && systemOf.ContainsKey(p.Name))
                .ToList();

            // Bounded so the whole-dataset sweep stays quick; the count is logged
            // rather than silently truncated.
            const int Sample = 120;
            var sample = filtered.Take(Sample).ToList();

            int resolved = 0, offeredNowhere = 0;
            foreach (Mission mission in sample)
            {
                Planet? source = landable.FirstOrDefault(
                    p => mission.IsOfferedAt(p, systemOf[p.Name]));

                if (source is null)
                {
                    offeredNowhere++;
                    continue;
                }

                if (mission.ResolveDestination(data, systemOf[source.Name]) != null)
                    resolved++;
            }

            int offeredSomewhere = sample.Count - offeredNowhere;

            TestContext.WriteLine(
                $"{filtered.Count} missions describe a destination in terms this port models; " +
                $"of {sample.Count} sampled, {offeredSomewhere} are offered somewhere and " +
                $"{resolved} of those resolve a destination ({offeredNowhere} offered nowhere)");

            Assert.Greater(filtered.Count, 400, "most filters are now understood");
            Assert.Greater(offeredSomewhere, 0);
            Assert.Greater(resolved, offeredSomewhere / 2,
                "a mission asked from somewhere it is actually offered should usually find a destination");

            // The remainder is a real gap, recorded rather than tuned away: a source
            // filter that matches no inhabited world in this model leaves its mission
            // unreachable. Some of that is content narrowly scoped to places the port
            // does not yet describe; some is filter terms still unmodelled.
            Assert.Less(offeredNowhere, sample.Count,
                "not every mission should be stranded");
        }
    }
}
