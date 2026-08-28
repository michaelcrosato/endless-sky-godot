using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Puts NPC traffic into a system. Port of upstream <c>Engine::SpawnFleets</c>
    /// and the placement half of <c>Fleet::Enter</c>.
    /// </summary>
    /// <remarks>
    /// Fleet definitions and the per-system spawn lists both existed and nothing ever
    /// turned either into a ship, so every system in the galaxy was empty. This is
    /// what makes the game feel inhabited: freighters crossing between planets,
    /// patrols on station, pirates arriving where they are not wanted.
    ///
    /// The period is odds, not a timer. Upstream rolls one chance in
    /// <c>period</c> every frame, so traffic arrives in a ragged trickle rather than
    /// on a schedule, and two visits to the same system are never the same.
    ///
    /// The strength check is the part that is easy to leave out and badly missed
    /// without: a system already dominated by one side stops calling for more of it,
    /// or a running battle turns into an endless pile-on of reinforcements.
    ///
    /// Randomness is injected rather than ambient, so a test can pin exactly which
    /// fleets arrive. The simulation layer has no business owning a global RNG.
    ///
    /// INCOMPLETE, tracked rather than dropped: arriving over a system's jump links
    /// rather than at its edge, fleets that launch from a planet's surface, carried
    /// fighters, formation placement, personality, and the gamerule fleet multiplier.
    /// </remarks>
    public class FleetSpawner
    {
        private readonly GameData _data;
        private readonly Func<int, int> _random;

        /// <param name="random">
        /// Returns a value in [0, n). Upstream spawns when this rolls zero.
        /// </param>
        public FleetSpawner(GameData data, Func<int, int>? random = null)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));

            var shared = new Random();
            _random = random ?? (n => n <= 0 ? 0 : shared.Next(n));
        }

        /// <summary>Distance from the system centre that arriving traffic appears at.</summary>
        public double ArrivalDistance { get; set; } = 10_000.0;

        /// <summary>
        /// Rolls one frame of spawning for a system and returns the ships that arrived.
        /// </summary>
        /// <param name="present">
        /// Ships already in the system, used for the strength check.
        /// </param>
        public List<Ship> Step(StarSystem? system, IEnumerable<Ship>? present = null)
        {
            var arrived = new List<Ship>();
            if (system is null)
                return arrived;

            var here = present?.Where(s => !s.IsDestroyed).ToList() ?? new List<Ship>();

            foreach (FleetSpawn spawn in system.Fleets)
            {
                if (_random(spawn.Period) != 0)
                    continue;

                if (!_data.Fleets.TryGetValue(spawn.Name, out Fleet? fleet))
                    continue;

                Government? government = fleet.Government != null &&
                                         _data.Governments.TryGetValue(fleet.Government, out Government? g)
                    ? g
                    : null;

                if (government is null)
                    continue;

                if (IsAlreadyDominant(government, here))
                    continue;

                List<Ship> ships = Instantiate(fleet, government, system);
                arrived.AddRange(ships);
                here.AddRange(ships);
            }

            return arrived;
        }

        /// <summary>
        /// Builds one randomly chosen variant of a fleet and places it at the system's
        /// edge.
        /// </summary>
        public List<Ship> Instantiate(Fleet? fleet, Government? government, StarSystem? system)
        {
            var ships = new List<Ship>();
            if (fleet is null || fleet.Variants.Count == 0)
                return ships;

            FleetVariant? variant = PickVariant(fleet);
            if (variant is null)
                return ships;

            // Arrive together, from one direction, spread over a short front so a
            // fleet does not materialise as a single stack of overlapping hulls.
            double bearing = _random(360);
            var heading = new Angle(bearing);
            Point origin = heading.Unit() * ArrivalDistance;

            int index = 0;
            foreach (string model in variant.Ships)
            {
                if (!_data.Ships.ContainsKey(model))
                    continue;

                Ship ship = _data.BuildShip(model);
                ship.BuildMounts();
                ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                               energy: ship.MaxEnergy, fuel: ship.MaxFuel);

                ship.Government = government;
                ship.CurrentSystem = system;
                ship.Position = origin + new Point(index * 120.0, index * 60.0);

                // Facing inward, which is where anything worth flying to is.
                ship.Facing = Angle.FromPoint(-origin);

                ships.Add(ship);
                index++;
            }

            return ships;
        }

        /// <summary>
        /// Picks a variant, weighted as content authored it.
        /// </summary>
        private FleetVariant? PickVariant(Fleet fleet)
        {
            int total = fleet.Variants.Sum(v => v.Weight);
            if (total <= 0)
                return null;

            int roll = _random(total);
            foreach (FleetVariant variant in fleet.Variants)
            {
                roll -= variant.Weight;
                if (roll < 0)
                    return variant;
            }

            return fleet.Variants[^1];
        }

        /// <summary>
        /// Whether this government's side already outnumbers its opposition two to one,
        /// in which case upstream sends no more.
        /// </summary>
        private static bool IsAlreadyDominant(Government government, IReadOnlyList<Ship> present)
        {
            long allies = 0, enemies = 0;
            foreach (Ship ship in present)
            {
                if (ship.Government is null)
                    continue;

                long strength = (long)(ship.MaxHull + ship.MaxShields);
                if (government.IsEnemy(ship.Government))
                    enemies += strength;
                else
                    allies += strength;
            }

            // With nothing hostile present there is nothing to reinforce against, so
            // the check does not apply - that is what keeps ordinary traffic flowing
            // through a peaceful system.
            return enemies > 0 && allies > 2 * enemies;
        }
    }
}
