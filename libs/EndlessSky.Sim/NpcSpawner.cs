using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Turns a mission's NPC templates into real ships in real systems. Port of the
    /// placement half of upstream <c>NPC::Instantiate</c>.
    /// </summary>
    /// <remarks>
    /// Missions could be offered, accepted, carried and handed in, and their npc
    /// blocks parsed in full - but nothing ever built a hull from one, so every
    /// objective needing a ship to exist was unreachable. In the generated universe
    /// that is 429 of 1000 jobs: every bounty, every escort, every salvage claim could
    /// be taken and never finished.
    ///
    /// Instantiation happens once, when the mission is ACCEPTED, not each time the
    /// player enters a system. Upstream does the same, and it is what makes a bounty
    /// stay dead: fly away and come back and it is the same three raiders, minus the
    /// one already killed.
    ///
    /// Randomness is injected for the same reason it is in <see cref="FleetSpawner"/>:
    /// a test has to be able to say exactly which hulls were placed.
    ///
    /// INCOMPLETE, tracked rather than dropped: the template's "to spawn" gate is
    /// recorded but not yet consulted, and NPCs never despawn.
    /// </remarks>
    public class NpcSpawner
    {
        private readonly GameData _data;
        private readonly FleetSpawner _fleets;
        private readonly Func<int, int> _random;

        public NpcSpawner(GameData data, FleetSpawner? fleets = null, Func<int, int>? random = null)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));

            var shared = new Random();
            _random = random ?? (n => n <= 0 ? 0 : shared.Next(n));
            _fleets = fleets ?? new FleetSpawner(data, _random);
        }

        /// <summary>How far from the system centre placed ships appear.</summary>
        public double PlacementDistance { get; set; } = 1000.0;

        /// <summary>
        /// Builds every NPC of a mission. Returns one instance per template, including
        /// ones that produced no ships - an empty instance still has to be present, or
        /// its objective would be dropped rather than merely unmet.
        /// </summary>
        public List<NpcInstance> Place(Mission mission, StarSystem? origin, StarSystem? destination)
        {
            var placed = new List<NpcInstance>();
            if (mission is null)
                return placed;

            foreach (MissionNpc template in mission.Npcs)
                placed.Add(Place(template, origin, destination));

            return placed;
        }

        /// <summary>Builds one NPC's ships and puts them where the template says.</summary>
        public NpcInstance Place(MissionNpc template, StarSystem? origin, StarSystem? destination)
        {
            if (template is null)
                throw new ArgumentNullException(nameof(template));

            StarSystem? system = ResolveSystem(template, origin, destination);
            Government? government = template.Government != null &&
                                     _data.Governments.TryGetValue(template.Government, out Government? g)
                ? g
                : null;

            var ships = new List<Ship>();
            BuildNamedShips(template, ships);
            BuildFleets(template, government, system, ships);

            // Landed NPCs sit on a world, and the world has to actually be in the
            // system they ended up in or upstream ignores it.
            string? planet = template.Planet != null && system != null &&
                             system.Objects.Any(o => o.PlanetName == template.Planet)
                ? template.Planet
                : null;

            foreach (Ship ship in ships)
                Position(ship, government, system, template);

            return new NpcInstance(template, system, planet, ships);
        }

        /// <summary>
        /// Where these ships belong. Upstream's order: an explicit system, then a
        /// location filter, then the destination if the template asked for it, and
        /// finally wherever the player was standing when the mission was taken.
        /// </summary>
        private StarSystem? ResolveSystem(MissionNpc template, StarSystem? origin,
                                          StarSystem? destination)
        {
            if (template.System != null &&
                _data.Systems.TryGetValue(template.System, out StarSystem? named))
            {
                return named;
            }

            if (template.Location != null && !template.Location.IsEmpty)
            {
                StarSystem? picked = PickSystem(template.Location, origin);
                if (picked != null)
                    return picked;
            }

            if (template.IsAtDestination && destination != null)
                return destination;

            return origin;
        }

        /// <summary>Picks one system whose worlds satisfy a filter.</summary>
        private StarSystem? PickSystem(LocationFilter filter, StarSystem? origin)
        {
            var matches = new List<StarSystem>();

            foreach (StarSystem system in _data.Systems.Values)
            {
                bool any = system.Objects.Any(o =>
                    o.Planet != null && filter.Matches(o.Planet, system.Name, _data, origin?.Name));

                if (any)
                    matches.Add(system);
            }

            if (matches.Count == 0)
                return null;

            // Sorted, so the choice is a function of the roll rather than of dictionary
            // ordering, which is not stable across runs.
            matches.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return matches[_random(matches.Count)];
        }

        private void BuildNamedShips(MissionNpc template, List<Ship> into)
        {
            for (int i = 0; i < template.ShipNames.Count; i++)
            {
                string model = template.ShipNames[i];
                if (!_data.Ships.ContainsKey(model))
                    continue;

                Ship ship = _data.BuildShip(model);
                ship.BuildMounts();

                string? given = i < template.GivenNames.Count ? template.GivenNames[i] : null;
                if (!string.IsNullOrEmpty(given))
                    ship.GivenName = given;

                into.Add(ship);
            }
        }

        private void BuildFleets(MissionNpc template, Government? government,
                                 StarSystem? system, List<Ship> into)
        {
            if (template.Fleet != null)
                into.AddRange(_fleets.Instantiate(template.Fleet, government, system));

            if (template.FleetName != null &&
                _data.Fleets.TryGetValue(template.FleetName, out Fleet? named))
            {
                into.AddRange(_fleets.Instantiate(named, government, system));
            }
        }

        /// <summary>
        /// Puts one ship into the world: full tanks, flying the right flag, and spread
        /// out enough that a group does not arrive as a single stack of hulls.
        /// </summary>
        private void Position(Ship ship, Government? government, StarSystem? system,
                              MissionNpc template)
        {
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);

            if (government != null)
                ship.Government = government;

            ship.CurrentSystem = system;

            // Fleet-built ships already carry a placement; only site the rest.
            if (ship.Position == default)
            {
                var bearing = new Angle(_random(360));
                double reach = PlacementDistance * (0.4 + _random(60) / 100.0);
                ship.Position = bearing.Unit() * reach;
                ship.Facing = Angle.FromPoint(-ship.Position);
            }

            // A derelict is a hull with nobody aboard, which is what makes it something
            // to board rather than something to fight.
            if (template.IsDerelict)
                ship.Disable();
        }
    }
}
