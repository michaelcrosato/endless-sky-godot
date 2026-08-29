using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// When the player bought each thing they own, so it can be valued when they sell
    /// it. Port of the player's half of upstream <c>Depreciation</c>.
    /// </summary>
    /// <remarks>
    /// Depreciation only means anything if something remembers the purchase.
    /// <see cref="Depreciation"/> defaults an item with no record to fully depreciated
    /// — correct, and upstream's rule — but with nothing ever writing a record, EVERY
    /// sale took that default. Buying a 25,000-credit blaster and immediately changing
    /// your mind refunded 6,250, which is not a depreciation curve, it is a tax on
    /// touching the outfitter.
    ///
    /// The player sells their NEWEST copy first (<c>Depreciation.cpp:305</c> takes
    /// <c>--record.end()</c> for a non-stock record), which is the least depreciated
    /// one. That is deliberate and in the player's favour: selling two of something
    /// bought years apart should not force them to give up the newer one cheaply.
    /// </remarks>
    public class PurchaseLog
    {
        // Purchase dates per item, kept sorted so the newest is last.
        private readonly Dictionary<string, List<DateTime>> _bought =
            new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);

        /// <summary>The record key for an outfit.</summary>
        public static string OutfitKey(string name) => "outfit:" + name;

        /// <summary>The record key for a ship model.</summary>
        public static string ShipKey(string model) => "ship:" + model;

        /// <summary>Every key with a record, for saving.</summary>
        public IEnumerable<KeyValuePair<string, List<DateTime>>> Records => _bought;

        /// <summary>Notes that the player bought one of something on a given day.</summary>
        public void Record(string key, DateTime day, int count = 1)
        {
            if (string.IsNullOrEmpty(key) || count <= 0)
                return;

            if (!_bought.TryGetValue(key, out List<DateTime>? days))
            {
                days = new List<DateTime>();
                _bought[key] = days;
            }

            for (int i = 0; i < count; i++)
                days.Add(day);

            days.Sort();
        }

        /// <summary>
        /// Consumes the newest record for this item and returns its age in days, or
        /// null when the player has no record of buying it.
        /// </summary>
        public int? TakeAge(string key, DateTime today)
        {
            if (string.IsNullOrEmpty(key) ||
                !_bought.TryGetValue(key, out List<DateTime>? days) || days.Count == 0)
            {
                return null;
            }

            DateTime newest = days[^1];
            days.RemoveAt(days.Count - 1);
            if (days.Count == 0)
                _bought.Remove(key);

            // Negative ages are meaningless; a save edited by hand could produce one.
            return Math.Max(0, (int)(today - newest).TotalDays);
        }

        /// <summary>How many purchases are on record for an item.</summary>
        public int Count(string key) =>
            _bought.TryGetValue(key, out List<DateTime>? days) ? days.Count : 0;
    }
}
