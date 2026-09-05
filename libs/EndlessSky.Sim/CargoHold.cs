using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A ship's commodities and mission freight, sharing the same cargo capacity.
    /// </summary>
    /// <remarks>
    /// Cargo is measured in tons and counts toward the ship's mass, so a loaded
    /// freighter genuinely handles worse than an empty one. That coupling is why this
    /// lives in the simulation rather than in a UI model.
    ///
    /// Every mutation is bounded and reports what it actually moved rather than
    /// throwing: upstream lets a player buy "as much as fits" and a partial fill is
    /// the normal case, not an error.
    ///
    /// INCOMPLETE, tracked rather than dropped: outfits stored in the hold,
    /// passengers and bunks, fighter bays, and per-commodity legality.
    /// </remarks>
    public class CargoHold
    {
        private readonly Dictionary<string, int> _commodities =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, int> _missionCargo = new();

        public CargoHold(int capacity = 0)
        {
            Capacity = Math.Max(0, capacity);
        }

        /// <summary>Total tonnage the hold can carry.</summary>
        public int Capacity { get; private set; }

        /// <summary>Tons currently carried.</summary>
        public int Used { get; private set; }

        /// <summary>Tons still available.</summary>
        public int Free => Math.Max(0, Capacity - Used);

        public IReadOnlyDictionary<string, int> Commodities => _commodities;
        public IReadOnlyDictionary<Guid, int> MissionCargo => _missionCargo;

        public bool IsEmpty => Used == 0 && _missionCargo.Count == 0;

        /// <summary>Loads freight for one mission, including a zero-ton parcel marker.</summary>
        public int AddMissionCargo(Guid mission, int tons)
        {
            if (mission == Guid.Empty || tons < 0)
                return 0;
            int loaded = Math.Min(tons, Free);
            if (tons > 0 && loaded == 0)
                return 0;
            _missionCargo.TryGetValue(mission, out int held);
            _missionCargo[mission] = held + loaded;
            Used += loaded;
            return loaded;
        }

        /// <summary>Removes this mission's freight without touching ordinary commodities.</summary>
        public int RemoveMissionCargo(Guid mission)
        {
            if (!_missionCargo.Remove(mission, out int tons))
                return 0;
            Used -= tons;
            return tons;
        }

        internal int ReserveMissionCargo(Guid mission, string commodity, int tons)
        {
            int moved = Math.Min(Math.Max(0, tons), Count(commodity));
            if (moved == 0) return 0;
            int remaining = Count(commodity) - moved;
            if (remaining == 0) _commodities.Remove(commodity);
            else _commodities[commodity] = remaining;
            _missionCargo.TryGetValue(mission, out int held);
            _missionCargo[mission] = held + moved;
            // This is a change of ownership within the same hold, even if overfull.
            return moved;
        }

        /// <summary>
        /// Resizes the hold. Shrinking below the current load does NOT silently dump
        /// cargo; the hold reports as overfull until something is unloaded, which is
        /// what lets an outfitter refuse a change that would strand goods.
        /// </summary>
        public void SetCapacity(int capacity) => Capacity = Math.Max(0, capacity);

        /// <summary>True when a capacity change has left more aboard than fits.</summary>
        public bool IsOverfull => Used > Capacity;

        public int Count(string commodity) =>
            commodity is not null && _commodities.TryGetValue(commodity, out int tons) ? tons : 0;

        /// <summary>
        /// Loads up to <paramref name="tons"/>, limited by free space.
        /// Returns the amount actually loaded.
        /// </summary>
        public int Add(string commodity, int tons)
        {
            if (string.IsNullOrWhiteSpace(commodity) || tons <= 0)
                return 0;

            int loaded = Math.Min(tons, Free);
            if (loaded <= 0)
                return 0;

            _commodities[commodity] = Count(commodity) + loaded;
            Used += loaded;
            return loaded;
        }

        /// <summary>
        /// Unloads up to <paramref name="tons"/>, limited by what is aboard.
        /// Returns the amount actually removed.
        /// </summary>
        public int Remove(string commodity, int tons)
        {
            if (commodity is null || tons <= 0)
                return 0;

            int held = Count(commodity);
            int removed = Math.Min(tons, held);
            if (removed <= 0)
                return 0;

            // An emptied entry is dropped rather than left as a zero, so callers
            // enumerating the hold do not see phantom goods.
            if (removed == held)
                _commodities.Remove(commodity);
            else
                _commodities[commodity] = held - removed;

            Used -= removed;
            return removed;
        }

        /// <summary>Unloads all ordinary commodities, leaving mission freight aboard.</summary>
        public Dictionary<string, int> RemoveAll()
        {
            var contents = new Dictionary<string, int>(_commodities, StringComparer.Ordinal);
            foreach (int tons in _commodities.Values) Used -= tons;
            _commodities.Clear();
            return contents;
        }

        /// <summary>
        /// What the hold's contents would fetch at the given system's prices.
        /// Goods the system does not trade in are worth nothing there.
        /// </summary>
        public long ValueAt(TradeData trade, string systemName)
        {
            if (trade is null || systemName is null)
                return 0;

            long total = 0;
            foreach (KeyValuePair<string, int> entry in _commodities)
            {
                int? price = trade.Price(systemName, entry.Key);
                if (price.HasValue)
                    total += (long)price.Value * entry.Value;
            }

            return total;
        }

        public override string ToString() => $"cargo {Used}/{Capacity} tons";
    }
}
