using System;
using System.Collections.Generic;
using System.Linq;

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

        public MissionLog(PlayerState player)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
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

            _player.AddCredits(mission.Fire(MissionTrigger.Accept, _player.Conditions));
            _active.Add(taken);

            // Upstream tracks an accepted mission as a condition so content can gate on
            // it without holding a reference.
            _player.Conditions.Set($"mission: {mission.Name}", 1);
            return taken;
        }

        /// <summary>Declines an offer, firing its decline action.</summary>
        public void Decline(Mission mission) =>
            _player.AddCredits(mission?.Fire(MissionTrigger.Decline, _player.Conditions) ?? 0);

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
        public void RecordNpcEvent(ActiveMission taken, MissionNpc npc, ShipEvent happened)
        {
            if (taken is null || npc is null)
                return;

            taken.NpcEvents.TryGetValue(npc, out ShipEvent already);
            taken.NpcEvents[npc] = already | happened;
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
                bool npcLost = taken.Mission.Npcs.Any(npc =>
                    taken.NpcEvents.TryGetValue(npc, out ShipEvent happened) && npc.HasFailed(happened));

                if (!overdue && !failed && !npcLost)
                    continue;

                _player.AddCredits(taken.Mission.Fire(MissionTrigger.Fail, _player.Conditions));
                Finish(taken, MissionOutcome.Failed);
                ended.Add(taken);
            }

            return ended;
        }

        private void Finish(ActiveMission taken, MissionOutcome outcome)
        {
            taken.Outcome = outcome;
            _active.Remove(taken);
            _finished.Add(taken);
            _player.Conditions.Set($"mission: {taken.Mission.Name}", 0);

            if (outcome == MissionOutcome.Completed)
                _player.Conditions.Set($"mission completed: {taken.Mission.Name}", 1);
        }
    }
}
