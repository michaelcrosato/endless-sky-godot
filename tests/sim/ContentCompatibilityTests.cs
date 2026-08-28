using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndlessSky.Data;
using EndlessSky.Sim;
using NUnit.Framework;

namespace EndlessSky.Tests
{
    /// <summary>
    /// Milestone 7: consume the whole upstream dataset, not a curated corner of it.
    /// </summary>
    /// <remarks>
    /// The human campaign is the friendly case. These load every faction - Hai,
    /// Korath, Coalition, Pug, Quarg, Wanderer, Remnant, Avgi, Bunrodea, Incipias,
    /// Kahet, Gegno, Iije, Drak and the rest - because the alien content is where
    /// unusual syntax actually lives, and because the directive's goal is to run
    /// unmodified upstream content rather than a translated subset.
    ///
    /// These assert on totals and on the ABSENCE of parse failures rather than on
    /// specific values, so upstream adding content does not break them.
    /// </remarks>
    [TestFixture]
    public class ContentCompatibilityTests
    {
        private static IEnumerable<string> AllDataFiles()
        {
            string root = UpstreamData.Path;
            Assert.IsNotNull(root, "upstream data required");
            return Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories);
        }

        private sealed class Corpus
        {
            public int Files;
            public int RootNodes;
            public readonly List<Mission> Missions = new List<Mission>();
            public readonly List<Conversation> Conversations = new List<Conversation>();
            public readonly List<Planet> Planets = new List<Planet>();
            public readonly Dictionary<string, Sale> Shipyards = new Dictionary<string, Sale>(StringComparer.Ordinal);
            public readonly Dictionary<string, Sale> Outfitters = new Dictionary<string, Sale>(StringComparer.Ordinal);
            public readonly TradeData Trade = new TradeData();
            public readonly Dictionary<string, int> RootKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private static Corpus _cached;

        /// <summary>Parses the entire dataset once per run; it is a few hundred files.</summary>
        private static Corpus LoadEverything()
        {
            if (_cached != null)
                return _cached;

            var corpus = new Corpus();

            foreach (string path in AllDataFiles())
            {
                corpus.Files++;

                foreach (DataNode node in DataFile.FromPath(path).Nodes)
                {
                    corpus.RootNodes++;
                    string kind = node.Token(0);
                    corpus.RootKinds.TryGetValue(kind, out int seen);
                    corpus.RootKinds[kind] = seen + 1;

                    switch (kind)
                    {
                        case "mission" when node.Size >= 2:
                            {
                                var mission = new Mission(node.Token(1));
                                mission.Load(node);
                                corpus.Missions.Add(mission);
                                break;
                            }

                        case "conversation" when node.Size >= 2:
                            corpus.Conversations.Add(Conversation.Load(node));
                            break;

                        case "planet" when node.Size >= 2:
                            {
                                var planet = new Planet(node.Token(1));
                                planet.Load(node);
                                corpus.Planets.Add(planet);
                                break;
                            }

                        case "shipyard" when node.Size >= 2:
                            Accumulate(corpus.Shipyards, node);
                            break;

                        case "outfitter" when node.Size >= 2:
                            Accumulate(corpus.Outfitters, node);
                            break;

                        case "system" when node.Size >= 2:
                            corpus.Trade.LoadSystemPrices(node.Token(1), node);
                            break;

                        case "trade":
                            corpus.Trade.LoadTradeDefinition(node);
                            break;
                    }
                }
            }

            _cached = corpus;
            return corpus;
        }

        private static void Accumulate(Dictionary<string, Sale> into, DataNode node)
        {
            string name = node.Token(1);
            if (!into.TryGetValue(name, out Sale sale))
            {
                sale = new Sale(name);
                into[name] = sale;
            }

            sale.Load(node);
        }

        // --- Breadth --------------------------------------------------------------

        [Test]
        public void TheWholeDatasetParses()
        {
            Corpus corpus = LoadEverything();

            Assert.Greater(corpus.Files, 150, "upstream ships a few hundred data files");
            Assert.Greater(corpus.RootNodes, 5000, "and many thousands of definitions");

            TestContext.WriteLine($"parsed {corpus.RootNodes} definitions from {corpus.Files} files");
        }

        [Test]
        public void EveryFactionsContentLoadsNotJustTheHumanCampaign()
        {
            // Alien content is where the unusual syntax lives, so a parser that only
            // survives data/human has not really proven anything.
            string root = UpstreamData.Path;
            var factionDirs = Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !n.StartsWith("_", StringComparison.Ordinal))
                .ToList();

            Assert.Greater(factionDirs.Count, 8, "expected many faction directories");

            foreach (string dir in factionDirs)
            {
                int nodes = 0;
                foreach (string path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.txt", SearchOption.AllDirectories))
                    nodes += DataFile.FromPath(path).Nodes.Count;

                Assert.Greater(nodes, 0, $"{dir} produced no definitions");
            }

            TestContext.WriteLine("factions: " + string.Join(", ", factionDirs));
        }

        [Test]
        public void TheDatasetIsDominatedByKindsWeUnderstand()
        {
            // A coverage signal rather than a pass/fail on individual kinds: if the
            // definitions we can model stopped covering the bulk of the data, the port
            // would be drifting toward hard-coded content.
            Corpus corpus = LoadEverything();

            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "ship", "outfit", "system", "planet", "mission", "conversation",
                "shipyard", "outfitter", "government", "fleet", "phrase", "event",
                "trade", "effect", "person", "start", "news", "interface", "hazard",
                "galaxy", "sale", "color", "tip", "help", "category", "landing message",
                "star", "minable", "substitutions", "gamerules", "formation", "wormhole",
                "test", "test-data", "swizzle", "harvesting", "mission-set", "disturbance",
            };

            int knownCount = corpus.RootKinds.Where(k => known.Contains(k.Key)).Sum(k => k.Value);
            double coverage = (double)knownCount / corpus.RootNodes;

            var unknown = corpus.RootKinds.Where(k => !known.Contains(k.Key))
                .OrderByDescending(k => k.Value).Take(10).ToList();

            TestContext.WriteLine($"root-node coverage {coverage:P1}; top unrecognised: " +
                string.Join(", ", unknown.Select(u => $"{u.Key}({u.Value})")));

            Assert.Greater(coverage, 0.95, "unrecognised root kinds: " +
                string.Join(", ", unknown.Select(u => $"{u.Key}({u.Value})")));
        }

        // --- Missions across every faction ---------------------------------------

        [Test]
        public void EveryMissionInTheDatasetParsesAndKeepsItsGates()
        {
            Corpus corpus = LoadEverything();

            Assert.Greater(corpus.Missions.Count, 500, "the full dataset defines many missions");

            int gated = corpus.Missions.Count(m => !m.ToOffer.IsEmpty);
            int withActions = corpus.Missions.Count(m => m.Actions.Count > 0);

            // If the parser were silently dropping blocks these ratios would collapse.
            Assert.Greater(gated, corpus.Missions.Count / 2,
                $"only {gated} of {corpus.Missions.Count} missions kept an offer gate");
            Assert.Greater(withActions, corpus.Missions.Count / 4,
                $"only {withActions} of {corpus.Missions.Count} missions kept any action block");

            TestContext.WriteLine($"{corpus.Missions.Count} missions, {gated} gated, {withActions} with actions");
        }

        [Test]
        public void EveryMissionsGatesEvaluateWithoutThrowing()
        {
            // Conditions come from content, so an expression shape we mishandle would
            // surface here rather than in a player's save.
            Corpus corpus = LoadEverything();
            var conditions = new Conditions();

            foreach (Mission mission in corpus.Missions)
            {
                Assert.DoesNotThrow(() => mission.CanOffer(conditions), mission.Name);
                Assert.DoesNotThrow(() => mission.CanComplete(conditions), mission.Name);
                Assert.DoesNotThrow(() => mission.HasFailed(conditions), mission.Name);
            }
        }

        // --- Conversations across every faction ----------------------------------

        [Test]
        public void EveryConversationWalksToCompletionWithoutCycling()
        {
            Corpus corpus = LoadEverything();

            // Root-level conversations are only part of the picture: most mission
            // dialogue is defined INLINE inside an action block, so both are walked.
            var all = new List<Conversation>(corpus.Conversations);
            foreach (Mission mission in corpus.Missions)
                foreach (MissionAction action in mission.Actions.Values)
                    if (action.InlineConversation is not null)
                        all.Add(action.InlineConversation);

            Assert.Greater(all.Count, 100,
                $"expected many conversations; {corpus.Conversations.Count} named + inline");

            var cycled = new List<string>();
            int ended = 0;

            foreach (Conversation conversation in all)
            {
                var runner = new ConversationRunner(conversation, new Conditions());
                int guard = 0;
                while (!runner.IsFinished && guard++ < 2000)
                    runner.Choose(0);

                if (runner.AbortedOnCycle) cycled.Add(conversation.Name);
                if (runner.Outcome != ConversationOutcome.None) ended++;
            }

            Assert.IsEmpty(cycled, "conversations that tripped the cycle guard: " + string.Join(", ", cycled.Take(5)));
            Assert.Greater(ended, all.Count / 3,
                $"only {ended} of {all.Count} reached an explicit ending");

            TestContext.WriteLine($"{all.Count} conversations walked " +
                $"({corpus.Conversations.Count} named, {all.Count - corpus.Conversations.Count} inline), " +
                $"{ended} ended explicitly");
        }

        // --- Planets, shops and trade --------------------------------------------

        [Test]
        public void PlanetsResolveTheirStockListsAgainstTheRealCatalogue()
        {
            Corpus corpus = LoadEverything();

            Assert.Greater(corpus.Planets.Count, 200, "the dataset defines many worlds");
            Assert.Greater(corpus.Shipyards.Count, 10);
            Assert.Greater(corpus.Outfitters.Count, 10);

            var withShipyard = corpus.Planets.Where(p => p.HasShipyard).ToList();
            Assert.IsNotEmpty(withShipyard);

            // Every named list a planet sells from should exist somewhere in the data;
            // a dangling name means our accumulation or naming is wrong.
            var dangling = new List<string>();
            foreach (Planet planet in corpus.Planets)
            {
                foreach (string name in planet.Shipyards)
                    if (!corpus.Shipyards.ContainsKey(name)) dangling.Add($"{planet.Name} -> shipyard {name}");
                foreach (string name in planet.Outfitters)
                    if (!corpus.Outfitters.ContainsKey(name)) dangling.Add($"{planet.Name} -> outfitter {name}");
            }

            Assert.IsEmpty(dangling, "planets referencing stock lists that do not exist: " +
                string.Join("; ", dangling.Take(5)));
        }

        [Test]
        public void ShipyardStockNamesRealShips()
        {
            Corpus corpus = LoadEverything();
            GameData data = UpstreamData.Instance;

            var missing = new List<string>();
            foreach (Sale shipyard in corpus.Shipyards.Values)
                foreach (string ship in shipyard.Items)
                    if (!data.Ships.ContainsKey(ship)) missing.Add($"{shipyard.Name}: {ship}");

            Assert.IsEmpty(missing, "shipyards stocking undefined ships: " + string.Join("; ", missing.Take(5)));
        }

        [Test]
        public void OutfitterStockNamesRealOutfits()
        {
            Corpus corpus = LoadEverything();
            GameData data = UpstreamData.Instance;

            var missing = new List<string>();
            foreach (Sale outfitter in corpus.Outfitters.Values)
                foreach (string outfit in outfitter.Items)
                    if (!data.Outfits.ContainsKey(outfit)) missing.Add($"{outfitter.Name}: {outfit}");

            Assert.IsEmpty(missing, "outfitters stocking undefined outfits: " + string.Join("; ", missing.Take(5)));
        }

        [Test]
        public void TradePricesLoadAcrossTheGalaxy()
        {
            Corpus corpus = LoadEverything();

            var pricedSystems = corpus.Trade.PricedSystems.ToList();
            Assert.Greater(pricedSystems.Count, 200, "most inhabited systems quote prices");

            // Commodity definitions came from commodities.txt in the same sweep.
            Assert.IsTrue(corpus.Trade.Commodities.ContainsKey("Food"));
            Assert.IsTrue(corpus.Trade.Commodities["Food"].IsTradeable);

            // And a real run between two priced systems should be computable.
            string a = pricedSystems[0];
            string b = pricedSystems.First(s => s != a);
            Assert.DoesNotThrow(() => corpus.Trade.BestRun(a, b));

            TestContext.WriteLine($"{pricedSystems.Count} systems quote prices; " +
                $"{corpus.Trade.Commodities.Count} commodities defined");
        }

        [Test]
        public void MostPricesSitInsideTheirCommodityBandButUpstreamAllowsOutliers()
        {
            // The low/high on a commodity are the range the galaxy GENERATES within,
            // not a clamp: upstream hand-authored systems sit outside it (Anax pays
            // 600 for Heavy Metals against a 610-1310 band). So this checks the bulk
            // sits inside rather than asserting a bound upstream does not enforce.
            Corpus corpus = LoadEverything();

            int inside = 0, outside = 0;
            foreach (string system in corpus.Trade.PricedSystems)
            {
                foreach (TradeQuote quote in corpus.Trade.Quotes(system))
                {
                    if (!corpus.Trade.Commodities.TryGetValue(quote.Commodity, out Commodity commodity)
                        || !commodity.IsTradeable)
                        continue;

                    if (quote.Price >= commodity.LowPrice && quote.Price <= commodity.HighPrice)
                        inside++;
                    else
                        outside++;
                }
            }

            int total = inside + outside;
            Assert.Greater(total, 1000, "expected many quotes across the galaxy");

            double ratio = (double)inside / total;
            TestContext.WriteLine($"{inside}/{total} quotes inside their band ({ratio:P1}), {outside} outliers");

            // A parser reading the wrong tokens would scatter prices randomly; the
            // real data is overwhelmingly inside its bands.
            Assert.Greater(ratio, 0.9, $"only {ratio:P1} of quotes sat inside their commodity band");
        }
    }
}
