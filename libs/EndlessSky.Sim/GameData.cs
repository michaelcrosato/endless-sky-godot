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
    /// replace it. Node kinds that are not yet modelled are COUNTED in
    /// <see cref="UnhandledNodes"/> rather than retained — a tally of root-node keys and
    /// how often each was skipped, which is enough to measure content coverage and
    /// drive it down, but is not the nodes themselves. Nothing can be replayed from
    /// one, and a node kind counted there IS dropped — it is visible in the tally
    /// rather than silent, which is the honest claim.
    /// </summary>
    public class GameData
    {
        private readonly Dictionary<string, ShipDefinition> _ships =
            new Dictionary<string, ShipDefinition>(StringComparer.Ordinal);

        private readonly Dictionary<string, Outfit> _outfits =
            new Dictionary<string, Outfit>(StringComparer.Ordinal);

        private readonly Dictionary<string, StarSystem> _systems =
            new Dictionary<string, StarSystem>(StringComparer.Ordinal);

        private readonly Dictionary<string, Planet> _planets =
            new Dictionary<string, Planet>(StringComparer.Ordinal);

        private readonly Dictionary<string, Sale> _shipyards =
            new Dictionary<string, Sale>(StringComparer.Ordinal);

        private readonly Dictionary<string, Sale> _outfitters =
            new Dictionary<string, Sale>(StringComparer.Ordinal);

        private readonly Dictionary<string, Minable> _minables =
            new Dictionary<string, Minable>(StringComparer.Ordinal);

        private readonly Dictionary<string, StartScenario> _starts =
            new Dictionary<string, StartScenario>(StringComparer.Ordinal);

        private readonly Dictionary<string, Wormhole> _wormholes =
            new Dictionary<string, Wormhole>(StringComparer.Ordinal);

        private readonly Dictionary<string, Government> _governments =
            new Dictionary<string, Government>(StringComparer.Ordinal);

        private readonly Dictionary<string, Conversation> _conversations =
            new Dictionary<string, Conversation>(StringComparer.Ordinal);

        private readonly Dictionary<string, GameEvent> _events =
            new Dictionary<string, GameEvent>(StringComparer.Ordinal);

        private readonly Dictionary<string, Mission> _missions =
            new Dictionary<string, Mission>(StringComparer.Ordinal);

        private readonly Dictionary<string, Fleet> _fleets =
            new Dictionary<string, Fleet>(StringComparer.Ordinal);

        /// <summary>Ship name to the government that flies or sells it.</summary>
        private readonly Dictionary<string, string> _shipGovernment =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, double> _spriteMass =
            new Dictionary<string, double>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _unhandled =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ShipDefinition> Ships => _ships;

        public IReadOnlyDictionary<string, Outfit> Outfits => _outfits;

        public IReadOnlyDictionary<string, StarSystem> Systems => _systems;

        /// <summary>
        /// The system a named world orbits in, or null if nothing in the galaxy carries
        /// that name.
        /// </summary>
        /// <remarks>
        /// Worlds are named globally but live inside a system's object tree, so going
        /// the other way is a search rather than a lookup. Missions, text substitution
        /// and the tutorial share this walk so they resolve the same destination.
        /// </remarks>
        public StarSystem? SystemOf(string? planetName)
        {
            if (string.IsNullOrEmpty(planetName))
                return null;

            foreach (StarSystem system in _systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                    if (obj.PlanetName == planetName)
                        return system;

            return null;
        }

        public IReadOnlyDictionary<string, Planet> Planets => _planets;

        public IReadOnlyDictionary<string, Sale> Shipyards => _shipyards;

        public IReadOnlyDictionary<string, Sale> Outfitters => _outfitters;

        public IReadOnlyDictionary<string, Fleet> Fleets => _fleets;

        public IReadOnlyDictionary<string, Mission> Missions => _missions;

        public IReadOnlyDictionary<string, GameEvent> Events => _events;

        /// <summary>Mineable asteroid types, by name.</summary>
        public IReadOnlyDictionary<string, Minable> Minables => _minables;

        /// <summary>Where a new pilot can begin.</summary>
        public IReadOnlyDictionary<string, StartScenario> Starts => _starts;

        /// <summary>
        /// The start a new game uses: the one named "default" if content defines it,
        /// else the first that loaded.
        /// </summary>
        public StartScenario? DefaultStart =>
            _starts.TryGetValue("default", out StartScenario? found)
                ? found
                : _starts.Values.FirstOrDefault();

        /// <summary>Passages between systems that are not hyperspace links.</summary>
        public IReadOnlyDictionary<string, Wormhole> Wormholes => _wormholes;

        /// <summary>Every faction, with its attitudes and reputation.</summary>
        public IReadOnlyDictionary<string, Government> Governments => _governments;

        /// <summary>Conversations defined at top level, referenced by missions by name.</summary>
        public IReadOnlyDictionary<string, Conversation> Conversations => _conversations;

        /// <summary>Commodity definitions plus per-system prices.</summary>
        public TradeData Trade { get; } = new TradeData();

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
            ResolvePlanets();
            ResolveShipGovernments();
            ResolveWeapons();
        }

        /// <summary>
        /// The government that flies or sells a ship, or null when nothing does.
        /// </summary>
        /// <remarks>
        /// Ship definitions never name a government. Upstream associates a hull with a
        /// faction only indirectly, through the fleets that fly it and the shipyards
        /// that stock it, so this index is the only route from a hull to the faction
        /// whose design language it should wear.
        /// </remarks>
        public string? GovernmentOf(string shipName) =>
            shipName != null && _shipGovernment.TryGetValue(shipName, out string? government)
                ? government
                : null;

        /// <summary>
        /// Builds the ship-to-government index from fleets first, then shipyards.
        /// </summary>
        /// <remarks>
        /// Fleets win over shipyards because flying a hull is a stronger claim on it
        /// than selling one: shipyards on independent worlds stock other factions'
        /// ships, so a shipyard-first index labels half the galaxy Independent.
        ///
        /// Within fleets a hull goes to the government that flies it MOST, scored by
        /// summed variant weight - the same weight that decides how often the variant
        /// actually spawns. Taking the first claimant instead is both arbitrary and
        /// order-dependent: it made the Star Barge, the most common human freighter in
        /// the game, come out as "Hai Merchant (Human)", because one Hai fleet flies
        /// human hulls and happened to load first. Ties break on the government name so
        /// the result never depends on file iteration order.
        ///
        /// Variants are folded onto their base hull: "Star Barge (Armed)" is a
        /// Merchant ship because "Star Barge" is, and most variants are never named by
        /// a fleet directly.
        /// </remarks>
        private void ResolveShipGovernments()
        {
            _shipGovernment.Clear();

            var flownBy = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            foreach (Fleet fleet in _fleets.Values)
            {
                if (string.IsNullOrEmpty(fleet.Government))
                    continue;

                foreach (FleetVariant variant in fleet.Variants)
                    foreach (string ship in variant.Ships)
                        Score(flownBy, ship, fleet.Government!, variant.Weight);
            }

            foreach (KeyValuePair<string, Dictionary<string, long>> entry in flownBy)
                _shipGovernment[entry.Key] = Winner(entry.Value);

            // Shipyards fill the gaps: a hull nobody flies but somebody sells.
            var soldBy = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            foreach (Planet planet in _planets.Values)
            {
                if (string.IsNullOrEmpty(planet.Government))
                    continue;

                foreach (string shipyardName in planet.Shipyards)
                {
                    if (!_shipyards.TryGetValue(shipyardName, out Sale? shipyard))
                        continue;

                    foreach (string ship in shipyard.Items)
                        if (!_shipGovernment.ContainsKey(ship))
                            Score(soldBy, ship, planet.Government!, 1);
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, long>> entry in soldBy)
                _shipGovernment[entry.Key] = Winner(entry.Value);

            // Finally let variants inherit from the hull they are based on. A variant is
            // keyed by its own display name, while Name still holds the base model.
            foreach (ShipDefinition ship in _ships.Values)
            {
                if (ship.VariantName is null || _shipGovernment.ContainsKey(ship.DisplayName))
                    continue;

                if (_shipGovernment.TryGetValue(ship.Name, out string? government))
                    _shipGovernment[ship.DisplayName] = government;
            }
        }

        private static void Score(Dictionary<string, Dictionary<string, long>> tally,
                                  string ship, string government, long weight)
        {
            if (!tally.TryGetValue(ship, out Dictionary<string, long>? byGovernment))
            {
                byGovernment = new Dictionary<string, long>(StringComparer.Ordinal);
                tally[ship] = byGovernment;
            }

            byGovernment.TryGetValue(government, out long running);
            byGovernment[government] = running + weight;
        }

        /// <summary>Highest score wins; ties break on name so the result is stable.</summary>
        private static string Winner(Dictionary<string, long> byGovernment)
        {
            string best = string.Empty;
            long bestScore = long.MinValue;

            foreach (KeyValuePair<string, long> entry in byGovernment)
            {
                if (entry.Value > bestScore ||
                    (entry.Value == bestScore && string.CompareOrdinal(entry.Key, best) < 0))
                {
                    best = entry.Key;
                    bestScore = entry.Value;
                }
            }

            return best;
        }

        /// <summary>
        /// Attaches each stellar object to the planet it names. Systems and planets
        /// live in separate files and load in whatever order the directory yields, so
        /// the link can only be made once both are in.
        /// </summary>
        private void ResolvePlanets()
        {
            foreach (StarSystem system in _systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                    if (obj.PlanetName != null && _planets.TryGetValue(obj.PlanetName, out Planet? planet))
                        obj.Planet = planet;
        }

        /// <summary>
        /// Links each cluster weapon to the weapons it releases.
        /// </summary>
        /// <remarks>
        /// Must run after every outfit is loaded, because a weapon's damage INCLUDES
        /// its submunitions' damage. Until this runs, a carrier round such as the
        /// Korath Minelayer reports only its own negative damage and appears to repair
        /// whatever it hits.
        /// </remarks>
        private void ResolveWeapons()
        {
            Weapon? Lookup(string name) =>
                _outfits.TryGetValue(name, out Outfit? outfit) && outfit.IsWeapon ? outfit.Weapon : null;

            foreach (Outfit outfit in _outfits.Values)
            {
                if (outfit.IsWeapon)
                    outfit.Weapon.ResolveSubmunitions(Lookup);
            }
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
                if (_ships.TryGetValue(ship.Name, out ShipDefinition? baseShip))
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
            ship.DeriveComputedAttributes();
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
        public void LoadText(string text, string? sourceName = null)
        {
            LoadTextDeferred(text, sourceName);
            FinishLoading();
        }

        public void LoadTextDeferred(string text, string? sourceName = null)
        {
            var file = new DataFile(text, sourceName);
            Diagnostics.AddRange(file.Diagnostics);
            LoadNodes(file.Nodes);
        }

        /// <summary>
        /// Re-loads a single definition node over the universe, which is how an event
        /// patches the galaxy.
        /// </summary>
        /// <remarks>
        /// Deliberately the same path a file takes. Every Load in this layer is
        /// additive over whatever the object already holds - that is how variants,
        /// plugin overrides and "add"/"remove" children all work - so an event
        /// applying "shipyard Kestrel" with a ship under it patches the existing
        /// shipyard rather than replacing it. Links are the exception: they are a
        /// modification rather than a definition and have no node of their own.
        /// </remarks>
        public void ApplyChange(DataNode node)
        {
            if (node is null || node.Size < 2)
                return;

            switch (node.Token(0))
            {
                case "link":
                case "unlink":
                    // "link <a> <b>" joins two systems in both directions.
                    if (node.Size < 3)
                        return;

                    bool add = node.Token(0) == "link";
                    Relink(node.Token(1), node.Token(2), add);
                    Relink(node.Token(2), node.Token(1), add);
                    return;

                default:
                    LoadNodes(new[] { node });
                    return;
            }
        }

        private void Relink(string from, string to, bool add)
        {
            if (!_systems.TryGetValue(from, out StarSystem? system))
                return;

            if (add)
                system.AddLink(to);
            else
                system.RemoveLink(to);
        }

        private void LoadNodes(IReadOnlyList<DataNode> nodes)
        {
            // A bare `overwrite` root node puts the NEXT definition into replace mode
            // rather than the usual merge (UniverseObjects.cpp:381-405). That is
            // upstream's mechanism for a plugin to supersede a vanilla definition
            // instead of adding to it — the difference between replacing a ship and
            // doubling its mass — and the directive asks this loader to be designed for
            // plugin overrides. It applies to one definition only (:727-728).
            bool overwrite = false;

            foreach (DataNode node in nodes)
            {
                if (node.Token(0) == "overwrite")
                {
                    overwrite = true;
                    continue;
                }

                if (overwrite)
                {
                    Forget(node);
                }

                overwrite = false;

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
                        Trade.LoadSystemPrices(node.Token(1), node);
                        break;

                    case "planet" when node.Size >= 2:
                        GetOrCreate(_planets, node.Token(1), n => new Planet(n)).Load(node);
                        break;

                    case "trade":
                        Trade.LoadTradeDefinition(node);
                        break;

                    case "minable" when node.Size >= 2:
                        GetOrCreate(_minables, node.Token(1), n => new Minable(n)).Load(node);
                        break;

                    case "start" when node.Size >= 2:
                        GetOrCreate(_starts, node.Token(1), n => new StartScenario(n)).Load(node);
                        break;

                    case "wormhole" when node.Size >= 2:
                        GetOrCreate(_wormholes, node.Token(1), n => new Wormhole(n)).Load(node);
                        break;

                    case "government" when node.Size >= 2:
                        GetOrCreate(_governments, node.Token(1), n => new Government(n)).Load(node);
                        break;

                    case "conversation" when node.Size >= 2:
                        _conversations[node.Token(1)] = Conversation.Load(node);
                        break;

                    case "event" when node.Size >= 2:
                        GetOrCreate(_events, node.Token(1), n => new GameEvent(n)).Load(node);
                        break;

                    case "mission" when node.Size >= 2:
                        GetOrCreate(_missions, node.Token(1), n => new Mission(n)).Load(node);
                        break;

                    case "fleet" when node.Size >= 2:
                        GetOrCreate(_fleets, node.Token(1), n => new Fleet(n)).Load(node);
                        break;

                    case "shipyard" when node.Size >= 2:
                        GetOrCreate(_shipyards, node.Token(1), n => new Sale(n)).Load(node);
                        break;

                    case "outfitter" when node.Size >= 2:
                        GetOrCreate(_outfitters, node.Token(1), n => new Sale(n)).Load(node);
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

        /// <summary>
        /// Drops any stored definition this node would otherwise merge into, so the
        /// loader builds a fresh one. This is what <c>overwrite</c> means.
        /// </summary>
        private void Forget(DataNode node)
        {
            if (node.Size < 2)
                return;

            string name = node.Token(1);

            switch (node.Token(0))
            {
                case "ship": _ships.Remove(node.Size >= 3 ? node.Token(2) : name); break;
                case "outfit": _outfits.Remove(name); break;
                case "system": _systems.Remove(name); break;
                case "planet": _planets.Remove(name); break;
                case "shipyard": _shipyards.Remove(name); break;
                case "outfitter": _outfitters.Remove(name); break;
                case "government": _governments.Remove(name); break;
                case "fleet": _fleets.Remove(name); break;
                case "mission": _missions.Remove(name); break;
                case "event": _events.Remove(name); break;
                case "conversation": _conversations.Remove(name); break;
                case "minable": _minables.Remove(name); break;
                case "start": _starts.Remove(name); break;
                case "wormhole": _wormholes.Remove(name); break;
            }
        }

        private void LoadShip(DataNode node)
        {
            // "ship <name>" defines a model; "ship <base> <variant>" defines a variant that
            // starts from the base model's definition.
            string name = node.Token(1);
            string? variant = node.Size >= 3 ? node.Token(2) : null;
            string key = variant ?? name;

            if (!_ships.TryGetValue(key, out ShipDefinition? ship))
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
            where T : class
        {
            if (!map.TryGetValue(name, out T? value))
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
            if (!_ships.TryGetValue(definitionName, out ShipDefinition? definition))
            {
                throw new KeyNotFoundException($"No ship definition named \"{definitionName}\".");
            }

            var ship = new Ship(definition);

            // Give it the faction that flies it. A ship with no government is not a
            // valid game state: hostility, reputation and even projectile filtering all
            // key off it - a shot passes through any body its shooter is not at war
            // with, and that check is what stops a round striking the hull it was fired
            // from.
            string? government = GovernmentOf(definition.DisplayName);
            if (government != null && _governments.TryGetValue(government, out Government? faction))
                ship.Government = faction;

            // Hardpoints before weapons, which is upstream's order in
            // Ship::FinishLoading: the Armament exists first, and each weapon outfit is
            // handed to it as it is installed. Leaving the mounts until later meant
            // every hull this method produced carried its guns as inventory with no
            // hardpoint to fire them from -- so callers had to know to call BuildMounts
            // themselves, and the ones that forgot shipped an unarmed ship.
            ship.BuildMounts();

            foreach (string outfitName in definition.OutfitNames)
            {
                if (_outfits.TryGetValue(outfitName, out Outfit? outfit))
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
