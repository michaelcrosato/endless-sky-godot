using System;
using System.Linq;
using EndlessSky.Sim;
using Godot;

namespace EndlessSky.Game
{
    public partial class FlightWorld
    {
        private bool _outfitterSmoke;
        private int _outfitterSmokeFrames, _outfitterSmokeStage, _outfitterSmokeInstalled;
        private Ship? _outfitterSmokeEscort;
        private Outfit? _outfitterSmokeOutfit;
        private string? _outfitterSmokePath;
        private long _outfitterSmokeCredits, _outfitterSmokeValue;

        // Stock equipment, a positioned landing, and actual port/save menu input.
        // After departure the reinstalled mount must fire and the escort must be in flight.
        private bool StepOutfitterSmoke()
        {
            if (++_outfitterSmokeFrames > 600) return EndOutfitterSmoke(false, "outfitter scenario timed out");
            if (_outfitterSmokeStage == 0)
            {
                if (_ship == null || _universe == null) return EndOutfitterSmoke(false, "no starting ship or dataset");
                StellarObject? port = _ship.CurrentSystem!.AllObjects()
                    .FirstOrDefault(o => o.Planet?.HasOutfitter == true && _ship.CanEverLandOn(o));
                if (port == null) return EndOutfitterSmoke(false, "no usable outfitter in the starting system");
                var catalog = Trading.OutfitsFor(_universe, port.Planet!).ToHashSet(StringComparer.Ordinal);
                foreach (string model in _universe.Ships.Keys.OrderBy(s => s, StringComparer.Ordinal))
                {
                    Ship candidate = _universe.BuildShip(model);
                    Outfit? weapon = candidate.Outfits.FirstOrDefault(o => o.Weapon is { AmmoName: null }
                        && o.Attributes.Get("gun ports") < 0 && !catalog.Contains(o.Name)
                        && Outfitting.CanInstall(candidate, o, -1) == -1);
                    if (weapon == null || !candidate.IsFlyable) continue;
                    _outfitterSmokeEscort = candidate;
                    _outfitterSmokeOutfit = weapon;
                    break;
                }
                if (_outfitterSmokeEscort == null) return EndOutfitterSmoke(false, "no stock escort with a removable unlisted gun");
                _outfitterSmokeEscort.GivenName = "Outfitter smoke escort";
                _outfitterSmokeEscort.CurrentSystem = _ship.CurrentSystem;
                _player.Fleet.Add(_outfitterSmokeEscort);
                _outfitterSmokeInstalled = _outfitterSmokeEscort.Outfits.Count(o => o == _outfitterSmokeOutfit);
                _outfitterSmokeCredits = _player.Credits;
                _outfitterSmokeValue = Trading.OutfitSaleValue(_player, _outfitterSmokeOutfit!);
                _ship.Position = port.Position;
                _ship.Velocity = Point.Zero;
                _ship.TargetStellar = port;
                TryLand();
                if (!_isLanded) return EndOutfitterSmoke(false, "could not land at the outfitter");
                SelectOutfitterSmokeItem();
                SmokeKey(Key.N);
                if (_player.Credits != _outfitterSmokeCredits + _outfitterSmokeValue
                    || _outfitterSmokeEscort.Outfits.Count(o => o == _outfitterSmokeOutfit) != _outfitterSmokeInstalled - 1
                    || _player.Stock(_outfitterSmokeOutfit!.Name) != 1)
                    return EndOutfitterSmoke(false, "the selected escort did not sell its unlisted gun: " +
                        string.Join(" / ", _landedOverlay!.FindChildren("*", "Label", true, false)
                            .OfType<Label>().Select(label => label.Text)));
                string before = SaveGame.Write(_player, _missions);
                _outfitterSmokePath = $"user://smoke-outfitter-{Guid.NewGuid():N}.txt";
                if (!SmokeSaveMenu(_outfitterSmokePath, load: false)) return EndOutfitterSmoke(false, "outfitter save failed");
                _player.StockDepreciation.Clear();
                _player.SetCredits(0);
                if (!SmokeSaveMenu(_outfitterSmokePath, load: true)
                    || !SavedStateMatches(before, SaveGame.Write(_player, _missions)))
                    return EndOutfitterSmoke(false, "reload lost the used stock, equipment or credits");
                _outfitterSmokeEscort = _player.Fleet.Ships.Single(s => s.GivenName == "Outfitter smoke escort");
                SelectOutfitterSmokeItem();
                _outfitterSmokeStage = 1;
                GD.Print($"[smoke] sold {_outfitterSmokeOutfit.Name} from {_outfitterSmokeEscort.Definition.DisplayName}; " +
                    $"reloaded buyback at {_outfitterSmokeValue} cr");
            }
            else if (_outfitterSmokeStage == 1 && _outfitterSmokeFrames >= 60)
            {
                SmokeKey(Key.B);
                Ship escort = _outfitterSmokeEscort!;
                if (_player.Credits != _outfitterSmokeCredits
                    || escort.Outfits.Count(o => o == _outfitterSmokeOutfit) != _outfitterSmokeInstalled
                    || Trading.OutfitSaleValue(_player, _outfitterSmokeOutfit!) != _outfitterSmokeValue
                    || _player.Stock(_outfitterSmokeOutfit!.Name) != 0)
                    return EndOutfitterSmoke(false, "buyback changed the price, age or installed count");
                SmokeKey(Key.D);
                WeaponMount? mount = escort.Mounts.FirstOrDefault(m => m.Weapon == _outfitterSmokeOutfit.Weapon);
                if (_isLanded || _player.Flagship != _ship || _ownedFleet?.Views.ContainsKey(escort) != true
                    || !_field!.Ships.Contains(escort) || mount == null || escort.Fire(mount) == null)
                    return EndOutfitterSmoke(false, "the rearmed escort could not depart and fire its weapon");
                _outfitterSmokeStage = 2;
            }
            else if (_outfitterSmokeStage == 2 && _outfitterSmokeFrames >= 90)
                return EndOutfitterSmoke(_outfitterSmokeEscort!.Velocity.Length > 0,
                    "escort equipment sold, reloaded, bought back at its used price, rearmed and flown");
            return false;
        }

        private void SelectOutfitterSmokeItem()
        {
            // A new/reloaded port opens on Trade. Selection is by ship and item, never flagship mutation.
            string? selected = Trading.OutfitsToShow(_player, _universe!, _player.Flagship).FirstOrDefault();
            SmokeKey(Key.Tab);
            SmokeKey(Key.Tab);
            SmokeKey(Key.Right);
            string[] rows = Trading.OutfitsToShow(_player, _universe!, _outfitterSmokeEscort).ToArray();
            int index = Array.IndexOf(rows, _outfitterSmokeOutfit!.Name);
            // Changing ships preserves the selected item even when new rows precede it.
            int current = Math.Max(0, Array.IndexOf(rows, selected));
            int presses = (index - current + rows.Length) % rows.Length;
            for (int i = 0; i < presses; i++) SmokeKey(Key.Down);
        }

        private bool EndOutfitterSmoke(bool success, string message)
        {
            _outfitterSmokeStage = 3;
            GD.Print($"[smoke] {(success ? "PASS" : "FAIL")}: {message}");
            GetTree().Quit(success ? 0 : 1);
            return true;
        }
    }
}
