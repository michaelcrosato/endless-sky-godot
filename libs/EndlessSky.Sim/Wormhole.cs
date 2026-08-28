using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A fixed passage between systems that is not a hyperspace link. Port of
    /// upstream <c>Wormhole</c>.
    /// </summary>
    /// <remarks>
    /// A wormhole is a PLANET the player lands on, not a jump they make. The link
    /// list is a cycle: landing on the wormhole's planet in the first system puts the
    /// ship in the second, from the second into the third, and from the last back to
    /// the first. That cycling is the whole mechanism, and it is why a two-system
    /// wormhole works in both directions without stating either.
    ///
    /// They matter because they connect places the link network does not. Regions
    /// reachable only through a wormhole are unreachable without one, however much
    /// fuel a player carries.
    ///
    /// INCOMPLETE, tracked rather than dropped: map colours, the "mappable" flag that
    /// decides whether a wormhole is drawn before it is used, and per-wormhole
    /// landing text.
    /// </remarks>
    public class Wormhole
    {
        private readonly List<string> _links = new List<string>();

        public Wormhole(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public string Name { get; }

        /// <summary>Whether this wormhole is drawn on the map before it is travelled.</summary>
        public bool IsMappable { get; private set; }

        /// <summary>The systems this wormhole joins, in cycle order.</summary>
        public IReadOnlyList<string> Links => _links;

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "mappable":
                        IsMappable = true;
                        break;

                    case "link" when child.Size >= 2:
                        if (!_links.Contains(child.Token(1)))
                            _links.Add(child.Token(1));
                        break;
                }
            }
        }

        /// <summary>
        /// Where a ship entering from <paramref name="from"/> comes out.
        /// </summary>
        /// <remarks>
        /// The next link in the cycle, wrapping at the end. A wormhole that names a
        /// system it is not entered from leads nowhere, which is how a one-way
        /// entrance is written: an entrance and an exit are two separate wormholes.
        /// </remarks>
        public string? ExitFrom(string? enteredFrom)
        {
            if (_links.Count == 0 || enteredFrom is null)
                return null;

            int index = _links.IndexOf(enteredFrom);
            if (index < 0)
                return null;

            return _links[(index + 1) % _links.Count];
        }

        public override string ToString() =>
            $"{Name} ({string.Join(" -> ", _links)})";
    }

    /// <summary>Travel through wormholes, which happens by landing rather than jumping.</summary>
    public static class WormholeTravel
    {
        /// <summary>
        /// The wormhole a planet is, or null if it is an ordinary world.
        /// </summary>
        /// <remarks>
        /// Upstream matches a planet to a wormhole by NAME: a planet called "Ember
        /// Gegno" is the surface of the wormhole of the same name.
        /// </remarks>
        public static Wormhole? At(GameData? data, Planet? planet) =>
            data != null && planet != null && data.Wormholes.TryGetValue(planet.Name, out Wormhole? found)
                ? found
                : null;

        /// <summary>
        /// Sends a player through the wormhole they have landed on. Returns the system
        /// they came out in, or null if they were not on one.
        /// </summary>
        public static StarSystem? Traverse(GameData? data, PlayerState? player)
        {
            if (data is null || player?.CurrentPlanet is null || player.CurrentSystem is null)
                return null;

            Wormhole? wormhole = At(data, player.CurrentPlanet);
            string? exitName = wormhole?.ExitFrom(player.CurrentSystem.Name);

            if (exitName is null || !data.Systems.TryGetValue(exitName, out StarSystem? exit))
                return null;

            player.EnterSystem(exit);
            return exit;
        }

        /// <summary>
        /// Every system reachable from here by wormhole, for route-finding.
        /// </summary>
        public static IEnumerable<string> ExitsFrom(GameData? data, StarSystem? system)
        {
            if (data is null || system is null)
                yield break;

            foreach (Wormhole wormhole in data.Wormholes.Values)
            {
                string? exit = wormhole.ExitFrom(system.Name);
                if (exit != null)
                    yield return exit;
            }
        }
    }
}
