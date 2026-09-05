using System;
using System.Collections.Generic;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>Which screen the shell is showing, if any.</summary>
    public enum UiScreen
    {
        None,
        MainMenu,
        Pause,
        Status,
        Map,
        Controls,
        Options,
        Tutorial,
        Destroyed,
    }

    /// <summary>
    /// The interface around the game: menu, pause, status, map, controls, options and
    /// the first-run tutorial.
    /// </summary>
    /// <remarks>
    /// One owner for every modal screen, rather than each panel managing itself. That
    /// matters for two reasons. Only one screen may be up at a time, and something has
    /// to know which - two overlays both reading the arrow keys is a game that appears
    /// to have broken input. And the simulation must not tick while a screen is up, so
    /// there has to be a single answer to "are we paused", which is
    /// <see cref="IsModal"/>.
    ///
    /// Keys are read here rather than in the individual screens so the bindings live in
    /// one list and cannot disagree with the reference screen that documents them.
    ///
    /// Edge-triggered throughout: <c>Input.IsPhysicalKeyPressed</c> is level, so a key
    /// held for three frames would otherwise open and close a panel three times.
    /// </remarks>
    public partial class GameUi : CanvasLayer
    {
        private readonly Dictionary<Key, bool> _was = new Dictionary<Key, bool>();
        private readonly HashSet<Key> _pressed = new HashSet<Key>();
        private static readonly Key[] Keys =
        {
            Key.Escape, Key.M, Key.I, Key.F1, Key.F2, Key.Up, Key.Down, Key.Left, Key.Right,
            Key.W, Key.S, Key.Enter, Key.KpEnter, Key.Space, Key.B, Key.N, Key.D, Key.Tab,
            Key.G, Key.H, Key.V, Key.F,
        };
        private Control? _current;

        // One device sample per frame, including keys the current screen ignores.
        // Tests can drive the same routing without a native keyboard in headless Godot.
        internal Func<Key, bool> KeyDown { get; set; } = key => Input.IsPhysicalKeyPressed(key);

        private PlayerState _player = null!;
        private MissionLog _missions = null!;
        private GameData _universe = null!;
        private Func<Ship?> _ship = () => null;

        /// <summary>The screen on top, or None.</summary>
        public UiScreen Screen { get; private set; } = UiScreen.None;

        /// <summary>
        /// Whether a screen is up, and therefore whether the simulation should hold.
        /// </summary>
        public bool IsModal => Screen != UiScreen.None;

        /// <summary>Raised when the player picks a jump destination on the map.</summary>
        public event Action<StarSystem>? DestinationChosen;

        public event Action<FleetOrder>? FleetOrderRequested;

        /// <summary>Raised when the player asks to quit.</summary>
        public event Action? QuitRequested;

        /// <summary>Raised when the player asks to save; the handler reports success.</summary>
        public event Func<bool>? SaveRequested;

        /// <summary>Raised when the player asks to continue a saved game.</summary>
        public event Func<bool>? LoadRequested;

        /// <summary>Screen to open on the first frame; only used for captures.</summary>
        public static UiScreen OpenAtStart { get; set; } = UiScreen.None;

        /// <summary>Whether the tutorial has been shown this run.</summary>
        private bool _tutorialSeen;

        public void Bind(PlayerState player, MissionLog missions, GameData universe, Func<Ship?> ship)
        {
            _player = player;
            _missions = missions;
            _universe = universe;
            _ship = ship;
        }

        public override void _Ready()
        {
            // Shell menus own the screen above both the port and tutorial hints.
            Layer = 30;

            if (OpenAtStart != UiScreen.None)
            {
                Show(OpenAtStart);
                _tutorialSeen = true;
            }
        }

        /// <summary>
        /// The current port, driven only when no shell menu is on top of it.
        /// </summary>
        /// <remarks>
        /// One controller owns the keyboard in flight and at a port. In particular,
        /// menu navigation must not trade, depart, or answer an offer underneath it.
        /// </remarks>
        public LandedOverlay? Port { get; set; }

        public override void _Process(double delta)
        {
            _pressed.Clear();
            foreach (Key key in Keys)
            {
                bool down = KeyDown(key);
                _was.TryGetValue(key, out bool was);
                if (down && !was)
                    _pressed.Add(key);
                _was[key] = down;
            }

            if (!IsModal && Port?.HasDialog == true)
            {
                Port.Step(this);
                return;
            }

            UiScreen previous = Screen;
            // Esc closes whatever is up, or opens the pause menu in flight or at port.
            if (Pressed(Key.Escape))
            {
                Show(Screen == UiScreen.None ? UiScreen.Pause : UiScreen.None);
            }

            // The direct keys work from flight and from any other screen, so a player
            // can go straight from the map to the status screen without backing out.
            else if (Pressed(Key.M)) Toggle(UiScreen.Map);

            // I, not Tab: Tab already switches counters on the landed screen, and one
            // key with two meanings is worse than one key.
            else if (Pressed(Key.I)) Toggle(UiScreen.Status);
            else if (Pressed(Key.F1)) Toggle(UiScreen.Controls);
            else if (Pressed(Key.F2)) Toggle(UiScreen.Options);

            // A transition consumes this frame, including a simultaneous confirm or
            // shop key. Held keys have already been sampled and cannot leak through.
            if (Screen != previous)
                return;

            if (_current is IUiScreen screen)
                screen.Step(this);
            else if (!IsModal)
            {
                if (Port != null) Port.Step(this);
                else if (_player.CurrentPlanet == null)
                {
                    if (Pressed(Key.H)) FleetOrderRequested?.Invoke(FleetOrder.Hold);
                    else if (Pressed(Key.G)) FleetOrderRequested?.Invoke(FleetOrder.Gather);
                    else if (Pressed(Key.V)) FleetOrderRequested?.Invoke(FleetOrder.Escort);
                    else if (Pressed(Key.F)) FleetOrderRequested?.Invoke(FleetOrder.AttackTarget);
                }
            }
        }

        /// <summary>
        /// Whether the run is over. The death screen is not a panel the player can
        /// dismiss, so every other screen transition has to refuse to move off it.
        /// </summary>
        public bool IsGameOver => Screen == UiScreen.Destroyed;

        /// <summary>Opens a screen, or closes it if it is already the one showing.</summary>
        public void Toggle(UiScreen screen)
        {
            if (IsGameOver)
                return;

            Show(Screen == screen ? UiScreen.None : screen);
        }

        public void Show(UiScreen screen)
        {
            // Destruction is terminal: nothing dismisses it, so a stray Esc cannot put
            // the player back at the controls of a ship that no longer exists.
            if (IsGameOver && screen != UiScreen.Destroyed)
                return;

            if (_current != null)
            {
                _current.QueueFree();
                _current = null;
            }

            Screen = screen;
            if (screen == UiScreen.None)
                return;

            Control panel = Build(screen);
            AddChild(panel);
            _current = panel;
        }

        /// <summary>
        /// Leaves the menu for the game: the tutorial on a first run, flight after.
        /// </summary>
        public void BeginPlay()
        {
            if (_tutorialSeen)
            {
                Show(UiScreen.None);
                return;
            }

            _tutorialSeen = true;
            Show(UiScreen.Tutorial);
        }

        private Control Build(UiScreen screen) => screen switch
        {
            UiScreen.MainMenu => new MainMenuScreen(_universe),
            UiScreen.Pause => new PauseScreen(),
            UiScreen.Status => new StatusScreen(_player, _missions, _universe, _ship()),
            UiScreen.Map => new MapScreen(_player, _universe, _ship(), OnDestinationChosen),
            UiScreen.Controls => new ControlsScreen(),
            UiScreen.Options => new OptionsScreen(),
            UiScreen.Tutorial => new TutorialScreen(),
            UiScreen.Destroyed => new DestroyedScreen(),
            _ => new Control(),
        };

        private void OnDestinationChosen(StarSystem system)
        {
            DestinationChosen?.Invoke(system);
            Show(UiScreen.None);
        }

        internal void RequestQuit() => QuitRequested?.Invoke();

        /// <summary>Saves, and reports what happened so a menu row can say so.</summary>
        internal string RequestSave() =>
            SaveRequested?.Invoke() == true ? "saved" : "could not save";

        /// <summary>Loads the saved game and closes the menu if it worked.</summary>
        internal string RequestLoad()
        {
            if (LoadRequested?.Invoke() != true)
                return "no save to load";

            Show(UiScreen.None);
            return "loaded";
        }

        /// <summary>Edge-triggered key read: true only on the frame a key goes down.</summary>
        public bool Pressed(Key key) => _pressed.Remove(key);
    }

    /// <summary>A screen that wants a frame tick, for selection and input of its own.</summary>
    public interface IUiScreen
    {
        void Step(GameUi ui);
    }
}
