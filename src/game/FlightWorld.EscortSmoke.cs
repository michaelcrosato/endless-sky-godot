using System;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    public partial class FlightWorld
    {
        private bool _fleetSmoke;
        private int _fleetSmokeStage;
        private Ship? _fleetSmokeEscort, _fleetSmokeThreat;
        private ShipView? _fleetSmokeThreatView;
        private StarSystem? _fleetSmokeOrigin, _fleetSmokeDestination, _fleetSmokeRemote;
        private StellarObject? _fleetSmokePort;
        private ActiveMission? _fleetSmokeJob;
        private double _fleetSmokeThreatHull, _fleetSmokeExpectedFuel;
        private string? _fleetSmokePath;

        // Stock ships and one freight fixture use the normal physics loop. Only the
        // landing approaches are positioned; both ships must fly and pay for a jump.
        private bool StepFleetSmoke()
        {
            if (_simFrames > 1800)
                return EndFleetSmoke(false, $"stage {_fleetSmokeStage} timed out; escort " +
                    $"{_fleetSmokeEscort?.CurrentSystem?.Name}, jump {_fleetSmokeEscort?.HyperspaceCount}");
            if (_fleetSmokeStage == 0) return BeginFleetSmoke();
            if (_fleetSmokeStage == 1 && _fleetSmokeThreat!.Hull < _fleetSmokeThreatHull)
            {
                _traffic.RemoveAll(pair => ReferenceEquals(pair.Ship, _fleetSmokeThreat));
                _field!.Remove(_fleetSmokeThreat);
                _fleetSmokeThreatView!.QueueFree();
                SmokeKey(Key.H);
                _ship!.TargetSystem = _fleetSmokeDestination;
                _ship.Velocity = Point.Zero;
                _ship.Facing = Angle.FromPoint(_ship.JumpDirection);
                _jumpAutopilot = true;
                _fleetSmokeStage = 2;
                GD.Print("[smoke] owned escort damaged a hostile; hold order issued before the flagship jumps");
            }
            else if (_fleetSmokeStage == 2 && ReferenceEquals(_ship!.CurrentSystem, _fleetSmokeDestination))
            {
                if (_fleetSmokeEscort!.CurrentSystem != _fleetSmokeOrigin || _fleetSmokeEscort.IsEnteringHyperspace)
                    return EndFleetSmoke(false, "hold order did not leave the escort in the departure system");
                SmokeKey(Key.V);
                _fleetSmokeStage = 3;
            }
            else if (_fleetSmokeStage == 3 && _fleetSmokeEscort!.IsEnteringHyperspace
                && _fleetSmokeEscort.HyperspaceCount >= 25)
            {
                _fleetSmokeExpectedFuel = _fleetSmokeEscort.Fuel
                    - (Ship.HyperspaceFrames - _fleetSmokeEscort.HyperspaceCount)
                    * _fleetSmokeEscort.JumpFuelCost / Ship.HyperspaceFrames;
                string before = SaveGame.Write(_player, _missions);
                Guid job = _fleetSmokeJob!.Id;
                _fleetSmokePath = $"user://smoke-fleet-{Guid.NewGuid():N}.txt";
                if (!SmokeSaveMenu(_fleetSmokePath, load: false))
                    return EndFleetSmoke(false, "could not save the escort's jump");
                _fleetSmokeEscort.SetLevels(fuel: 1);
                _fleetSmokeEscort.Position += new Point(999, 888);
                if (!SmokeSaveMenu(_fleetSmokePath, load: true)
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions)))
                    return EndFleetSmoke(false, "mid-jump reload did not restore the fleet and freight");
                _fleetSmokeEscort = _player.Fleet.Ships.Single(s => s.GivenName == "Fleet smoke escort");
                _fleetSmokeJob = _missions.Active.Single(m => m.Id == job);
                _fleetSmokeStage = 4;
                GD.Print("[smoke] reloaded the escort during its own jump with freight and remaining fuel intact");
            }
            else if (_fleetSmokeStage == 4 && _fleetSmokeEscort!.CurrentSystem == _fleetSmokeDestination
                && !_fleetSmokeEscort.IsHyperspacing && !_ship!.IsHyperspacing)
            {
                if (Math.Abs(_fleetSmokeEscort.Fuel - _fleetSmokeExpectedFuel) > 1e-6
                    || _ownedFleet?.Views.ContainsKey(_fleetSmokeEscort) != true
                    || _field!.Ships.Count(s => ReferenceEquals(s, _fleetSmokeEscort)) != 1
                    || _player.Fleet.Ships.Single(s => s.GivenName == "Fleet smoke parked").CurrentSystem != _fleetSmokeOrigin
                    || _player.Fleet.Ships.Single(s => s.GivenName == "Fleet smoke stranded").CurrentSystem != _fleetSmokeRemote)
                    return EndFleetSmoke(false, "arrival lost a mesh, duplicated a ship, changed fuel or moved an ineligible hull");
                _ship.Position = _fleetSmokePort!.Position;
                _ship.Velocity = Point.Zero;
                _ship.TargetStellar = _fleetSmokePort;
                TryLand();
                long credits = _player.Credits;
                bool delivered = _isLanded && _missions.Complete(_fleetSmokeJob!) && _player.Credits == credits + 123
                    && _player.Fleet.CargoUsed() == 0;
                return EndFleetSmoke(delivered, delivered
                    ? "owned escort fought, held position, jumped independently, resumed after reload and delivered its freight"
                    : "escort freight could not be delivered after arrival");
            }
            return false;
        }

        private bool BeginFleetSmoke()
        {
            _fleetSmokeOrigin = _ship!.CurrentSystem!;
            _fleetSmokeDestination = _fleetSmokeOrigin.Links.Select(name => _universe.Systems[name])
                .FirstOrDefault(s => s.AllObjects().Any(o => o.Planet?.HasSpaceport == true && !o.IsStar));
            StellarObject? home = _fleetSmokeOrigin.AllObjects().FirstOrDefault(o => _ship.CanEverLandOn(o));
            if (_fleetSmokeDestination == null || home == null || !_ship.HasHyperdrive)
                return EndFleetSmoke(false, "no stock hyperdrive route between two ports");
            _fleetSmokePort = _fleetSmokeDestination.AllObjects().First(o => o.Planet?.HasSpaceport == true && !o.IsStar);
            _fleetSmokeRemote = _universe.Systems.Values.First(s => s != _fleetSmokeOrigin && s != _fleetSmokeDestination);
            _fleetSmokeEscort = _universe.Ships.Keys.Select(_universe.BuildShip)
                .FirstOrDefault(s => s.HasHyperdrive && s.Cargo.Capacity >= 1 && s.TurnRate >= 1
                    && s.Acceleration >= 0.05 && ShipAi.IsArmed(s));
            if (_fleetSmokeEscort == null) return EndFleetSmoke(false, "no armed stock escort available");
            _fleetSmokeEscort.GivenName = "Fleet smoke escort";
            _fleetSmokeEscort.CurrentSystem = _fleetSmokeOrigin;
            _player.Fleet.Add(_fleetSmokeEscort);
            Ship parked = _universe.BuildShip(_ship.Definition.DisplayName);
            parked.GivenName = "Fleet smoke parked";
            parked.CurrentSystem = _fleetSmokeOrigin;
            parked.IsParked = true;
            _player.Fleet.Add(parked);
            Ship stranded = _universe.BuildShip(_ship.Definition.DisplayName);
            stranded.GivenName = "Fleet smoke stranded";
            stranded.CurrentSystem = _fleetSmokeRemote;
            _player.Fleet.Add(stranded);

            var definition = new DataWriter();
            definition.Write("mission", "Owned escort smoke freight");
            definition.BeginChild();
            definition.Write("cargo", "documents", _ship.Cargo.Capacity + 1);
            definition.Write("destination", _fleetSmokePort.Planet!.Name);
            definition.Write("on", "complete");
            definition.BeginChild();
            definition.Write("payment", 123);
            definition.EndChild();
            definition.EndChild();
            _universe.LoadText(definition.ToString());
            _ship.Position = home.Position;
            _ship.Velocity = Point.Zero;
            _ship.TargetStellar = home;
            TryLand();
            _fleetSmokeJob = _missions.Accept(_universe.Missions["Owned escort smoke freight"]);
            OnDepart();
            stranded.SetLevels(fuel: 0);
            if (_isLanded || _fleetSmokeJob == null
                || !_fleetSmokeEscort.Cargo.MissionCargo.TryGetValue(_fleetSmokeJob.Id, out long freight) || freight <= 0
                || _ownedFleet?.Views.Count != 1 || !_field!.Ships.Contains(_fleetSmokeEscort))
                return EndFleetSmoke(false, "departure did not put freight and a mesh on the local escort");

            _fleetSmokeEscort.Position = _ship.Position + new Point(0, -150);
            _fleetSmokeEscort.Velocity = Point.Zero;
            _fleetSmokeEscort.Facing = new Angle(0);
            _fleetSmokeThreat = _universe.BuildShip(_ship.Definition.DisplayName);
            var enemy = new Government("Fleet smoke hostile");
            enemy.SetReputation(-100);
            _fleetSmokeThreat.Government = enemy;
            _fleetSmokeThreat.CurrentSystem = _fleetSmokeOrigin;
            _fleetSmokeThreat.Position = _ship.Position + new Point(0, -300);
            _fleetSmokeThreat.SetLevels(shields: 0, hull: _fleetSmokeThreat.MinimumHull + 10);
            _fleetSmokeThreatHull = _fleetSmokeThreat.Hull;
            _fleetSmokeThreatView = new ShipView();
            AddChild(_fleetSmokeThreatView);
            _fleetSmokeThreatView.SyncWith(_fleetSmokeThreat);
            _traffic.Add((_fleetSmokeThreat, _fleetSmokeThreatView));
            _field.Add(_fleetSmokeThreat);
            SyncOwnedEscorts();
            SmokeKey(Key.F);
            _fleetSmokeStage = 1;
            GD.Print($"[smoke] {_fleetSmokeEscort.Definition.DisplayName} carries {freight} t of mission freight; " +
                $"route {_fleetSmokeOrigin.Name} → {_fleetSmokeDestination.Name}");
            return false;
        }

        private void SmokeKey(Key key)
        {
            Func<Key, bool> original = _ui.KeyDown;
            try
            {
                _ui.KeyDown = _ => false;
                _ui._Process(1.0 / 60.0);
                _ui.KeyDown = candidate => candidate == key;
                _ui._Process(1.0 / 60.0);
                _ui.KeyDown = _ => false;
                _ui._Process(1.0 / 60.0);
            }
            finally { _ui.KeyDown = original; }
        }

        private bool EndFleetSmoke(bool success, string message)
        {
            _fleetSmokeStage = 5;
            GD.Print($"[smoke] {(success ? "PASS" : "FAIL")}: {message}");
            GetTree().Quit(success ? 0 : 1);
            return true;
        }
    }
}
