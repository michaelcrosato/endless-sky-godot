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

        /// <summary>Age of the next record to be sold, without consuming the purchase.</summary>
        public int? PeekAge(string key, DateTime today)
        {
            if (string.IsNullOrEmpty(key) ||
                !_bought.TryGetValue(key, out List<DateTime>? days) || days.Count == 0)
                return null;
            return Math.Max(0, (int)(today - days[^1]).TotalDays);
        }

        /// <summary>Consumes the newest purchase and returns its age, or null if unknown.</summary>
        public int? TakeAge(string key, DateTime today)
        {
            int? age = PeekAge(key, today);
            if (!age.HasValue) return null;
            List<DateTime> days = _bought[key];
            days.RemoveAt(days.Count - 1);
            if (days.Count == 0)
                _bought.Remove(key);
            return age;
        }

        /// <summary>How many purchases are on record for an item.</summary>
        public int Count(string key) =>
            _bought.TryGetValue(key, out List<DateTime>? days) ? days.Count : 0;
    }
}
