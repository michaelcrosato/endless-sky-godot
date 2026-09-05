namespace EndlessSky.Tests.Presentation
{
    using System.Linq;
    using System.Threading.Tasks;
    using EndlessSky.Data;
    using EndlessSky.Game;
    using EndlessSky.Sim;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    /// <summary>
    /// Presentation-tier guards for the landing indicators: the label beside each
    /// world and the ring on the selected one.
    /// </summary>
    /// <remarks>
    /// Reported from play: "I am finding it hard to know which planets I can land on."
    /// The rule that answers it is pinned in the simulation suite
    /// (<c>LandingTargetTests</c>); what a headless NUnit run cannot see is whether the
    /// answer ever reaches the screen — whether a label is built at all, whether it
    /// says what is down there, and whether selecting a world actually lights it up.
    /// Those need a real engine, so they are here.
    ///
    /// Colour is asserted as a RELATION rather than as literal values. Pinning the
    /// exact RGB would break on every palette tweak while proving nothing; what has to
    /// hold is that a port, a bare rock and the current target are three visibly
    /// different things.
    /// </remarks>
    [TestSuite]
    public class LandingIndicatorTest
    {
        private const string SystemText =
            "system Testbed\n" +
            "\tpos 0 0\n" +
            "\tobject\n" +
            "\t\tsprite star/g5\n" +
            "\t\tdistance 0\n" +
            "\t\tperiod 10\n" +
            "\tobject \"Bare Rock\"\n" +
            "\t\tsprite planet/rock\n" +
            "\t\tdistance 1000\n" +
            "\t\tperiod 100\n" +
            "\tobject \"Port World\"\n" +
            "\t\tsprite planet/ocean\n" +
            "\t\tdistance 3000\n" +
            "\t\tperiod 300\n" +
            "\n" +
            "planet \"Bare Rock\"\n" +
            "\tattributes uninhabited\n" +
            "\n" +
            "planet \"Port World\"\n" +
            "\tspaceport `A working port.`\n";

        private static StarSystem BuildSystem()
        {
            var data = new GameData();
            data.LoadText(SystemText, "landing-indicator-test");
            StarSystem system = data.Systems["Testbed"];
            system.SetDate(0.0);
            return system;
        }

        private static StellarObjectView Realise(StellarObject obj)
        {
            StellarObjectView view = StellarObjectView.Create(obj);
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(view);
            return view;
        }

        private static async Task Release(Node node)
        {
            // Label3D defers text shaping. Let Godot finish deferred work and free
            // the node at the frame boundary, as it does during system transitions.
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            node.QueueFree();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        private static Label3D? LabelOf(StellarObjectView view) =>
            view.GetChildren().OfType<Label3D>().FirstOrDefault();

        private static MeshInstance3D? RingOf(StellarObjectView view) =>
            view.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(m => m.Name == "TargetRing");

        [TestCase]
        [RequireGodotRuntime]
        public async Task EveryWorldIsLabelledAndSceneryIsNot()
        {
            StarSystem system = BuildSystem();

            foreach (StellarObject obj in system.AllObjects())
            {
                StellarObjectView view = Realise(obj);
                Label3D? label = LabelOf(view);

                if (obj.Planet == null)
                {
                    AssertThat(label).IsNull();
                }
                else
                {
                    AssertThat(label).IsNotNull();
                    AssertString(label!.Text).Contains(obj.PlanetName!);
                }

                await Release(view);
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ALabelSaysWhetherThereIsAPortDownThere()
        {
            StarSystem system = BuildSystem();

            StellarObjectView port = Realise(system.AllObjects().First(o => o.PlanetName == "Port World"));
            StellarObjectView rock = Realise(system.AllObjects().First(o => o.PlanetName == "Bare Rock"));

            AssertString(LabelOf(port)!.Text).Contains("spaceport");
            AssertString(LabelOf(rock)!.Text).Contains("no port");

            // And they do not look the same: a palette may change, but a world worth
            // flying to must never render identically to one that is not.
            AssertThat(LabelOf(port)!.Modulate).IsNotEqual(LabelOf(rock)!.Modulate);

            await Release(port);
            await Release(rock);
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task SelectingAWorldLightsItUpAndDeselectingPutsItBack()
        {
            StarSystem system = BuildSystem();
            StellarObjectView view = Realise(system.AllObjects().First(o => o.PlanetName == "Port World"));

            Color unselected = LabelOf(view)!.Modulate;
            AssertBool(RingOf(view)!.Visible).IsFalse();

            view.SetSelected(true);
            AssertBool(RingOf(view)!.Visible).IsTrue();
            AssertThat(LabelOf(view)!.Modulate).IsNotEqual(unselected);

            view.SetSelected(false);
            AssertBool(RingOf(view)!.Visible).IsFalse();
            AssertThat(LabelOf(view)!.Modulate).IsEqual(unselected);

            await Release(view);
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task TheRadarPlotsTheSystemWithoutATrackedShip()
        {
            // The dial is built before the ship exists on the error path (BuildHud runs
            // with a message and no world), so drawing with nothing tracked must not
            // throw — that would take the whole flight scene down with it.
            StarSystem system = BuildSystem();
            var radar = new SystemRadar();
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(radar);
            radar.Track(null, system.AllObjects().ToList());

            AssertThat(radar.Size.X).IsGreater(0f);

            await Release(radar);
        }
    }
}
