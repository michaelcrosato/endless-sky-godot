using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The landed screen: menu-driven per the directive (no walking sims).
    /// Shows the planet's spaceport, a working commodity market driven by the
    /// real per-system prices, and the shipyard stock. The sim is frozen
    /// while this is open; Depart hands control back to FlightWorld.
    ///
    /// Trade math: buy/sell at the local quote, 5 tons per keypress, bounded
    /// by credits, cargo space, and what's in the hold. Selling cargo you
    /// don't have, or buying past a full hold, simply does nothing.
    /// </summary>
    public partial class LandedOverlay : CanvasLayer
    {
        private const int TonsPerPress = 5;

        private Ship _ship = null!;
        private Planet _planet = null!;
        private string _systemName = "";
        private TradeData _trade = null!;
        private List<TradeQuote> _quotes = new();
        private IReadOnlyList<string> _shipyardStock = Array.Empty<string>();

        private Label _marketLabel = null!;
        private Label _statusLine = null!;
        private int _selected;
        private bool _upWas, _downWas, _buyWas, _sellWas, _departWas;

        public long Credits { get; set; }

        public bool PlanetHasSpaceport => _planet.HasSpaceport;

        public event Action? Departed;

        public static LandedOverlay Open(Node parent, Ship ship, Planet planet, string systemName,
            GameData universe, long credits)
        {
            var overlay = new LandedOverlay
            {
                Name = "Landed",
                _ship = ship,
                _planet = planet,
                _systemName = systemName,
                _trade = universe.Trade,
                Credits = credits,
            };
            overlay._quotes = universe.Trade.Quotes(systemName)
                .Where(q => universe.Trade.Commodities.TryGetValue(q.Commodity, out Commodity? c) && c.IsTradeable)
                .OrderBy(q => q.Commodity, StringComparer.Ordinal)
                .ToList();
            overlay._shipyardStock =
                Planet.Stock(planet.Shipyards, universe.Shipyards).OrderBy(s => s, StringComparer.Ordinal).ToList();
            parent.AddChild(overlay);
            return overlay;
        }

        public override void _Ready()
        {
            var dim = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.55f),
            };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            var panel = new PanelContainer();
            panel.SetAnchorsPreset(Control.LayoutPreset.Center);
            var style = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.07f, 0.11f, 0.95f),
                BorderColor = new Color(0.35f, 0.55f, 0.75f, 0.8f),
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 22, ContentMarginRight = 22,
                ContentMarginTop = 16, ContentMarginBottom = 16,
            };
            style.SetBorderWidthAll(1);
            panel.AddThemeStyleboxOverride("panel", style);
            AddChild(panel);

            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 6);
            panel.AddChild(column);

            var title = new Label { Text = $"{_planet.Name.ToUpperInvariant()} — {_systemName}" };
            title.AddThemeFontSizeOverride("font_size", 24);
            title.AddThemeColorOverride("font_color", new Color(0.88f, 0.93f, 1.0f));
            column.AddChild(title);

            string government = string.IsNullOrEmpty(_planet.Government) ? "Independent" : _planet.Government!;
            var subtitle = new Label
            {
                Text = $"{government} world · {(_planet.HasSpaceport ? "spaceport" : "no spaceport")}" +
                       $"{(_planet.HasShipyard ? " · shipyard" : "")}{(_planet.HasOutfitter ? " · outfitter" : "")}",
            };
            subtitle.AddThemeFontSizeOverride("font_size", 14);
            subtitle.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.75f));
            column.AddChild(subtitle);

            column.AddChild(new HSeparator());

            var marketHeader = new Label { Text = "COMMODITY MARKET" };
            marketHeader.AddThemeFontSizeOverride("font_size", 13);
            marketHeader.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 0.85f));
            column.AddChild(marketHeader);

            _marketLabel = new Label();
            _marketLabel.AddThemeFontSizeOverride("font_size", 15);
            column.AddChild(_marketLabel);

            if (_shipyardStock.Count > 0)
            {
                column.AddChild(new HSeparator());
                var yardHeader = new Label { Text = "SHIPYARD" };
                yardHeader.AddThemeFontSizeOverride("font_size", 13);
                yardHeader.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 0.85f));
                column.AddChild(yardHeader);
                var yard = new Label { Text = string.Join("  ·  ", _shipyardStock) };
                yard.AddThemeFontSizeOverride("font_size", 14);
                yard.AddThemeColorOverride("font_color", new Color(0.72f, 0.78f, 0.85f));
                column.AddChild(yard);
            }

            column.AddChild(new HSeparator());
            _statusLine = new Label();
            _statusLine.AddThemeFontSizeOverride("font_size", 14);
            _statusLine.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.75f));
            column.AddChild(_statusLine);

            RefreshMarket();
        }

        public override void _Process(double delta)
        {
            bool up = Input.IsPhysicalKeyPressed(Key.Up) || Input.IsPhysicalKeyPressed(Key.W);
            bool down = Input.IsPhysicalKeyPressed(Key.Down) || Input.IsPhysicalKeyPressed(Key.S);
            bool buy = Input.IsPhysicalKeyPressed(Key.B);
            bool sell = Input.IsPhysicalKeyPressed(Key.N);
            bool depart = Input.IsPhysicalKeyPressed(Key.D);

            if (up && !_upWas && _quotes.Count > 0)
            {
                _selected = (_selected + _quotes.Count - 1) % _quotes.Count;
                RefreshMarket();
            }

            if (down && !_downWas && _quotes.Count > 0)
            {
                _selected = (_selected + 1) % _quotes.Count;
                RefreshMarket();
            }

            if (buy && !_buyWas)
            {
                Buy();
            }

            if (sell && !_sellWas)
            {
                Sell();
            }

            if (depart && !_departWas)
            {
                Departed?.Invoke();
            }

            (_upWas, _downWas, _buyWas, _sellWas, _departWas) = (up, down, buy, sell, depart);
        }

        private void Buy()
        {
            if (_selected >= _quotes.Count)
            {
                return;
            }

            TradeQuote quote = _quotes[_selected];
            int affordable = quote.Price > 0 ? (int)Math.Min(TonsPerPress, Credits / quote.Price) : TonsPerPress;
            int bought = _ship.Cargo.Add(quote.Commodity, Math.Max(0, affordable));
            Credits -= (long)bought * quote.Price;
            RefreshMarket();
        }

        private void Sell()
        {
            if (_selected >= _quotes.Count)
            {
                return;
            }

            TradeQuote quote = _quotes[_selected];
            int sold = _ship.Cargo.Remove(quote.Commodity, TonsPerPress);
            Credits += (long)sold * quote.Price;
            RefreshMarket();
        }

        private void RefreshMarket()
        {
            var lines = new List<string>();
            for (int i = 0; i < _quotes.Count; i++)
            {
                TradeQuote quote = _quotes[i];
                string cursor = i == _selected ? "▶ " : "   ";
                int held = _ship.Cargo.Count(quote.Commodity);
                lines.Add($"{cursor}{quote.Commodity,-16} {quote.Price,6} cr/t   hold {held,3} t");
            }

            if (lines.Count == 0)
            {
                lines.Add("   (no market on this world)");
            }

            _marketLabel.Text = string.Join("\n", lines);
            _statusLine.Text =
                $"credits {Credits:n0}   cargo {_ship.Cargo.Used}/{_ship.Cargo.Capacity} t   fuel {_ship.Fuel:0}\n" +
                "↑/↓ select · B buy 5t · N sell 5t · D depart";
        }
    }
}
