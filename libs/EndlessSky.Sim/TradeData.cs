using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Sales affect the next day's supply, which also changes through production
    /// and exchanges with neighboring systems. Supply, quotes and pending sales
    /// survive save/load; shipyard and outfitter stock belongs to Trading.
    /// INCOMPLETE, tracked rather than dropped: commodity cost basis and
    /// smuggling/illegal-goods fines.
    /// </remarks>
    public partial class TradeData
    {
        private readonly Dictionary<string, Commodity> _commodities =
            new Dictionary<string, Commodity>(StringComparer.Ordinal);

        // system name -> commodity name -> price
        /// <summary>Base price per system and commodity, before supply moves it.</summary>
        private readonly Dictionary<string, Dictionary<string, int>> _bases =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        /// <summary>Standing supply per system and commodity, which drives price.</summary>
        private readonly Dictionary<string, Dictionary<string, double>> _supply =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, int>> _prices =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        // Upstream queues sales until the next day so prices stay fixed at the counter.
        private readonly Dictionary<(string System, string Commodity), long> _purchases = new();

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

                int basePrice = (int)child.Value(2);

                // Keep the base separately: the data file states a base, and the price
                // a player sees is that base moved by standing supply.
                if (!_bases.TryGetValue(systemName, out Dictionary<string, int>? bases))
                {
                    bases = new Dictionary<string, int>(StringComparer.Ordinal);
                    _bases[systemName] = bases;
                }

                bases[child.Token(1)] = basePrice;
                SetPrice(systemName, child.Token(1), basePrice);
            }
        }

        /// <summary>How much of yesterday's supply a system keeps (upstream KEEP).</summary>
        private const double SupplyKept = 0.89;

        private const double SupplyExported = 0.10;

        /// <summary>Scale of the random daily swing in supply (upstream VOLUME).</summary>
        private const double SupplyVolume = 2000.0;

        /// <summary>Supply at which price has moved most of its range (upstream LIMIT).</summary>
        private const double SupplyLimit = 20000.0;

        /// <summary>Widest the price can move from its base, in credits.</summary>
        private const double PriceSwing = 100.0;

        /// <summary>
        /// Applies pending sales, local production and exchanges along the supplied
        /// links. GameData supplies the topology; standalone callers model local markets.
        /// </summary>
        /// <remarks>
        /// Prices in the data files are BASES, not the price a player pays. Without
        /// this every world quotes the same number forever, so a trade route found once
        /// is the best route always and there is no reason to look for another. The
        /// swing is bounded - at most 100 credits either side of base - so a route
        /// stays broadly worth running while never being quite the same twice.
        ///
        /// Randomness is injected rather than ambient, so a test can pin the walk.
        /// </remarks>
        public void StepEconomy(Func<double>? normal = null,
                                IReadOnlyDictionary<string, StarSystem>? systems = null)
        {
            var draw = normal ?? StandardNormal;
            foreach (var purchase in _purchases)
                SetSupply(purchase.Key.System, purchase.Key.Commodity,
                    Supply(purchase.Key.System, purchase.Key.Commodity) - purchase.Value);
            _purchases.Clear();

            var exports = new Dictionary<(string System, string Commodity), double>();
            foreach (var system in _bases.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                foreach (var quote in system.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    double supply = Supply(system.Key, quote.Key);
                    exports[(system.Key, quote.Key)] = supply * SupplyExported;
                    SetSupply(system.Key, quote.Key, supply * SupplyKept + draw() * SupplyVolume);
                }
            }

            if (systems is null)
                return;

            // Exports are a snapshot from before local production, never the imports
            // of a system visited earlier in this loop (GameData::StepEconomy).
            foreach (var market in _bases)
            {
                if (!systems.TryGetValue(market.Key, out StarSystem? system) || system.Links.Count == 0)
                    continue;
                foreach (string commodity in market.Value.Keys)
                {
                    if (!_commodities.TryGetValue(commodity, out Commodity? good) || !good.IsTradeable)
                        continue;
                    double supply = Supply(market.Key, commodity);
                    foreach (string name in system.Links)
                        if (systems.TryGetValue(name, out StarSystem? neighbor) && neighbor.Links.Count > 0
                            && exports.TryGetValue((name, commodity), out double sent))
                            supply += sent / neighbor.Links.Count;
                    SetSupply(market.Key, commodity, supply);
                }
            }
        }

        /// <summary>Records the signed quantity moved; only sales affect supply upstream.</summary>
        public void AddPurchase(string systemName, string commodity, int tons)
        {
            if (tons < 0 && !string.IsNullOrEmpty(systemName) && !string.IsNullOrEmpty(commodity))
                AddPendingPurchase(systemName, commodity, tons);
        }

        private void AddPendingPurchase(string systemName, string commodity, long tons)
        {
            var key = (systemName, commodity);
            _purchases.TryGetValue(key, out long previous);
            long combined = (long)Math.Clamp((decimal)previous + tons, long.MinValue, long.MaxValue);
            if (combined == 0) _purchases.Remove(key);
            else _purchases[key] = combined;
        }

        /// <summary>The price a base and a standing supply produce.</summary>
        public static int PriceFor(int basePrice, double supply) =>
            basePrice + (int)(-PriceSwing * Erf(supply / SupplyLimit));

        /// <summary>Standing supply of a commodity, 0 if the economy has not run.</summary>
        public double Supply(string systemName, string commodity) =>
            systemName != null && _supply.TryGetValue(systemName, out Dictionary<string, double>? s) &&
            s.TryGetValue(commodity, out double value)
                ? value
                : 0.0;

        /// <summary>Restores supply for an existing market and updates its price.</summary>
        public void SetSupply(string systemName, string commodity, double supply)
        {
            if (!double.IsFinite(supply) || !_bases.TryGetValue(systemName, out var bases)
                || !bases.TryGetValue(commodity, out int basePrice))
                return;
            if (!_supply.TryGetValue(systemName, out var supplies))
                _supply[systemName] = supplies = new Dictionary<string, double>(StringComparer.Ordinal);
            supplies[commodity] = supply;
            SetPrice(systemName, commodity, PriceFor(basePrice, supply));
        }

        /// <summary>The unmoved price a system's data file states.</summary>
        public int? BasePrice(string systemName, string commodity) =>
            systemName != null && _bases.TryGetValue(systemName, out Dictionary<string, int>? b) &&
            b.TryGetValue(commodity, out int value)
                ? value
                : null;

        private static readonly Random SharedRandom = new Random();

        /// <summary>Box-Muller: a standard normal from two uniforms.</summary>
        private static double StandardNormal() => StandardNormal(SharedRandom);

        internal static double StandardNormal(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>
        /// Error function, Abramowitz and Stegun 7.1.26 (accurate to about 1e-7).
        /// .NET has no erf, and the price curve is defined in terms of it.
        /// </summary>
        private static double Erf(double x)
        {
            int sign = Math.Sign(x);
            x = Math.Abs(x);

            const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741;
            const double a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;

            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
            return sign * y;
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
