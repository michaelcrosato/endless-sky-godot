using System.Linq;
using System.Threading.Tasks;
using EndlessSky.Game;
using EndlessSky.Sim;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace EndlessSky.Tests.Presentation
{
    [TestSuite]
    public class OwnedFleetViewTest
    {
        private static PlayerState Pilot(out GameData data)
        {
            data = new GameData();
            data.LoadText("ship Courier\n\tattributes\n\t\tmass 100\n\t\thull 500\n" +
                "system A\n\tpos 0 0\nsystem B\n\tpos 100 0\n");
            var player = new PlayerState(data);
            player.EnterSystem(data.Systems["A"]);
            for (int i = 0; i < 5; i++)
            {
                Ship ship = data.BuildShip("Courier");
                ship.CurrentSystem = player.CurrentSystem;
                player.Fleet.Add(ship);
            }
            return player;
        }

        private static OwnedFleetView Open()
        {
            var view = new OwnedFleetView();
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(view);
            return view;
        }

        private static async Task Close(OwnedFleetView view)
        {
            view.QueueFree();
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task OnlyLocalActiveEscortsGetMeshesAndCombatSlots()
        {
            PlayerState player = Pilot(out GameData data);
            Ship local = player.Fleet.Ships[1];
            Ship disabled = player.Fleet.Ships[2];
            disabled.SetLevels(hull: disabled.MinimumHull - 1);
            player.Fleet.Ships[3].IsParked = true;
            player.Fleet.Ships[4].CurrentSystem = data.Systems["B"];
            var field = new CombatField();
            field.Add(player.Flagship);
            OwnedFleetView view = Open();
            try
            {
                view.Sync(player.Fleet, player.CurrentSystem, field);
                local.Position = new Point(123, -456);
                view.Sync(player.Fleet, player.CurrentSystem, field);
                AssertThat(view.Views.Count).IsEqual(2);
                AssertThat(field.Ships.Count).IsEqual(3);
                AssertBool(view.Views.ContainsKey(disabled)).IsTrue();
                AssertThat(view.Views[local].Position).IsEqual(WorldSpace.ToWorld(local.Position));
                AssertThat(disabled.Hull).IsEqual(disabled.MinimumHull - 1);
                ShipView old = view.Views[local];
                local.SetLevels(hull: -1);
                view.Sync(player.Fleet, player.CurrentSystem, field);
                AssertBool(old.IsQueuedForDeletion()).IsTrue();
                AssertThat(view.Views.Count).IsEqual(1);
                AssertThat(field.Ships.Count).IsEqual(2);
            }
            finally { await Close(view); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ChangingFlagshipReusesCombatMembershipAndReplacesOnlyTheEscortMesh()
        {
            PlayerState player = Pilot(out _);
            foreach (Ship ship in player.Fleet.Ships.Skip(2).ToArray()) player.Fleet.Remove(ship);
            Ship previous = player.Flagship!;
            Ship next = player.Fleet.Ships[1];
            var field = new CombatField();
            field.Add(previous);
            OwnedFleetView view = Open();
            try
            {
                view.Sync(player.Fleet, player.CurrentSystem, field);
                ShipView old = view.Views[next];
                player.Fleet.SetFlagship(next);
                field.Remove(previous);
                field.Add(next);
                view.Sync(player.Fleet, player.CurrentSystem, field);
                AssertBool(old.IsQueuedForDeletion()).IsTrue();
                AssertBool(view.Views.ContainsKey(previous)).IsTrue();
                AssertBool(view.Views.ContainsKey(next)).IsFalse();
                AssertThat(field.Ships.Count).IsEqual(2);
                AssertThat(field.Ships.Distinct().Count()).IsEqual(2);
                player.Fleet.Remove(previous);
                view.Sync(player.Fleet, player.CurrentSystem, field);
                AssertThat(view.Views.Count).IsEqual(0);
                AssertThat(field.Ships.Single()).IsEqual(next);
            }
            finally { await Close(view); }
        }

        [TestCase]
        [RequireGodotRuntime]
        public async Task ReloadRebuildsEscortsFromTheRestoredFleetAndRetiresOldViews()
        {
            PlayerState player = Pilot(out GameData data);
            var field = new CombatField();
            field.Add(player.Flagship);
            OwnedFleetView view = Open();
            try
            {
                player.Fleet.Ships[1].Position = new Point(800, 200);
                view.Sync(player.Fleet, player.CurrentSystem, field);
                ShipView[] old = view.Views.Values.ToArray();
                PlayerState restored = SaveGame.Read(SaveGame.Write(player), data);
                var replacement = new CombatField();
                replacement.Add(restored.Flagship);
                view.Sync(restored.Fleet, restored.CurrentSystem, replacement);
                AssertBool(old.All(v => v.IsQueuedForDeletion())).IsTrue();
                AssertThat(field.Ships.Count).IsEqual(1);
                AssertThat(replacement.Ships.Count).IsEqual(5);
                AssertBool(view.Views.Keys.All(restored.Fleet.Ships.Contains)).IsTrue();
                AssertThat(view.Views[restored.Fleet.Ships[1]].Position)
                    .IsEqual(WorldSpace.ToWorld(new Point(800, 200)));
            }
            finally { await Close(view); }
        }
    }
}
