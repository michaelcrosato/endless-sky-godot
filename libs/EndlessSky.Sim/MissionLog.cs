using System;
using System.Collections.Generic;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>Why a mission ended.</summary>
    public enum MissionOutcome
    {
        Active,
        Completed,
        Failed,
        Aborted,
    }

    /// <summary>
    /// One mission the player has taken, and its state while it runs.
    /// </summary>
    public class ActiveMission
    {
        public ActiveMission(Mission mission, DateTime accepted, DateTime? deadline)
        {
            Mission = mission ?? throw new ArgumentNullException(nameof(mission));
            Accepted = accepted;
            Deadline = deadline;
        }

        public Mission Mission { get; }

        public DateTime Accepted { get; }

        /// <summary>The day after which this mission has failed, if it has one.</summary>
        public DateTime? Deadline { get; }

        /// <summary>
        /// The world this mission is actually going to, fixed when it was accepted.
        /// </summary>
        /// <remarks>
        /// Most missions describe their destination with a filter rather than naming a
        /// planet, so it has to be resolved into a real place at accept time -- exactly
        /// as upstream fixes one in Mission::Instantiate. Reading only the literal
        /// Mission.Destination left this null for every described job, which silently
        /// removed the "be there to hand it in" requirement and placed every
        /// `system destination` NPC wherever the player happened to be standing.
        /// </remarks>
        public string? Destination { get; internal set; }

        public MissionOutcome Outcome { get; internal set; } = MissionOutcome.Active;

        /// <summary>Cargo actually loaded, which may be less than asked if the hold was small.</summary>
        public int CargoLoaded { get; internal set; }

        public int PassengersCarried { get; internal set; }

        /// <summary>What has happened to each of this mission's NPCs so far.</summary>
        public Dictionary<MissionNpc, ShipEvent> NpcEvents { get; } =
            new Dictionary<MissionNpc, ShipEvent>();

        /// <summary>
        /// This mission's NPCs as actual ships, built when it was accepted.
        /// </summary>
        public List<NpcInstance> Npcs { get; } = new List<NpcInstance>();

        /// <summary>The NPC ships of this mission that are in a given system.</summary>
        /// <remarks>
        /// Filtered on where each hull actually is, not on where its NPC block was
        /// placed: an escort that jumped with the player has left its original system
        /// behind, and looking at the placement would keep it there forever.
        /// </remarks>
        public IEnumerable<Ship> ShipsIn(StarSystem? system) =>
            Npcs.SelectMany(n => n.Survivors)
                .Where(s => ReferenceEquals(s.CurrentSystem, system));

        public bool IsOverdue(DateTime today) => Deadline.HasValue && today > Deadline.Value;

        public override string ToString() => $"{Mission.DisplayName} ({Outcome})";
    }

    /// <summary>
    /// The player's mission board and job list: what is on offer, what has been
    /// taken, and what it takes to finish. Ports the lifecycle from upstream
    /// <c>PlayerInfo</c> and <c>Mission::IsSatisfied</c>.
    /// </summary>
    /// <remarks>
    /// Missions could be parsed, gated and fired individually, but nothing held one
    /// between being accepted on one world and completed on another, so the whole
    /// system had no memory and no mission could actually be run.
    ///
    /// The rules that matter are the ones a player notices. A mission is only offered
    /// where it can be offered, cargo has to physically fit in the hold, a mission is
    /// only complete at its DESTINATION with its cargo still aboard and its NPC
    /// objectives met, and a deadline that passes fails it whether or not the player
    /// is paying attention.
    ///
    /// INCOMPLETE, tracked rather than dropped: waypoints and stopovers (upstream
    /// refuses completion while any remain), mission timers, fines and outfit
    /// transfers as part of a completion action, and per-ship distribution of mission
    /// cargo across a fleet.
    /// </remarks>
    public class MissionLog
    {
        private readonly List<ActiveMission> _active = new List<ActiveMission>();
        private readonly List<ActiveMission> _finished = new List<ActiveMission>();
        private readonly PlayerState _player;
        private readonly NpcSpawner? _npcs;
        private readonly Func<int, int> _random;

        public MissionLog(PlayerState player, NpcSpawner? npcs = null, Func<int, int>? random = null)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));

            // Injected for the same reason the spawners take one: a scheduled event's
            // delay is a range, and a test needs to pin which day it lands on.
            var shared = new Random();
            _random = random ?? (n => n <= 0 ? 0 : shared.Next(n));

            // Without a spawner an npc block stays a template, which is fine for a log
            // under test but leaves every combat objective unreachable in a running
            // game - so default to one whenever the player knows the galaxy.
            _npcs = npcs ?? (player.Data is null ? null : new NpcSpawner(player.Data));
        }

        public IReadOnlyList<ActiveMission> Active => _active;

        public IReadOnlyList<ActiveMission> Finished => _finished;

        /// <summary>
        /// Missions a given counter will offer right now: the gate passes, the player
        /// is standing somewhere, and it is not already running or done.
        /// </summary>
        /// <param name="from">
        /// Which counter is asking, or null for everything this world offers. Missions
        /// declare where they are offered (Mission.h:108) and reading only "job" left
        /// every other kind at the default, so a job board listed boarding missions,
        /// shipyard missions and the ones that fire on entering a system alongside the
        /// actual work.
        /// </param>
        public IEnumerable<Mission> Available(GameData data, MissionLocation? from = null)
        {
            if (data is null || _player.CurrentPlanet is null)
                return Enumerable.Empty<Mission>();

            return data.Missions.Values.Where(m =>
                (from is null || m.IsOfferedFrom(from.Value)) &&
                m.IsOfferedAt(_player.CurrentPlanet, _player.CurrentSystem?.Name) &&
                m.CanOffer(_player.Conditions) &&
                !_active.Any(a => ReferenceEquals(a.Mission, m)) &&
                (m.IsRepeating || !_finished.Any(f => ReferenceEquals(f.Mission, m))));
        }

        /// <summary>
        /// Presents a mission to the player: fires its <c>on offer</c> action and hands
        /// back whatever that action wants shown.
        /// </summary>
        /// <returns>
        /// The offer action, or null when the mission has none. A caller with a screen
        /// runs its dialog or conversation and decides whether to <see cref="Accept"/>
        /// on the answer; a caller without one can ignore it.
        /// </returns>
        /// <remarks>
        /// OFFER is where a mission's dialogue lives and where content sets the
        /// conditions the offer itself implies. Nothing fired the trigger, so `on offer`
        /// was dead in every mission that had one — and since that is where upstream's
        /// opening conversation sells the player their first ship, it is also why this
        /// game has to pick a starting hull by other means.
        /// </remarks>
        public MissionAction? Offer(Mission mission)
        {
            if (mission is null)
                return null;

            MissionAction? action = mission.Action(MissionTrigger.Offer);
            if (action is null)
                return null;

            _player.AddCredits(mission.Fire(MissionTrigger.Offer, _player.Conditions));
            ApplyFailures(action, null);
            return action;
        }

        /// <summary>
        /// Takes a mission: fires its accept action, loads its cargo and passengers,
        /// and starts its clock.
        /// </summary>
        /// <remarks>
        /// Cargo is loaded through the fleet's hold, so a mission asking for more tons
        /// than the player can carry loads what fits and reports it. Upstream refuses
        /// the offer outright in that case; recording the shortfall keeps the
        /// information rather than discarding it.
        /// </remarks>
        public ActiveMission? Accept(Mission mission)
        {
            if (mission is null || _active.Any(a => ReferenceEquals(a.Mission, mission)))
                return null;

            // Fix the destination first: a mission's deadline is base plus a
            // per-jump multiplier over the distance it is actually going
            // (Mission.cpp:165-172), so there is no deadline to compute until the
            // "there" is known.
            string? destination = mission.ResolveDestination(_player.Data, _player.CurrentSystem?.Name);
            DateTime? deadline = mission.DeadlineAfter(_player.Date, JumpsTo(destination));

            var taken = new ActiveMission(mission, _player.Date, deadline)
            {
                // Fixed now, not looked up later: a job that names its destination in
                // prose has to be going to ONE place, and that place has to be the same
                // one the text quoted, the NPCs are placed at, and the hand-in checks.
                Destination = destination,
            };

            if (mission.CargoTons > 0 && mission.CargoType != null)
                taken.CargoLoaded = _player.Fleet.LoadCargo(mission.CargoType, mission.CargoTons);

            taken.PassengersCarried = mission.Passengers;

            // Built here, once, rather than on every system entry. Upstream does the
            // same, and it is why a bounty left half-finished stays half-finished.
            if (_npcs != null && mission.Npcs.Count > 0)
            {
                StarSystem? goingTo = taken.Destination is null
                    ? null
                    : FindSystemOf(taken.Destination);

                taken.Npcs.AddRange(_npcs.Place(mission, _player.CurrentSystem, goingTo));
            }

            _player.AddCredits(mission.Fire(MissionTrigger.Accept, _player.Conditions));
            _active.Add(taken);

            RecordProgress(mission, "offered", 1);
            RecordProgress(mission, "active", 1);

            ApplyFailures(mission.Action(MissionTrigger.Accept), taken);
            return taken;
        }

        /// <summary>Declines an offer, firing its decline action.</summary>
        public void Decline(Mission mission)
        {
            if (mission is null)
                return;

            _player.AddCredits(mission.Fire(MissionTrigger.Decline, _player.Conditions));
            RecordProgress(mission, "offered", 1);
            RecordProgress(mission, "declined", 1);
        }

        /// <summary>
        /// Carries out an action's <c>fail</c> clauses.
        /// </summary>
        /// <remarks>
        /// <c>GameAction.cpp:239-242</c>: a bare <c>fail</c> fails the mission the
        /// action belongs to, and <c>fail "&lt;name&gt;"</c> fails a different one —
        /// which is how content ends a storyline when the player takes an incompatible
        /// job. Both were parsed as nothing, so a mission that said "you have blown it"
        /// quietly kept running.
        /// </remarks>
        private void ApplyFailures(MissionAction? action, ActiveMission? caller)
        {
            if (action is null)
                return;

            // Schedule anything this action sets in motion. The delay is a range, so a
            // storyline does not fire on a predictable day for every player.
            foreach (ScheduledEvent scheduled in action.Events)
            {
                int span = Math.Max(0, scheduled.MaxDays - scheduled.MinDays);
                int days = scheduled.MinDays + (span > 0 ? _random(span + 1) : 0);
                _player.ScheduleEvent(scheduled.Name, _player.Date.AddDays(days));
            }

            foreach (string name in action.FailsMissions)
            {
                ActiveMission? other = _active.FirstOrDefault(
                    a => string.Equals(a.Mission.Name, name, StringComparison.Ordinal));

                if (other != null)
                {
                    _player.AddCredits(other.Mission.Fire(MissionTrigger.Fail, _player.Conditions));
                    Finish(other, MissionOutcome.Failed);
                }
            }

            // Last, so a mission that fails itself has already done everything else the
            // action asked for.
            if (action.FailsCaller && caller != null && caller.Outcome == MissionOutcome.Active)
            {
                Finish(caller, MissionOutcome.Failed);
            }
        }

        /// <summary>How many jumps from where the player is to a named world.</summary>
        private int JumpsTo(string? planet)
        {
            if (planet is null || _player.Data is null || _player.CurrentSystem is null)
                return 0;

            StarSystem? there = FindSystemOf(planet);
            if (there is null)
                return 0;

            int jumps = LocationFilter.JumpDistance(
                _player.Data, _player.CurrentSystem.Name, there.Name);

            return jumps < 0 ? 0 : jumps;
        }

        /// <summary>
        /// Moves one of a mission's six progress counters.
        /// </summary>
        /// <remarks>
        /// These are the keys content actually gates on: upstream keys them on the
        /// mission's true name with the suffixes <c>offered</c>, <c>active</c>,
        /// <c>declined</c>, <c>done</c>, <c>failed</c> and <c>aborted</c>
        /// (<c>Mission.cpp:1281-1316</c>). The upstream dataset references them 1,966
        /// times -- <c>": done"</c> alone 1,156 -- so a port that invents its own
        /// spelling leaves every one of those gates reading zero for the whole game.
        ///
        /// They are counters, not flags, because a repeatable job is taken many times
        /// and content asks how often.
        /// </remarks>
        private void RecordProgress(Mission mission, string suffix, long delta) =>
            _player.Conditions.Add($"{mission.Name}: {suffix}", delta);

        /// <summary>
        /// Whether this mission can be handed in right now.
        /// </summary>
        public bool CanComplete(ActiveMission taken)
        {
            if (taken is null || taken.Outcome != MissionOutcome.Active)
                return false;

            // At the destination, on the ground. The destination is the one fixed when
            // the mission was accepted, which is the only way a job that DESCRIBES
            // where it is going still has a "there" to be at. Checking the literal
            // Mission.Destination instead meant every described job -- which is most of
            // them -- skipped this check entirely and paid out anywhere.
            if (taken.Destination != null && _player.CurrentPlanet?.Name != taken.Destination)
            {
                return false;
            }

            if (_player.CurrentPlanet is null)
                return false;

            if (!taken.Mission.CanComplete(_player.Conditions))
                return false;

            // The cargo has to still be aboard.
            if (taken.CargoLoaded > 0 && taken.Mission.CargoType != null &&
                _player.Fleet.CargoCount(taken.Mission.CargoType) < taken.CargoLoaded)
            {
                return false;
            }

            // Real hulls whenever there are any: objectives are per-ship, so a bounty
            // on three raiders is not settled by one kill.
            if (taken.Npcs.Count > 0)
                return taken.Npcs.All(n => n.HasSucceeded(_player.CurrentSystem));

            return taken.Mission.NpcObjectivesMet(npc =>
                taken.NpcEvents.TryGetValue(npc, out ShipEvent happened) ? happened : ShipEvent.None);
        }

        /// <summary>
        /// Hands in a mission: unloads its cargo, pays it, and fires its completion
        /// action.
        /// </summary>
        public bool Complete(ActiveMission taken)
        {
            if (!CanComplete(taken))
                return false;

            if (taken.CargoLoaded > 0 && taken.Mission.CargoType != null)
                _player.Fleet.UnloadCargo(taken.Mission.CargoType, taken.CargoLoaded);

            _player.AddCredits(taken.Mission.Fire(MissionTrigger.Complete, _player.Conditions));
            Finish(taken, MissionOutcome.Completed);
            return true;
        }

        /// <summary>Abandons a mission, firing its fail action.</summary>
        public void Abort(ActiveMission taken)
        {
            if (taken is null || taken.Outcome != MissionOutcome.Active)
                return;

            if (taken.CargoLoaded > 0 && taken.Mission.CargoType != null)
                _player.Fleet.UnloadCargo(taken.Mission.CargoType, taken.CargoLoaded);

            // Giving up is not the same as failing. Upstream fires ABORT and only falls
            // back to FAIL when the mission defines no abort action
            // (Mission.cpp:360-371); content uses the difference to refund a change of
            // mind while penalising a botched job. Hard-coding FAIL gave the wrong one
            // to every mission that bothered to tell them apart.
            MissionTrigger trigger = taken.Mission.Action(MissionTrigger.Abort) != null
                ? MissionTrigger.Abort
                : MissionTrigger.Fail;

            _player.AddCredits(taken.Mission.Fire(trigger, _player.Conditions));
            Finish(taken, MissionOutcome.Aborted);
        }

        /// <summary>
        /// Records something that happened to one of a mission's NPCs.
        /// </summary>
        /// <remarks>
        /// This records the event against the NPC BLOCK, so it lands on every hull the
        /// block placed. It also supports logs without physical instances.
        /// <see cref="ReportShipEvent"/> is the per-hull path the running game uses;
        /// saves preserve those individual records instead of broadcasting them.
        /// </remarks>
        public void RecordNpcEvent(ActiveMission taken, MissionNpc npc, ShipEvent happened)
        {
            if (taken is null || npc is null)
                return;

            taken.NpcEvents.TryGetValue(npc, out ShipEvent already);
            taken.NpcEvents[npc] = already | happened;

            foreach (NpcInstance instance in taken.Npcs)
            {
                if (!ReferenceEquals(instance.Template, npc))
                    continue;

                foreach (Ship ship in instance.Ships)
                    instance.Record(ship, happened);
            }
        }

        /// <summary>
        /// Reports something that happened to a ship in the world, routing it to
        /// whichever mission owns that hull.
        /// </summary>
        /// <remarks>
        /// This is the wire between combat and the mission log. Without it a mission
        /// can place its raiders and the player can destroy them and nothing ever
        /// notices, which is how every bounty in the game used to end.
        /// </remarks>
        /// <returns>The missions that took note.</returns>
        public IReadOnlyList<ActiveMission> ReportShipEvent(Ship? ship, ShipEvent happened)
        {
            var touched = new List<ActiveMission>();
            if (ship is null || happened == ShipEvent.None)
                return touched;

            foreach (ActiveMission taken in _active)
            {
                bool changed = false;
                foreach (NpcInstance npc in taken.Npcs)
                    changed |= npc.Record(ship, happened);

                if (changed)
                    touched.Add(taken);
            }

            return touched;
        }

        /// <summary>
        /// Takes accompanying NPCs along when the player jumps.
        /// </summary>
        /// <remarks>
        /// An escort mission asks that its convoy ARRIVE with the player, so the
        /// convoy has to be able to travel. Upstream gets this from escort-personality
        /// AI flying its own jump; until that exists, moving them with the flagship is
        /// the same observable outcome and keeps escort jobs completable rather than
        /// failing the instant the player leaves the system.
        ///
        /// A ship that is disabled or destroyed is left behind, which is what makes
        /// losing one during the run actually cost the mission.
        /// </remarks>
        /// <returns>The ships that made the jump.</returns>
        public IReadOnlyList<Ship> CarryAccompanying(StarSystem? from, StarSystem? to)
        {
            var moved = new List<Ship>();

            foreach (ActiveMission taken in _active)
                foreach (NpcInstance npc in taken.Npcs)
                {
                    if (!npc.Template.MustAccompany)
                        continue;

                    foreach (Ship ship in npc.Ships)
                    {
                        if (ship.IsDestroyed || ship.IsDisabled)
                            continue;

                        if (!ReferenceEquals(ship.CurrentSystem, from))
                            continue;

                        ship.CurrentSystem = to;
                        moved.Add(ship);
                    }
                }

            return moved;
        }

        /// <summary>The NPC ships of every active mission that are in a system.</summary>
        public IEnumerable<Ship> NpcShipsIn(StarSystem? system) =>
            _active.SelectMany(m => m.ShipsIn(system));

        /// <summary>Whichever active mission owns this hull, if any does.</summary>
        public ActiveMission? MissionOwning(Ship? ship) =>
            ship is null ? null : _active.FirstOrDefault(m => m.Npcs.Any(n => n.Owns(ship)));

        /// <summary>The system a named world is in, for resolving NPC placement.</summary>
        private StarSystem? FindSystemOf(string planetName)
        {
            if (_player.Data is null)
                return null;

            foreach (StarSystem system in _player.Data.Systems.Values)
                if (system.Objects.Any(o => o.PlanetName == planetName))
                    return system;

            return null;
        }

        /// <summary>
        /// Advances every active mission a day's worth: expires deadlines and fails
        /// missions whose fail conditions have come true.
        /// </summary>
        /// <returns>The missions that ended.</returns>
        public IReadOnlyList<ActiveMission> Step()
        {
            var ended = new List<ActiveMission>();

            foreach (ActiveMission taken in _active.ToList())
            {
                bool overdue = taken.IsOverdue(_player.Date);
                bool failed = taken.Mission.HasFailed(_player.Conditions);
                bool npcLost = taken.Npcs.Count > 0
                    ? taken.Npcs.Any(n => n.HasFailed())
                    : taken.Mission.Npcs.Any(npc =>
                        taken.NpcEvents.TryGetValue(npc, out ShipEvent happened) &&
                        npc.HasFailed(happened));

                if (!overdue && !failed && !npcLost)
                    continue;

                _player.AddCredits(taken.Mission.Fire(MissionTrigger.Fail, _player.Conditions));
                Finish(taken, MissionOutcome.Failed);
                ended.Add(taken);
            }

            return ended;
        }

        /// <summary>
        /// Puts a mission back in the log as it was saved, without re-firing its accept
        /// action or reloading its cargo - both already happened when it was taken.
        /// </summary>
        public ActiveMission Restore(Mission mission, DataNode node)
        {
            DateTime accepted = _player.Date, deadline = default;
            bool hasDeadline = false;
            int cargo = 0;
            int passengers = mission.Passengers;
            string? destination = null;
            DataNode? savedNpcs = null;

            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "accepted" when child.Size >= 4:
                        accepted = ReadDate(child) ?? accepted;
                        break;

                    case "deadline" when child.Size >= 4:
                        DateTime? read = ReadDate(child);
                        if (read.HasValue)
                        {
                            deadline = read.Value;
                            hasDeadline = true;
                        }
                        break;

                    case "cargo" when child.Size >= 2:
                        cargo = (int)child.Value(1);
                        break;

                    case "passengers" when child.Size >= 2:
                        passengers = (int)child.Value(1);
                        break;

                    case "destination" when child.Size >= 2:
                        destination = child.Token(1);
                        break;

                    case "npcs":
                        savedNpcs = child;
                        break;
                }
            }

            var taken = new ActiveMission(mission, accepted, hasDeadline ? deadline : null)
            {
                CargoLoaded = cargo,
                PassengersCarried = passengers,

                // Saves written before destinations were recorded have none; resolving
                // one now is better than leaving the job with nowhere to be handed in.
                Destination = destination
                    ?? mission.ResolveDestination(_player.Data, _player.CurrentSystem?.Name),
            };

            if (savedNpcs != null)
            {
                foreach (DataNode child in savedNpcs.Children)
                {
                    if (child.Size < 2 || !int.TryParse(child.Token(1), out int index)
                        || index < 0 || index >= mission.Npcs.Count)
                        continue;
                    MissionNpc template = mission.Npcs[index];
                    if (child.Token(0) == "npc" && _player.Data != null)
                        taken.Npcs.Add(SaveGame.ReadNpc(child, template, _player.Data));
                    else if (child.Token(0) == "events" && child.Size >= 3)
                        taken.NpcEvents[template] = (ShipEvent)(int)child.Value(2);
                }
            }
            else if (_npcs != null)
            {
                // Older saves omitted NPCs entirely. Recover playable targets once;
                // lost historical kills cannot be recovered from those files.
                StarSystem? goingTo = taken.Destination is null ? null : FindSystemOf(taken.Destination);
                taken.Npcs.AddRange(_npcs.Place(mission, _player.CurrentSystem, goingTo));
            }

            // No progress counters here on purpose: this is the load path, and the
            // condition store is saved and restored whole, so the counters come back
            // with it. Re-incrementing would double every mission the player has taken
            // each time the game is loaded.
            _active.Add(taken);
            return taken;
        }

        private static DateTime? ReadDate(DataNode node)
        {
            try
            {
                return new DateTime((int)node.Value(3), (int)node.Value(2), (int)node.Value(1));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private void Finish(ActiveMission taken, MissionOutcome outcome)
        {
            taken.Outcome = outcome;
            _active.Remove(taken);
            _finished.Add(taken);

            Mission mission = taken.Mission;
            RecordProgress(mission, "active", -1);

            switch (outcome)
            {
                case MissionOutcome.Completed:
                    RecordProgress(mission, "done", 1);
                    break;

                case MissionOutcome.Failed:
                    RecordProgress(mission, "failed", 1);
                    break;

                case MissionOutcome.Aborted:
                    // Upstream raises "failed" alongside "aborted" for backwards
                    // compatibility with content written before abort existed
                    // (Mission.cpp:1289-1292).
                    RecordProgress(mission, "aborted", 1);
                    RecordProgress(mission, "failed", 1);
                    break;
            }
        }
    }
}
