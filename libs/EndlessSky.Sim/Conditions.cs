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
    /// Some of that keyspace is not stored at all. Upstream calls them autoconditions:
    /// "credits", "flagship landed", "flagship planet: Mars" and the rest are computed
    /// from live game state whenever they are read, so content can interrogate the
    /// player without anything having to remember to write those values down. They are
    /// registered here as providers - see <see cref="PlayerState"/>, which supplies
    /// them - and are read-only, because their value belongs to the game state rather
    /// than the store.
    /// </remarks>
    public class Conditions
    {
        private readonly Dictionary<string, long> _values =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private readonly Dictionary<string, Func<long>> _named =
            new Dictionary<string, Func<long>>(StringComparer.Ordinal);

        private readonly List<KeyValuePair<string, Func<string, long>>> _prefixed =
            new List<KeyValuePair<string, Func<string, long>>>();

        public IReadOnlyDictionary<string, long> Values => _values;

        /// <summary>
        /// Registers a computed condition under an exact name, e.g. "credits".
        /// </summary>
        public void ProvideNamed(string name, Func<long> provider)
        {
            if (string.IsNullOrEmpty(name) || provider is null)
                return;

            _named[name] = provider;
        }

        /// <summary>
        /// Registers a family of computed conditions under a prefix, e.g.
        /// "flagship planet: ". The provider receives the rest of the key.
        /// </summary>
        public void ProvidePrefixed(string prefix, Func<string, long> provider)
        {
            if (string.IsNullOrEmpty(prefix) || provider is null)
                return;

            _prefixed.RemoveAll(entry => entry.Key == prefix);
            _prefixed.Add(new KeyValuePair<string, Func<string, long>>(prefix, provider));

            // Longest prefix first, so "outfit (all installed): " is matched before
            // "outfit: " rather than being swallowed by it.
            _prefixed.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        }

        /// <summary>Whether a name is answered by a provider rather than stored.</summary>
        public bool IsProvided(string name) => Provider(name) is not null;

        private Func<long>? Provider(string name)
        {
            if (name is null)
                return null;

            if (_named.TryGetValue(name, out Func<long>? named))
                return named;

            foreach (KeyValuePair<string, Func<string, long>> entry in _prefixed)
            {
                if (name.StartsWith(entry.Key, StringComparison.Ordinal))
                {
                    string remainder = name.Substring(entry.Key.Length);
                    return () => entry.Value(remainder);
                }
            }

            return null;
        }

        /// <summary>Reads a condition. Unset conditions are 0, never an error.</summary>
        public long Get(string name)
        {
            Func<long>? provider = Provider(name);
            if (provider is not null)
                return provider();

            return name is not null && _values.TryGetValue(name, out long value) ? value : 0L;
        }

        /// <summary>Whether a condition is non-zero, which is what "has" tests.</summary>
        public bool Has(string name) => Get(name) != 0L;

        public void Set(string name, long value)
        {
            if (string.IsNullOrEmpty(name))
                return;

            // A provided condition is derived from game state, so writing it would be
            // silently discarded on the next read. Upstream treats these as read-only.
            if (IsProvided(name))
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
