using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The player: their fleet, money, date, where they are and where they have been.
    /// Partial port of upstream <c>PlayerInfo</c>, focused on the state that content
    /// can interrogate.
    /// </summary>
    /// <remarks>
    /// The point of this type is the autoconditions. Endless Sky's content does not
    /// track the player by asking the engine questions through an API - it reads
    /// conditions. "credits", "flagship landed", "flagship planet: Mars",
    /// "flagship attribute: shields" and several hundred more are computed from live
    /// state on every read, which is why missions, conversations and events can gate
    /// on the player's situation without anything having to remember to write those
    /// values down. Registering them here is what makes the mission and conversation
    /// layers able to see the game at all.
    ///
    /// INCOMPLETE, tracked rather than dropped: outfit storage on planets, parked
    /// ships, carried-fighter bays, plugin and person conditions, and the "previous
    /// system/planet" family are not provided yet.
    /// Reading one returns 0 through the ordinary stored-value path rather than
    /// throwing, which is upstream's behaviour for an unknown condition anyway.
    /// </remarks>
    public partial class PlayerState
    {
        /// <summary>
        /// Upstream's epoch. Endless Sky starts on 16 November 3013, and "days since
        /// epoch" counts from year 0 of its calendar rather than from the start date.
        /// </summary>
        public static readonly DateTime Epoch = new DateTime(3013, 11, 16);

        private readonly HashSet<string> _visitedSystems = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _visitedPlanets = new HashSet<string>(StringComparer.Ordinal);
        private readonly GameData? _data;

        // Seeded from the start date: content that gates on a roll should vary between
        // gates but not between two runs of the same save.
        private readonly Random _rolls = new Random(20261116);

        public PlayerState(GameData? data = null, Conditions? conditions = null)
        {
            _data = data;
            Conditions = conditions ?? new Conditions();
            Fleet = new PlayerFleet();
            Fleet.RemovingShips += RemoveShipsBasis;
            Date = Epoch;
            StartDate = Epoch;
            RegisterAutoConditions();
        }

        /// <summary>The galaxy this player is playing in, where one was given.</summary>
        public GameData? Data => _data;

        /// <summary>
        /// When the player bought what they own, so selling it back is priced on how
        /// long they kept it rather than on the no-record default.
        /// </summary>
        public PurchaseLog Purchases { get; } = new PurchaseLog();

        public Conditions Conditions { get; }

        public PlayerFleet Fleet { get; }

        public long Credits { get; private set; }

        public DateTime Date { get; private set; }

        public DateTime StartDate { get; private set; }

        /// <summary>The system the flagship is in.</summary>
        public StarSystem? CurrentSystem { get; set; }

        /// <summary>The planet the flagship is landed on, or null while in flight.</summary>
        public Planet? CurrentPlanet { get; private set; }

        public IReadOnlyCollection<string> VisitedSystems => _visitedSystems;

        public IReadOnlyCollection<string> VisitedPlanets => _visitedPlanets;

        public Ship? Flagship => Fleet.Flagship;

        // --- Mutation -------------------------------------------------------------

        public void SetCredits(long credits) => Credits = credits;

        /// <summary>Adds (or, negative, removes) money. Upstream allows debt.</summary>
        public void AddCredits(long amount) => Credits += amount;

        public void SetDate(DateTime date) => Date = date;

        /// <summary>Restores the campaign's start date, which "days since start" counts from.</summary>
        public void SetStartDate(DateTime date) => StartDate = date;

        public void AdvanceDays(int days) => Date = Date.AddDays(days);

        /// <summary>Records arrival in a system, marking it visited.</summary>
        public void EnterSystem(StarSystem? system)
        {
            CurrentSystem = system;
            CurrentPlanet = null;
            if (system != null)
                _visitedSystems.Add(system.Name);
        }

        /// <summary>Lands on a planet, marking it and its system visited.</summary>
        public void Land(Planet? planet)
        {
            CurrentPlanet = planet;
            if (planet != null)
            {
                _visitedPlanets.Add(planet.Name);
                // PlayerInfo::Land removes destroyed hulls and their share of the
                // cargo cost before the port can trade the surviving goods.
                Fleet.RemoveDestroyed();
            }
        }

        public void Depart() => CurrentPlanet = null;

        /// <summary>
        /// Services the fleet for the world it is standing on, and leaves the ground.
        /// Port of the recharge loop in upstream <c>PlayerInfo::TakeOff</c>
        /// (<c>PlayerInfo.cpp:1868-1884</c>).
        /// </summary>
        /// <remarks>
        /// Parked and disabled ships are skipped, exactly as upstream skips them: a
        /// hull left in a hangar is not being serviced, and a wreck is not repaired by
        /// landing beside one. Remote ships receive only what they generate themselves.
        ///
        /// Before this existed the only thing landing did was top up the FLAGSHIP'S
        /// fuel, so every escort in the fleet carried its battle damage for the rest of
        /// the game and no ship ever got its hull back.
        /// </remarks>
        public void TakeOff()
        {
            if (CurrentSystem is null || CurrentPlanet is null || Flagship is null)
                return;

            RechargeType port = CurrentPlanet is { HasSpaceport: true }
                ? RechargeType.All
                : RechargeType.None;

            foreach (Ship ship in Fleet.Ships)
                if (!ship.IsParked && !ship.IsDisabled)
                    ship.Recharge(ship.CurrentSystem == CurrentSystem ? port : RechargeType.None);

            Depart();
        }

        // Events waiting for their day, in schedule order.
        private readonly List<(string Name, DateTime When)> _scheduledEvents =
            new List<(string, DateTime)>();

        /// <summary>Events scheduled but not yet fired, for saving.</summary>
        public IReadOnlyList<(string Name, DateTime When)> ScheduledEvents => _scheduledEvents;

        /// <summary>
        /// Queues a game event to fire on a given day.
        /// </summary>
        /// <remarks>
        /// Events were parsed and nothing ever fired them — no queue, no date check — so
        /// 416 of them sat in the dataset doing nothing, which is most of how the galaxy
        /// is supposed to change underneath the player.
        /// </remarks>
        public void ScheduleEvent(string name, DateTime when)
        {
            if (!string.IsNullOrEmpty(name))
                _scheduledEvents.Add((name, when));
        }

        /// <summary>
        /// Fires every scheduled event whose day has come, and forgets it.
        /// </summary>
        /// <returns>The events that fired, in the order they did.</returns>
        public IReadOnlyList<string> FireDueEvents(GameData? data)
        {
            var fired = new List<string>();
            if (data is null || _scheduledEvents.Count == 0)
                return fired;

            // Taken by value first: an event that schedules another must not have its
            // own list mutated underneath the walk.
            var due = _scheduledEvents.Where(e => e.When <= Date).ToList();
            _scheduledEvents.RemoveAll(e => e.When <= Date);

            foreach ((string name, _) in due)
            {
                if (!data.Events.TryGetValue(name, out GameEvent? evt))
                    continue;

                evt.Apply(data, this);
                fired.Add(name);
            }

            return fired;
        }

        /// <summary>Marks a system visited without moving the player there.</summary>
        public void MarkVisited(StarSystem? system)
        {
            if (system != null)
                _visitedSystems.Add(system.Name);
        }

        /// <summary>Marks a planet visited without landing on it.</summary>
        public void MarkVisited(Planet? planet)
        {
            if (planet != null)
                _visitedPlanets.Add(planet.Name);
        }

        /// <summary>Forgets a system, which events use to re-hide explored space.</summary>
        public void ClearVisitedSystem(string name) => _visitedSystems.Remove(name);

        public void ClearVisitedPlanet(string name) => _visitedPlanets.Remove(name);

        public bool HasVisited(StarSystem system) =>
            system != null && _visitedSystems.Contains(system.Name);

        public bool HasVisited(Planet planet) =>
            planet != null && _visitedPlanets.Contains(planet.Name);

        /// <summary>
        /// Everything the player owns, valued as upstream's "net worth" does: money
        /// plus the sale value of the fleet.
        /// </summary>
        public long NetWorth() => Credits + Fleet.FleetValue();

        // --- Autoconditions -------------------------------------------------------

        private void RegisterAutoConditions()
        {
            Conditions store = Conditions;

            // Calendar. Upstream exposes the components separately because content
            // gates on them directly ("month == 12").
            store.ProvideNamed("day", () => Date.Day);
            store.ProvideNamed("month", () => Date.Month);
            store.ProvideNamed("year", () => Date.Year);
            store.ProvideNamed("days since year start", () => Date.DayOfYear - 1);
            store.ProvideNamed("days until year end",
                () => new DateTime(Date.Year, 12, 31).DayOfYear - Date.DayOfYear);
            store.ProvideNamed("days since epoch", () => (long)(Date - Epoch).TotalDays);
            store.ProvideNamed("days since start", () => (long)(Date - StartDate).TotalDays);

            // A roll, in upstream's range [0, 100). Content gates on `random < 40` to
            // make an outcome happen four times in ten; an unregistered condition reads
            // 0, so `random < N` was ALWAYS true and every such gate fired every time.
            // Seeded from the start date rather than the clock, so a run reproduces.
            store.ProvideNamed("random", () => _rolls.Next(100));

            // "roll: 100", or "roll: max attempts" naming another condition
            // (PlayerInfo.cpp:4677-4686). Zero for anything at or below 1.
            store.ProvidePrefixed("roll: ", input =>
            {
                long bound = long.TryParse(input, out long literal) ? literal : Conditions.Get(input);
                return bound <= 1 ? 0 : _rolls.NextInt64(bound);
            });

            // Government standing, read AND written: content adjusts reputation
            // directly as a reward or a penalty (PlayerInfo.cpp:4654-4667). Left
            // unregistered, every `reputation: X` gate read a dead zero however the
            // player had actually behaved.
            store.ProvidePrefixed("reputation: ",
                name => _data != null && _data.Governments.TryGetValue(name, out Government? gov)
                    ? (long)gov.Reputation
                    : 0L,
                (name, value) =>
                {
                    if (_data != null && _data.Governments.TryGetValue(name, out Government? gov))
                        gov.SetReputation(value);
                });

            // Money.
            store.ProvideNamed("credits", () => Credits);
            store.ProvideNamed("net worth", NetWorth);

            // Fleet counts.
            store.ProvideNamed("total ships", () => Fleet.Ships.Count);
            store.ProvidePrefixed("ship model: ", model =>
                Fleet.Ships.Count(s => Matches(s, model)));

            // Flagship identity and state.
            store.ProvidePrefixed("flagship model: ",
                model => Flagship != null && Matches(Flagship, model) ? 1 : 0);
            store.ProvideNamed("flagship disabled", () => Flagship?.IsDisabled == true ? 1 : 0);
            store.ProvideNamed("flagship crew", () => Flagship?.Crew ?? 0);
            store.ProvideNamed("flagship required crew",
                () => (long)(Flagship?.Attributes.Get("required crew") ?? 0.0));
            store.ProvideNamed("flagship bunks",
                () => (long)(Flagship?.Attributes.Get("bunks") ?? 0.0));

            // "flagship attribute: shields" is how content reads a live ship stat.
            store.ProvidePrefixed("flagship attribute: ",
                attribute => (long)(Flagship?.Attributes.Get(attribute) ?? 0.0));
            store.ProvidePrefixed("flagship base attribute: ",
                attribute => (long)(Flagship?.Definition.Attributes.Get(attribute) ?? 0.0));

            // Where the flagship is. "flagship landed" is the one the landing tests
            // hinge on, and it is false in flight rather than absent.
            store.ProvideNamed("flagship landed", () => CurrentPlanet != null ? 1 : 0);
            store.ProvidePrefixed("flagship system: ",
                name => CurrentSystem?.Name == name ? 1 : 0);
            store.ProvidePrefixed("flagship system government: ",
                name => CurrentSystem?.Government == name ? 1 : 0);
            store.ProvidePrefixed("flagship planet: ",
                name => CurrentPlanet?.Name == name ? 1 : 0);
            store.ProvidePrefixed("flagship planet government: ",
                name => CurrentPlanet?.Government == name ? 1 : 0);
            store.ProvidePrefixed("flagship planet attribute: ",
                name => CurrentPlanet?.Attributes.Contains(name) == true ? 1 : 0);

            // Travel history.
            store.ProvidePrefixed("visited system: ",
                name => _visitedSystems.Contains(name) ? 1 : 0);
            store.ProvidePrefixed("visited planet: ",
                name => _visitedPlanets.Contains(name) ? 1 : 0);

            // Cargo. Upstream reports free space on the flagship separately from the
            // whole fleet, because a mission's cargo has to fit somewhere specific.
            store.ProvideNamed("cargo space", () => Fleet.CargoCapacity());
            store.ProvideNamed("cargo space free", () => Fleet.CargoFree());
            store.ProvideNamed("flagship: cargo space",
                () => Flagship?.Cargo.Capacity ?? 0);
            store.ProvideNamed("flagship: cargo space free",
                () => Flagship is null ? 0 : Math.Max(0, Flagship.Cargo.Capacity - Flagship.Cargo.Used));

            // Outfits installed across the fleet, and on the flagship alone.
            store.ProvidePrefixed("outfit (flagship installed): ",
                outfit => Flagship is null ? 0 : CountOutfit(Flagship, outfit));
            store.ProvidePrefixed("outfit (installed): ",
                outfit => Fleet.Ships.Sum(s => CountOutfit(s, outfit)));

            // Hyperjump distance to a destination, in jumps.
            store.ProvidePrefixed("hyperjumps to system: ", JumpsToSystem);
        }

        private static long CountOutfit(Ship ship, string outfit) =>
            ship.Outfits.Count(o => o.Name == outfit);

        private static bool Matches(Ship ship, string model) =>
            ship?.Definition != null &&
            (ship.Definition.DisplayName == model || ship.Definition.Name == model);

        /// <summary>
        /// Breadth-first jump count from the current system, upstream's
        /// "hyperjumps to system: ". Unreachable or unknown destinations give 0, as an
        /// unset condition would.
        /// </summary>
        private long JumpsToSystem(string destination)
        {
            if (CurrentSystem is null || string.IsNullOrEmpty(destination) || _data is null)
                return 0;

            if (CurrentSystem.Name == destination)
                return 0;

            var seen = new HashSet<string>(StringComparer.Ordinal) { CurrentSystem.Name };
            var frontier = new Queue<(string Name, long Jumps)>();
            frontier.Enqueue((CurrentSystem.Name, 0));

            while (frontier.Count > 0)
            {
                (string name, long jumps) = frontier.Dequeue();
                if (!_data.Systems.TryGetValue(name, out StarSystem? system))
                    continue;

                foreach (string link in system.Links)
                {
                    if (!seen.Add(link))
                        continue;

                    if (link == destination)
                        return jumps + 1;

                    frontier.Enqueue((link, jumps + 1));
                }
            }

            return 0;
        }

        public override string ToString() =>
            $"{Fleet.Ships.Count} ships, {Credits.ToString("N0", CultureInfo.InvariantCulture)} credits, " +
            $"{Date:yyyy-MM-dd}";
    }
}
