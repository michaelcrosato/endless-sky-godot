namespace EndlessSky.Tests.Presentation
{
    using System.Linq;
    using System.Threading.Tasks;
    using EndlessSky.Game;
    using EndlessSky.Sim;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    [TestSuite]
    public class CameraRigTest
    {
        private static Ship Hull(bool capital)
        {
            var data = new GameData();
            data.LoadText("ship Test\n\tattributes\n\t\tmass 100\n\t\thull 500\n" +
                "\t\tthrust 100\n\t\tdrag 1\n" + (capital ? "\tengine 0 600\n" : ""));
            return data.BuildShip("Test");
        }

        private static SubViewport Viewport(Vector2I size, out CameraRig rig)
        {
            var viewport = new SubViewport { Size = size, OwnWorld3D = true };
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(viewport);
            rig = new CameraRig();
            viewport.AddChild(rig);
            return viewport;
        }

        private static async Task Release(Node node)
        {
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            node.QueueFree();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        private static void AssertHullVisible(SubViewport viewport, CameraRig rig, Ship ship)
        {
            Camera3D camera = rig.GetChildren().OfType<Camera3D>().Single();
            float radius = WorldSpace.Length(new ShipAppearance(ship.Definition).Radius);
            // Project a ring enclosing every planar heading. A camera that only fits
            // a north-facing ship still clips the same hull when it turns sideways.
            var safe = new Rect2(new Vector2(viewport.Size.X, viewport.Size.Y) * 0.05f,
                new Vector2(viewport.Size.X, viewport.Size.Y) * 0.90f);
            for (int i = 0; i < 16; i++)
            {
                float angle = i * Mathf.Tau / 16;
                Vector3 world = WorldSpace.ToWorld(ship.Position)
                    + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                AssertBool(camera.IsPositionBehind(world)).IsFalse();
                Vector2 screen = camera.UnprojectPosition(world);
                AssertBool(safe.HasPoint(screen))
                    .OverrideFailureMessage($"Hull edge {screen} is outside {safe}; velocity {ship.Velocity}")
                    .IsTrue();
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task LargeHullsFitLandscapeAndPortraitViews()
        {
            foreach (Vector2I size in new[] { new Vector2I(1280, 720), new Vector2I(720, 1280) })
            {
                SubViewport viewport = Viewport(size, out CameraRig rig);
                try
                {
                    Ship ship = Hull(capital: true);
                    rig.Snap(ship);
                    AssertHullVisible(viewport, rig, ship);
                }
                finally { await Release(viewport); }
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task LookAheadKeepsFastShipsVisibleInEveryDirection()
        {
            SubViewport viewport = Viewport(new Vector2I(1280, 720), out CameraRig rig);
            try
            {
                Ship ship = Hull(capital: false);
                foreach (Point direction in new[] { new Point(1, 0), new Point(0, 1),
                    new Point(-1, 0), new Point(0, -1) })
                {
                    ship.Velocity = direction * 1000;
                    rig.Snap(ship);
                    AssertHullVisible(viewport, rig, ship);
                }
            }
            finally { await Release(viewport); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ZoomStillWorksAfterChangingFlagships()
        {
            SubViewport viewport = Viewport(new Vector2I(1280, 720), out CameraRig rig);
            try
            {
                rig.Snap(Hull(capital: false));
                Ship capital = Hull(capital: true);
                rig.Snap(capital);
                Camera3D camera = rig.GetChildren().OfType<Camera3D>().Single();
                float initial = camera.Position.Length();
                using var wheel = new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelDown };
                rig._UnhandledInput(wheel);
                rig.Snap(capital);
                AssertThat(camera.Position.Length()).IsGreater(initial);
                AssertHullVisible(viewport, rig, capital);
            }
            finally { await Release(viewport); }
        }
    }
}
