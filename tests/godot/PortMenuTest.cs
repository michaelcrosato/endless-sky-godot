namespace EndlessSky.Tests.Presentation
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using EndlessSky.Game;
    using EndlessSky.Sim;
    using GdUnit4;
    using Godot;
    using static GdUnit4.Assertions;

    [TestSuite]
    public class PortMenuTest
    {
        private sealed class Session
        {
            private readonly Node _root = new Node();
            private readonly HashSet<Key> _down = new HashSet<Key>();
            public readonly GameUi Ui = new GameUi();
            public readonly LandedOverlay Port;
            public readonly PlayerState Player;
            public readonly MissionLog Missions;
            public readonly GameData Data;
            public int Saves, Loads, Departures;

            public Session(int counter = 0, int price = 100, int? freight = null, int ships = 1, int cargo = 0,
                int stockOutfitCost = 0, bool outfitter = false)
            {
                var data = new GameData();
                Data = data;
                data.LoadText("trade\n\tcommodity Food 50 100\n" +
                    "ship Trader\n\tattributes\n\t\tcost 1000\n\t\tmass 80\n\t\thull 500\n\t\t\"cargo space\" 40\n" +
                    "shipyard Yard\n\tTrader\nplanet Home\n\tspaceport Busy\n\tshipyard Yard\n" +
                    "system Sol\n\tpos 0 0\n\tobject Home\n\ttrade Food 100\n" +
                    "mission Delivery\n\tjob\n\tdestination Home\n" +
                    (freight.HasValue ? $"\tcargo Food {freight.Value}\n" : "") +
                    "\ton offer\n\t\tdialog Ready\n\ton accept\n\t\tpayment 20\n");
                data.Trade.SetPrice("Sol", "Food", price);
                if (stockOutfitCost > 0)
                    data.LoadText($"outfit Scanner\n\tcost {stockOutfitCost}\nship Trader\n\toutfits\n\t\tScanner 1\n");
                if (outfitter)
                    data.LoadText("outfit Battery\n\tcost 200\noutfitter Shelf\n\tBattery\n" +
                        "planet Home\n\toutfitter Shelf\n");
                Player = new PlayerState(data);
                for (int i = 0; i < ships; i++)
                {
                    Ship ship = data.BuildShip("Trader");
                    ship.CurrentSystem = data.Systems["Sol"];
                    Player.Fleet.Add(ship);
                }
                Player.SetCredits(1000);
                Player.EnterSystem(data.Systems["Sol"]);
                Player.Land(data.Planets["Home"]);
                Player.Fleet.LoadCargo("Food", cargo);
                Player.AdjustBasis("Food", cargo * 50L);
                Missions = new MissionLog(Player);
                ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_root);
                Ui.Bind(Player, Missions, data, () => Player.Fleet.Flagship);
                Ui.KeyDown = key => _down.Contains(key);
                Ui.SaveRequested += () => { Saves++; return true; };
                Ui.LoadRequested += () => { Loads++; return true; };
                _root.AddChild(Ui);
                Ui.SetProcess(false);
                int previous = LandedOverlay.OpenOnCounter;
                try
                {
                    LandedOverlay.OpenOnCounter = counter;
                    Port = LandedOverlay.Open(_root, Player, Missions, data.Planets["Home"], "Sol", data);
                }
                finally { LandedOverlay.OpenOnCounter = previous; }
                Port.Departed += _ => Departures++;
                Ui.Port = Port;
            }

            public void Frame(params Key[] keys)
            {
                _down.Clear();
                _down.UnionWith(keys);
                Ui._Process(1.0 / 60.0);
            }

            public void Tap(params Key[] keys)
            {
                Frame(keys);
                Frame();
            }

            public async Task Release()
            {
                _root.QueueFree();
                SceneTree tree = (SceneTree)Engine.GetMainLoop();
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            public async Task Capture(string name)
            {
                if (DisplayServer.GetName() == "headless") return;
                await Ui.ToSignal(Ui.GetTree(), SceneTree.SignalName.ProcessFrame);
                RenderingServer.ForceDraw();
                using Image capture = Ui.GetViewport().GetTexture().GetImage();
                AssertThat(capture.SavePng(ProjectSettings.GlobalizePath($"res://reports/{name}.png")))
                    .IsEqual(Error.Ok);
            }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task OutfitterSelectsLocalEscortsAndBuysBackUnstockedEquipment()
        {
            var session = new Session(ships: 3, stockOutfitCost: 400, outfitter: true);
            try
            {
                Ship flagship = session.Player.Flagship!;
                Ship remote = session.Player.Fleet.Ships[1];
                Ship escort = session.Player.Fleet.Ships[2];
                session.Data.LoadText("system Remote\n\tpos 100 0\n");
                remote.CurrentSystem = session.Data.Systems["Remote"];
                escort.GivenName = "Selected escort";
                escort.IsParked = true;
                session.Tap(Key.Tab);
                session.Tap(Key.Tab);
                session.Frame(Key.Right);
                session.Frame(Key.Right);
                session.Frame();
                session.Tap(Key.Down); // Scanner is installed but not normally sold here.
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Selected escort · parked")
                        && label.Text.Contains("Scanner: Not for sale · sell 100 cr"))).IsTrue();
                session.Tap(Key.N);
                AssertThat(escort.Outfits.Count).IsEqual(0);
                AssertThat(flagship.Outfits.Count).IsEqual(1);
                AssertThat(remote.Outfits.Count).IsEqual(1);
                AssertThat(session.Player.Credits).IsEqual(1100L);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Scanner: Buy 100 cr") && label.Text.Contains("buyback 1"))).IsTrue();
                await session.Capture("outfitter-buyback");
                session.Tap(Key.B);
                AssertThat(escort.Outfits.Single().Name).IsEqual("Scanner");
                AssertThat(session.Player.Credits).IsEqual(1000L);
                AssertThat(session.Player.Flagship).IsEqual(flagship);
                session.Tap(Key.B); // Used stock is exhausted; this does not buy a new copy.
                AssertThat(escort.Outfits.Count).IsEqual(1);
                AssertThat(session.Player.Credits).IsEqual(1000L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task OutfitterSelectionSurvivesMenusAndFollowsTheItemWhenRowsChange()
        {
            var session = new Session(counter: 2, ships: 2, stockOutfitCost: 400, outfitter: true);
            try
            {
                Ship flagship = session.Player.Flagship!;
                Ship escort = session.Player.Fleet.Ships[1];
                session.Tap(Key.Down); // Scanner on the flagship.
                session.Tap(Key.Escape);
                session.Tap(Key.Right, Key.N);
                AssertThat(flagship.Outfits.Count).IsEqual(1);
                AssertThat(escort.Outfits.Count).IsEqual(1);
                session.Tap(Key.Escape);
                session.Tap(Key.N);
                session.Tap(Key.Right);
                session.Tap(Key.N); // Same item remains selected on the escort.
                AssertThat(flagship.Outfits.Count).IsEqual(0);
                AssertThat(escort.Outfits.Count).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(1200L);
                session.Tap(Key.B);
                session.Tap(Key.B);
                AssertThat(escort.Outfits.Count).IsEqual(2);
                AssertThat(session.Player.Credits).IsEqual(1000L);
                session.Tap(Key.Left); // No Scanner row on this ship after buyback stock is exhausted.
                session.Tap(Key.B);
                AssertThat(flagship.Outfits.Single().Name).IsEqual("Battery");
                AssertThat(session.Player.Credits).IsEqual(800L);
                await session.Capture("outfitter-installed");
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ShipyardRefreshesUsedPurchaseQuotesAfterSellingAndBuying()
        {
            var session = new Session(counter: 1, stockOutfitCost: 300);
            try
            {
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                AssertThat(session.Player.Credits).IsEqual(1325L);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("FOR SALE") && label.Text.Contains("325 cr"))).IsTrue();
                await session.Capture("shipyard-used-price");
                session.Tap(Key.B);
                AssertThat(session.Player.Credits).IsEqual(1000L);
                AssertThat(session.Player.Flagship!.Outfits.Single().Name).IsEqual("Scanner");
                AssertThat(Trading.OutfitSaleValue(session.Player, session.Data.Outfits["Scanner"])).IsEqual(75L);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("FOR SALE") && label.Text.Contains("1,300 cr"))).IsTrue();
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ShipSaleConfirmationPreservesTheValueOfRecentlyInstalledEquipment()
        {
            var session = new Session(counter: 2, stockOutfitCost: 300, outfitter: true);
            try
            {
                session.Tap(Key.B); // Install a new 200-credit battery on the old ship.
                AssertThat(session.Player.Credits).IsEqual(800L);
                session.Tap(Key.Tab);
                session.Tap(Key.Tab);
                session.Tap(Key.Tab);
                session.Tap(Key.N);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Sell Trader for 525 cr?"))).IsTrue();
                await session.Capture("shipyard-mixed-age-sale");
                session.Tap(Key.Enter);
                AssertThat(session.Player.Credits).IsEqual(1325L);
                AssertThat(session.Player.Stock("Scanner")).IsEqual(1L);
                AssertThat(session.Player.Stock("Battery")).IsEqual(0L);
                AssertThat(Trading.OutfitPurchaseValue(session.Player, session.Data, session.Data.Outfits["Battery"]))
                    .IsEqual(200L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ShipyardSaleUsesTheSelectedLocalHull()
        {
            var session = new Session(ships: 3);
            try
            {
                session.Data.LoadText("system Remote\n\tpos 100 0\n");
                Ship remote = session.Player.Fleet.Ships[0];
                Ship local = session.Player.Fleet.Ships[1];
                Ship selected = session.Player.Fleet.Ships[2];
                remote.CurrentSystem = session.Data.Systems["Remote"];
                remote.GivenName = "Remote hull";
                local.GivenName = "Local hull";
                selected.GivenName = "Selected hull";
                session.Tap(Key.Tab);
                session.Tap(Key.Down);
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                AssertBool(session.Player.Fleet.Ships.Contains(remote)).IsTrue();
                AssertBool(session.Player.Fleet.Ships.Contains(local)).IsTrue();
                AssertBool(session.Player.Fleet.Ships.Contains(selected)).IsFalse();
                AssertThat(session.Player.Credits).IsEqual(1250L);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("FOR SALE")
                        && (label.Text.Contains("Remote hull") || label.Text.Contains("Selected hull")))).IsFalse();
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ShipyardAcceptsAnOwnedModelOutsideItsStock()
        {
            var session = new Session(ships: 0);
            try
            {
                session.Data.LoadText("ship Unlisted\n\tattributes\n\t\tcost 2000\n\t\thull 500\n");
                Ship ship = session.Data.BuildShip("Unlisted");
                ship.CurrentSystem = session.Player.CurrentSystem;
                session.Player.Fleet.Add(ship);
                session.Tap(Key.Tab);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Unlisted") && label.Text.Contains("sell 500 cr"))).IsTrue();
                await session.Capture("shipyard-owned-roster");
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                AssertThat(session.Player.Fleet.Ships.Count).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(1500L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task SellingTheOnlyShipKeepsThePortAndAllowsAReplacement()
        {
            var session = new Session(counter: 1, cargo: 5);
            try
            {
                Ship old = session.Player.Flagship!;
                int changed = 0;
                session.Port.FleetChanged += () => changed++;
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                AssertBool(session.Player.Flagship == null).IsTrue();
                AssertBool(session.Ui.Port == session.Port).IsTrue();
                AssertThat(session.Player.Fleet.PortCargo!.Count("Food")).IsEqual(5L);
                session.Tap(Key.D);
                AssertThat(session.Departures).IsEqual(0);
                await session.Capture("shipyard-no-flagship");
                session.Tap(Key.B);
                AssertBool(session.Player.Flagship != null && session.Player.Flagship != old).IsTrue();
                AssertThat(changed).IsEqual(2);
                session.Port.Departed += confirmed => session.Player.TakeOff(session.Missions, confirmed);
                session.Tap(Key.D);
                AssertThat(session.Departures).IsEqual(1);
                AssertThat(session.Player.Flagship!.Cargo.Count("Food")).IsEqual(5L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task CancellingAShipSaleKeepsTheHullCargoAndMoney()
        {
            var session = new Session(counter: 1, cargo: 5);
            try
            {
                string before = SaveGame.Write(session.Player, session.Missions);
                session.Frame(Key.N, Key.Enter, Key.D);
                session.Frame(Key.Enter, Key.D);
                AssertBool(session.Port.IsConfirmingShipSale).IsTrue();
                AssertThat(SaveGame.Write(session.Player, session.Missions)).IsEqual(before);
                await session.Capture("shipyard-sale-confirmation");
                session.Frame(Key.Escape, Key.N, Key.B, Key.Enter);
                session.Frame(Key.N, Key.B, Key.Enter);
                AssertBool(session.Port.IsConfirmingShipSale).IsFalse();
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.None);
                AssertThat(SaveGame.Write(session.Player, session.Missions)).IsEqual(before);
                AssertThat(session.Departures).IsEqual(0);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task TheShipyardQuotesTheEquippedShipPriceItActuallyCharges()
        {
            var session = new Session(counter: 1, ships: 0, stockOutfitCost: 300);
            try
            {
                session.Player.SetCredits(2000);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("FOR SALE") && label.Text.Contains("1,300 cr"))).IsTrue();
                session.Tap(Key.B);
                AssertThat(session.Player.Credits).IsEqual(700L);
                AssertThat(session.Player.Flagship!.Cost).IsEqual(1300L);
                AssertThat(session.Player.Flagship.Outfits.Count).IsEqual(1);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task FleetOrdersOnlyFireOnceInFlightAndDoNotLeakThroughMenus()
        {
            var session = new Session();
            try
            {
                var orders = new List<FleetOrder>();
                session.Ui.FleetOrderRequested += orders.Add;
                session.Tap(Key.G, Key.H, Key.V, Key.F);
                AssertThat(orders.Count).IsEqual(0);
                AssertBool(session.Player.Depart()).IsTrue();
                session.Ui.Port = null;
                session.Port.Hide();
                session.Frame(Key.H);
                session.Frame(Key.H);
                session.Frame();
                session.Tap(Key.G);
                session.Tap(Key.V);
                session.Tap(Key.F);
                AssertThat(string.Join(",", orders)).IsEqual("Hold,Gather,Escort,AttackTarget");
                session.Frame(Key.Escape, Key.H);
                session.Frame(Key.H);
                session.Frame(Key.Escape, Key.H);
                session.Frame(Key.H);
                AssertThat(orders.Count).IsEqual(4);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task CancellingExcessCargoDepartureKeepsGoodsAndConsumesTheKeys()
        {
            var session = new Session(counter: 1, ships: 2, cargo: 60);
            try
            {
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                AssertThat(session.Player.Fleet.Ships.Count).IsEqual(1);
                string before = SaveGame.Write(session.Player, session.Missions);
                session.Tap(Key.D);
                AssertBool(session.Port.IsConfirmingDeparture).IsTrue();
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Sell 20 t of Food")
                        && label.Text.Contains("profit +1,000 cr"))).IsTrue();
                await session.Capture("port-cargo-warning");
                session.Frame(Key.Escape, Key.Enter, Key.B, Key.N, Key.D);
                session.Frame(Key.Enter, Key.B, Key.N, Key.D);
                AssertBool(session.Port.IsConfirmingDeparture).IsFalse();
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.None);
                AssertThat(session.Departures).IsEqual(0);
                AssertThat(SaveGame.Write(session.Player, session.Missions)).IsEqual(before);
                await session.Capture("port-cargo-kept");
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ConfirmingExcessCargoDepartureSellsOnceAndLoadsTheRemainingHull()
        {
            var session = new Session(counter: 1, ships: 2, cargo: 60);
            try
            {
                session.Port.Departed += confirmed =>
                {
                    AssertBool(confirmed).IsTrue();
                    AssertBool(session.Player.TakeOff(session.Missions, confirmed)).IsTrue();
                    session.Ui.Port = null;
                    session.Port.Hide();
                };
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                long credits = session.Player.Credits;
                session.Frame(Key.D, Key.Enter);
                AssertBool(session.Port.IsConfirmingDeparture).IsTrue();
                AssertThat(session.Departures).IsEqual(0);
                session.Frame();
                session.Frame(Key.KpEnter, Key.D, Key.N, Key.B);
                session.Frame(Key.KpEnter, Key.D, Key.N, Key.B);
                AssertThat(session.Departures).IsEqual(1);
                AssertThat(session.Player.Credits).IsEqual(credits + 2000L);
                AssertThat(session.Player.Flagship!.Cargo.Count("Food")).IsEqual(40L);
                AssertThat(session.Player.CostBasis["Food"]).IsEqual(2000L);
                AssertBool(session.Player.CurrentPlanet == null).IsTrue();
                AssertBool(session.Player.Fleet.PortCargo == null).IsTrue();
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task FreightWarningNamesTheJobBeforeAbandoningIt()
        {
            var session = new Session(counter: 3, freight: 60, ships: 2);
            try
            {
                session.Tap(Key.B);
                session.Tap(Key.Enter);
                ActiveMission job = session.Missions.Active.Single();
                session.Tap(Key.Tab);
                session.Tap(Key.Tab);
                session.Tap(Key.N);
                session.Tap(Key.Enter);
                session.Tap(Key.D);
                AssertBool(session.Port.IsConfirmingDeparture).IsTrue();
                AssertThat(job.Outcome).IsEqual(MissionOutcome.Active);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("Abandon: Delivery"))).IsTrue();
                await session.Capture("port-freight-warning");
                session.Port.Departed += confirmed =>
                    AssertBool(session.Player.TakeOff(session.Missions, confirmed)).IsTrue();
                session.Tap(Key.Enter);
                AssertThat(job.Outcome).IsEqual(MissionOutcome.Aborted);
                AssertThat(session.Departures).IsEqual(1);
                AssertThat(session.Player.Fleet.CargoUsed()).IsEqual(0L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task TradeCounterShowsAverageCostAndRealizedProfit()
        {
            var session = new Session();
            try
            {
                session.Tap(Key.B);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("avg 100 cr/t"))).IsTrue();
                await session.Capture("port-average-cost");
                session.Player.Data!.Trade.SetPrice("Sol", "Food", 200);
                session.Tap(Key.N);
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("200 cr/t"))).IsTrue();
                AssertBool(session.Port.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("profit +500 cr"))).IsTrue();
                AssertThat(session.Player.Credits).IsEqual(1500L);
                AssertThat(session.Player.CostBasis.Count).IsEqual(0);
                await session.Capture("port-sale-profit");
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task AcceptedFreightCannotBeSoldAtTheTradeCounter()
        {
            var session = new Session(counter: 3, freight: 20);
            try
            {
                session.Tap(Key.B);
                session.Tap(Key.Enter);
                AssertThat(session.Missions.Active.Count).IsEqual(1);
                AssertThat(session.Player.Fleet.CargoUsed()).IsEqual(20);
                session.Tap(Key.Tab);
                session.Tap(Key.N);
                AssertThat(session.Player.Fleet.CargoUsed()).IsEqual(20);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(1020L);
                session.Tap(Key.B);
                session.Tap(Key.N);
                AssertThat(session.Player.Fleet.CargoUsed()).IsEqual(20);
                AssertThat(session.Player.Credits).IsEqual(1020L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task AJobThatDoesNotFitCannotPayAnAdvanceOrTakeAHoldPartially()
        {
            var session = new Session(counter: 3, freight: 50);
            try
            {
                session.Tap(Key.B);
                session.Tap(Key.Enter);
                AssertThat(session.Missions.Active.Count).IsEqual(0);
                AssertThat(session.Player.Fleet.CargoUsed()).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(1000L);
                AssertThat(session.Player.Conditions.Get("Delivery: active")).IsEqual(0L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ZeroPriceCannotGiveAwayOrDiscardCargo()
        {
            var session = new Session(price: 0);
            try
            {
                session.Player.Fleet.LoadCargo("Food", 7);
                session.Tap(Key.B);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(7);
                session.Tap(Key.N);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(7);
                AssertThat(session.Player.Credits).IsEqual(1000L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task LargeDebtCannotWrapIntoAnAffordablePurchase()
        {
            var session = new Session();
            try
            {
                const long debt = -429_496_729_500L;
                session.Player.SetCredits(debt);
                session.Tap(Key.B);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(debt);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task LandedPilotsCanSaveAndLoadFromThePauseMenu()
        {
            var session = new Session();
            try
            {
                session.Tap(Key.Escape);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.Pause);
                session.Tap(Key.Down);
                session.Tap(Key.Enter);
                AssertThat(session.Saves).IsEqual(1);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.Pause);
                session.Tap(Key.Down);
                session.Tap(Key.Enter);
                AssertThat(session.Loads).IsEqual(1);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.None);
                AssertThat(session.Player.CurrentPlanet!.Name).IsEqual("Home");
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task MenuKeysAndHeldKeysCannotTradeOrDepartUnderneathIt()
        {
            var session = new Session();
            try
            {
                session.Frame(Key.Escape, Key.B, Key.D);
                session.Frame(Key.B, Key.D);
                session.Frame(Key.Escape, Key.B, Key.D);
                session.Frame(Key.B, Key.D);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.None);
                AssertThat(session.Player.Credits).IsEqual(1000L);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(0);
                AssertThat(session.Departures).IsEqual(0);

                session.Frame();
                session.Tap(Key.B);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(5);
                AssertThat(session.Player.Credits).IsEqual(500L);
                session.Tap(Key.N);
                AssertThat(session.Player.Fleet.CargoCount("Food")).IsEqual(0);
                AssertThat(session.Player.Credits).IsEqual(1000L);
                session.Tap(Key.D);
                AssertThat(session.Departures).IsEqual(1);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task EscapeDeclinesAnOfferBeforeItCanOpenThePauseMenu()
        {
            var session = new Session(counter: 3);
            try
            {
                session.Tap(Key.B);
                AssertBool(session.Port.IsOfferingMission).IsTrue();
                session.Tap(Key.Escape);
                AssertBool(session.Port.IsOfferingMission).IsFalse();
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.None);
                AssertThat(session.Player.Conditions.Get("Delivery: declined")).IsEqual(1L);
                session.Tap(Key.Escape);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.Pause);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task AnsweringAnOfferDoesNotLeakHeldKeysBackToTheCounter()
        {
            var session = new Session(counter: 3);
            try
            {
                session.Frame(Key.B);
                session.Frame(Key.B, Key.KpEnter, Key.D);
                session.Frame(Key.B, Key.D);
                AssertBool(session.Port.IsOfferingMission).IsFalse();
                AssertThat(session.Missions.Active.Count).IsEqual(1);
                AssertThat(session.Player.Credits).IsEqual(1020L);
                AssertThat(session.Departures).IsEqual(0);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task OpeningAnOfferCannotAlsoDepartOrAbandonIt()
        {
            var session = new Session(counter: 3);
            try
            {
                session.Tap(Key.B, Key.N, Key.D);
                AssertBool(session.Port.IsOfferingMission).IsTrue();
                AssertThat(session.Departures).IsEqual(0);
                AssertThat(session.Missions.Active.Count).IsEqual(0);
                session.Tap(Key.Enter);
                AssertThat(session.Missions.Active.Count).IsEqual(1);
                AssertThat(session.Player.Credits).IsEqual(1020L);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task OpeningAMenuCannotAlsoConfirmItsFirstRow()
        {
            var session = new Session();
            try
            {
                session.Tap(Key.Escape, Key.Enter);
                AssertThat(session.Ui.Screen).IsEqual(UiScreen.Pause);
                AssertThat(session.Saves).IsEqual(0);
                AssertThat(session.Loads).IsEqual(0);
            }
            finally { await session.Release(); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task HeldArrowAndLetterAliasesOnlyMoveOneMenuRow()
        {
            var session = new Session();
            try
            {
                session.Tap(Key.Escape);
                session.Frame(Key.Down, Key.S);
                session.Frame(Key.Down, Key.S);
                session.Frame();
                session.Tap(Key.Enter);
                AssertThat(session.Saves).IsEqual(1);
                AssertThat(session.Loads).IsEqual(0);
            }
            finally { await session.Release(); }
        }
    }
}
