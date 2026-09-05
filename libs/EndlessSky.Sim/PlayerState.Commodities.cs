using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    public partial class PlayerState
    {
        private readonly Dictionary<string, long> _costBasis = new(StringComparer.Ordinal);

        /// <summary>The remaining purchase cost of each owned commodity.</summary>
        public IReadOnlyDictionary<string, long> CostBasis => _costBasis;

        internal long TotalBasis(string commodity) =>
            _costBasis.TryGetValue(commodity, out long basis) ? basis : 0;

        /// <summary>
        /// PlayerInfo::GetBasis: all owned tons share the same average cost,
        /// including remote and parked ships. Mission freight is not a commodity.
        /// </summary>
        public long GetBasis(string commodity, long tons = 1)
        {
            long total = Fleet.Ships.Sum(s => (long)s.Cargo.Count(commodity));
            // Multiply before dividing so integer rounding applies to the whole lot.
            // Int128 preserves exactness when a valid 64-bit basis times tons would overflow.
            return total == 0 ? 0 : checked((long)((Int128)TotalBasis(commodity) * tons / total));
        }

        public void AdjustBasis(string commodity, long adjustment)
        {
            long value = checked(TotalBasis(commodity) + adjustment);
            if (value == 0) _costBasis.Remove(commodity);
            else _costBasis[commodity] = value;
        }

        private void RemoveShipsBasis(IReadOnlyList<Ship> ships)
        {
            // All lost ships still belong to the fleet while computing their combined
            // share. Per-ship rounding would leave the surviving cargo with extra cost.
            foreach (var cargo in ships.SelectMany(s => s.Cargo.Commodities).GroupBy(c => c.Key, StringComparer.Ordinal))
            {
                long remaining = TotalBasis(cargo.Key) - GetBasis(cargo.Key, cargo.Sum(c => (long)c.Value));
                if (remaining == 0) _costBasis.Remove(cargo.Key);
                else _costBasis[cargo.Key] = remaining;
            }
        }
    }
}
