using System.Collections.Generic;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    /// <summary>Meshes and local combat registration for owned escorts; never moves a ship.</summary>
    public partial class OwnedFleetView : Node3D
    {
        private readonly Dictionary<Ship, ShipView> _views = new();
        private CombatField? _field;
        public IReadOnlyDictionary<Ship, ShipView> Views => _views;

        public void Sync(PlayerFleet fleet, StarSystem? system, CombatField field)
        {
            if (!ReferenceEquals(_field, field))
            {
                Clear();
                _field = field;
            }
            var present = fleet.Escorts.Where(s => ReferenceEquals(s.CurrentSystem, system)).ToHashSet();
            foreach (Ship ship in _views.Keys.Where(s => !present.Contains(s)).ToArray())
            {
                _views[ship].QueueFree();
                _views.Remove(ship);
                // Promoting an escort replaces its mesh with the flagship mesh but
                // must leave the ship itself in the combat field.
                if (!ReferenceEquals(ship, fleet.Flagship)) field.Remove(ship);
            }
            foreach (Ship ship in present)
            {
                field.Add(ship);
                if (!_views.TryGetValue(ship, out ShipView? view))
                {
                    view = new ShipView();
                    AddChild(view);
                    _views.Add(ship, view);
                }
                view.SyncWith(ship);
                view.SetHyperspaceStretch(ship.HyperspaceCount / (float)Ship.HyperspaceFrames);
            }
        }

        public void Clear()
        {
            foreach (var entry in _views)
            {
                _field?.Remove(entry.Key);
                entry.Value.QueueFree();
            }
            _views.Clear();
        }
    }
}
