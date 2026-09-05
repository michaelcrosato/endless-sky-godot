using EndlessSky.Sim;

namespace EndlessSky.Game
{
    public partial class FlightWorld
    {
        private Point _portPosition;
        private Angle _portFacing;

        private void OpenPort()
        {
            _landedOverlay = LandedOverlay.Open(this, _player, _missions, _player.CurrentPlanet!,
                _player.CurrentSystem!.Name, _universe);
            _landedOverlay.Departed += OnDepart;
            _landedOverlay.FleetChanged += SyncPortFlagship;
            _ui.Port = _landedOverlay;
        }

        private void SyncPortFlagship()
        {
            Ship? next = _player.Flagship;
            if (!ReferenceEquals(_ship, next))
            {
                _field?.Remove(_ship);
                _ship = next;
                if (_ship != null)
                {
                    _ship.Position = _portPosition;
                    _ship.Facing = _portFacing;
                    _ship.BuildMounts();
                    _field?.Add(_ship);
                    _shipView.SyncWith(_ship);
                }
            }
            _shipView.Visible = _ship != null;
            _startShip = _ship?.Definition.DisplayName ?? "no flagship";
            if (_titleLabel != null)
                _titleLabel.Text = $"{_player.CurrentSystem!.Name.ToUpperInvariant()}  ·  {_startShip.ToUpperInvariant()}";
            if (_ship == null)
            {
                _camera?.Snap(_portPosition);
                if (_tutorialPanel != null) _tutorialPanel.Visible = false;
                if (_flightKeys != null) _flightKeys.Visible = false;
            }
            SyncOwnedEscorts();
            UpdateHud();
        }
    }
}
