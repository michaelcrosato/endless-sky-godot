namespace EndlessSky.Tests.Presentation
{
    using System.Linq;
    using EndlessSky.Game;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    /// <summary>
    /// Guards for the interface shell.
    /// </summary>
    /// <remarks>
    /// The controls screen is the one piece of UI a player is entitled to trust: if it
    /// lists a key the game does not read, or omits one it does, they will conclude the
    /// game is broken rather than the documentation. Keeping the bindings as data means
    /// they can at least be checked for the mistakes that are checkable — duplicates,
    /// blanks, and a key documented in two places with two different meanings.
    /// </remarks>
    [TestSuite]
    public class GameUiTest
    {
        [TestCase]
        public void EveryBindingIsFilledIn()
        {
            AssertThat(ControlsScreen.Bindings.Length).IsGreater(8);

            foreach ((string keys, string does, string where) in ControlsScreen.Bindings)
            {
                AssertString(keys).IsNotEmpty();
                AssertString(does).IsNotEmpty();
                AssertString(where).IsNotEmpty();
            }
        }

        [TestCase]
        public void NoKeyIsDocumentedTwiceInTheSameContext()
        {
            // Tab legitimately appears twice — status anywhere, counter when landed —
            // so the check is per context rather than global.
            var clashes = ControlsScreen.Bindings
                .GroupBy(b => (b.Where, b.Keys))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Keys} in {g.Key.Where}")
                .ToList();

            AssertArray(clashes).IsEmpty();
        }

        [TestCase]
        public void TheScreensAWindowCanReachAreAllDistinct()
        {
            // A duplicated enum value would make Toggle close the wrong panel.
            var values = System.Enum.GetValues<UiScreen>();
            AssertThat(values.Distinct().Count()).IsEqual(values.Length);
        }

        [TestCase]
        [RequireGodotRuntime]
        public void EveryScreenBuildsWithoutAPlayer()
        {
            // The menu, controls, options and tutorial are reachable before a game
            // exists, so none of them may assume player state.
            foreach (UiScreen screen in new[]
            {
                UiScreen.MainMenu, UiScreen.Pause, UiScreen.Controls,
                UiScreen.Options, UiScreen.Tutorial,
            })
            {
                Control panel = screen switch
                {
                    UiScreen.MainMenu => new MainMenuScreen(),
                    UiScreen.Pause => new PauseScreen(),
                    UiScreen.Controls => new ControlsScreen(),
                    UiScreen.Options => new OptionsScreen(),
                    _ => new TutorialScreen(),
                };

                // _Ready is where a screen actually builds itself, and building is
                // where it would reach for player state. Constructing one and freeing
                // it ran no screen code at all: the assertion could not fail for the
                // reason the test exists. Godot calls _Ready when a node enters the
                // tree, so the panel has to enter one.
                Node root = ((SceneTree)Engine.GetMainLoop()).Root;
                root.AddChild(panel);

                AssertThat(panel.GetChildCount())
                    .OverrideFailureMessage($"{screen} built no content in _Ready")
                    .IsGreater(0);

                root.RemoveChild(panel);
                panel.Free();
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public void AShellStartsClosedSoTheGameIsNotBornPaused()
        {
            var ui = new GameUi();
            AssertBool(ui.IsModal).IsFalse();
            AssertThat(ui.Screen).IsEqual(UiScreen.None);
            ui.Free();
        }
    }
}
