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

        public MissionLog(PlayerState player, NpcSpawner? npcs = null)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));

            // Without a spawner an npc block stays a template, which is fine for a log
            // under test but leaves every combat objective unreachable in a running
            // game - so default to one whenever the player knows the galaxy.
            _npcs = npcs ?? (player.Data is null ? null : new NpcSpawner(player.Data));
        }

        public IReadOnlyList<ActiveMission> Active => _active;

        public IReadOnlyList<ActiveMission> Finished => _finished;

        /// <summary>
        /// Missions this world will offer right now: the gate passes, the player is
        /// standing somewhere, and it is not already running or done.
        /// </summary>
        public IEnumerable<Mission> Available(GameData data)
        {
            if (data is null || _player.CurrentPlanet is null)
                return Enumerable.Empty<Mission>();

            return data.Missions.Values.Where(m =>
                m.IsOfferedAt(_player.CurrentPlanet, _player.CurrentSystem?.Name) &&
                m.CanOffer(_player.Conditions) &&
                !_active.Any(a => ReferenceEquals(a.Mission, m)) &&
                (m.IsRepeating || !_finished.Any(f => ReferenceEquals(f.Mission, m))));
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

            DateTime? deadline = mission.DeadlineDays > 0
                ? _player.Date.AddDays(mission.DeadlineDays)
                : null;

            var taken = new ActiveMission(mission, _player.Date, deadline);

            if (mission.CargoTons > 0 && mission.CargoType != null)
                taken.CargoLoaded = _player.Fleet.LoadCargo(mission.CargoType, mission.CargoTons);

            taken.PassengersCarried = mission.Passengers;

            // Built here, once, rather than on every system entry. Upstream does the
            // same, and it is why a bounty left half-finished stays half-finished.
            if (_npcs != null && mission.Npcs.Count > 0)
            {
                StarSystem? destination = mission.Destination is null
                    ? null
                    : FindSystemOf(mission.Destination);

                taken.Npcs.AddRange(_npcs.Place(mission, _player.CurrentSystem, destination));
            }

            _player.AddCredits(mission.Fire(MissionTrigger.Accept, _player.Conditions));
            _active.Add(taken);

            RecordProgress(mission, "offered", 1);
            RecordProgress(mission, "active", 1);
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

            // At the destination, on the ground.
            if (taken.Mission.Destination != null &&
                _player.CurrentPlanet?.Name != taken.Mission.Destination)
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

            _player.AddCredits(taken.Mission.Fire(MissionTrigger.Fail, _player.Conditions));
            Finish(taken, MissionOutcome.Aborted);
        }

        /// <summary>
        /// Records something that happened to one of a mission's NPCs.
        /// </summary>
        /// <remarks>
        /// This records the event against the NPC BLOCK, so it lands on every hull the
        /// block placed. That is the right granularity for a restored save, where the
        /// ships have been rebuilt but the record of what happened to each was kept at
        /// block level; <see cref="ReportShipEvent"/> is the per-hull path the running
        /// game uses.
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
                }
            }

            var taken = new ActiveMission(mission, accepted, hasDeadline ? deadline : null)
            {
                CargoLoaded = cargo,
            };

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
