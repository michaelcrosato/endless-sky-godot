using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The player's ships and what owning them costs. Port of the fleet-accounting
    /// half of upstream <c>PlayerInfo</c>.
    /// </summary>
    /// <remarks>
    /// INCOMPLETE, tracked rather than dropped: outfit maintenance costs and income,
    /// mortgages and other accounts, fighter bays, passenger berths, and the
    /// per-ship "gave orders" escort command set.
    /// </remarks>
    public class PlayerFleet
    {
        private readonly List<Ship> _ships = new List<Ship>();

        /// <summary>Every owned ship, flagship first once one is set.</summary>
        public IReadOnlyList<Ship> Ships => _ships;

        /// <summary>The ship the player flies. Null when the fleet is empty.</summary>
        public Ship? Flagship { get; private set; }

        /// <summary>Ships that are neither parked nor destroyed: the ones actually flying.</summary>
        public IEnumerable<Ship> ActiveShips =>
            _ships.Where(s => !s.IsParked && !s.IsDestroyed);

        /// <summary>Ships other than the flagship that are flying: the escorts.</summary>
        public IEnumerable<Ship> Escorts =>
            ActiveShips.Where(s => !ReferenceEquals(s, Flagship));

        public void Add(Ship ship)
        {
            if (ship is null || _ships.Contains(ship))
                return;

            _ships.Add(ship);
            Flagship ??= ship;
        }

        public bool Remove(Ship ship)
        {
            if (ship is null || !_ships.Remove(ship))
                return false;

            if (ReferenceEquals(Flagship, ship))
                Flagship = _ships.FirstOrDefault(s => !s.IsParked && !s.IsDestroyed) ?? _ships.FirstOrDefault();

            return true;
        }

        /// <summary>Promotes a ship the player already owns to flagship.</summary>
        public void SetFlagship(Ship ship)
        {
            if (ship is not null && _ships.Contains(ship))
                Flagship = ship;
        }

        // --- Daily costs ----------------------------------------------------------

        /// <summary>Credits paid per crew member per day, upstream's flat rate.</summary>
        public const int DailySalaryPerCrew = 100;

        /// <summary>
        /// Daily crew salaries. Port of upstream <c>PlayerInfo::Salaries</c>.
        /// </summary>
        /// <remarks>
        /// Three details that are easy to get wrong and change the game's economy:
        /// extra crew are only paid on the FLAGSHIP (escorts are paid their required
        /// complement no matter how many berths they have), parked and destroyed
        /// ships cost nothing, and the total is <c>100 * (crew - 1)</c> because the
        /// player is one of the crew and does not pay themselves.
        /// </remarks>
        public long DailySalaries()
        {
            long crew = 0;

            // NOT clamped at zero: an under-crewed flagship reduces the bill
            // upstream, and flooring the shortfall overcharges the player.
            if (Flagship is not null)
                crew += Flagship.Crew - Flagship.RequiredCrew;

            foreach (Ship ship in ActiveShips)
                crew += ship.RequiredCrew;

            if (crew == 0)
                return 0;

            return DailySalaryPerCrew * (crew - 1);
        }

        /// <summary>Total value of every owned ship, parked or not.</summary>
        public long FleetValue() => _ships.Sum(s => s.Cost);

        // --- Cargo ----------------------------------------------------------------

        /// <summary>Combined hold capacity, optionally limited to ships in one system.</summary>
        public int CargoCapacity(StarSystem? system = null) => Ordered(system).Sum(s => s.Cargo.Capacity);

        /// <summary>Tons carried, optionally limited to ships in one system.</summary>
        public int CargoUsed(StarSystem? system = null) => Ordered(system).Sum(s => s.Cargo.Used);

        public int CargoFree(StarSystem? system = null) => Math.Max(0, CargoCapacity(system) - CargoUsed(system));

        /// <summary>
        /// Loads a commodity across the fleet, filling ships in order until the tonnage
        /// is placed or the fleet is full. Returns the amount actually loaded.
        /// </summary>
        /// <remarks>
        /// Upstream spreads cargo over the whole fleet rather than making the player
        /// place it by hand, so a run's capacity is the fleet's, not the flagship's.
        /// Port transactions pass a system to exclude distant ships.
        /// </remarks>
        public int LoadCargo(string commodity, int tons, StarSystem? system = null)
        {
            if (string.IsNullOrWhiteSpace(commodity) || tons <= 0)
                return 0;

            int remaining = tons;

            // Flagship first, then escorts, so a single-ship fleet behaves obviously.
            foreach (Ship ship in Ordered(system))
            {
                if (remaining <= 0)
                    break;

                remaining -= ship.LoadCargo(commodity, remaining);
            }

            return tons - remaining;
        }

        /// <summary>Unloads a commodity from anywhere in the fleet. Returns tons removed.</summary>
        public int UnloadCargo(string commodity, int tons, StarSystem? system = null)
        {
            if (commodity is null || tons <= 0)
                return 0;

            int remaining = tons;
            foreach (Ship ship in Ordered(system))
            {
                if (remaining <= 0)
                    break;

                remaining -= ship.UnloadCargo(commodity, remaining);
            }

            return tons - remaining;
        }

        /// <summary>How much of a commodity the fleet is carrying in total.</summary>
        public int CargoCount(string commodity, StarSystem? system = null) =>
            Ordered(system).Sum(s => s.Cargo.Count(commodity));

        /// <summary>What the fleet's cargo would fetch at a system's prices.</summary>
        public long CargoValueAt(TradeData trade, string systemName) =>
            ActiveShips.Sum(s => s.Cargo.ValueAt(trade, systemName));

        private IEnumerable<Ship> Ordered(StarSystem? system = null)
        {
            if (Flagship is not null && !Flagship.IsParked && !Flagship.IsDestroyed
                && (system is null || ReferenceEquals(Flagship.CurrentSystem, system)))
                yield return Flagship;

            foreach (Ship ship in Escorts)
                if (system is null || ReferenceEquals(ship.CurrentSystem, system))
                    yield return ship;
        }

        public override string ToString() =>
            $"{_ships.Count} ships, flagship {(Flagship?.Definition.DisplayName ?? "none")}";
    }
}
