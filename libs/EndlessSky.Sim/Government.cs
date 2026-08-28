using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A faction. Port of the subset of upstream <c>Government</c> that combat needs:
    /// who shoots at whom, and how that changes when you shoot back.
    /// </summary>
    /// <remarks>
    /// Hostility upstream is not a static table. A government has a default attitude
    /// toward others, a player-specific reputation, and a set of provoked governments
    /// that grows when someone fires on it. Reputation is what makes attacking a
    /// friendly patrol have consequences that outlive the fight.
    ///
    /// INCOMPLETE, tracked rather than dropped: penalty scaling by ship cost, bribes,
    /// fines, "friendly"/"hostile" hail phrases, custom enemy/friendly overrides per
    /// government pair, and the atrocity flag.
    /// </remarks>
    public class Government
    {
        // Default reputation penalties upstream applies for acting against a government.
        private static readonly IReadOnlyDictionary<string, double> DefaultPenalties =
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["assist"] = -0.1,
                ["disable"] = 0.5,
                ["board"] = 0.3,
                ["capture"] = 1.0,
                ["destroy"] = 1.0,
                ["atrocity"] = 10.0,
            };

        private readonly HashSet<string> _provoked = new HashSet<string>(StringComparer.Ordinal);

        public Government(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DisplayName = name;
        }

        public string Name { get; }

        /// <summary>Name shown to the player; upstream lets content override it.</summary>
        public string DisplayName { get; private set; }

        /// <summary>
        /// The player's standing. Negative means hostile; upstream starts most
        /// governments at 0 and lets missions and combat move it.
        /// </summary>
        public double Reputation { get; private set; }

        /// <summary>Below this reputation the government turns hostile to the player.</summary>
        public double AttitudeThreshold { get; private set; }

        /// <summary>Governments this one is permanently at war with, by name.</summary>
        public HashSet<string> Enemies { get; } = new HashSet<string>(StringComparer.Ordinal);

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "display name" when child.Size >= 2:
                        DisplayName = child.Token(1);
                        break;

                    case "player reputation" when child.Size >= 2:
                        Reputation = child.Value(1);
                        break;

                    case "attitude toward":
                        // Nested: each child is "<government> <value>", negative meaning hostile.
                        foreach (DataNode attitude in child.Children)
                        {
                            if (attitude.Size >= 2 && attitude.Value(1) < 0.0)
                                Enemies.Add(attitude.Token(0));
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Whether this government currently shoots at <paramref name="other"/>.
        /// A government is never its own enemy, and provocation is one-directional
        /// until the other side reciprocates.
        /// </summary>
        public bool IsEnemy(Government other)
        {
            if (other is null) return false;
            if (ReferenceEquals(other, this) || other.Name == Name) return false;

            return Enemies.Contains(other.Name) || _provoked.Contains(other.Name);
        }

        /// <summary>Whether this government is hostile to the player, by reputation.</summary>
        public bool IsPlayerEnemy => Reputation < AttitudeThreshold;

        /// <summary>
        /// Marks <paramref name="aggressor"/> as an enemy for the rest of this flight.
        /// Upstream provokes a government when it is fired on by one it did not already
        /// consider hostile, which is why a stray shot starts a fight.
        /// </summary>
        public void Provoke(Government aggressor)
        {
            if (aggressor is null || ReferenceEquals(aggressor, this)) return;
            _provoked.Add(aggressor.Name);
        }

        /// <summary>Clears provocations, as upstream does when the player leaves the system.</summary>
        public void ClearProvocation() => _provoked.Clear();

        /// <summary>
        /// Applies the reputation penalty for an action such as "destroy" or "disable".
        /// Positive penalties reduce standing.
        /// </summary>
        public void Offend(string action, double count = 1.0)
        {
            if (action is null || !DefaultPenalties.TryGetValue(action, out double penalty))
                return;

            Reputation -= penalty * count;
        }

        public void SetReputation(double reputation) => Reputation = reputation;

        public override string ToString() => Name;
    }
}
