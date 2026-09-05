using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    public partial class FlightWorld
    {
        private void SyncOwnedEscorts()
        {
            if (_ownedFleet == null || _field == null) return;
            if (_ship == null)
            {
                _ownedFleet.Clear();
                _ownedFleet.Visible = false;
                return;
            }
            _ownedFleet.Sync(_player.Fleet, _ship.CurrentSystem, _field);
            _ownedFleet.Visible = _player.CurrentPlanet == null;
        }

        private void StepOwnedEscorts()
        {
            if (_field == null || _ship == null) return;
            SyncOwnedEscorts();
            _field.Add(_player.Fleet.StepEscorts(_universe,
                _jumpAutopilot || _ship.IsEnteringHyperspace, _field.Ships));
            SyncOwnedEscorts();
        }

        private void IssueFleetOrder(FleetOrder order)
        {
            if (_ship == null || _field == null || _isLanded) return;
            Ship? target = order == FleetOrder.AttackTarget
                ? _field.Ships.Where(s => s.IsTargetable && ReferenceEquals(s.CurrentSystem, _ship.CurrentSystem)
                    && ShipAi.IsHostile(_ship, s)).MinBy(s => (s.Position - _ship.Position).LengthSquared)
                : null;
            if (order == FleetOrder.AttackTarget && target == null) return;
            _player.Fleet.IssueOrder(order, target);
            GD.Print($"[fleet] {order}" + (target == null ? "" : $" → {target.Definition.DisplayName}"));
            UpdateHud();
        }
    }
}
