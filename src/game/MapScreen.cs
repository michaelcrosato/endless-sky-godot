using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The galaxy map: where you are, what you can reach, and where you have been.
    /// </summary>
    /// <remarks>
    /// This is how a player picks a destination. Endless Sky's map is the game's
    /// primary navigation interface, and without one the only way to travel is to
    /// press J and accept whichever link happens to be best aligned with the nose —
    /// which is not navigation, it is guessing.
    ///
    /// What is drawn is deliberately limited to what the player has earned: systems
    /// they have visited, plus the immediate neighbours of those, so the map fills in
    /// as they explore rather than handing over the galaxy at once. Upstream does the
    /// same thing for the same reason.
    ///
    /// Drawn with _Draw rather than assembled from nodes: it is a few hundred lines and
    /// dots that change every frame as the selection moves, and a node per system would
    /// be a thousand Controls to rebuild on every keypress.
    /// </remarks>
    public partial class MapScreen : UiPanelScreen, IUiScreen
    {
        private readonly PlayerState _player;
        private readonly GameData _universe;
        private readonly Ship? _ship;
        private readonly Action<StarSystem> _chosen;

        private readonly List<StarSystem> _reachable = new List<StarSystem>();
        private MapCanvas _canvas = null!;
        private Label _detail = null!;
        private int _selected;

        public MapScreen(PlayerState player, GameData universe, Ship? ship,
                         Action<StarSystem> chosen)
        {
            _player = player;
            _universe = universe;
            _ship = ship;
            _chosen = chosen;
        }

        protected override string Title => "GALAXY MAP";

        protected override string Subtitle =>
            $"{_player.CurrentSystem?.Name ?? "unknown"}   ·   " +
            $"{_player.VisitedSystems.Count} of {_universe.Systems.Count} systems visited";

        protected override string Footer =>
            "↑/↓ cycle destination   ·   ENTER set course   ·   ESC close   ·   then J to jump";

        protected override float MinWidth => 900f;

        protected override void BuildBody()
        {
            // Somewhere to jump to: linked neighbours, plus anywhere a jump drive can
            // reach. Sorted so the order is stable between openings.
            StarSystem? here = _player.CurrentSystem;
            if (here != null)
            {
                IEnumerable<StarSystem> options = _ship != null
                    ? _ship.ReachableSystems(_universe)
                    : here.Links
                        .Where(_universe.Systems.ContainsKey)
                        .Select(name => _universe.Systems[name]);

                _reachable.AddRange(options.OrderBy(s => s.Name, StringComparer.Ordinal));
            }

            _canvas = new MapCanvas(_player, _universe, _reachable)
            {
                CustomMinimumSize = new Vector2(MinWidth, 420f),
            };
            Column.AddChild(_canvas);

            Column.AddChild(new HSeparator());
            _detail = UiTheme.Text("", 14);
            Column.AddChild(_detail);

            Refresh();
        }

        public void Step(GameUi ui)
        {
            if (_reachable.Count == 0)
                return;

            if (ui.Pressed(Key.Up) || ui.Pressed(Key.Left))
            {
                _selected = (_selected + _reachable.Count - 1) % _reachable.Count;
                Refresh();
            }

            if (ui.Pressed(Key.Down) || ui.Pressed(Key.Right))
            {
                _selected = (_selected + 1) % _reachable.Count;
                Refresh();
            }

            if (ui.Pressed(Key.Enter) || ui.Pressed(Key.KpEnter))
                _chosen(_reachable[_selected]);
        }

        private void Refresh()
        {
            _canvas.Selected = _reachable.Count > 0 ? _reachable[_selected] : null;
            _canvas.QueueRedraw();

            if (_reachable.Count == 0)
            {
                _detail.Text = "  Nowhere to jump from here — no linked system, and no jump drive.";
                return;
            }

            StarSystem target = _reachable[_selected];
            bool linked = _player.CurrentSystem?.Links.Contains(target.Name) == true;
            double fuel = _ship?.JumpFuelCost ?? 0.0;
            bool enough = _ship == null || _ship.Fuel >= fuel;

            string government = target.Government is { Length: > 0 } g ? g : "unaligned";
            string known = _player.VisitedSystems.Contains(target.Name) ? "visited" : "unexplored";

            var worlds = target.AllObjects()
                .Where(o => o.Planet is { IsInhabited: true })
                .Select(o => o.PlanetName!)
                .ToList();

            _detail.Text = string.Join("\n", new[]
            {
                UiTheme.Row($"  {target.Name}",
                    $"{government}   ·   {known}   ·   {(linked ? "hyperspace link" : "jump drive range")}", 26),
                UiTheme.Row("  fuel for the jump",
                    $"{fuel:0} of {_ship?.Fuel ?? 0:0}" + (enough ? "" : "   NOT ENOUGH FUEL"), 26),
                UiTheme.Row("  worlds",
                    worlds.Count > 0 ? string.Join(", ", worlds.Take(4)) : "none inhabited", 26),
                $"  destination {_selected + 1} of {_reachable.Count}",
            });
        }
    }

    /// <summary>Draws the map itself: links, systems, the player, the selection.</summary>
    internal partial class MapCanvas : Control
    {
        private readonly PlayerState _player;
        private readonly GameData _universe;
        private readonly List<StarSystem> _reachable;

        public StarSystem? Selected { get; set; }

        public MapCanvas(PlayerState player, GameData universe, List<StarSystem> reachable)
        {
            _player = player;
            _universe = universe;
            _reachable = reachable;
        }

        public override void _Draw()
        {
            StarSystem? here = _player.CurrentSystem;
            if (here is null)
                return;

            // Only what the player has earned: visited systems and their neighbours.
            var shown = new Dictionary<string, StarSystem>(StringComparer.Ordinal);
            foreach (string name in _player.VisitedSystems)
            {
                if (!_universe.Systems.TryGetValue(name, out StarSystem? visited))
                    continue;

                shown[name] = visited;
                foreach (string link in visited.Links)
                    if (_universe.Systems.TryGetValue(link, out StarSystem? neighbour))
                        shown[link] = neighbour;
            }

            shown[here.Name] = here;
            foreach (StarSystem reachable in _reachable)
                shown[reachable.Name] = reachable;

            // Fit what is shown into the panel, centred on the player.
            Vector2 size = Size;
            float span = 1f;
            foreach (StarSystem system in shown.Values)
            {
                Vector2 offset = Offset(system, here);
                span = Math.Max(span, Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)));
            }

            float scale = Math.Min(size.X, size.Y) * 0.45f / span;
            Vector2 centre = size * 0.5f;

            Vector2 Place(StarSystem system) => centre + Offset(system, here) * scale;

            // Links first, so systems sit on top of them.
            var drawn = new HashSet<string>(StringComparer.Ordinal);
            foreach (StarSystem system in shown.Values)
            {
                foreach (string link in system.Links)
                {
                    if (!shown.TryGetValue(link, out StarSystem? other))
                        continue;

                    string key = string.CompareOrdinal(system.Name, link) < 0
                        ? $"{system.Name}|{link}" : $"{link}|{system.Name}";
                    if (!drawn.Add(key))
                        continue;

                    bool fromHere = ReferenceEquals(system, here) || ReferenceEquals(other, here);
                    DrawLine(Place(system), Place(other),
                        fromHere ? new Color(0.45f, 0.65f, 0.85f, 0.75f)
                                 : new Color(0.30f, 0.38f, 0.48f, 0.45f),
                        fromHere ? 1.6f : 1f);
                }
            }

            foreach (StarSystem system in shown.Values)
            {
                Vector2 at = Place(system);
                bool isHere = ReferenceEquals(system, here);
                bool isSelected = Selected != null && ReferenceEquals(system, Selected);
                bool visited = _player.VisitedSystems.Contains(system.Name);

                Color colour = isHere ? new Color(0.55f, 0.9f, 0.65f)
                    : isSelected ? new Color(1f, 0.85f, 0.45f)
                    : visited ? new Color(0.72f, 0.80f, 0.90f)
                    : new Color(0.40f, 0.46f, 0.55f);

                DrawCircle(at, isHere || isSelected ? 5f : 3f, colour);

                if (isSelected)
                    DrawArc(at, 10f, 0f, Mathf.Tau, 24, colour, 1.5f);

                // Naming every system turns the map into a wall of text, so only the
                // ones a player is deciding between are labelled.
                if (isHere || isSelected || _reachable.Contains(system))
                {
                    DrawString(ThemeDB.FallbackFont, at + new Vector2(8f, -6f), system.Name,
                        HorizontalAlignment.Left, -1, 13, colour);
                }
            }
        }

        /// <summary>Map position relative to the player, with Y flipped for the screen.</summary>
        private static Vector2 Offset(StarSystem system, StarSystem here) =>
            new Vector2((float)(system.MapPosition.X - here.MapPosition.X),
                        (float)(system.MapPosition.Y - here.MapPosition.Y));
    }
}
