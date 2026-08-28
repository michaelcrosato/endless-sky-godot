using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A landable world. Port of the services half of upstream <c>Planet</c>: what
    /// you can do once you are on the ground.
    /// </summary>
    /// <remarks>
    /// A planet does not own its stock lists; it names shared ones (see
    /// <see cref="Sale"/>) and its inventory is their union. That indirection is why
    /// a faction can restock every one of its worlds by editing a single list.
    ///
    /// Whether a planet is landable at all is not a flag upstream: it is implied by
    /// having a spaceport, a shipyard or an outfitter. A world with none of those is
    /// scenery.
    ///
    /// INCOMPLETE, tracked rather than dropped: conditional descriptions and
    /// spaceport text (the "to display" blocks), wormholes, tribute fleets, bribe and
    /// fine behaviour, required reputation gates, and the music/landscape fields.
    /// </remarks>
    public class Planet
    {
        private readonly List<string> _shipyards = new List<string>();
        private readonly List<string> _outfitters = new List<string>();
        private readonly HashSet<string> _attributes = new HashSet<string>(StringComparer.Ordinal);

        public Planet(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>Faction that holds the world, by name.</summary>
        public string? Government { get; private set; }

        /// <summary>Free-form tags content uses to target worlds ("dirt belt", "farming").</summary>
        public IReadOnlyCollection<string> Attributes => _attributes;

        /// <summary>Names of the shipyard stock lists this world sells from.</summary>
        public IReadOnlyList<string> Shipyards => _shipyards;

        /// <summary>Names of the outfitter stock lists this world sells from.</summary>
        public IReadOnlyList<string> Outfitters => _outfitters;

        public bool HasSpaceport { get; private set; }

        public bool HasShipyard => _shipyards.Count > 0;

        public bool HasOutfitter => _outfitters.Count > 0;

        /// <summary>
        /// Chance a landing is challenged; drives the smuggling checks upstream.
        /// Defaults to 0.25, not 0 - a world that declares nothing still runs
        /// inspections, and defaulting to zero makes every unlisted world a free port.
        /// </summary>
        public double Security { get; private set; } = 0.25;

        /// <summary>Credits per day this world pays once dominated, or 0.</summary>
        public int Tribute { get; private set; }

        /// <summary>
        /// Whether a ship can land here at all.
        /// </summary>
        /// <remarks>
        /// Upstream computes this from services AND an explicit veto:
        /// <c>(HasServices() || requiredReputation || defenseFleets) &amp;&amp;
        /// !attributes.contains("uninhabited")</c>. The veto matters - content tags a
        /// world uninhabited to make it scenery even when it carries other data.
        /// </remarks>
        public bool IsInhabited =>
            (HasSpaceport || HasShipyard || HasOutfitter) && !_attributes.Contains("uninhabited");

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "attributes":
                        // Inline list: "attributes "dirt belt" farming textiles"
                        for (int i = 1; i < child.Size; i++)
                            _attributes.Add(child.Token(i));
                        break;

                    case "government" when child.Size >= 2:
                        Government = child.Token(1);
                        break;

                    case "shipyard" when child.Size >= 2:
                        if (!_shipyards.Contains(child.Token(1)))
                            _shipyards.Add(child.Token(1));
                        break;

                    case "outfitter" when child.Size >= 2:
                        if (!_outfitters.Contains(child.Token(1)))
                            _outfitters.Add(child.Token(1));
                        break;

                    case "spaceport":
                    case "port":
                        // "port" is upstream's newer spelling and is handled by the
                        // same branch there; treating it as unrecognised leaves 19
                        // vanilla worlds reporting no spaceport, unable to refuel.
                        HasSpaceport = true;
                        break;

                    case "security" when child.Size >= 2:
                        Security = child.Value(1);
                        break;

                    case "tribute" when child.Size >= 2:
                        Tribute = (int)child.Value(1);
                        break;
                }
            }
        }

        /// <summary>
        /// Everything this world sells from the given catalogue of stock lists, as the
        /// union of the lists it names. Unknown list names are skipped rather than
        /// throwing: content routinely references lists defined in files not loaded.
        /// </summary>
        public static IEnumerable<string> Stock(IReadOnlyList<string> saleNames,
                                                IReadOnlyDictionary<string, Sale> catalogue)
        {
            if (saleNames is null || catalogue is null)
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string saleName in saleNames)
            {
                if (!catalogue.TryGetValue(saleName, out Sale? sale))
                    continue;

                foreach (string item in sale.Items)
                {
                    if (seen.Add(item))
                        yield return item;
                }
            }
        }

        public override string ToString() => Name;
    }
}
