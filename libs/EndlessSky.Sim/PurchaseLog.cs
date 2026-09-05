using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// When the player bought each thing they own, so it can be valued when they sell
    /// it. Shop stock uses the same records, selling the oldest copy first.
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
        // Ordinal days allow unknown, fully depreciated stock even at DateTime.MinValue.
        // Upstream also stores integer days, with oldest stock and newest owned items first.
        private readonly Dictionary<string, List<int>> _bought =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly bool _oldestFirst;

        public PurchaseLog(bool oldestFirst = false) => _oldestFirst = oldestFirst;

        /// <summary>The record key for an outfit.</summary>
        public static string OutfitKey(string name) => "outfit:" + name;

        /// <summary>The record key for a ship model.</summary>
        public static string ShipKey(string model) => "ship:" + model;

        /// <summary>Every key with a record, for saving.</summary>
        public IEnumerable<KeyValuePair<string, List<int>>> Records => _bought;

        /// <summary>Notes that the player bought one of something on a given day.</summary>
        public void Record(string key, DateTime day, int count = 1) =>
            RecordDay(key, DateOnly.FromDateTime(day).DayNumber, count);

        /// <summary>Transfers an item's existing age instead of making a used purchase new.</summary>
        public void RecordAge(string key, DateTime today, int age, int count = 1) =>
            RecordDay(key, DateOnly.FromDateTime(today).DayNumber - Math.Clamp(age, 0, Depreciation.MaxAge), count);

        internal void RecordDay(string key, int day, int count = 1)
        {
            if (string.IsNullOrEmpty(key) || count <= 0 || day < -Depreciation.MaxAge
                || day > DateOnly.MaxValue.DayNumber)
                return;

            if (!_bought.TryGetValue(key, out List<int>? days))
            {
                days = new List<int>();
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
                !_bought.TryGetValue(key, out List<int>? days) || days.Count == 0)
                return null;
            return Math.Max(0, DateOnly.FromDateTime(today).DayNumber - days[_oldestFirst ? 0 : days.Count - 1]);
        }

        /// <summary>Consumes the next record and returns its age, or null if unknown.</summary>
        public int? TakeAge(string key, DateTime today)
        {
            int? age = PeekAge(key, today);
            if (!age.HasValue) return null;
            List<int> days = _bought[key];
            days.RemoveAt(_oldestFirst ? 0 : days.Count - 1);
            if (days.Count == 0)
                _bought.Remove(key);
            return age;
        }

        /// <summary>How many purchases are on record for an item.</summary>
        public int Count(string key) =>
            _bought.TryGetValue(key, out List<int>? days) ? days.Count : 0;

        /// <summary>Quotes a group without consuming records; upstream truncates once per item type.</summary>
        public Int128 Value(string key, long cost, DateTime today, int count = 1,
            int defaultAge = Depreciation.MaxAge)
        {
            if (count <= 0) return 0;
            double fraction = 0;
            int remaining = count;
            int currentDay = DateOnly.FromDateTime(today).DayNumber;
            if (_bought.TryGetValue(key, out List<int>? days))
            {
                int index = _oldestFirst ? 0 : days.Count - 1;
                int direction = _oldestFirst ? 1 : -1;
                while (remaining > 0 && index >= 0 && index < days.Count)
                {
                    int day = days[index], used = 0;
                    do { used++; index += direction; }
                    while (used < remaining && index >= 0 && index < days.Count && days[index] == day);
                    fraction += used * Depreciation.Fraction(currentDay - day);
                    remaining -= used;
                }
            }
            fraction += remaining * Depreciation.Fraction(defaultAge);
            // Keep exact integer prices throughout the grace period, including large quotes.
            return fraction == count ? (Int128)count * cost : (Int128)(fraction * cost);
        }

        internal void TransferFrom(PurchaseLog source, string key, DateTime today, int count, int defaultAge)
        {
            if (count <= 0) return;
            if (!_bought.TryGetValue(key, out List<int>? destination))
                _bought[key] = destination = new List<int>();
            if (source._bought.TryGetValue(key, out List<int>? dates))
            {
                int taken = Math.Min(count, dates.Count);
                int first = source._oldestFirst ? 0 : dates.Count - taken;
                destination.AddRange(dates.GetRange(first, taken));
                dates.RemoveRange(first, taken);
                if (dates.Count == 0) source._bought.Remove(key);
                count -= taken;
            }
            int fallback = DateOnly.FromDateTime(today).DayNumber - Math.Clamp(defaultAge, 0, Depreciation.MaxAge);
            for (int i = 0; i < count; i++) destination.Add(fallback);
            destination.Sort();
        }

        public void Clear() => _bought.Clear();
    }
}
