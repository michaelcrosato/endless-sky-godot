using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A named stock list, as declared by a root <c>shipyard</c> or <c>outfitter</c>
    /// node. Port of upstream <c>Sale</c>.
    /// </summary>
    /// <remarks>
    /// Upstream keeps stock lists separate from the planets that sell them, so one
    /// list can be shared by every world of a faction and content can add to an
    /// existing list without editing the planets. A planet then names one or more
    /// lists, and its stock is their union.
    /// <code>
    /// shipyard "Basic Ships"
    ///     "Shuttle"
    ///     "Star Barge"
    /// </code>
    /// </remarks>
    public class Sale
    {
        private readonly HashSet<string> _items = new HashSet<string>(StringComparer.Ordinal);

        public Sale(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>Names of the ships or outfits stocked.</summary>
        public IReadOnlyCollection<string> Items => _items;

        public bool Contains(string item) => item is not null && _items.Contains(item);

        /// <summary>
        /// Adds this node's entries. Repeated declarations of the same list accumulate
        /// rather than replace, which is how upstream lets one file extend another's.
        /// </summary>
        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string entry = child.Token(0);

                // "remove <name>" lets content take an item back out of a shared list.
                if (entry == "remove" && child.Size >= 2)
                    _items.Remove(child.Token(1));
                else if (entry == "add" && child.Size >= 2)
                    _items.Add(child.Token(1));
                else if (!string.IsNullOrEmpty(entry))
                    _items.Add(entry);
            }
        }

        public override string ToString() => $"{Name} ({_items.Count} items)";
    }
}
