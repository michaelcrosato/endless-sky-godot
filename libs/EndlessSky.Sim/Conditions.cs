using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The player's condition variables. Port of upstream <c>ConditionsStore</c>.
    /// </summary>
    /// <remarks>
    /// Endless Sky's entire progression system is one flat integer keyspace. Mission
    /// availability, event triggers, conversation branches, planet descriptions and
    /// reputation all read and write the same map, and an unset condition reads as 0
    /// rather than being an error. That default is load-bearing: content asks about
    /// conditions no other content has ever written.
    ///
    /// INCOMPLETE, tracked rather than dropped: derived conditions provided by the
    /// engine (reputation, ship counts, date components, "flagship: ..." queries) are
    /// not yet wired in. They read as plain stored values here.
    /// </remarks>
    public class Conditions
    {
        private readonly Dictionary<string, long> _values =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, long> Values => _values;

        /// <summary>Reads a condition. Unset conditions are 0, never an error.</summary>
        public long Get(string name) =>
            name is not null && _values.TryGetValue(name, out long value) ? value : 0L;

        /// <summary>Whether a condition is non-zero, which is what "has" tests.</summary>
        public bool Has(string name) => Get(name) != 0L;

        public void Set(string name, long value)
        {
            if (string.IsNullOrEmpty(name))
                return;

            // Storing zeroes would bloat a save with every condition ever asked about,
            // and reads already default to 0.
            if (value == 0L)
                _values.Remove(name);
            else
                _values[name] = value;
        }

        public void Add(string name, long amount) => Set(name, Get(name) + amount);

        public void Clear(string name) => Set(name, 0L);

        public void ClearAll() => _values.Clear();

        public override string ToString() => $"{_values.Count} conditions set";
    }
}
