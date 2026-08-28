using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>One possible composition of a fleet, and how often it is chosen.</summary>
    public class FleetVariant
    {
        private readonly List<string> _ships = new List<string>();

        /// <summary>Relative likelihood of this variant. At least 1, as upstream clamps.</summary>
        public int Weight { get; internal set; } = 1;

        /// <summary>
        /// Ship names in this variant, one entry per hull. A line of
        /// <c>"Star Barge" 2</c> contributes two entries, matching upstream, which
        /// stores the ship pointer once per copy rather than a count.
        /// </summary>
        public IReadOnlyList<string> Ships => _ships;

        internal void Add(string ship) => _ships.Add(ship);

        /// <summary>Variant equality is by contents, which is how upstream's remove finds one.</summary>
        internal bool SameShipsAs(FleetVariant other)
        {
            if (other is null || other._ships.Count != _ships.Count)
                return false;

            var remaining = new List<string>(other._ships);
            foreach (string ship in _ships)
            {
                if (!remaining.Remove(ship))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// A fleet definition: a government and the ship compositions it flies.
    /// Port of upstream <c>Fleet::Load</c>.
    /// </summary>
    /// <remarks>
    /// The reason this exists here is association rather than spawning. A ship
    /// definition does not name a government anywhere in the data - upstream ties
    /// hulls to factions through the fleets that fly them and the shipyards that sell
    /// them - so without fleets there is no way to know that a Bactrian is a Syndicate
    /// hull, and every ship in the game is drawn in the same neutral plating.
    ///
    /// INCOMPLETE, tracked rather than dropped: personality, cargo and commodity
    /// settings, carried fighters, formations and name lists are parsed only far
    /// enough to be skipped safely. Spawning fleets into a system needs them.
    /// </remarks>
    public class Fleet
    {
        private readonly List<FleetVariant> _variants = new List<FleetVariant>();

        public Fleet(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public string Name { get; }

        /// <summary>The government that flies this fleet, or null if unstated.</summary>
        public string? Government { get; private set; }

        /// <summary>Which phrase supplies ship names, retained for later work.</summary>
        public string? Names { get; private set; }

        public IReadOnlyList<FleetVariant> Variants => _variants;

        public void Load(DataNode node)
        {
            // A second Load on the same fleet REPLACES its variants rather than adding
            // to them, but only when the first bare "variant" node arrives - so a file
            // that only adds variants leaves the originals alone.
            bool resetVariants = _variants.Count > 0;

            foreach (DataNode child in node.Children)
            {
                bool add = child.Token(0) == "add";
                bool remove = child.Token(0) == "remove";
                bool hasValue = child.Size >= 2;

                // "add" and "remove" are only meaningful before "variant" or
                // "personality"; anything else is malformed and upstream skips it.
                if ((add || remove) &&
                    (!hasValue || (child.Token(1) != "variant" && child.Token(1) != "personality")))
                {
                    continue;
                }

                int keyIndex = add || remove ? 1 : 0;
                string key = child.Token(keyIndex);

                if (key == "government" && hasValue)
                {
                    Government = child.Token(1);
                }
                else if (key == "names" && hasValue)
                {
                    Names = child.Token(1);
                }
                else if (key == "variant" && !remove)
                {
                    if (resetVariants && !add)
                    {
                        resetVariants = false;
                        _variants.Clear();
                    }

                    _variants.Add(LoadVariant(child, keyIndex));
                }
                else if (key == "variant")
                {
                    FleetVariant toRemove = LoadVariant(child, keyIndex);
                    _variants.RemoveAll(v => v.SameShipsAs(toRemove));
                }
            }
        }

        private static FleetVariant LoadVariant(DataNode node, int keyIndex)
        {
            var variant = new FleetVariant();

            // "variant 60" - the weight follows the key, and is at least 1.
            if (node.Size >= keyIndex + 2 && node.IsNumber(keyIndex + 1))
            {
                variant.Weight = Math.Max(1, (int)node.Value(keyIndex + 1));
            }

            foreach (DataNode child in node.Children)
            {
                // `"Star Barge" 2` means two of them, stored one entry per hull.
                int count = child.Size >= 2 && child.IsNumber(1) ? Math.Max(1, (int)child.Value(1)) : 1;
                for (int i = 0; i < count; i++)
                {
                    variant.Add(child.Token(0));
                }
            }

            return variant;
        }

        public override string ToString() => Name;
    }
}
