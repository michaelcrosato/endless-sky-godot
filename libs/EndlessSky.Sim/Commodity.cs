using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A tradeable good. Port of upstream <c>Trade::Commodity</c>.
    /// </summary>
    /// <remarks>
    /// Upstream declares these in a root <c>trade</c> block:
    /// <code>
    /// trade
    ///     commodity "Food" 100 600
    ///         "acorns"
    ///         "alfalfa"
    /// </code>
    /// The two numbers bound the price band the galaxy generates within; the child
    /// names are flavour text used when describing a hold's contents.
    ///
    /// Not every commodity has a band. Garbage, Construction, Military and the
    /// illegal categories are declared with a name only: they exist so missions and
    /// special cargo can reference them, and they are never sold on the open market.
    /// <see cref="IsTradeable"/> is the distinction, and a shop that iterates all
    /// commodities has to respect it or it will offer goods with no price.
    /// </remarks>
    public class Commodity
    {
        private readonly List<string> _items = new List<string>();

        public Commodity(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>Lowest price the galaxy generates for this good.</summary>
        public int LowPrice { get; private set; }

        /// <summary>Highest price the galaxy generates for this good.</summary>
        public int HighPrice { get; private set; }

        /// <summary>Example goods in this category, used for flavour text.</summary>
        public IReadOnlyList<string> Items => _items;

        /// <summary>
        /// Whether this good is sold on the open market. Mission and special cargo
        /// categories are declared without a price band.
        /// </summary>
        public bool IsTradeable => HighPrice > 0 && HighPrice > LowPrice;

        /// <summary>Width of the price band: the most a run of this good can earn per ton.</summary>
        public int PriceSpread => IsTradeable ? HighPrice - LowPrice : 0;

        public void Load(DataNode node)
        {
            // "commodity <name> [low] [high]"
            if (node.Size >= 4 && node.IsNumber(2) && node.IsNumber(3))
            {
                LowPrice = (int)node.Value(2);
                HighPrice = (int)node.Value(3);
            }

            foreach (DataNode child in node.Children)
                _items.Add(child.Token(0));
        }

        public override string ToString() =>
            IsTradeable ? $"{Name} ({LowPrice}-{HighPrice})" : $"{Name} (not traded)";
    }
}
