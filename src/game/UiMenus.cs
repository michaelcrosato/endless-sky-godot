using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// A screen that is a list of choices, driven by the arrow keys.
    /// </summary>
    /// <remarks>
    /// Selection lives here rather than in each menu so every list in the game moves
    /// the same way. The cursor wraps, because a list that stops dead at the last row
    /// makes a player think their input was dropped.
    /// </remarks>
    public abstract partial class UiMenuScreen : UiPanelScreen, IUiScreen
    {
        private Label _list = null!;
        private int _selected;

        protected abstract IReadOnlyList<MenuItem> Items { get; }

        protected override string Footer => "↑/↓ select   ·   ENTER choose   ·   ESC close";

        protected sealed override void BuildBody()
        {
            _list = UiTheme.Text("", 15);
            Column.AddChild(_list);
            Redraw();
        }

        public virtual void Step(GameUi ui)
        {
            IReadOnlyList<MenuItem> items = Items;
            if (items.Count == 0)
                return;

            if (ui.Pressed(Key.Up) || ui.Pressed(Key.W))
            {
                _selected = (_selected + items.Count - 1) % items.Count;
                Redraw();
            }

            if (ui.Pressed(Key.Down) || ui.Pressed(Key.S))
            {
                _selected = (_selected + 1) % items.Count;
                Redraw();
            }

            if (ui.Pressed(Key.Enter) || ui.Pressed(Key.KpEnter) || ui.Pressed(Key.Space))
            {
                items[_selected].Choose(ui);
                Redraw();
            }

            // Left/Right adjust a setting in place, which is what options screens want.
            if (ui.Pressed(Key.Left)) { items[_selected].Adjust(-1); Redraw(); }
            if (ui.Pressed(Key.Right)) { items[_selected].Adjust(+1); Redraw(); }
        }

        protected void Redraw()
        {
            IReadOnlyList<MenuItem> items = Items;
            var lines = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                string cursor = i == _selected ? "▶ " : "   ";
                string value = items[i].Value();
                lines.Add(value.Length > 0
                    ? UiTheme.Row(cursor + items[i].Label, value, 30)
                    : cursor + items[i].Label);
            }

            _list.Text = string.Join("\n", lines);
        }
    }

    /// <summary>One row of a menu: a label, an optional value, and what it does.</summary>
    public class MenuItem
    {
        public MenuItem(string label, Action<GameUi>? choose = null,
                        Func<string>? value = null, Action<int>? adjust = null)
        {
            Label = label;
            _choose = choose;
            _value = value;
            _adjust = adjust;
        }

        private readonly Action<GameUi>? _choose;
        private readonly Func<string>? _value;
        private readonly Action<int>? _adjust;

        public string Label { get; }

        public string Value() => _value?.Invoke() ?? string.Empty;

        public void Choose(GameUi ui) => _choose?.Invoke(ui);

        public void Adjust(int direction) => _adjust?.Invoke(direction);
    }

    /// <summary>What Esc brings up in flight.</summary>
    public partial class PauseScreen : UiMenuScreen
    {
        protected override string Title => "PAUSED";

        protected override string Subtitle => "the simulation is held while this is open";

        protected override float MinWidth => 460f;

        private readonly MenuItem[] _items;

        /// <summary>What the last save attempt did, shown beside the row.</summary>
        private string _saveState = string.Empty;

        public PauseScreen()
        {
            _items = new[]
            {
                new MenuItem("Resume", ui => ui.Show(UiScreen.None)),
                new MenuItem("Save game", ui => _saveState = ui.RequestSave(),
                             value: () => _saveState),
                new MenuItem("Status", ui => ui.Show(UiScreen.Status)),
                new MenuItem("Galaxy map", ui => ui.Show(UiScreen.Map)),
                new MenuItem("Controls", ui => ui.Show(UiScreen.Controls)),
                new MenuItem("Graphics options", ui => ui.Show(UiScreen.Options)),
                new MenuItem("How to play", ui => ui.Show(UiScreen.Tutorial)),
                new MenuItem("Quit", ui => ui.RequestQuit()),
            };
        }

        protected override IReadOnlyList<MenuItem> Items => _items;
    }

    /// <summary>
    /// The end of a run: the player's flagship has been destroyed.
    /// </summary>
    /// <remarks>
    /// Before this existed there was no death at all. A hull below zero hid its mesh
    /// and the game carried on — HUD live, landing key working, controls answering for
    /// a ship that no longer existed. Losing has to be a state the game can be in, or
    /// none of the combat means anything.
    /// </remarks>
    public partial class DestroyedScreen : UiMenuScreen
    {
        protected override string Title => "YOUR SHIP WAS DESTROYED";

        protected override string Subtitle =>
            "the wreckage is spreading out behind you, and there is no one left to fly it";

        protected override float MinWidth => 520f;

        private readonly MenuItem[] _items;

        public DestroyedScreen()
        {
            _items = new[]
            {
                new MenuItem("Quit", ui => ui.RequestQuit()),
            };
        }

        protected override IReadOnlyList<MenuItem> Items => _items;
    }

    /// <summary>The screen the game opens on.</summary>
    public partial class MainMenuScreen : UiMenuScreen
    {
        protected override string Title => "ENDLESS SKY 3D";

        protected override string Subtitle => _subtitle;

        protected override float MinWidth => 460f;

        private readonly string _subtitle;
        private readonly MenuItem[] _items;

        /// <summary>
        /// The counts come from the dataset that is actually loaded rather than from
        /// constants. They used to name upstream's 694 systems and 902 ships, which the
        /// game has not loaded since it started playing its own universe -- so the
        /// first thing a player read was a number that was not true of their game.
        /// </summary>
        public MainMenuScreen(GameData? universe = null)
        {
            _subtitle = universe is null
                ? "Endless Sky's simulation, rendered in 3D"
                : $"Endless Sky's simulation, rendered in 3D — " +
                  $"{universe.Systems.Count:n0} systems, {universe.Ships.Count:n0} ships";

            var items = new List<MenuItem>
            {
                // First run gets the tutorial; after that, straight into flight.
                new MenuItem("Begin", ui => ui.BeginPlay()),
            };

            // Only offered when there is a game to come back to; a Continue that does
            // nothing is worse than no Continue at all.
            if (SaveSlot.Exists)
                items.Add(new MenuItem("Continue", ui => ui.RequestLoad()));

            items.AddRange(new[]
            {
                new MenuItem("How to play", ui => ui.Show(UiScreen.Tutorial)),
                new MenuItem("Controls", ui => ui.Show(UiScreen.Controls)),
                new MenuItem("Graphics options", ui => ui.Show(UiScreen.Options)),
                new MenuItem("Quit", ui => ui.RequestQuit()),
            });

            _items = items.ToArray();
        }

        protected override IReadOnlyList<MenuItem> Items => _items;
    }

    /// <summary>
    /// Graphics settings, applied immediately so the effect of a change is visible
    /// while the menu is still open.
    /// </summary>
    /// <remarks>
    /// Every setting here is one a player reaches for when the game runs badly or
    /// looks wrong on their monitor: window mode, resolution, frame cap, and the two
    /// costs worth trading away first — antialiasing and the glow pass.
    ///
    /// Every change is saved immediately rather than on a confirm step. There is no
    /// "apply" button to forget, and a player who changes a setting and quits should
    /// find it changed next time. See <see cref="GameSettings"/>.
    /// </remarks>
    public partial class OptionsScreen : UiMenuScreen
    {
        private static readonly Vector2I[] Resolutions =
        {
            new Vector2I(1280, 720),
            new Vector2I(1600, 900),
            new Vector2I(1920, 1080),
            new Vector2I(2560, 1440),
        };

        private static readonly int[] FrameCaps = { 0, 30, 60, 120, 144, 240 };

        private readonly MenuItem[] _items;

        public OptionsScreen()
        {
            _items = new[]
            {
                new MenuItem("Window mode",
                    choose: _ => CycleWindowMode(1),
                    value: () => DisplayServer.WindowGetMode() switch
                    {
                        DisplayServer.WindowMode.Fullscreen => "fullscreen",
                        DisplayServer.WindowMode.ExclusiveFullscreen => "exclusive fullscreen",
                        DisplayServer.WindowMode.Maximized => "maximised",
                        _ => "windowed",
                    },
                    adjust: CycleWindowMode),

                new MenuItem("Resolution",
                    choose: _ => CycleResolution(1),
                    value: () =>
                    {
                        Vector2I size = DisplayServer.WindowGetSize();
                        return $"{size.X} x {size.Y}";
                    },
                    adjust: CycleResolution),

                new MenuItem("V-sync",
                    choose: _ => ToggleVsync(),
                    value: () => DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled
                        ? "off" : "on",
                    adjust: _ => ToggleVsync()),

                new MenuItem("Frame cap",
                    choose: _ => CycleFrameCap(1),
                    value: () => Engine.MaxFps == 0 ? "unlimited" : $"{Engine.MaxFps}",
                    adjust: CycleFrameCap),

                new MenuItem("Antialiasing",
                    choose: _ => CycleMsaa(1),
                    value: () => MsaaLabel(),
                    adjust: CycleMsaa),

                new MenuItem("Glow",
                    choose: _ => ToggleGlow(),
                    value: () => GlowEnabled() ? "on" : "off",
                    adjust: _ => ToggleGlow()),

                new MenuItem("Back", ui => ui.Show(UiScreen.Pause)),
            };
        }

        protected override string Title => "GRAPHICS";

        protected override string Subtitle => "changes apply at once; not saved between runs";

        protected override string Footer =>
            "↑/↓ select   ·   ←/→ or ENTER change   ·   ESC close";

        protected override float MinWidth => 560f;

        protected override IReadOnlyList<MenuItem> Items => _items;

        /// <summary>Persist after any change, so there is no apply step to forget.</summary>
        public override void Step(GameUi ui)
        {
            base.Step(ui);
            GameSettings.Save(GlowEnabled());
        }

        private static void CycleWindowMode(int direction)
        {
            DisplayServer.WindowMode[] modes =
            {
                DisplayServer.WindowMode.Windowed,
                DisplayServer.WindowMode.Maximized,
                DisplayServer.WindowMode.Fullscreen,
            };

            int index = Array.IndexOf(modes, DisplayServer.WindowGetMode());
            if (index < 0) index = 0;
            DisplayServer.WindowSetMode(modes[Wrap(index + direction, modes.Length)]);
        }

        private static void CycleResolution(int direction)
        {
            // Only meaningful windowed; fullscreen owns the display's own size.
            if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
                return;

            Vector2I current = DisplayServer.WindowGetSize();
            int index = Array.FindIndex(Resolutions, r => r == current);
            if (index < 0) index = 0;
            DisplayServer.WindowSetSize(Resolutions[Wrap(index + direction, Resolutions.Length)]);
        }

        private static void ToggleVsync()
        {
            bool on = DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled;
            DisplayServer.WindowSetVsyncMode(on
                ? DisplayServer.VSyncMode.Disabled
                : DisplayServer.VSyncMode.Enabled);
        }

        private static void CycleFrameCap(int direction)
        {
            int index = Array.IndexOf(FrameCaps, Engine.MaxFps);
            if (index < 0) index = 0;
            Engine.MaxFps = FrameCaps[Wrap(index + direction, FrameCaps.Length)];
        }

        private static void CycleMsaa(int direction)
        {
            var viewport = (SceneTree)Engine.GetMainLoop();
            Viewport root = viewport.Root;

            Viewport.Msaa[] levels =
            {
                Viewport.Msaa.Disabled,
                Viewport.Msaa.Msaa2X,
                Viewport.Msaa.Msaa4X,
                Viewport.Msaa.Msaa8X,
            };

            int index = Array.IndexOf(levels, root.Msaa3D);
            if (index < 0) index = 0;
            root.Msaa3D = levels[Wrap(index + direction, levels.Length)];
        }

        private static string MsaaLabel()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            return tree.Root.Msaa3D switch
            {
                Viewport.Msaa.Msaa2X => "2x",
                Viewport.Msaa.Msaa4X => "4x",
                Viewport.Msaa.Msaa8X => "8x",
                _ => "off",
            };
        }

        private static Godot.Environment? WorldEnvironment()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            return tree.Root.World3D?.Environment ?? tree.Root.GetCamera3D()?.Environment;
        }

        private static bool GlowEnabled() => WorldEnvironment()?.GlowEnabled ?? false;

        private static void ToggleGlow()
        {
            Godot.Environment? environment = WorldEnvironment();
            if (environment != null)
                environment.GlowEnabled = !environment.GlowEnabled;
        }

        private static int Wrap(int index, int count) => ((index % count) + count) % count;
    }
}
