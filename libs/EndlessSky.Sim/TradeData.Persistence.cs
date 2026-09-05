using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    public partial class TradeData
    {
        /// <summary>Writes the upstream economy header, supply rows and pending trades.</summary>
        public void WriteEconomy(DataWriter writer)
        {
            writer.Write("economy");
            writer.BeginChild();
            if (_purchases.Count > 0)
            {
                writer.Write("purchases");
                writer.BeginChild();
                foreach (var purchase in _purchases.OrderBy(p => p.Key.System, StringComparer.Ordinal)
                    .ThenBy(p => p.Key.Commodity, StringComparer.Ordinal))
                    if (purchase.Value != 0)
                        writer.Write(purchase.Key.System, purchase.Key.Commodity, purchase.Value);
                writer.EndChild();
            }

            string[] headings = _prices.Values.SelectMany(p => p.Keys).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            writer.WriteToken("system");
            foreach (string commodity in headings) writer.WriteToken(commodity);
            writer.EndLine();

            foreach (var market in _prices.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // Compare against the supply that the reader will actually parse.
                // G8 rounding can cross an integer-price boundary even without a
                // direct override; retain that displayed quote as well.
                var overrides = market.Value.Where(q => BasePrice(market.Key, q.Key) is not int basis
                    || q.Value != PriceFor(basis, DataNode.Value(DataWriter.Number(Supply(market.Key, q.Key)))))
                    .OrderBy(q => q.Key, StringComparer.Ordinal).ToArray();
                if (overrides.Length == 0 && !headings.Any(c => Supply(market.Key, c) != 0))
                    continue;

                writer.WriteToken(market.Key);
                foreach (string commodity in headings) writer.WriteToken(Supply(market.Key, commodity));
                writer.EndLine();
                // Retain a direct quote override too. Nested rows extend the format
                // without changing how upstream readers interpret the supply columns.
                if (overrides.Length > 0)
                {
                    writer.BeginChild();
                    foreach (var quote in overrides) writer.Write("price", quote.Key, quote.Value);
                    writer.EndChild();
                }
            }
            writer.EndChild();
        }

        /// <summary>
        /// Replaces the active market with a saved one. Missing economy data starts at
        /// the definitions' base prices, never the state of the session being left.
        /// </summary>
        public void ReadEconomy(DataNode? node)
        {
            _supply.Clear();
            _purchases.Clear();
            _prices.Clear();
            foreach (var market in _bases)
                foreach (var quote in market.Value) SetPrice(market.Key, quote.Key, quote.Value);
            if (node is null || node.Token(0) != "economy")
                return;

            var headings = new List<string>();
            foreach (DataNode row in node.Children)
            {
                string name = row.Token(0);
                if (name == "purchases")
                {
                    foreach (DataNode purchase in row.Children)
                    {
                        long tons = purchase.IntegerValue(2);
                        if (purchase.Size >= 3 && tons != 0)
                            AddPendingPurchase(purchase.Token(0), purchase.Token(1), tons);
                    }
                }
                else if (name == "system")
                {
                    headings.Clear();
                    headings.AddRange(row.Tokens.Skip(1));
                }
                else
                {
                    for (int i = 0; i < headings.Count && i + 1 < row.Size; ++i)
                        if (row.IsNumber(i + 1)) SetSupply(name, headings[i], row.Value(i + 1));
                    foreach (DataNode quote in row.Children)
                        if (quote.Token(0) == "price" && quote.Size >= 3
                            && int.TryParse(quote.Token(2), NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out int price) && BasePrice(name, quote.Token(1)).HasValue)
                            SetPrice(name, quote.Token(1), price);
                }
            }
        }
    }
}
