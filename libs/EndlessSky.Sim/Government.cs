using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A faction. Port of upstream <c>Government</c> plus the hostility rules from
    /// <c>Politics::IsEnemy</c>.
    /// </summary>
    /// <remarks>
    /// Hostility is not a symmetric flag on one side's enemy list, and it is not the
    /// same question for the player as for anyone else:
    ///
    /// Between two non-player governments it is <c>a.AttitudeToward(b) &lt; 0 ||
    /// b.AttitudeToward(a) &lt; 0</c> - EITHER side's dislike is enough. Checking only
    /// one side means a government that nobody has listed as an enemy is hostile to
    /// nobody, even when half the galaxy has listed it.
    ///
    /// For the player it is reputation-driven: bribed governments are never enemies,
    /// provoked ones always are, and otherwise the player is an enemy exactly while
    /// standing is negative. Without that path no government is ever hostile to the
    /// player no matter how many of their ships you destroy.
    ///
    /// INCOMPLETE, tracked rather than dropped: penalty scaling by ship cost, fines,
    /// hail phrases, raid fleets, and per-government crew attack/defense overrides
    /// beyond the defaults.
    /// </remarks>
    public class Government
    {
        /// <summary>Default reputation penalties upstream applies for acting against a government.</summary>
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

        private readonly Dictionary<string, double> _attitude =
            new Dictionary<string, double>(StringComparer.Ordinal);

        private readonly HashSet<string> _provoked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _bribed = new HashSet<string>(StringComparer.Ordinal);

        public Government(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DisplayName = name;
        }

        public string Name { get; }

        /// <summary>Name shown to the player; upstream lets content override it.</summary>
        public string DisplayName { get; private set; }

        /// <summary>True for the government representing the player themself.</summary>
        public bool IsPlayer { get; set; }

        /// <summary>
        /// The player's standing with this government. Negative means hostile.
        /// </summary>
        public double Reputation { get; private set; }

        /// <summary>
        /// Attitude toward governments not named explicitly. Upstream defaults it to
        /// 0 (indifferent) but content may set it, which is how a xenophobic faction
        /// is hostile to everyone without listing them.
        /// </summary>
        public double DefaultAttitude { get; private set; }

        /// <summary>Crew power when boarding another ship.</summary>
        public double CrewAttack { get; private set; } = 1.0;

        /// <summary>Crew power when repelling boarders.</summary>
        public double CrewDefense { get; private set; } = 2.0;

        /// <summary>Attitudes toward named governments, negative meaning hostile.</summary>
        public IReadOnlyDictionary<string, double> Attitudes => _attitude;

        /// <summary>
        /// Governments this one has explicitly listed as disliked. Retained as a
        /// convenience for content and tests that only care about the boolean.
        /// </summary>
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

                    case "default attitude" when child.Size >= 2:
                        DefaultAttitude = child.Value(1);
                        break;

                    case "crew attack" when child.Size >= 2:
                        CrewAttack = Math.Max(0.0, child.Value(1));
                        break;

                    case "crew defense" when child.Size >= 2:
                        CrewDefense = Math.Max(0.0, child.Value(1));
                        break;

                    case "attitude toward":
                        foreach (DataNode attitude in child.Children)
                        {
                            if (attitude.Size < 2 || !attitude.IsNumber(1))
                                continue;

                            double value = attitude.Value(1);
                            _attitude[attitude.Token(0)] = value;
                            if (value < 0.0)
                                Enemies.Add(attitude.Token(0));
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// How this government feels about another. Falls back to
        /// <see cref="DefaultAttitude"/> for governments it never names.
        /// </summary>
        public double AttitudeToward(Government other)
        {
            if (other is null)
                return 0.0;

            // Wholly on its own side. Upstream returns 1 here (Government.cpp:588-589),
            // and it is load-bearing: an offence is weighted by each government's
            // attitude toward the victim, so a government indifferent to what was done
            // to ITSELF would never lose any standing at all.
            if (ReferenceEquals(other, this) || other.Name == Name)
                return 1.0;

            if (_attitude.TryGetValue(other.Name, out double value))
                return value;

            // The convenience set is authoritative when content set it directly.
            return Enemies.Contains(other.Name) ? -1.0 : DefaultAttitude;
        }

        /// <summary>
        /// Whether these two governments currently shoot at each other. Port of
        /// <c>Politics::IsEnemy</c>.
        /// </summary>
        public bool IsEnemy(Government? other)
        {
            if (other is null || ReferenceEquals(other, this) || other.Name == Name)
                return false;

            // Normalise so the player, if involved, is the first party.
            Government first = this, second = other;
            if (second.IsPlayer)
                (first, second) = (second, first);

            if (first.IsPlayer)
            {
                if (first._bribed.Contains(second.Name))
                    return false;
                if (first._provoked.Contains(second.Name))
                    return true;

                // The player is an enemy exactly while their standing is negative.
                return second.Reputation < 0.0;
            }

            // Between non-player governments the question depends ONLY on the
            // attitude matrix. Provocation is scoped to the player upstream; letting
            // it leak here makes NPC fleets that should stay neutral open fire on each
            // other after any unrelated incident.
            return first.AttitudeToward(second) < 0.0 || second.AttitudeToward(first) < 0.0;
        }

        /// <summary>Whether this government is currently hostile to the player.</summary>
        public bool IsPlayerEnemy => Reputation < 0.0;

        /// <summary>
        /// Marks <paramref name="aggressor"/> as an enemy for the rest of this flight.
        /// Upstream provokes a government when it is fired on by one it did not
        /// already consider hostile, which is why a stray shot starts a fight.
        /// </summary>
        public void Provoke(Government? aggressor)
        {
            if (aggressor is null || ReferenceEquals(aggressor, this)) return;
            _provoked.Add(aggressor.Name);
            _bribed.Remove(aggressor.Name);
        }

        /// <summary>A bribed government stops treating the briber as an enemy.</summary>
        public void Bribe(Government? briber)
        {
            if (briber is null || ReferenceEquals(briber, this)) return;
            _bribed.Add(briber.Name);
            _provoked.Remove(briber.Name);
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
