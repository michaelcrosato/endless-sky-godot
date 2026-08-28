using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Variant inheritance. Upstream writes a variant as <c>ship "Base" "Base (Variant)"</c>
    /// and states only what differs; everything else comes from the base model. The dataset
    /// has 550+ of these, so getting this wrong silently produces ships with no hull, no
    /// drag and infinite top speed.
    /// </summary>
    public class ShipVariantTests
    {
        private const string BaseAndVariant =
            "ship \"Freighter\"\n" +
            "\tsprite \"ship/freighter\"\n" +
            "\tattributes\n" +
            "\t\tcategory \"Light Freighter\"\n" +
            "\t\t\"mass\" 100\n" +
            "\t\t\"drag\" 10\n" +
            "\t\t\"hull\" 1000\n" +
            "\toutfits\n" +
            "\t\t\"Engine\"\n" +
            "\tengine -9.5 38\n" +
            "\tengine 9.5 38\n" +
            "\tdescription \"A base freighter.\"\n" +
            "\n" +
            "ship \"Freighter\" \"Freighter (Armed)\"\n" +
            "\tadd attributes\n" +
            "\t\t\"hull\" 500\n" +
            "\toutfits\n" +
            "\t\t\"Engine\"\n" +
            "\t\t\"Blaster\"\n" +
            "\n" +
            "outfit \"Engine\"\n" +
            "\t\"mass\" 0\n" +
            "\t\"thrust\" 20\n" +
            "\t\"turn\" 3000\n" +
            "\n" +
            "outfit \"Blaster\"\n" +
            "\t\"mass\" 5\n";

        private static GameData Load(string text = BaseAndVariant)
        {
            var data = new GameData();
            data.LoadText(text, "variant-fixture");
            return data;
        }

        [Test]
        public void VariantIsRegisteredUnderItsVariantName()
        {
            GameData data = Load();

            Assert.IsTrue(data.Ships.ContainsKey("Freighter"));
            Assert.IsTrue(data.Ships.ContainsKey("Freighter (Armed)"));
        }

        [Test]
        public void VariantInheritsHullAttributesItDidNotRestate()
        {
            GameData data = Load();
            ShipDefinition variant = data.Ships["Freighter (Armed)"];

            Assert.AreEqual(100.0, variant.Attributes.Get("mass"), 1e-9, "mass comes from the base");
            Assert.AreEqual(10.0, variant.Attributes.Get("drag"), 1e-9, "drag comes from the base");
            Assert.AreEqual("Light Freighter", variant.Category);
        }

        [Test]
        public void AddAttributesLayersOnTopOfTheInheritedValue()
        {
            GameData data = Load();

            Assert.AreEqual(1000.0, data.Ships["Freighter"].Attributes.Get("hull"), 1e-9);
            Assert.AreEqual(1500.0, data.Ships["Freighter (Armed)"].Attributes.Get("hull"), 1e-9,
                "'add attributes' adds to the base value rather than replacing it");
        }

        [Test]
        public void VariantInheritsSpriteAndEnginePoints()
        {
            GameData data = Load();
            ShipDefinition variant = data.Ships["Freighter (Armed)"];

            Assert.AreEqual("ship/freighter", variant.Sprite);
            Assert.AreEqual(2, variant.Engines.Count);
        }

        [Test]
        public void VariantOutfitsReplaceRatherThanAppend()
        {
            GameData data = Load();

            CollectionAssert.AreEquivalent(
                new[] { "Engine", "Blaster" },
                data.Ships["Freighter (Armed)"].OutfitNames.ToList(),
                "a variant that lists outfits states its whole loadout");
        }

        [Test]
        public void VariantIsFlyableWithInheritedPhysics()
        {
            GameData data = Load();

            Ship ship = data.BuildShip("Freighter (Armed)", out List<string> missing);

            Assert.IsEmpty(missing);
            Assert.AreEqual(105.0, ship.Mass, 1e-9, "base hull mass plus the blaster");
            Assert.Greater(ship.Thrust, 0.0);
            Assert.IsFalse(double.IsInfinity(ship.MaxVelocity),
                "an inherited drag value must produce a finite top speed");
        }

        [Test]
        public void BaseDefinedAfterTheVariantStillResolves()
        {
            // Load order is alphabetical by file, so a variant in "_deprecated" is parsed
            // long before its base in a later directory. Inheritance must be deferred.
            const string reversed =
                "ship \"Freighter\" \"Freighter (Armed)\"\n" +
                "\tadd attributes\n" +
                "\t\t\"hull\" 500\n" +
                "\n" +
                "ship \"Freighter\"\n" +
                "\tattributes\n" +
                "\t\t\"mass\" 100\n" +
                "\t\t\"drag\" 10\n" +
                "\t\t\"hull\" 1000\n";

            GameData data = Load(reversed);
            ShipDefinition variant = data.Ships["Freighter (Armed)"];

            Assert.AreEqual(100.0, variant.Attributes.Get("mass"), 1e-9);
            Assert.AreEqual(1500.0, variant.Attributes.Get("hull"), 1e-9);
        }

        [Test]
        public void VariantOfAVariantResolvesThroughTheChain()
        {
            const string chain =
                "ship \"A\"\n\tattributes\n\t\t\"mass\" 100\n\t\t\"drag\" 10\n" +
                "\nship \"A\" \"B\"\n\tadd attributes\n\t\t\"hull\" 50\n" +
                "\nship \"B\" \"C\"\n\tadd attributes\n\t\t\"hull\" 25\n";

            GameData data = Load(chain);

            Assert.AreEqual(100.0, data.Ships["C"].Attributes.Get("mass"), 1e-9,
                "mass should come all the way from A");
            Assert.AreEqual(75.0, data.Ships["C"].Attributes.Get("hull"), 1e-9,
                "hull additions accumulate down the chain");
        }

        [Test]
        public void EmptyAttributesBlockStillInheritsFromTheBase()
        {
            // Real content does this: "Modified Dromedary Wreck" writes a bare "attributes"
            // header with no children, then an "add attributes" block. Upstream tests
            // whether the loaded attribute set is empty, not whether the node was present,
            // so the wreck still inherits the base hull - including its drag.
            const string emptyBlock =
                "ship \"Hauler\"\n" +
                "\tattributes\n" +
                "\t\t\"mass\" 400\n" +
                "\t\t\"drag\" 12\n" +
                "\t\t\"hull\" 6000\n" +
                "\n" +
                "ship \"Hauler\" \"Hauler Wreck\"\n" +
                "\tattributes\n" +
                "\tadd attributes\n" +
                "\t\t\"hull\" -5000\n";

            GameData data = Load(emptyBlock);
            ShipDefinition wreck = data.Ships["Hauler Wreck"];

            Assert.AreEqual(400.0, wreck.Attributes.Get("mass"), 1e-9);
            Assert.AreEqual(12.0, wreck.Attributes.Get("drag"), 1e-9,
                "an empty attributes block must not block inheritance");
            Assert.AreEqual(1000.0, wreck.Attributes.Get("hull"), 1e-9);
        }

        [Test]
        public void MissingBaseIsReportedNotSilentlyIgnored()
        {
            GameData data = Load("ship \"Ghost\" \"Ghost (Armed)\"\n\tadd attributes\n\t\t\"hull\" 5\n");

            Assert.IsNotEmpty(data.Diagnostics, "a variant of an undefined base should be reported");
        }

        [Test]
        public void CyclicVariantChainTerminates()
        {
            // Malformed content must not hang the loader.
            GameData data = Load("ship \"A\" \"B\"\n\tadd attributes\n\t\t\"hull\" 1\n" +
                                 "\nship \"B\" \"A\"\n\tadd attributes\n\t\t\"hull\" 1\n");

            Assert.IsNotNull(data.Ships);
            Assert.IsNotEmpty(data.Diagnostics);
        }
    }
}
