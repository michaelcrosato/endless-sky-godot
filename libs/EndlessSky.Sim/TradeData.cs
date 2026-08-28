using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>One system's price for one good.</summary>
    public readonly struct TradeQuote
    {
        public TradeQuote(string systemName, string commodity, int price)
        {
            SystemName = systemName;
            Commodity = commodity;
            Price = price;
        }

        public string SystemName { get; }
        public string Commodity { get; }
        public int Price { get; }

        public override string ToString() => $"{Commodity} @ {SystemName}: {Price}";
    }

    /// <summary>
    /// The galaxy's commodity market: what goods exist and what each system pays.
    /// </summary>
    /// <remarks>
    /// Upstream stores a price per commodity on each <c>System</c>. This keeps the
    /// same information in a separate index keyed by system name, so the star-system
    /// model does not have to change to carry it.
    ///
    /// Trading in Endless Sky is entirely spatial: a good has one price per system
    /// and no market depth, so profit is just the difference between two systems'
    /// quotes times the tonnage you can carry. There is no order book to model.
    ///
    /// INCOMPLETE, tracked rather than dropped: price drift over time, the
    /// "shipyard"/"outfitter" stock lists, smuggling and illegal-goods fines, and
    /// per-planet price modifiers.
    /// </remarks>
    public class TradeData
    {
        private readonly Dictionary<string, Commodity> _commodities =
            new Dictionary<string, Commodity>(StringComparer.Ordinal);

        // system name -> commodity name -> price
        private readonly Dictionary<string, Dictionary<string, int>> _prices =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, Commodity> Commodities => _commodities;

        /// <summary>Systems that quote at least one price.</summary>
        public IEnumerable<string> PricedSystems => _prices.Keys;

        /// <summary>
        /// Reads the root <c>trade</c> block that declares every commodity.
        /// </summary>
        public void LoadTradeDefinition(DataNode tradeNode)
        {
            foreach (DataNode child in tradeNode.Children)
            {
                if (child.Token(0) != "commodity" || child.Size < 2)
                    continue;

                string name = child.Token(1);
                if (!_commodities.TryGetValue(name, out Commodity? commodity))
                {
                    commodity = new Commodity(name);
                    _commodities[name] = commodity;
                }

                commodity.Load(child);
            }
        }

        /// <summary>
        /// Reads the <c>trade &lt;commodity&gt; &lt;price&gt;</c> lines out of a system
        /// definition.
        /// </summary>
        public void LoadSystemPrices(string systemName, DataNode systemNode)
        {
            if (string.IsNullOrEmpty(systemName))
                return;

            foreach (DataNode child in systemNode.Children)
            {
                if (child.Token(0) != "trade" || child.Size < 3 || !child.IsNumber(2))
                    continue;

                SetPrice(systemName, child.Token(1), (int)child.Value(2));
            }
        }

        public void SetPrice(string systemName, string commodity, int price)
        {
            if (!_prices.TryGetValue(systemName, out Dictionary<string, int>? quotes))
            {
                quotes = new Dictionary<string, int>(StringComparer.Ordinal);
                _prices[systemName] = quotes;
            }

            quotes[commodity] = price;
        }

        /// <summary>The price a system pays for a good, or null when it does not trade in it.</summary>
        public int? Price(string systemName, string commodity)
        {
            if (systemName is null || commodity is null)
                return null;

            return _prices.TryGetValue(systemName, out Dictionary<string, int>? quotes)
                   && quotes.TryGetValue(commodity, out int price)
                ? price
                : null;
        }

        /// <summary>Everything a system trades in, as quotes.</summary>
        public IEnumerable<TradeQuote> Quotes(string systemName)
        {
            if (systemName is null || !_prices.TryGetValue(systemName, out Dictionary<string, int>? quotes))
                yield break;

            foreach (KeyValuePair<string, int> quote in quotes)
                yield return new TradeQuote(systemName, quote.Key, quote.Value);
        }

        /// <summary>
        /// Profit per ton of moving a good from one system to another. Negative when
        /// the run would lose money; null when either end does not trade the good.
        /// </summary>
        public int? ProfitPerTon(string fromSystem, string toSystem, string commodity)
        {
            int? buy = Price(fromSystem, commodity);
            int? sell = Price(toSystem, commodity);

            return buy.HasValue && sell.HasValue ? sell.Value - buy.Value : null;
        }

        /// <summary>
        /// The most profitable good to carry between two systems, or null when no run
        /// between them makes money.
        /// </summary>
        public string? BestRun(string fromSystem, string toSystem)
        {
            string? best = null;
            int bestProfit = 0;

            foreach (TradeQuote quote in Quotes(fromSystem))
            {
                int? profit = ProfitPerTon(fromSystem, toSystem, quote.Commodity);
                if (profit.HasValue && profit.Value > bestProfit)
                {
                    bestProfit = profit.Value;
                    best = quote.Commodity;
                }
            }

            return best;
        }
    }
}
