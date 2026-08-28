using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// The two ways to travel that are not a hyperdrive following a link: jump drives,
    /// which ignore the link network entirely, and wormholes, which are landed on
    /// rather than jumped through. Port checks against upstream
    /// <c>ShipJumpNavigation</c> and <c>Wormhole</c>.
    /// </summary>
    [TestFixture]
    public class JumpDriveWormholeTests
    {
        // Alpha - Beta are linked and 40 apart. Gamma is 60 from Alpha and linked to
        // nothing. Far is 400 away, out of reach of any ordinary drive.
        private const string Universe =
            "ship \"Hyper\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 500\n" +
            "\t\t\"hyperdrive\" 1\n\t\t\"fuel capacity\" 500\n\t\t\"energy capacity\" 100\n" +
            "ship \"Jumper\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 500\n" +
            "\t\t\"jump drive\" 1\n\t\t\"fuel capacity\" 500\n\t\t\"energy capacity\" 100\n" +
            "ship \"Ranger\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 500\n" +
            "\t\t\"jump drive\" 1\n\t\t\"jump range\" 300\n\t\t\"fuel capacity\" 500\n" +
            "\t\t\"energy capacity\" 100\n" +
            "planet \"Gate\"\n\tgovernment \"Republic\"\n" +
            "system \"Alpha\"\n\tpos 0 0\n\tlink \"Beta\"\n" +
            "\tobject \"Gate\"\n\t\tsprite planet/rock\n\t\tdistance 400\n\t\tperiod 100\n" +
            "system \"Beta\"\n\tpos 40 0\n\tlink \"Alpha\"\n" +
            "system \"Gamma\"\n\tpos 0 60\n" +
            "system \"Far\"\n\tpos 400 0\n" +
            "wormhole \"Gate\"\n\tmappable\n\tlink Alpha\n\tlink Far\n";

        private static GameData Load()
        {
            var data = new GameData();
            data.LoadText(Universe);
            return data;
        }

        private static Ship At(GameData data, string model, string system)
        {
            Ship ship = data.BuildShip(model);
            ship.BuildMounts();
            ship.SetLevels(fuel: ship.MaxFuel, hull: ship.MaxHull, energy: ship.MaxEnergy);
            ship.CurrentSystem = data.Systems[system];
            return ship;
        }

        // --- Jump drives ----------------------------------------------------------

        [Test]
        public void AHyperdriveFollowsLinksAndNothingElse()
        {
            GameData data = Load();
            Ship ship = At(data, "Hyper", "Alpha");

            Assert.IsTrue(ship.CanReach(data.Systems["Beta"]), "Beta is linked");
            Assert.IsFalse(ship.CanReach(data.Systems["Gamma"]),
                "Gamma is close but not linked, and a hyperdrive cannot go there");
        }

        [Test]
        public void AJumpDriveIgnoresTheLinkNetwork()
        {
            // This is the point of a jump drive: it reaches anywhere within range on
            // the map, which is how alien ships cross regions the human network does
            // not connect.
            GameData data = Load();
            Ship ship = At(data, "Jumper", "Alpha");

            Assert.AreEqual(Ship.DefaultJumpRange, ship.JumpDriveRange,
                "a drive that states no range gets the default");
            Assert.IsTrue(ship.CanReach(data.Systems["Gamma"]), "unlinked but within range");
            Assert.IsTrue(ship.CanReach(data.Systems["Beta"]));
            Assert.IsFalse(ship.CanReach(data.Systems["Far"]), "400 is out of range");
        }

        [Test]
        public void ADriveThatStatesItsRangeUsesIt()
        {
            GameData data = Load();
            Ship ranger = At(data, "Ranger", "Alpha");

            Assert.AreEqual(300.0, ranger.JumpDriveRange);
            Assert.IsFalse(ranger.CanReach(data.Systems["Far"]), "400 is still too far");

            Ship plain = At(data, "Jumper", "Alpha");
            Assert.Greater(ranger.JumpDriveRange, plain.JumpDriveRange);
        }

        [Test]
        public void AShipWithNoDriveGoesNowhere()
        {
            var data = new GameData();
            data.LoadText(Universe +
                "ship \"Barge\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"hull\" 500\n" +
                "\t\t\"fuel capacity\" 500\n\t\t\"energy capacity\" 100\n");

            Ship barge = At(data, "Barge", "Alpha");

            Assert.IsFalse(barge.HasHyperdrive);
            Assert.IsFalse(barge.HasJumpDrive);
            Assert.IsFalse(barge.CanReach(data.Systems["Beta"]), "even a linked system");
        }

        [Test]
        public void ASystemCanExtendTheReachOfDrivesInsideIt()
        {
            var data = new GameData();
            data.LoadText(Universe.Replace("system \"Alpha\"\n\tpos 0 0\n\tlink \"Beta\"\n",
                                           "system \"Alpha\"\n\tpos 0 0\n\tlink \"Beta\"\n\t\"jump range\" 500\n"));

            Assert.AreEqual(500.0, data.Systems["Alpha"].JumpRange);

            Ship ship = At(data, "Jumper", "Alpha");
            Assert.IsTrue(ship.CanReach(data.Systems["Far"]),
                "the system's own range carries the ship further than its drive would");
        }

        [Test]
        public void ReachableSystemsListsEverywhereAJumpCouldGo()
        {
            GameData data = Load();
            Ship ship = At(data, "Jumper", "Alpha");

            var reachable = ship.ReachableSystems(data).Select(s => s.Name).OrderBy(n => n).ToList();

            CollectionAssert.AreEqual(new[] { "Beta", "Gamma" }, reachable);
        }

        [Test]
        public void AJumpDriveShipCanCommitAJumpToAnUnlinkedSystem()
        {
            // End to end: the readiness gate has to accept it, not just CanReach.
            GameData data = Load();
            foreach (StarSystem system in data.Systems.Values)
                system.SetDate(0.0);

            Ship ship = At(data, "Jumper", "Alpha");
            ship.TargetSystem = data.Systems["Gamma"];
            ship.Facing = Angle.FromPoint(ship.JumpDirection);

            Assert.IsTrue(ship.TryCommitJump(), "a jump drive should reach an unlinked neighbour");

            for (int i = 0; i < 400 && ship.CurrentSystem != data.Systems["Gamma"]; i++)
                ship.StepHyperspace();

            Assert.AreEqual(data.Systems["Gamma"], ship.CurrentSystem);
        }

        // --- Wormholes ------------------------------------------------------------

        [Test]
        public void AWormholeLinksSystemsInACycle()
        {
            GameData data = Load();
            Wormhole gate = data.Wormholes["Gate"];

            Assert.IsTrue(gate.IsMappable);
            Assert.AreEqual("Far", gate.ExitFrom("Alpha"));
            Assert.AreEqual("Alpha", gate.ExitFrom("Far"),
                "the cycle wraps, so a two-system wormhole works both ways");
        }

        [Test]
        public void AWormholeLeadsNowhereFromASystemItDoesNotTouch()
        {
            // How a one-way passage is written: an entrance and an exit are two
            // separate wormholes.
            GameData data = Load();

            Assert.IsNull(data.Wormholes["Gate"].ExitFrom("Beta"));
            Assert.IsNull(data.Wormholes["Gate"].ExitFrom(null));
        }

        [Test]
        public void LandingOnAWormholeMovesTheShipToTheFarSide()
        {
            GameData data = Load();
            var player = new PlayerState(data);
            Ship ship = At(data, "Hyper", "Alpha");
            player.Fleet.Add(ship);
            player.Fleet.SetFlagship(ship);
            player.EnterSystem(data.Systems["Alpha"]);
            player.Land(data.Planets["Gate"]);

            StarSystem? exit = WormholeTravel.Traverse(data, player);

            Assert.AreEqual(data.Systems["Far"], exit);
            Assert.AreEqual("Far", player.CurrentSystem!.Name);
            Assert.IsNull(player.CurrentPlanet, "coming out of a wormhole leaves you in flight");
            Assert.AreEqual(1, player.Conditions.Get("visited system: Far"));
        }

        [Test]
        public void AnOrdinaryPlanetIsNotAWormhole()
        {
            GameData data = Load();
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["Alpha"]);

            Assert.IsNull(WormholeTravel.At(data, null));
            Assert.IsNull(WormholeTravel.Traverse(data, player), "not landed anywhere");
        }

        [Test]
        public void AWormholeReachesWhereNoDriveCan()
        {
            // The reason wormholes matter: Far is 400 out, past any drive here, and
            // linked to nothing.
            GameData data = Load();
            Ship ranger = At(data, "Ranger", "Alpha");

            Assert.IsFalse(ranger.CanReach(data.Systems["Far"]));
            CollectionAssert.Contains(
                WormholeTravel.ExitsFrom(data, data.Systems["Alpha"]).ToList(), "Far");
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetsWormholesLoadAndConnect()
        {
            GameData data = UpstreamData.Instance;

            Assert.IsNotEmpty(data.Wormholes, "upstream defines wormholes");

            int connecting = data.Wormholes.Values.Count(w => w.Links.Count >= 2);
            var sample = data.Wormholes.Values.First(w => w.Links.Count >= 2);

            TestContext.WriteLine($"{data.Wormholes.Count} wormholes, {connecting} joining two or " +
                                  $"more systems; e.g. {sample}");

            foreach (Wormhole wormhole in data.Wormholes.Values)
                foreach (string system in wormhole.Links)
                    Assert.IsTrue(data.Systems.ContainsKey(system),
                        $"{wormhole.Name} links to an unknown system \"{system}\"");
        }

        [Test]
        public void RealJumpDriveShipsCanReachUnlinkedSystems()
        {
            GameData data = UpstreamData.Instance;

            ShipDefinition? definition = data.Ships.Values
                .FirstOrDefault(d => d.Attributes.Get("jump drive") > 0.0 ||
                                     data.BuildShip(d.DisplayName).HasJumpDrive);

            if (definition is null)
                Assert.Ignore("no jump-drive ships in the dataset");

            Ship ship = data.BuildShip(definition!.DisplayName);
            ship.BuildMounts();
            ship.CurrentSystem = data.Systems.Values.First(s => s.Links.Count > 0);

            Assert.IsTrue(ship.HasJumpDrive, definition.DisplayName);
            Assert.Greater(ship.JumpDriveRange, 0.0);

            var reachable = ship.ReachableSystems(data).ToList();
            int unlinked = reachable.Count(s => !ship.CurrentSystem.Links.Contains(s.Name));

            TestContext.WriteLine(
                $"{definition.DisplayName} in {ship.CurrentSystem.Name} reaches {reachable.Count} " +
                $"systems, {unlinked} of them unlinked");

            Assert.Greater(reachable.Count, 0);
        }
    }
}
