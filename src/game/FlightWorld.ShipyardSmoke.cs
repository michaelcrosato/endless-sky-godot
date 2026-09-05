using System;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    public partial class FlightWorld
    {
        private bool _shipyardSmoke;
        private int _shipyardSmokeStage, _shipyardSmokeFrames, _shipyardSmokeStockIndex;
        private string? _shipyardSmokePath, _shipyardSmokeCommodity, _shipyardSmokeModel;
        private long _shipyardSmokePurchasePrice;
        private Point _shipyardSmokeDeparture;

        // Uses stock ships and the real port/menu actions. The landing approach is
        // positioned; after replacement the normal flight loop must move the hull.
        private bool StepShipyardSmoke()
        {
            if (++_shipyardSmokeFrames > 600) return EndShipyardSmoke(false, "shipyard scenario timed out");
            if (_shipyardSmokeStage == 0)
            {
                if (_ship == null || _universe == null) return EndShipyardSmoke(false, "no starting ship or dataset");
                StellarObject? port = _ship!.CurrentSystem!.AllObjects()
                    .FirstOrDefault(o => o.Planet?.HasShipyard == true && _ship.CanEverLandOn(o));
                if (port == null) return EndShipyardSmoke(false, "the starting system has no usable shipyard");
                string[] stock = Trading.ShipsFor(_universe, port.Planet!).Where(_universe.Ships.ContainsKey)
                    .OrderBy(s => s, StringComparer.Ordinal).ToArray();
                Ship? replacement = stock.Select(_universe.BuildShip).Where(s => s.Cargo.Capacity >= 5).MinBy(s => s.Cost);
                if (replacement == null) return EndShipyardSmoke(false, "no stock replacement with cargo space");
                _shipyardSmokeModel = replacement.Definition.DisplayName;
                _shipyardSmokeStockIndex = Array.IndexOf(stock, _shipyardSmokeModel);
                _shipyardSmokePurchasePrice = replacement.Cost;
                // Explicit funds make this a transaction fixture, independent of
                // how much a new pilot can afford at this particular yard.
                _player.SetCredits(Math.Max(_player.Credits, replacement.Cost));
                _ship.Position = port.Position;
                _ship.Velocity = Point.Zero;
                _ship.TargetStellar = port;
                TryLand();
                _shipyardSmokeCommodity = Trading.CommoditiesFor(_universe, _player).First().Commodity;
                if (_player.Fleet.LoadCargo(_shipyardSmokeCommodity, 5) != 5)
                    return EndShipyardSmoke(false, "could not prepare five tons of cargo");
                _player.AdjustBasis(_shipyardSmokeCommodity, 500);
                Ship sold = _ship;
                long afterSale = _player.Credits + Trading.ShipSaleValue(_player, sold);
                SmokeKey(Key.Tab);
                SmokeKey(Key.N);
                if (_landedOverlay?.IsConfirmingShipSale != true)
                    return EndShipyardSmoke(false, "sale did not request confirmation");
                SmokeKey(Key.Enter);
                if (_player.Flagship != null || _ship != null || _shipView.Visible || _field!.Ships.Contains(sold)
                    || _player.Fleet.Ships.Count != 0 || !_isLanded || _player.Credits != afterSale
                    || _tutorialPanel?.Visible == true || _flightKeys?.Visible == true)
                    return EndShipyardSmoke(false, "selling the last hull left a flagship or lost the port");
                SmokeKey(Key.D);
                if (!_isLanded || _landedOverlay == null) return EndShipyardSmoke(false, "departure succeeded without a ship");

                string before = SaveGame.Write(_player, _missions);
                _shipyardSmokePath = $"user://smoke-shipyard-{Guid.NewGuid():N}.txt";
                if (!SmokeSaveMenu(_shipyardSmokePath, load: false)) return EndShipyardSmoke(false, "shipless save failed");
                _player.AddCredits(-123);
                _player.Fleet.UnloadCargo(_shipyardSmokeCommodity, 1);
                _player.Fleet.Add(sold);
                SyncPortFlagship();
                if (!SmokeSaveMenu(_shipyardSmokePath, load: true)
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions))
                    || _ship != null || _shipView.Visible || _field!.Ships.Count != 0)
                    return EndShipyardSmoke(false, "shipless reload did not restore cargo, money and an empty combat field");

                // A valid port is required when the save has no flagship.
                string invalid = string.Join("\n", before.Split('\n').Where(line => !line.StartsWith("planet ", StringComparison.Ordinal)));
                if (!SaveSlot.Save(invalid, _shipyardSmokePath) || LoadFrom(_shipyardSmokePath)
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions)))
                    return EndShipyardSmoke(false, "a shipless flight save replaced the active pilot");
                _shipyardSmokeStage = 1;
                GD.Print("[smoke] sold the only ship, kept cargo ashore, reloaded the port and rejected a shipless flight save");
            }
            else if (_shipyardSmokeStage == 1 && _shipyardSmokeFrames >= 60)
            {
                SmokeKey(Key.Tab);
                for (int i = 0; i < _shipyardSmokeStockIndex; i++) SmokeKey(Key.Down);
                long beforePurchase = _player.Credits;
                SmokeKey(Key.B);
                if (_ship == null || _ship.Definition.DisplayName != _shipyardSmokeModel
                    || _player.Credits != beforePurchase - _shipyardSmokePurchasePrice)
                    return EndShipyardSmoke(false, "the replacement purchase did not bind the new flagship");
                SmokeKey(Key.D);
                if (_ship == null || _isLanded || _landedOverlay != null || _ui.Port != null || !_shipView.Visible
                    || _ship.Cargo.Count(_shipyardSmokeCommodity!) != 5 || _player.Fleet.PortCargo != null)
                    return EndShipyardSmoke(false, "the replacement could not depart with the saved cargo");
                _shipyardSmokeDeparture = _ship.Position;
                _ship.Velocity = new Point(2, 0);
                _shipyardSmokeStage = 2;
            }
            else if (_shipyardSmokeStage == 2 && _shipyardSmokeFrames >= 90)
            {
                bool flying = _ship != null && _ship.Position != _shipyardSmokeDeparture
                    && _shipView.Position == WorldSpace.ToWorld(_ship.Position)
                    && _field!.Ships.Count(s => ReferenceEquals(s, _ship)) == 1;
                return EndShipyardSmoke(flying, flying
                    ? "last ship sold; shipless port and cargo reloaded; replacement bought, loaded and flown"
                    : "the replacement failed to enter the normal flight loop");
            }
            return false;
        }

        private bool EndShipyardSmoke(bool success, string message)
        {
            _shipyardSmokeStage = 3;
            GD.Print($"[smoke] {(success ? "PASS" : "FAIL")}: {message}");
            GetTree().Quit(success ? 0 : 1);
            return true;
        }
    }
}
