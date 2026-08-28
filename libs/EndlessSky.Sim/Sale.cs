using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A named stock list, as declared by a root <c>shipyard</c> or <c>outfitter</c>
    /// node. Port of upstream <c>Shop&lt;Item&gt;::Load</c> and <c>Sale::LoadSingle</c>.
    /// </summary>
    /// <remarks>
    /// Upstream keeps stock lists separate from the planets that sell them, so one
    /// list can be shared by every world of a faction and content can extend an
    /// existing list without editing the planets. A planet then names one or more
    /// lists and its stock is their union.
    ///
    /// A shop is NOT simply a list of names. Alongside bare item entries it may carry
    /// structural blocks:
    /// <code>
    /// outfitter "Speck Round Restock"
    ///     to sell
    ///         has "gamerule: universal ammo restocking"
    ///     location
    ///         attributes "outfitter"
    ///     stock
    ///         "Speck Round"
    /// </code>
    /// Treating every child's first token as an item name invents goods called "to",
    /// "location" and "stock" while silently dropping the real ones nested under
    /// <c>stock</c>.
    ///
    /// INCOMPLETE, tracked rather than dropped: the <c>to sell</c> condition set and
    /// the <c>location</c> filter are recognised and skipped, so a conditional shop
    /// currently reads as unconditionally stocked.
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

        /// <summary>True when this shop is gated by conditions we do not evaluate yet.</summary>
        public bool HasSellConditions { get; private set; }

        /// <summary>True when this shop is restricted to worlds matching a location filter.</summary>
        public bool HasLocationFilter { get; private set; }

        public bool Contains(string item) => item is not null && _items.Contains(item);

        /// <summary>
        /// Adds this node's entries. Repeated declarations of the same list accumulate
        /// rather than replace, which is how upstream lets one file extend another's.
        /// </summary>
        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                bool add = child.Token(0) == "add";
                bool remove = child.Token(0) == "remove";

                // "add"/"remove" shift which token names the thing being modified.
                string key = child.Token(add || remove ? 1 : 0);
                int valueIndex = add || remove ? 2 : 1;
                bool hasValue = child.Size > valueIndex;

                if (key == "to" && hasValue && child.Token(valueIndex) == "sell")
                {
                    HasSellConditions = !remove;
                    continue;
                }

                if (key == "location")
                {
                    HasLocationFilter = !remove;
                    continue;
                }

                if (key == "stock")
                {
                    if (remove)
                        _items.Clear();
                    else
                        foreach (DataNode entry in child.Children)
                            LoadSingle(entry);

                    continue;
                }

                LoadSingle(child);
            }
        }

        /// <summary>
        /// One item entry. Port of upstream <c>Sale::LoadSingle</c>: a bare name is an
        /// item, <c>clear</c> or a bare <c>remove</c> empties the list, and
        /// <c>add</c>/<c>remove</c> with a name modify a single entry.
        /// </summary>
        private void LoadSingle(DataNode node)
        {
            string token = node.Token(0);
            bool remove = token == "clear" || token == "remove";

            if (remove && node.Size == 1)
                _items.Clear();
            else if (remove && node.Size >= 2)
                _items.Remove(node.Token(1));
            else if (token == "add" && node.Size >= 2)
                _items.Add(node.Token(1));
            else if (!string.IsNullOrEmpty(token))
                _items.Add(token);
        }

        public override string ToString() => $"{Name} ({_items.Count} items)";
    }
}
