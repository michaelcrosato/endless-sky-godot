using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>
    /// The landed screen: menu-driven per the directive (no walking sims). Four
    /// counters — the commodity market, the shipyard, the outfitter and the job
    /// board — over the planet the player set down on. The simulation is frozen
    /// while this is open; Depart hands control back to FlightWorld.
    /// </summary>
    /// <remarks>
    /// Everything here is a thin view over the simulation. The market moves cargo
    /// through the fleet's hold, the two shops go through <see cref="Trading"/> so
    /// they obey the same stock, capacity and money rules a test does, and the job
    /// board goes through <see cref="MissionLog"/>. Nothing on this screen knows a
    /// rule of its own, which is why a refusal here reads the same as a refusal
    /// anywhere else.
    ///
    /// The shipyard used to be a read-only line of text listing what was for sale,
    /// which is the shape of the whole gap this closes: the stock lists, the
    /// installation rules and the mission gates were all implemented and none of them
    /// could be reached from inside the game.
    /// </remarks>
    public partial class LandedOverlay : CanvasLayer
    {
        private const int TonsPerPress = 5;

        /// <summary>The counters this screen offers, in the order Tab cycles them.</summary>
        private enum Counter
        {
            Trade,
            Shipyard,
            Outfitter,
            Jobs,
        }

        private PlayerState _player = null!;
        private MissionLog _missions = null!;
        private GameData _universe = null!;
        private Planet _planet = null!;
        private string _systemName = "";

        private List<TradeQuote> _quotes = new();
        private List<string> _shipyardStock = new();
        private List<string> _outfitterStock = new();
        private List<Mission> _jobs = new();

        private Counter _counter = Counter.Trade;
        private readonly Dictionary<Counter, int> _selected = new();
        private string _message = "";

        private Label _listLabel = null!;
        private Label _tabLabel = null!;
        private Label _statusLine = null!;

        /// <summary>Credits, kept for the caller that still tracks them separately.</summary>
        public long Credits => _player.Credits;

        public bool PlanetHasSpaceport => _planet.HasSpaceport;

        public event Action? Departed;

        /// <summary>
        /// Which counter to open on. Only used to capture a screen that would
        /// otherwise need a keypress to reach.
        /// </summary>
        public static int OpenOnCounter { get; set; }

        public static LandedOverlay Open(Node parent, PlayerState player, MissionLog missions,
            Planet planet, string systemName, GameData universe)
        {
            var overlay = new LandedOverlay
            {
                _counter = (Counter)Math.Clamp(OpenOnCounter, 0, 3),
                Name = "Landed",
                _player = player,
                _missions = missions,
                _planet = planet,
                _systemName = systemName,
                _universe = universe,
            };

            parent.AddChild(overlay);
            return overlay;
        }

        public override void _Ready()
        {
            RefreshStock();

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(dim);

            // A CenterContainer over the whole screen, rather than the Center anchor
            // preset on the panel itself: that preset captures offsets from the panel's
            // size at the moment it is applied, which is before the contents below have
            // given it one, so the panel ends up pinned to a corner.
            var centre = new CenterContainer();
            centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(centre);

            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.07f, 0.11f, 0.95f),
                BorderColor = new Color(0.35f, 0.55f, 0.75f, 0.8f),
                BorderWidthLeft = 1, BorderWidthRight = 1,
                BorderWidthTop = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 22, ContentMarginRight = 22,
                ContentMarginTop = 16, ContentMarginBottom = 16,
            });
            centre.AddChild(panel);

            var column = new VBoxContainer { CustomMinimumSize = new Vector2(660f, 0f) };
            panel.AddChild(column);

            var title = new Label { Text = $"{_planet.Name.ToUpperInvariant()}  ·  {_systemName}" };
            title.AddThemeFontSizeOverride("font_size", 22);
            column.AddChild(title);

            var subtitle = new Label
            {
                Text = _planet.HasSpaceport ? "SPACEPORT" : "NO SPACEPORT",
            };
            subtitle.AddThemeFontSizeOverride("font_size", 12);
            subtitle.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.75f));
            column.AddChild(subtitle);

            column.AddChild(new HSeparator());

            _tabLabel = new Label();
            _tabLabel.AddThemeFontSizeOverride("font_size", 14);
            column.AddChild(_tabLabel);

            column.AddChild(new HSeparator());

            _listLabel = new Label();
            _listLabel.AddThemeFontSizeOverride("font_size", 14);
            _listLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.87f, 0.92f));
            column.AddChild(_listLabel);

            column.AddChild(new HSeparator());

            _statusLine = new Label();
            _statusLine.AddThemeFontSizeOverride("font_size", 14);
            _statusLine.AddThemeColorOverride("font_color", new Color(0.55f, 0.65f, 0.75f));
            column.AddChild(_statusLine);

            Refresh();
        }

        /// <summary>Re-reads what this world has on offer.</summary>
        private void RefreshStock()
        {
            _shipyardStock = Trading.ShipsFor(_universe, _planet)
                .Where(_universe.Ships.ContainsKey)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            _outfitterStock = Trading.OutfitsFor(_universe, _planet)
                .Where(_universe.Outfits.ContainsKey)
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToList();

            // The JOB board shows jobs. Missions declare which counter offers them, and
            // asking for everything put boarding missions, shipyard missions and the
            // ones that fire on entering a system on the same list as the work.
            _jobs = _missions.Available(_universe, MissionLocation.Job)
                .OrderBy(m => m.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        // The offer dialogue, while one is being shown. It owns the keyboard: the
        // counter behind it must not also read the arrow keys.
        private ConversationRunner? _talk;
        private Mission? _talkingAbout;
        private string? _dialog;
        private int _talkChoice;

        public bool IsOfferingMission => _talk != null || _dialog != null;

        /// <summary>Called by the shell while the port owns this frame's input.</summary>
        public void Step(GameUi ui)
        {
            if (IsOfferingMission)
            {
                StepOffer(ui);
                return;
            }

            bool up = ui.Pressed(Key.Up) | ui.Pressed(Key.W);
            bool down = ui.Pressed(Key.Down) | ui.Pressed(Key.S);
            bool buy = ui.Pressed(Key.B);
            bool sell = ui.Pressed(Key.N);
            bool depart = ui.Pressed(Key.D);
            bool tab = ui.Pressed(Key.Tab);

            if (tab)
            {
                _counter = (Counter)(((int)_counter + 1) % 4);
                _message = "";
                Refresh();
            }

            int count = CurrentCount();
            if (up && count > 0)
            {
                Select((Selection() + count - 1) % count);
            }

            if (down && count > 0)
            {
                Select((Selection() + 1) % count);
            }

            if (buy)
            {
                Buy();
                if (IsOfferingMission)
                    return;
            }

            if (sell)
            {
                Sell();
            }

            if (depart)
            {
                Departed?.Invoke();
            }
        }

        /// <summary>
        /// Puts an offer's dialogue on screen. Returns false when there is nothing to
        /// show and the caller should simply take the job.
        /// </summary>
        private bool BeginOffer(Mission job, MissionAction? offer)
        {
            if (offer is null)
            {
                return false;
            }

            _talkingAbout = job;
            _talkChoice = 0;

            Conversation? talk = offer.InlineConversation;
            if (talk is null && offer.Conversation != null)
            {
                _universe.Conversations.TryGetValue(offer.Conversation, out talk);
            }

            if (talk != null)
            {
                _talk = new ConversationRunner(talk, _player.Conditions);
                Refresh();
                return true;
            }

            if (!string.IsNullOrEmpty(offer.Dialog))
            {
                _dialog = offer.Dialog;
                Refresh();
                return true;
            }

            _talkingAbout = null;
            return false;
        }

        /// <summary>Drives the offer dialogue. Enter answers; Esc walks away.</summary>
        private void StepOffer(GameUi ui)
        {
            bool up = ui.Pressed(Key.Up) | ui.Pressed(Key.W);
            bool down = ui.Pressed(Key.Down) | ui.Pressed(Key.S);
            bool enter = ui.Pressed(Key.Enter) | ui.Pressed(Key.KpEnter) | ui.Pressed(Key.Space);
            bool escape = ui.Pressed(Key.Escape);

            if (escape)
            {
                EndOffer(accepted: false);
            }
            else if (_dialog != null)
            {
                // A plain dialog is read and dismissed; saying yes is pressing on.
                if (enter)
                {
                    EndOffer(accepted: true);
                }
            }
            else if (_talk != null)
            {
                int options = _talk.Choices.Count;
                if (options > 0)
                {
                    if (up) { _talkChoice = (_talkChoice + options - 1) % options; Refresh(); }
                    if (down) { _talkChoice = (_talkChoice + 1) % options; Refresh(); }
                    if (enter) { _talk.Choose(_talkChoice); _talkChoice = 0; Refresh(); }
                }
                else if (enter)
                {
                    Refresh();
                }

                if (_talk.IsFinished)
                {
                    EndOffer(_talk.Outcome != ConversationOutcome.Decline);
                }
            }
        }

        /// <summary>Closes the dialogue and takes the job if the player agreed.</summary>
        private void EndOffer(bool accepted)
        {
            Mission? job = _talkingAbout;

            _talk = null;
            _dialog = null;
            _talkingAbout = null;

            if (job is null)
            {
                Refresh();
                return;
            }

            if (accepted)
            {
                AcceptOffered(job);
            }
            else
            {
                _missions.Decline(job);
                _message = $"declined: {job.DisplayName}";
                RefreshStock();
                Refresh();
            }
        }

        private void AcceptOffered(Mission job)
        {
            ActiveMission? taken = _missions.Accept(job);
            _message = taken != null ? $"accepted: {job.DisplayName}"
                : job.CargoType != null && !_player.Fleet.CanLoadMissionCargo(job.CargoTons, _player.CurrentSystem)
                    ? $"needs {job.CargoTons} t of cargo space; {_player.Fleet.CargoFree(_player.CurrentSystem)} t free here"
                    : "could not accept";
            RefreshStock();
            Refresh();
        }

        private int Selection() => _selected.TryGetValue(_counter, out int value) ? value : 0;

        private void Select(int index)
        {
            _selected[_counter] = index;
            Refresh();
        }

        private int CurrentCount() => _counter switch
        {
            // Jobs lists what is offered AND what is being carried, so the cursor has
            // to reach both -- otherwise the active rows are decoration and the
            // abandon key can only ever guess which one was meant.
            Counter.Jobs => _jobs.Count + _missions.Active.Count,
            Counter.Trade => _quotes.Count,
            Counter.Shipyard => _shipyardStock.Count,
            Counter.Outfitter => _outfitterStock.Count,
            _ => 0,
        };

        // --- Buying -----------------------------------------------------------------

        private void Buy()
        {
            int index = Selection();
            if (index >= CurrentCount())
            {
                return;
            }

            switch (_counter)
            {
                case Counter.Trade:
                {
                    TradeQuote quote = _quotes[index];
                    TradeResult result = Trading.BuyCommodity(_player, _universe, quote.Commodity,
                        TonsPerPress, out int bought);
                    _message = result == TradeResult.Ok
                        ? $"bought {bought} t of {quote.Commodity}"
                        : Explain(result);
                    break;
                }

                case Counter.Shipyard:
                {
                    string model = _shipyardStock[index];
                    TradeResult result = Trading.BuyShip(_player, _universe, model, out Ship? bought);
                    _message = result == TradeResult.Ok
                        ? $"bought a {model}"
                        : Explain(result);
                    break;
                }

                case Counter.Outfitter:
                {
                    string outfit = _outfitterStock[index];
                    Ship? flagship = _player.Fleet.Flagship;
                    TradeResult result = flagship is null
                        ? TradeResult.NotOwned
                        : Trading.BuyOutfit(_player, _universe, flagship, outfit);
                    _message = result == TradeResult.Ok ? $"installed {outfit}" : Explain(result);
                    break;
                }

                case Counter.Jobs:
                {
                    // The rows past the offered jobs are missions already in progress.
                    // On one of those, this key HANDS IT IN — which is the other half
                    // of a job and had no way to happen at all: nothing outside the
                    // test suite ever called MissionLog.Complete, so a player could
                    // accept work, carry it to the right world and stand on the ground
                    // beside the person waiting for it with no key that would finish
                    // it, and no payment ever arrived.
                    if (index >= _jobs.Count)
                    {
                        ActiveMission carrying = _missions.Active[index - _jobs.Count];
                        long before = _player.Credits;

                        if (_missions.Complete(carrying))
                        {
                            _message = $"delivered: {carrying.Mission.DisplayName} " +
                                       $"(+{_player.Credits - before:n0})";
                        }
                        else if (carrying.Destination != null &&
                                 carrying.Destination != _planet.Name)
                        {
                            // Say WHERE, not just no. "Cannot complete" on a world that
                            // looks right is indistinguishable from a broken button.
                            _message = $"hand that one in at {carrying.Destination}";
                        }
                        else
                        {
                            _message = "that job is not finished yet";
                        }

                        break;
                    }

                    Mission job = _jobs[index];

                    // Ask first. A mission's `on offer` action is where its dialogue
                    // lives; upstream shows it and takes the player's answer as the
                    // decision. Nothing ever fired the trigger, so every line of offer
                    // dialogue in the dataset went unread and every mission was accepted
                    // silently.
                    MissionAction? offer = _missions.Offer(job);
                    if (BeginOffer(job, offer))
                    {
                        break;
                    }

                    AcceptOffered(job);
                    break;
                }
            }

            Refresh();
        }

        private void Sell()
        {
            int index = Selection();
            if (index >= CurrentCount())
            {
                return;
            }

            switch (_counter)
            {
                case Counter.Trade:
                {
                    TradeQuote quote = _quotes[index];
                    TradeResult result = Trading.SellCommodity(_player, _universe, quote.Commodity,
                        TonsPerPress, out int sold, out long profit);
                    _message = result == TradeResult.Ok
                        ? $"sold {sold} t of {quote.Commodity} · profit {profit:+#,0;-#,0;0} cr" : Explain(result);
                    break;
                }

                case Counter.Shipyard:
                {
                    // Sells the flagship's model if the player has a spare of it, which
                    // is the only unambiguous thing this list can mean.
                    string model = _shipyardStock[index];
                    Ship? owned = _player.Fleet.Ships
                        .FirstOrDefault(s => s.Definition.DisplayName == model);

                    _message = owned is null
                        ? "you own none of those"
                        : Explain(Trading.SellShip(_player, owned), $"sold a {model}");
                    break;
                }

                case Counter.Outfitter:
                {
                    string name = _outfitterStock[index];
                    Ship? flagship = _player.Fleet.Flagship;
                    Outfit? outfit = _universe.Outfits.TryGetValue(name, out Outfit? found) ? found : null;

                    _message = flagship is null || outfit is null
                        ? "nothing to sell"
                        : Explain(Trading.SellOutfit(_player, flagship, outfit), $"sold {name}");
                    break;
                }

                case Counter.Jobs:
                {
                    // The row the player is standing on, not whichever mission happens
                    // to be first. Abandoning a mission the player did not select is
                    // destructive and silent: they lose a job they were carrying and
                    // the one they meant to drop is still there.
                    int active = index - _jobs.Count;
                    if (active < 0 || active >= _missions.Active.Count)
                    {
                        _message = _missions.Active.Count == 0
                            ? "no mission to abandon"
                            : "select a mission you are carrying to abandon it";
                        break;
                    }

                    ActiveMission taken = _missions.Active[active];
                    _missions.Abort(taken);
                    _message = $"abandoned: {taken.Mission.DisplayName}";
                    RefreshStock();
                    break;
                }
            }

            Refresh();
        }

        private static string Explain(TradeResult result, string success = "done") => result switch
        {
            TradeResult.Ok => success,
            TradeResult.NotSold => "not sold here",
            TradeResult.CannotAfford => "you cannot afford that",
            TradeResult.DoesNotFit => "it will not fit",
            TradeResult.NotOwned => "you do not own that",
            TradeResult.LastShip => "you cannot sell your only ship",
            TradeResult.InvalidAmount => "quantity must be positive",
            TradeResult.CreditLimit => "credit balance limit reached",
            _ => "no",
        };

        // --- Rendering --------------------------------------------------------------

        /// <summary>The offer dialogue as it stands: what has been said, and the reply.</summary>
        private IEnumerable<string> OfferLines()
        {
            var lines = new List<string>();

            if (_talkingAbout != null)
                lines.Add(TextSubstitution.NameOf(_talkingAbout, _player, _universe));

            lines.Add("");

            if (_dialog != null)
            {
                lines.Add(_dialog);
                lines.Add("");
                lines.Add("   ENTER accept   ·   ESC decline");
                return lines;
            }

            if (_talk is null)
                return lines;

            foreach (string said in _talk.PendingText)
                lines.Add(said);

            if (_talk.Choices.Count > 0)
            {
                lines.Add("");
                for (int i = 0; i < _talk.Choices.Count; i++)
                    lines.Add(Cursor(i, _talkChoice) + _talk.Choices[i]);
            }

            lines.Add("");
            lines.Add(_talk.Choices.Count > 0
                ? "   ↑/↓ choose   ·   ENTER answer   ·   ESC walk away"
                : "   ENTER continue   ·   ESC walk away");

            return lines;
        }

        private void Refresh()
        {
            _quotes = Trading.CommoditiesFor(_universe, _player)
                .OrderBy(q => q.Commodity, StringComparer.Ordinal)
                .ToList();
            _tabLabel.Text = string.Join("   ", Enum.GetValues<Counter>()
                .Select(c => c == _counter ? $"[ {Title(c)} ]" : $"  {Title(c)}  "));

            // A dialogue takes over the panel while it is up: the counter behind it is
            // not what the player is answering.
            _listLabel.Text = _talk != null || _dialog != null
                ? string.Join("\n", OfferLines())
                : string.Join("\n", Lines());

            Ship? flagship = _player.Fleet.Flagship;
            string hold = flagship is null
                ? ""
                : $"   cargo {_player.Fleet.CargoUsed(_player.CurrentSystem)}/{_player.Fleet.CargoCapacity(_player.CurrentSystem)} t" +
                  $"   fuel {flagship.Fuel:0}";

            _statusLine.Text =
                $"credits {_player.Credits:n0}   ships {_player.Fleet.Ships.Count}" +
                $"   missions {_missions.Active.Count}{hold}\n" +
                $"TAB counter · ↑/↓ select · {ActionHint()} · D depart · ESC menu" +
                (_message.Length > 0 ? $"\n{_message}" : "");
        }

        private string ActionHint() => _counter switch
        {
            Counter.Trade => "B buy 5t · N sell 5t",
            Counter.Shipyard => "B buy ship · N sell ship",
            Counter.Outfitter => "B install · N remove",
            Counter.Jobs => "B accept / hand in · N abandon",
            _ => "",
        };

        private static string Title(Counter counter) => counter switch
        {
            Counter.Trade => "TRADE",
            Counter.Shipyard => "SHIPYARD",
            Counter.Outfitter => "OUTFITTER",
            Counter.Jobs => "JOBS",
            _ => "",
        };

        private IEnumerable<string> Lines()
        {
            int selected = Selection();
            var lines = new List<string>();

            switch (_counter)
            {
                case Counter.Trade:
                    for (int i = 0; i < _quotes.Count; i++)
                    {
                        TradeQuote quote = _quotes[i];
                        long held = _player.Fleet.CargoCount(quote.Commodity, _player.CurrentSystem);
                        lines.Add($"{Cursor(i, selected)}{quote.Commodity,-18} {quote.Price,6} cr/t" +
                                  $"   hold {held,3} t" +
                                  (held > 0 ? $"   avg {_player.GetBasis(quote.Commodity):n0} cr/t" : ""));
                    }

                    if (lines.Count == 0) lines.Add("   (no market on this world)");
                    break;

                case Counter.Shipyard:
                    for (int i = 0; i < _shipyardStock.Count; i++)
                    {
                        string model = _shipyardStock[i];
                        long cost = (long)_universe.Ships[model].Attributes.Get("cost");
                        int owned = _player.Fleet.Ships.Count(s => s.Definition.DisplayName == model);
                        lines.Add($"{Cursor(i, selected)}{model,-26} {cost,10:n0} cr" +
                                  (owned > 0 ? $"   owned {owned}" : ""));
                    }

                    if (lines.Count == 0) lines.Add("   (no shipyard on this world)");
                    break;

                case Counter.Outfitter:
                    for (int i = 0; i < _outfitterStock.Count; i++)
                    {
                        string name = _outfitterStock[i];
                        Outfit outfit = _universe.Outfits[name];
                        int installed = _player.Fleet.Flagship?.Outfits.Count(o => o.Name == name) ?? 0;
                        lines.Add($"{Cursor(i, selected)}{name,-30} {outfit.Cost,9:n0} cr" +
                                  (installed > 0 ? $"   fitted {installed}" : ""));
                    }

                    if (lines.Count == 0) lines.Add("   (no outfitter on this world)");
                    break;

                case Counter.Jobs:
                    for (int i = 0; i < _jobs.Count; i++)
                    {
                        Mission job = _jobs[i];

                        // Resolve the destination rather than reading the literal one.
                        // Almost every job DESCRIBES where it is going instead of naming
                        // a planet, so the raw property is null for them and the board
                        // showed a bare title: no destination, no payment, nothing to
                        // choose between one job and the next.
                        string? going = job.ResolveDestination(_universe, _player.CurrentSystem?.Name);
                        string where = going is null ? "" : $"  → {going}";
                        string load = job.CargoTons > 0 ? $"  {job.CargoTons} t" : "";
                        long payment = job.Action(MissionTrigger.Complete)?.Payment ?? 0;
                        string pays = payment > 0 ? $"  {payment:n0} cr" : "";

                        // Mission text is a template; showing it unsubstituted puts
                        // "<planet> business convention" on the board.
                        string name = TextSubstitution.NameOf(job, _player, _universe);
                        lines.Add($"{Cursor(i, selected)}{name}{where}{load}{pays}");
                    }

                    if (lines.Count == 0) lines.Add("   (nothing on the board)");

                    foreach (ActiveMission taken in _missions.Active)
                    {
                        lines.Add($"   ACTIVE: {TextSubstitution.NameOf(taken.Mission, _player, _universe)}" +
                                  (taken.Deadline.HasValue ? $" by {taken.Deadline:yyyy-MM-dd}" : ""));
                    }

                    break;
            }

            // Window the list around the selection. A job board can hold hundreds of
            // entries - New Boston offers 424 - and rendering them all runs off the
            // bottom of the screen and past the status line with it.
            const int Rows = 12;
            int first = Math.Max(0, Math.Min(selected - Rows / 2, lines.Count - Rows));
            var window = lines.Skip(first).Take(Rows).ToList();

            if (lines.Count > Rows)
            {
                window.Add($"   … {lines.Count} entries, showing {first + 1}-{first + window.Count}");
            }

            while (window.Count < Rows) window.Add("");
            return window;
        }

        private static string Cursor(int index, int selected) => index == selected ? "▶ " : "   ";
    }
}
