using System;
using System.Collections.Generic;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// One mission's NPCs, as actual hulls in the galaxy. Port of the instance half of
    /// upstream <c>NPC</c> - <c>NPC::Instantiate</c>, <c>HasSucceeded</c> and
    /// <c>HasFailed</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="MissionNpc"/> is the template read from disk and shared by every
    /// player who ever takes the mission. This is what one acceptance produced: these
    /// specific ships, in this system, and what has happened to each of them.
    ///
    /// The split matters because upstream's objectives are per-ship, not aggregate. A
    /// bounty on three raiders is met when all three are dead; folding their fates
    /// into one bitmask would pay out on the first kill. Likewise a mission fails if a
    /// ship you still owe an action to is destroyed - blow up the freighter you were
    /// sent to board and there is nothing left to board.
    ///
    /// INCOMPLETE, tracked rather than dropped: despawn conditions, carried fighters
    /// belonging to an NPC, and per-NPC cargo overrides.
    /// </remarks>
    public class NpcInstance
    {
        private readonly List<Ship> _ships;
        private readonly Dictionary<Ship, ShipEvent> _events = new Dictionary<Ship, ShipEvent>();

        public NpcInstance(MissionNpc template, StarSystem? system, string? planet,
                           IEnumerable<Ship>? ships)
        {
            Template = template ?? throw new ArgumentNullException(nameof(template));
            System = system;
            Planet = planet;
            _ships = ships?.ToList() ?? new List<Ship>();
        }

        public MissionNpc Template { get; }

        /// <summary>Where these ships are, resolved once at acceptance.</summary>
        public StarSystem? System { get; }

        /// <summary>The world they are landed on, when the template named one.</summary>
        public string? Planet { get; }

        public IReadOnlyList<Ship> Ships => _ships;

        /// <summary>Whether this ship is one of ours.</summary>
        public bool Owns(Ship? ship) => ship is not null && _ships.Contains(ship);

        /// <summary>What has happened to one of these ships so far.</summary>
        public ShipEvent EventsFor(Ship? ship) =>
            ship is not null && _events.TryGetValue(ship, out ShipEvent happened)
                ? happened
                : ShipEvent.None;

        /// <summary>Records something that happened to one of these ships.</summary>
        /// <returns>True if the ship belonged to this NPC and the record changed.</returns>
        public bool Record(Ship? ship, ShipEvent happened)
        {
            if (ship is null || happened == ShipEvent.None || !_ships.Contains(ship))
                return false;

            ShipEvent already = EventsFor(ship);
            if ((already | happened) == already)
                return false;

            _events[ship] = already | happened;
            return true;
        }

        /// <summary>
        /// Whether anything that has happened has failed this NPC outright.
        /// </summary>
        /// <remarks>
        /// The second clause is the one that is easy to miss and changes how the game
        /// plays: a ship destroyed while it still owes an unsatisfied objective fails
        /// the mission, because the thing you were sent to do to it can no longer be
        /// done.
        /// </remarks>
        public bool HasFailed()
        {
            foreach (KeyValuePair<Ship, ShipEvent> entry in _events)
            {
                if ((entry.Value & Template.FailIf) != 0)
                    return true;

                bool owesMore = (~(int)entry.Value & (int)Template.SucceedIf) != 0;
                if (owesMore && (entry.Value & ShipEvent.Destroy) != 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether every objective is met, given where the player is standing.
        /// </summary>
        /// <param name="playerSystem">
        /// Needed because "accompany" and "evade" are about where a ship ended up
        /// rather than what was done to it.
        /// </param>
        public bool HasSucceeded(StarSystem? playerSystem)
        {
            if (HasFailed())
                return false;

            if (Template.MustEvade || Template.MustAccompany)
            {
                foreach (Ship ship in _ships)
                {
                    ShipEvent happened = EventsFor(ship);

                    // A derelict is immobile until somebody repairs it.
                    bool immobile = Template.IsDerelict;

                    if (happened != ShipEvent.None)
                    {
                        // Dead or taken: settled either way, and no longer counted.
                        if ((happened & (ShipEvent.Destroy | ShipEvent.Capture)) != 0)
                            continue;

                        immobile = (happened & ShipEvent.Disable) != 0;
                        immobile |= Template.IsDerelict && (happened & ShipEvent.Assist) == 0;
                    }

                    // A ship with no system recorded has not gone anywhere.
                    bool here = ship.CurrentSystem is null ||
                                ReferenceEquals(ship.CurrentSystem, playerSystem);

                    // Accompany wants it here and moving; evade wants the opposite.
                    if ((here && !immobile) ^ Template.MustAccompany)
                        return false;
                }
            }

            if (Template.SucceedIf == ShipEvent.None)
                return true;

            // Every ship, not any ship: three raiders means three kills.
            foreach (Ship ship in _ships)
                if ((EventsFor(ship) & Template.SucceedIf) != Template.SucceedIf)
                    return false;

            return true;
        }

        /// <summary>Everything that has happened to any of these ships, merged.</summary>
        public ShipEvent Aggregate =>
            _events.Values.Aggregate(ShipEvent.None, (all, one) => all | one);

        /// <summary>Ships that are still flying.</summary>
        public IEnumerable<Ship> Survivors => _ships.Where(s => !s.IsDestroyed);

        public override string ToString() =>
            $"{Template} x{_ships.Count} in {System?.Name ?? "nowhere"}";
    }
}
