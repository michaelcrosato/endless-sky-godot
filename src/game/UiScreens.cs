using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// Shared layout for a modal screen: dimmed world, centred panel, title, body,
    /// footer hint.
    /// </summary>
    public abstract partial class UiPanelScreen : Control
    {
        protected VBoxContainer Column = null!;

        public override void _Ready()
        {
            // The screen itself has to fill the viewport before anything inside it can
            // be centred against it.
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(UiTheme.Dimmer());

            CenterContainer centre = UiTheme.Centred();
            AddChild(centre);

            PanelContainer panel = UiTheme.Panel();
            centre.AddChild(panel);

            Column = new VBoxContainer { CustomMinimumSize = new Vector2(MinWidth, 0f) };
            panel.AddChild(Column);

            Column.AddChild(UiTheme.Title(Title));
            if (Subtitle.Length > 0)
                Column.AddChild(UiTheme.Heading(Subtitle));

            Column.AddChild(new HSeparator());
            BuildBody();
            Column.AddChild(new HSeparator());
            Column.AddChild(UiTheme.Text(Footer, 13, UiTheme.Muted));
        }

        protected abstract string Title { get; }

        protected virtual string Subtitle => string.Empty;

        protected virtual string Footer => "ESC close";

        protected virtual float MinWidth => 660f;

        protected abstract void BuildBody();

        /// <summary>Adds a block of lines as one label, which keeps columns aligned.</summary>
        protected void Lines(IEnumerable<string> lines, Color? colour = null, int size = 14) =>
            Column.AddChild(UiTheme.Text(string.Join("\n", lines), size, colour));
    }

    /// <summary>
    /// Every binding the game listens for.
    /// </summary>
    /// <remarks>
    /// Written as data rather than prose so it can be rendered here AND checked against
    /// what the game actually reads. A controls screen that drifts from the bindings is
    /// worse than none, because a player trusts it.
    /// </remarks>
    public partial class ControlsScreen : UiPanelScreen
    {
        public static readonly (string Keys, string Does, string Where)[] Bindings =
        {
            ("W / Up", "thrust", "flight"),
            ("S / Down", "retrograde brake", "flight"),
            ("A / D, Left / Right", "turn", "flight"),
            ("Mouse wheel", "zoom", "flight"),
            ("Space", "fire weapons", "flight"),
            ("L", "autopilot to a planet; press again to cycle", "flight"),
            ("J", "jump to the targeted system", "flight"),
            ("G / H", "escorts: gather / hold position", "flight"),
            ("V / F", "escorts: follow / attack nearest hostile", "flight"),
            ("M", "galaxy map", "anywhere"),
            ("I", "status: ship, fleet, cargo, missions", "anywhere"),
            ("F1", "this screen", "anywhere"),
            ("F2", "graphics options", "anywhere"),
            ("F3", "dismiss the opening tutorial", "anywhere"),
            ("Esc", "pause menu, or close a screen", "anywhere"),
            ("Tab", "switch counter", "landed"),
            ("Up / Down", "select", "landed"),
            ("Left / Right", "select ship at the outfitter", "landed"),
            ("B", "buy, accept a job, or hand it in", "landed"),
            ("N", "sell, or abandon a job", "landed"),
            ("D", "depart", "landed"),
            ("Enter / Space", "answer the offer", "mission offer"),
            ("Esc", "decline the offer", "mission offer"),
            ("Up / Down", "cycle destination", "map"),
            ("Enter", "set course, then J to jump", "map"),
        };

        protected override string Title => "CONTROLS";

        protected override string Subtitle => "flight is planar; the camera looks down";

        protected override void BuildBody()
        {
            var columns = new HBoxContainer();
            columns.AddThemeConstantOverride("separation", 40);
            Column.AddChild(columns);
            var flight = new VBoxContainer();
            var menus = new VBoxContainer();
            columns.AddChild(flight);
            columns.AddChild(menus);
            foreach (IGrouping<string, (string Keys, string Does, string Where)> group in
                     Bindings.GroupBy(b => b.Where))
            {
                VBoxContainer column = group.Key is "flight" or "landed" ? flight : menus;
                column.AddChild(UiTheme.Heading(group.Key.ToUpperInvariant()));
                column.AddChild(UiTheme.Text(string.Join("\n",
                    group.Select(b => UiTheme.Row(b.Keys, b.Does, 24)))));
                column.AddChild(UiTheme.Text(" ", 6));
            }
        }
    }

    /// <summary>
    /// What the player has and what they are doing: the answer to "where am I up to".
    /// </summary>
    public partial class StatusScreen : UiPanelScreen
    {
        private readonly PlayerState _player;
        private readonly MissionLog _missions;
        private readonly GameData _universe;
        private readonly Ship? _ship;

        public StatusScreen(PlayerState player, MissionLog missions, GameData universe, Ship? ship)
        {
            _player = player;
            _missions = missions;
            _universe = universe;
            _ship = ship;
        }

        protected override string Title => "STATUS";

        protected override string Subtitle =>
            $"{_player.Date:d MMMM yyyy}   ·   " +
            (_player.CurrentPlanet != null
                ? $"landed on {_player.CurrentPlanet.Name}"
                : $"in flight, {_player.CurrentSystem?.Name ?? "deep space"}");

        protected override float MinWidth => 720f;

        protected override void BuildBody()
        {
            Column.AddChild(UiTheme.Heading("FLAGSHIP"));
            if (_ship is null)
            {
                Lines(new[] { "   (none)" }, UiTheme.Muted);
            }
            else
            {
                Lines(new[]
                {
                    UiTheme.Row("  " + _ship.Definition.DisplayName,
                                $"{Bar(_ship.Shields, _ship.MaxShields)} shields   " +
                                $"{Bar(_ship.Hull, _ship.MaxHull)} hull", 24),
                    UiTheme.Row("  fuel",
                                $"{_ship.Fuel:0} / {_ship.MaxFuel:0}" +
                                (_ship.MaxFuel > 0
                                    ? $"   ({(int)(_ship.Fuel / Math.Max(1.0, JumpCost()))} jumps)"
                                    : ""), 24),
                    UiTheme.Row("  energy", $"{_ship.Energy:0} / {_ship.MaxEnergy:0}", 24),
                    UiTheme.Row("  heat",
                                $"{100.0 * _ship.Heat / Math.Max(1.0, _ship.MaxHeat):0}%", 24),
                    UiTheme.Row("  crew", $"{_ship.Crew}", 24),
                });
            }

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("ACCOUNT AND FLEET"));
            Lines(new[]
            {
                UiTheme.Row("  credits", $"{_player.Credits:n0}", 24),
                UiTheme.Row("  ships", $"{_player.Fleet.Ships.Count}" +
                    (_player.Fleet.Ships.Count > 1
                        ? $" ({_player.Fleet.ActiveShips.Count()} flying)" : ""), 24),
                UiTheme.Row("  daily salaries", $"{_player.Fleet.DailySalaries():n0}", 24),
                UiTheme.Row("  cargo",
                    $"{_player.Fleet.CargoUsed()} / {_player.Fleet.CargoCapacity()} tons", 24),
            });

            var hold = _player.Fleet.AllCargo
                .SelectMany(c => c.Commodities)
                .GroupBy(e => e.Key, e => e.Value)
                .Select(g => (Name: g.Key, Tons: g.Sum()))
                .Where(e => e.Tons > 0)
                .OrderByDescending(e => e.Tons)
                .ToList();

            if (hold.Count > 0)
                Lines(hold.Select(e => UiTheme.Row($"    {e.Name}", $"{e.Tons} t", 24)),
                      UiTheme.Muted);

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("MISSIONS"));

            if (_missions.Active.Count == 0)
            {
                Lines(new[] { "  (none — take a job from a spaceport's JOBS counter)" }, UiTheme.Muted);
            }
            else
            {
                Lines(_missions.Active.Select(m =>
                {
                    string name = TextSubstitution.NameOf(m.Mission, _player, _universe);
                    string where = m.Mission.ResolveDestination(_universe, _player.CurrentSystem?.Name)
                                   is string d ? $"→ {d}" : "";
                    string due = m.Deadline.HasValue ? $"  by {m.Deadline:d MMM yyyy}" : "";
                    return UiTheme.Row("  " + name, where + due, 34);
                }));
            }

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("EXPLORATION"));
            Lines(new[]
            {
                UiTheme.Row("  systems visited",
                    $"{_player.VisitedSystems.Count} of {_universe.Systems.Count}", 24),
                UiTheme.Row("  worlds visited", $"{_player.VisitedPlanets.Count}", 24),
            });
        }

        private double JumpCost() => _ship?.JumpFuelCost > 0 ? _ship.JumpFuelCost : 100.0;

        /// <summary>A short text bar, which reads faster than a bare number.</summary>
        private static string Bar(double value, double max, int width = 10)
        {
            if (max <= 0.0)
                return "n/a";

            int filled = (int)Math.Round(width * Math.Clamp(value / max, 0.0, 1.0));
            return new string('=', filled) + new string('.', width - filled) +
                   $" {100.0 * Math.Clamp(value / max, 0.0, 1.0):0}%";
        }
    }

    /// <summary>
    /// The one page a new player needs. Shown once on the first flight, and available
    /// afterwards from the pause menu.
    /// </summary>
    public partial class TutorialScreen : UiPanelScreen
    {
        protected override string Title => "ENDLESS SKY 3D";

        protected override string Subtitle => "a short page, then you are on your own";

        protected override string Footer => "ESC begin   ·   F1 controls   ·   this page is in the pause menu";

        protected override void BuildBody()
        {
            Column.AddChild(UiTheme.Heading("WHERE YOU ARE"));
            Lines(new[]
            {
                "  You are in the Rutilicus system beside New Boston, flying a Shuttle,",
                "  480,000 credits in debt. The galaxy is the real Endless Sky one:",
                "  694 systems, 902 ships, and traffic that goes about its own business.",
            });

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("FLYING"));
            Lines(new[]
            {
                "  W thrusts, A and D turn. There is no brake: S turns you around so",
                "  your engine faces the way you are going. Momentum is the whole game.",
            });

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("MAKING MONEY"));
            Lines(new[]
            {
                "  Press L to fly to a planet and land. Press it again to cycle worlds.",
                "  TAB cycles the counters: TRADE buys and sells cargo, JOBS lists work,",
                "  SHIPYARD and OUTFITTER sell hulls and equipment. D departs.",
                "  Buy a commodity where it is cheap, carry it somewhere it is dear.",
            });

            Column.AddChild(UiTheme.Text(" ", 6));
            Column.AddChild(UiTheme.Heading("GOING FURTHER"));
            Lines(new[]
            {
                "  M opens the galaxy map. Pick a linked system, then press J to jump —",
                "  it takes fuel, and you refuel by landing at a spaceport.",
                "  I shows what you own and what you are carrying.",
            });
        }
    }
}
