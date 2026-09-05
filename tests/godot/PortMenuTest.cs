namespace EndlessSky.Tests.Presentation
{
    using System.Collections.Generic;
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
            public int Saves, Loads, Departures;

            public Session(int counter = 0)
            {
                var data = new GameData();
                data.LoadText("trade\n\tcommodity Food 50 100\n" +
                    "ship Trader\n\tattributes\n\t\tmass 80\n\t\thull 500\n\t\t\"cargo space\" 40\n" +
                    "planet Home\n\tspaceport Busy\n" +
                    "system Sol\n\tpos 0 0\n\tobject Home\n\ttrade Food 100\n" +
                    "mission Delivery\n\tjob\n\tdestination Home\n" +
                    "\ton offer\n\t\tdialog Ready\n\ton accept\n\t\tpayment 20\n");
                Player = new PlayerState(data);
                Player.Fleet.Add(data.BuildShip("Trader"));
                Player.SetCredits(1000);
                Player.EnterSystem(data.Systems["Sol"]);
                Player.Land(data.Planets["Home"]);
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
                Port.Departed += () => Departures++;
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
