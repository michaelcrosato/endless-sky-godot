using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Mission NPCs: the ships a mission places and the objectives attached to them.
    /// Port checks against upstream <c>NPC</c>.
    /// </summary>
    [TestFixture]
    public class MissionNpcTests
    {
        private static Mission LoadMission(params string[] lines)
        {
            var mission = new Mission("Test");
            mission.Load(new DataFile(
                "mission \"Test\"\n" + string.Join("\n", lines) + "\n", "test.txt").Nodes[0]);
            return mission;
        }

        private static MissionNpc Npc(params string[] lines) =>
            LoadMission(lines).Npcs.Single();

        // --- Objectives -----------------------------------------------------------

        [Test]
        public void KillMeansTheShipMustBeDestroyed()
        {
            MissionNpc npc = Npc("\tnpc kill", "\t\tgovernment \"Pirate\"");

            Assert.AreEqual(ShipEvent.Destroy, npc.SucceedIf);
            Assert.IsFalse(npc.IsSatisfied(ShipEvent.None));
            Assert.IsTrue(npc.IsSatisfied(ShipEvent.Destroy));
        }

        [Test]
        public void SaveMeansTheShipMustNotBeDestroyed()
        {
            // The asymmetric one: "save" states what must NOT happen, so it sets the
            // failure mask rather than the success mask.
            MissionNpc npc = Npc("\tnpc save", "\t\tgovernment \"Merchant\"");

            Assert.AreEqual(ShipEvent.Destroy, npc.FailIf);
            Assert.AreEqual(ShipEvent.None, npc.SucceedIf);

            Assert.IsTrue(npc.IsSatisfied(ShipEvent.None), "leaving it alone succeeds");
            Assert.IsFalse(npc.IsSatisfied(ShipEvent.Destroy), "destroying it fails");
            Assert.IsTrue(npc.HasFailed(ShipEvent.Destroy));
        }

        [Test]
        public void ObjectivesCombineOnOneLine()
        {
            MissionNpc npc = Npc("\tnpc disable board");

            Assert.AreEqual(ShipEvent.Disable | ShipEvent.Board, npc.SucceedIf);
            Assert.IsFalse(npc.IsSatisfied(ShipEvent.Disable), "both are required");
            Assert.IsTrue(npc.IsSatisfied(ShipEvent.Disable | ShipEvent.Board));
        }

        [Test]
        public void AnNpcWithNoObjectiveIsSatisfiedFromTheOutset()
        {
            // Most NPCs exist only to be present. Defaulting to unsatisfied would make
            // every escort mission uncompletable.
            MissionNpc npc = Npc("\tnpc", "\t\tgovernment \"Republic\"");

            Assert.AreEqual(ShipEvent.None, npc.SucceedIf);
            Assert.IsTrue(npc.IsSatisfied(ShipEvent.None));
        }

        [Test]
        public void EvadeAndAccompanyAreAboutWhereTheShipEndsUp()
        {
            MissionNpc evader = Npc("\tnpc evade");
            Assert.IsTrue(evader.MustEvade);
            Assert.IsFalse(evader.IsSatisfied(ShipEvent.None, evaded: false));
            Assert.IsTrue(evader.IsSatisfied(ShipEvent.None, evaded: true));

            MissionNpc escort = Npc("\tnpc accompany");
            Assert.IsTrue(escort.MustAccompany);
            Assert.IsFalse(escort.IsSatisfied(ShipEvent.None, accompanied: false));
            Assert.IsTrue(escort.IsSatisfied(ShipEvent.None, accompanied: true));
        }

        [Test]
        public void FailureBeatsSuccess()
        {
            // A ship that was both boarded and destroyed has failed a "save board" NPC,
            // whatever else was achieved.
            MissionNpc npc = Npc("\tnpc save board");

            Assert.IsFalse(npc.IsSatisfied(ShipEvent.Board | ShipEvent.Destroy));
            Assert.IsTrue(npc.IsSatisfied(ShipEvent.Board));
        }

        // --- Placement and composition --------------------------------------------

        [Test]
        public void PlacementAndGovernmentAreRecorded()
        {
            MissionNpc npc = Npc(
                "\tnpc kill",
                "\t\tgovernment \"Pirate\"",
                "\t\tsystem \"Rutilicus\"",
                "\t\tplanet \"New Boston\"");

            Assert.AreEqual("Pirate", npc.Government);
            Assert.AreEqual("Rutilicus", npc.System);
            Assert.AreEqual("New Boston", npc.Planet);
        }

        [Test]
        public void ExplicitShipsAreListed()
        {
            MissionNpc npc = Npc(
                "\tnpc kill",
                "\t\tship \"Sparrow\"",
                "\t\tship \"Star Barge\"");

            CollectionAssert.AreEqual(new[] { "Sparrow", "Star Barge" }, npc.ShipNames.ToList());
        }

        [Test]
        public void AnInlineFleetIsParsedAsAFleet()
        {
            // Real content describes most NPCs as a fleet rather than as loose ships.
            MissionNpc npc = Npc(
                "\tnpc kill",
                "\t\tgovernment \"Aberrant\"",
                "\t\tfleet",
                "\t\t\tnames \"aberrant names\"",
                "\t\t\tvariant",
                "\t\t\t\t\"Aberrant Latte\" 2",
                "\t\t\t\t\"Aberrant Whiskers\"");

            Assert.IsNotNull(npc.Fleet);
            var ships = npc.Fleet!.Variants.Single().Ships;
            Assert.AreEqual(3, ships.Count, "a count repeats the hull");
            Assert.AreEqual(2, ships.Count(s => s == "Aberrant Latte"));
        }

        [Test]
        public void ANamedFleetIsRecordedByName()
        {
            MissionNpc npc = Npc("\tnpc kill", "\t\tfleet \"Small Pirates\"");

            Assert.AreEqual("Small Pirates", npc.FleetName);
            Assert.IsNull(npc.Fleet, "a reference is not an inline definition");
        }

        [Test]
        public void PersonalityTraitsAreCapturedInlineAndAsChildren()
        {
            MissionNpc npc = Npc(
                "\tnpc",
                "\t\tpersonality coward secretive",
                "\t\t\tvindictive");

            CollectionAssert.Contains(npc.Personality, "coward");
            CollectionAssert.Contains(npc.Personality, "secretive");
            CollectionAssert.Contains(npc.Personality, "vindictive");
        }

        // --- Mission-level roll-up ------------------------------------------------

        [Test]
        public void AMissionIsCompleteOnlyWhenEveryNpcObjectiveIsMet()
        {
            Mission mission = LoadMission(
                "\tnpc kill",
                "\t\tgovernment \"Pirate\"",
                "\tnpc save",
                "\t\tgovernment \"Merchant\"");

            Assert.AreEqual(2, mission.Npcs.Count);

            // Nothing done yet: the pirate is still alive, so not complete.
            Assert.IsFalse(mission.NpcObjectivesMet(_ => ShipEvent.None));

            // Pirate destroyed, merchant untouched.
            Assert.IsTrue(mission.NpcObjectivesMet(
                npc => npc.SucceedIf == ShipEvent.Destroy ? ShipEvent.Destroy : ShipEvent.None));

            // Both destroyed: the escort died, so the mission has failed.
            Assert.IsFalse(mission.NpcObjectivesMet(_ => ShipEvent.Destroy));
        }

        // --- Against the real dataset ---------------------------------------------

        [Test]
        public void TheRealDatasetsMissionNpcsLoad()
        {
            GameData data = UpstreamData.Instance;

            var withNpcs = data.Missions.Values.Where(m => m.Npcs.Count > 0).ToList();
            int total = withNpcs.Sum(m => m.Npcs.Count);
            int killers = withNpcs.SelectMany(m => m.Npcs)
                .Count(n => (n.SucceedIf & ShipEvent.Destroy) != 0);
            int escorts = withNpcs.SelectMany(m => m.Npcs).Count(n => n.MustAccompany);
            int guarded = withNpcs.SelectMany(m => m.Npcs)
                .Count(n => (n.FailIf & ShipEvent.Destroy) != 0);

            TestContext.WriteLine(
                $"{total} NPCs across {withNpcs.Count} missions: {killers} to destroy, " +
                $"{guarded} to keep alive, {escorts} to escort");

            Assert.Greater(total, 100, "the dataset places a lot of mission ships");
            Assert.Greater(killers, 0);
            Assert.Greater(guarded, 0);
        }
    }
}
