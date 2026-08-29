using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A predicate over places. Port of upstream <c>LocationFilter</c>.
    /// </summary>
    /// <remarks>
    /// This is how content targets the galaxy without naming worlds one by one:
    /// mission sources and destinations, fleet spawn rules and shop availability all
    /// use it.
    /// <code>
    /// location
    ///     government "Republic"
    ///     attributes farming textiles
    ///     not
    ///         attributes "core"
    /// </code>
    ///
    /// The attribute rule is the subtle one and reads backwards from intuition:
    /// tokens WITHIN one <c>attributes</c> line are alternatives (the place needs any
    /// one of them), while SEPARATE <c>attributes</c> lines all have to be satisfied.
    /// So the example wants a farming-or-textiles world, and two lines would demand
    /// both. Upstream implements it as "each set must intersect the place's
    /// attributes".
    ///
    /// INCOMPLETE, tracked rather than dropped: <c>near</c> and <c>distance</c>
    /// (needs galaxy geometry), <c>neighbor</c>, <c>visited</c>, <c>category</c> and
    /// <c>outfits</c>. Those keys are parsed and recorded so a filter using them is
    /// not silently treated as unconditional - <see cref="HasUnmodelledTerms"/>
    /// reports it.
    /// </remarks>
    public class LocationFilter
    {
        private readonly HashSet<string> _planets = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _systems = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _governments = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<HashSet<string>> _attributeSets = new List<HashSet<string>>();
        private readonly List<LocationFilter> _notFilters = new List<LocationFilter>();

        public IReadOnlyCollection<string> Planets => _planets;
        public IReadOnlyCollection<string> Systems => _systems;
        public IReadOnlyCollection<string> Governments => _governments;
        public IReadOnlyList<HashSet<string>> AttributeSets => _attributeSets;
        public IReadOnlyList<LocationFilter> NotFilters => _notFilters;

        /// <summary>Keys we parse but cannot evaluate yet, e.g. "near", "distance".</summary>
        public IReadOnlyCollection<string> UnmodelledTerms => _unmodelled;
        private readonly HashSet<string> _unmodelled = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when this filter uses terms we cannot evaluate. A caller that needs
        /// correctness rather than a best guess should treat such a filter as unknown
        /// rather than as a match.
        /// </summary>
        public bool HasUnmodelledTerms => _unmodelled.Count > 0;

        /// <summary>Closest the place may be to the origin, in jumps. Null for no limit.</summary>
        public int? OriginMinJumps { get; private set; }

        /// <summary>Furthest the place may be from the origin, in jumps.</summary>
        public int? OriginMaxJumps { get; private set; }

        /// <summary>A named system to measure from instead of the origin.</summary>
        public string? CenterSystem { get; private set; }

        public int? CenterMinJumps { get; private set; }

        public int? CenterMaxJumps { get; private set; }

        /// <summary>Whether this filter tests distance at all.</summary>
        public bool HasDistanceTerms =>
            OriginMaxJumps.HasValue || CenterMaxJumps.HasValue || CenterSystem != null;

        /// <summary>An empty filter matches everything, as upstream's does.</summary>
        /// <summary>
        /// Whether this filter says nothing at all, and so restricts nothing.
        /// </summary>
        /// <remarks>
        /// Distance counts. A filter reading only "within three jumps" is a real
        /// restriction, and reporting it empty made every distance-only destination
        /// resolve to nothing — which is most of them, in both the generated universe
        /// and upstream content. The symptom is a job board reading "Deliver grain to"
        /// with the destination simply missing.
        /// </remarks>
        public bool IsEmpty =>
            _planets.Count == 0 && _systems.Count == 0 && _governments.Count == 0 &&
            _attributeSets.Count == 0 && _notFilters.Count == 0 && _unmodelled.Count == 0 &&
            !HasDistanceTerms;

        public static LocationFilter Load(DataNode node)
        {
            var filter = new LocationFilter();
            filter.LoadInto(node);
            return filter;
        }

        private void LoadInto(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                // `not` has two shapes, and reading only the first is what made most
                // filters in the dataset match nothing. Upstream
                // (LocationFilter.cpp:184-190): alone on its line it opens a nested
                // block; with tokens after it, the REST OF THAT LINE is the negated
                // term. Treating the inline form as a block produced an exclusion with
                // no terms in it, and an empty filter matches everything -- so the
                // parent rejected every place in the galaxy. The dataset uses the
                // inline form 676 times against 31 blocks.
                if (child.Token(0) == "not")
                {
                    var exclusion = new LocationFilter();
                    if (child.Size == 1)
                        exclusion.LoadInto(child);
                    else
                        exclusion.LoadTerm(child, keyIndex: 1);

                    _notFilters.Add(exclusion);
                    continue;
                }

                LoadTerm(child, keyIndex: 0);
            }
        }

        /// <summary>
        /// Reads one term of a filter from a line, where the key sits at
        /// <paramref name="keyIndex"/> and its values follow it.
        /// </summary>
        /// <remarks>
        /// The index is a parameter because upstream's <c>LoadChild</c> reads the same
        /// grammar at an offset when the line begins with <c>not</c> or
        /// <c>neighbor</c> -- its <c>valueIndex = 1 + isNot</c>
        /// (<c>LocationFilter.cpp:533-534</c>).
        /// </remarks>
        private void LoadTerm(DataNode child, int keyIndex)
        {
            string key = child.Token(keyIndex);
            int first = keyIndex + 1;

            switch (key)
            {
                case "planet":
                    Collect(child, _planets, first);
                    break;

                case "system":
                    Collect(child, _systems, first);
                    break;

                case "government":
                    Collect(child, _governments, first);
                    break;

                case "attributes":
                    {
                        var set = new HashSet<string>(StringComparer.Ordinal);
                        Collect(child, set, first);
                        // Upstream drops an empty set rather than matching nothing;
                        // it is almost always a typo.
                        if (set.Count > 0)
                            _attributeSets.Add(set);
                        break;
                    }

                case "distance" when child.Size > first && child.IsNumber(first):
                    // "distance <max>" or "distance <min> <max>", in JUMPS from
                    // wherever the mission is being offered.
                    if (child.Size > first + 1 && child.IsNumber(first + 1))
                    {
                        OriginMinJumps = (int)child.Value(first);
                        OriginMaxJumps = (int)child.Value(first + 1);
                    }
                    else
                    {
                        OriginMinJumps = 0;
                        OriginMaxJumps = (int)child.Value(first);
                    }
                    break;

                case "near" when child.Size > first:
                    // "near <system> [<min>] <max>" - the same test, but around a
                    // named system rather than around the player.
                    CenterSystem = child.Token(first);
                    if (child.Size > first + 2 && child.IsNumber(first + 1) && child.IsNumber(first + 2))
                    {
                        CenterMinJumps = (int)child.Value(first + 1);
                        CenterMaxJumps = (int)child.Value(first + 2);
                    }
                    else if (child.Size > first + 1 && child.IsNumber(first + 1))
                    {
                        CenterMinJumps = 0;
                        CenterMaxJumps = (int)child.Value(first + 1);
                    }
                    break;

                default:
                    if (!string.IsNullOrEmpty(key))
                        _unmodelled.Add(key);
                    break;
            }
        }

        /// <summary>
        /// Values come from the rest of the line AND from any indented continuation,
        /// so both of these mean the same thing upstream:
        /// <c>attributes a b</c> and <c>attributes</c> with children <c>a</c>, <c>b</c>.
        /// </summary>
        private static void Collect(DataNode node, HashSet<string> into, int first = 1)
        {
            for (int i = first; i < node.Size; i++)
                into.Add(node.Token(i));

            foreach (DataNode grand in node.Children)
                for (int i = 0; i < grand.Size; i++)
                    into.Add(grand.Token(i));
        }

        /// <summary>
        /// Whether a world satisfies this filter.
        /// </summary>
        /// <param name="planet">The world under test.</param>
        /// <param name="systemName">
        /// Its system, when known. A filter naming systems cannot be satisfied without
        /// it, so passing null makes such a filter fail rather than pass by default.
        /// </param>
        /// <summary>
        /// Whether a place satisfies this filter, including its distance terms.
        /// </summary>
        /// <remarks>
        /// Distance is measured in JUMPS over the hyperspace link graph, not in map
        /// units: "distance 3" means three jumps out, which is what a player
        /// experiences as near or far. It needs the galaxy to measure against, which is
        /// why this overload takes <paramref name="data"/> and the plain one cannot
        /// answer distance questions.
        /// </remarks>
        public bool Matches(Planet planet, string? systemName, GameData? data, string? originSystem)
        {
            if (!Matches(planet, systemName))
                return false;

            if (!HasDistanceTerms)
                return true;

            if (data is null || systemName is null)
                return false;

            if (OriginMaxJumps.HasValue)
            {
                if (originSystem is null)
                    return false;

                int jumps = JumpDistance(data, originSystem, systemName);
                if (jumps < 0 || jumps > OriginMaxJumps.Value ||
                    jumps < (OriginMinJumps ?? 0))
                {
                    return false;
                }
            }

            if (CenterSystem != null && CenterMaxJumps.HasValue)
            {
                int jumps = JumpDistance(data, CenterSystem, systemName);
                if (jumps < 0 || jumps > CenterMaxJumps.Value ||
                    jumps < (CenterMinJumps ?? 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Jumps from one system to every other, cached per origin.
        /// </summary>
        /// <remarks>
        /// Resolving one mission's destination tests every inhabited planet in the
        /// galaxy, and doing a fresh search for each turned a whole-dataset pass into
        /// a minute of work. One search per ORIGIN answers every question asked of it,
        /// and there are far fewer origins than planets.
        ///
        /// Keyed by the GameData instance as well as the system name, so a second
        /// universe (a test fixture, say) cannot read the first one's answers.
        /// </remarks>
        private static readonly Dictionary<(GameData, string), Dictionary<string, int>> DistanceCache =
            new Dictionary<(GameData, string), Dictionary<string, int>>();

        /// <summary>
        /// Jumps over the hyperspace link graph between two systems, or -1 when there
        /// is no route. Shared because a mission's per-jump deadline needs the same
        /// number a distance filter does.
        /// </summary>
        public static int JumpDistance(GameData data, string from, string to)
        {
            if (from == to)
                return 0;

            Dictionary<string, int> distances = DistancesFrom(data, from);
            return distances.TryGetValue(to, out int jumps) ? jumps : -1;
        }

        private static Dictionary<string, int> DistancesFrom(GameData data, string from)
        {
            if (DistanceCache.TryGetValue((data, from), out Dictionary<string, int>? cached))
                return cached;

            var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
            var frontier = new Queue<string>();
            frontier.Enqueue(from);

            while (frontier.Count > 0)
            {
                string name = frontier.Dequeue();
                if (!data.Systems.TryGetValue(name, out StarSystem? system))
                    continue;

                int next = distances[name] + 1;
                foreach (string link in system.Links)
                {
                    if (distances.ContainsKey(link))
                        continue;

                    distances[link] = next;
                    frontier.Enqueue(link);
                }
            }

            DistanceCache[(data, from)] = distances;
            return distances;
        }

        public bool Matches(Planet planet, string? systemName = null)
        {
            if (planet is null)
                return false;

            if (_planets.Count > 0 && !_planets.Contains(planet.Name))
                return false;

            if (_systems.Count > 0 && (systemName is null || !_systems.Contains(systemName)))
                return false;

            if (_governments.Count > 0 &&
                (planet.Government is null || !_governments.Contains(planet.Government)))
                return false;

            // Each set must share at least one attribute with the world.
            foreach (HashSet<string> set in _attributeSets)
            {
                if (!set.Any(planet.Attributes.Contains))
                    return false;
            }

            foreach (LocationFilter exclusion in _notFilters)
            {
                if (exclusion.Matches(planet, systemName))
                    return false;
            }

            return true;
        }

        public override string ToString()
        {
            if (IsEmpty) return "anywhere";

            var parts = new List<string>();
            if (_planets.Count > 0) parts.Add($"planet({_planets.Count})");
            if (_systems.Count > 0) parts.Add($"system({_systems.Count})");
            if (_governments.Count > 0) parts.Add($"government({_governments.Count})");
            if (_attributeSets.Count > 0) parts.Add($"attributes x{_attributeSets.Count}");
            if (_notFilters.Count > 0) parts.Add($"not x{_notFilters.Count}");
            if (_unmodelled.Count > 0) parts.Add("unmodelled: " + string.Join("/", _unmodelled));
            return string.Join(", ", parts);
        }
    }
}
