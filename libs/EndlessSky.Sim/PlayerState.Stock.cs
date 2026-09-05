using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    public partial class PlayerState
    {
        private readonly Dictionary<string, long> _outfitStock = new(StringComparer.Ordinal);

        /// <summary>Signed stock changes during this port visit; positive counts are buyback inventory.</summary>
        public IReadOnlyDictionary<string, long> OutfitStock => _outfitStock;

        /// <summary>Ages of sold hulls and outfits, which the shop resells oldest first.</summary>
        public PurchaseLog StockDepreciation { get; } = new(oldestFirst: true);

        public long Stock(string outfit) => _outfitStock.GetValueOrDefault(outfit);

        internal bool CanChangeStock(string outfit, int count, bool individualSale = false)
        {
            long previous = individualSale && count > 0 ? Math.Max(0, Stock(outfit)) : Stock(outfit);
            Int128 next = (Int128)previous + count;
            return next >= long.MinValue && next <= long.MaxValue;
        }

        internal void ChangeStock(string outfit, int count, bool individualSale = false)
        {
            long previous = individualSale && count > 0 ? Math.Max(0, Stock(outfit)) : Stock(outfit);
            RestoreStock(outfit, checked(previous + count));
        }

        internal void RestoreStock(string outfit, long count)
        {
            if (count == 0) _outfitStock.Remove(outfit);
            else _outfitStock[outfit] = count;
        }

        internal void ClearStock()
        {
            _outfitStock.Clear();
            StockDepreciation.Clear();
        }
    }
}
