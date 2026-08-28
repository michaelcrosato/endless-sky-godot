using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The loaded universe: every definition parsed out of a set of Endless Sky data files.
    ///
    /// Definitions merge by name across files, matching upstream, which is what allows a
    /// later file (or eventually a plugin) to extend an earlier definition rather than
    /// replace it. Node kinds that are not yet modelled are retained verbatim in
    /// <see cref="UnhandledNodes"/> rather than discarded, so that content coverage can be
    /// measured and no data is silently lost.
    /// </summary>
    public class GameData
    {
        private readonly Dictionary<string, ShipDefinition> _ships =
            new Dictionary<string, ShipDefinition>(StringComparer.Ordinal);

        private readonly Dictionary<string, Outfit> _outfits =
            new Dictionary<string, Outfit>(StringComparer.Ordinal);

        private readonly Dictionary<string, StarSystem> _systems =
            new Dictionary<string, StarSystem>(StringComparer.Ordinal);

        private readonly Dictionary<string, double> _spriteMass =
            new Dictionary<string, double>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _unhandled =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ShipDefinition> Ships => _ships;

        public IReadOnlyDictionary<string, Outfit> Outfits => _outfits;

        public IReadOnlyDictionary<string, StarSystem> Systems => _systems;

        /// <summary>Root-node keys that no loader claimed yet, with occurrence counts.</summary>
        public IReadOnlyDictionary<string, int> UnhandledNodes => _unhandled;

        public List<string> Diagnostics { get; } = new List<string>();

        /// <summary>Mass for a star or planet sprite, used to derive default orbital periods.</summary>
        public double SpriteMass(string sprite)
        {
            if (sprite == null)
            {
                return 0.0;
            }

            return _spriteMass.TryGetValue(sprite, out double mass) ? mass : 0.0;
        }

        /// <summary>Loads every *.txt data file under a directory tree.</summary>
        public void LoadDirectory(string root)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Data directory not found: {root}");
            }

            // Sorted so loading is deterministic and merge order is reproducible.
            IEnumerable<string> files = Directory
                .EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal);

            foreach (string file in files)
            {
                LoadFile(file);
            }

            FinishLoading();
        }

        /// <summary>
        /// Second pass, run once every file is parsed. Variants can name a base model that
        /// appears in a later file, so inheritance cannot be resolved during parsing.
        /// </summary>
        public void FinishLoading()
        {
            foreach (ShipDefinition ship in _ships.Values)
            {
                ResolveVariant(ship, new HashSet<string>(StringComparer.Ordinal));
            }

            ResolveOrbits();
        }

        private void ResolveVariant(ShipDefinition ship, HashSet<string> visiting)
        {
            if (ship.Resolved)
            {
                return;
            }

            // Mark first: a malformed cycle must not recurse forever.
            if (!visiting.Add(ship.DisplayName))
            {
                Diagnostics.Add($"Cyclic ship variant chain involving \"{ship.DisplayName}\".");
                ship.Resolved = true;
                return;
            }

            if (ship.VariantName != null)
            {
                if (_ships.TryGetValue(ship.Name, out ShipDefinition baseShip))
                {
                    // The base may itself be a variant, so resolve it first.
                    ResolveVariant(baseShip, visiting);
                    ship.InheritFrom(baseShip);
                }
                else
                {
                    Diagnostics.Add(
                        $"Ship variant \"{ship.VariantName}\" names unknown base model \"{ship.Name}\".");
                }
            }

            ship.ApplyAddedAttributes();
            ship.Resolved = true;
            visiting.Remove(ship.DisplayName);
        }

        public void LoadFile(string path)
        {
            DataFile file = DataFile.FromPath(path);
            Diagnostics.AddRange(file.Diagnostics);
            LoadNodes(file.Nodes);
        }

        /// <summary>
        /// Loads data from a string. Runs <see cref="FinishLoading"/> immediately, so a
        /// single self-contained snippet is usable straight away; callers assembling a
        /// universe from several snippets should use <see cref="LoadTextDeferred"/>.
        /// </summary>
        public void LoadText(string text, string sourceName = null)
        {
            LoadTextDeferred(text, sourceName);
            FinishLoading();
        }

        public void LoadTextDeferred(string text, string sourceName = null)
        {
            var file = new DataFile(text, sourceName);
            Diagnostics.AddRange(file.Diagnostics);
            LoadNodes(file.Nodes);
        }

        private void LoadNodes(IReadOnlyList<DataNode> nodes)
        {
            foreach (DataNode node in nodes)
            {
                switch (node.Token(0))
                {
                    case "ship" when node.Size >= 2:
                        LoadShip(node);
                        break;

                    case "outfit" when node.Size >= 2:
                        GetOrCreate(_outfits, node.Token(1), n => new Outfit(n)).Load(node);
                        break;

                    case "system" when node.Size >= 2:
                        GetOrCreate(_systems, node.Token(1), n => new StarSystem(n)).Load(node);
                        break;

                    case "star" when node.Size >= 2:
                        LoadStarMass(node);
                        break;

                    case "planet mass" when node.Size >= 2:
                        // "planet mass" <value> lists the sprites that share that mass as
                        // its children. Needed so moons get the right orbital period.
                        LoadPlanetMass(node);
                        break;

                    default:
                        string key = node.Token(0);
                        if (!string.IsNullOrEmpty(key))
                        {
                            _unhandled.TryGetValue(key, out int count);
                            _unhandled[key] = count + 1;
                        }

                        break;
                }
            }
        }

        private void LoadShip(DataNode node)
        {
            // "ship <name>" defines a model; "ship <base> <variant>" defines a variant that
            // starts from the base model's definition.
            string name = node.Token(1);
            string variant = node.Size >= 3 ? node.Token(2) : null;
            string key = variant ?? name;

            if (!_ships.TryGetValue(key, out ShipDefinition ship))
            {
                ship = new ShipDefinition(name, variant);
                _ships[key] = ship;
            }

            ship.Load(node);
        }

        /// <summary>"star &lt;sprite&gt;" with a "mass" child, from stars.txt.</summary>
        private void LoadStarMass(DataNode node)
        {
            string sprite = node.Token(1);
            foreach (DataNode child in node.Children)
            {
                if (child.Token(0) == "mass" && child.Size >= 2)
                {
                    _spriteMass[sprite] = child.Value(1);
                }
            }
        }

        /// <summary>"planet mass" &lt;value&gt; followed by the sprite names sharing it.</summary>
        private void LoadPlanetMass(DataNode node)
        {
            double mass = node.Value(1);
            foreach (DataNode child in node.Children)
            {
                string sprite = child.Token(0);
                if (!string.IsNullOrEmpty(sprite))
                {
                    _spriteMass[sprite] = mass;
                }
            }
        }

        private static T GetOrCreate<T>(IDictionary<string, T> map, string name, Func<string, T> create)
        {
            if (!map.TryGetValue(name, out T value))
            {
                value = create(name);
                map[name] = value;
            }

            return value;
        }

        private void ResolveOrbits()
        {
            foreach (StarSystem system in _systems.Values)
            {
                system.ResolveOrbits(SpriteMass);
            }
        }

        /// <summary>
        /// Builds a flyable ship from a definition, installing its default outfits.
        /// Outfits that are not defined in the loaded data are reported, not skipped
        /// silently, because a missing engine changes the ship's handling completely.
        /// </summary>
        public Ship BuildShip(string definitionName, out List<string> missingOutfits)
        {
            missingOutfits = new List<string>();
            if (!_ships.TryGetValue(definitionName, out ShipDefinition definition))
            {
                throw new KeyNotFoundException($"No ship definition named \"{definitionName}\".");
            }

            var ship = new Ship(definition);
            foreach (string outfitName in definition.OutfitNames)
            {
                if (_outfits.TryGetValue(outfitName, out Outfit outfit))
                {
                    ship.AddOutfit(outfit);
                }
                else
                {
                    missingOutfits.Add(outfitName);
                }
            }

            return ship;
        }

        public Ship BuildShip(string definitionName) => BuildShip(definitionName, out _);
    }
}
