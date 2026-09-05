using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Writes and reads a player's game. Partial port of upstream
    /// <c>PlayerInfo::Save</c> and <c>PlayerInfo::Load</c>.
    /// </summary>
    /// <remarks>
    /// Progression is one of the things the directive names, and without persistence
    /// there is no progression: every session starts over, and the whole condition
    /// store - which is where Endless Sky keeps the player's entire history - is
    /// thrown away at the end of it.
    ///
    /// The format is the same tab-indented tree as every other file, written through
    /// the same <see cref="DataWriter"/> as the game's own data. That is deliberate:
    /// a save is just more content, it can be read by eye, and the writer's
    /// eight-significant-digit precision keeps it from filling with binary noise.
    ///
    /// Conditions are saved WHOLE rather than selectively, minus the ones the engine
    /// computes. Autoconditions are derived from live state on read, so writing them
    /// down would both bloat the file and, worse, let a stale copy shadow the real
    /// value - a saved "credits" would fight the account it was copied from.
    ///
    /// Ship names, crew, cargo, shields, hull, fuel and positions follow upstream
    /// Ship::Save. Energy, heat, velocity and facing also survive because this port
    /// permits saving in flight. Old saves without those fields retain their defaults.
    ///
    /// INCOMPLETE, tracked rather than dropped: pilot name, a jump in progress,
    /// applied universe changes, politics, and the mission log's NPC event history.
    /// </remarks>
    public static class SaveGame
    {
        /// <summary>Serialises a player to the data-file format.</summary>
        public static string Write(PlayerState player, MissionLog? missions = null)
        {
            if (player is null)
                throw new ArgumentNullException(nameof(player));

            var writer = new DataWriter();

            writer.Write("date", player.Date.Day, player.Date.Month, player.Date.Year);
            writer.Write("start date", player.StartDate.Day, player.StartDate.Month,
                         player.StartDate.Year);

            if (player.CurrentSystem != null)
                writer.Write("system", player.CurrentSystem.Name);

            if (player.CurrentPlanet != null)
                writer.Write("planet", player.CurrentPlanet.Name);

            writer.Write("account");
            writer.BeginChild();
            writer.Write("credits", player.Credits);
            writer.EndChild();

            foreach (Ship ship in player.Fleet.Ships)
            {
                writer.Write("ship", ship.Definition.DisplayName);
                writer.BeginChild();

                if (ReferenceEquals(ship, player.Fleet.Flagship))
                    writer.Write("flagship");

                if (ship.IsParked)
                    writer.Write("parked");

                if (ship.GivenName != null)
                    writer.Write("name", ship.GivenName);
                if (ship.CurrentSystem != null)
                    writer.Write("system", ship.CurrentSystem.Name);
                writer.Write("position", ship.Position.X, ship.Position.Y);
                writer.Write("velocity", ship.Velocity.X, ship.Velocity.Y);
                writer.Write("facing", ship.Facing.Degrees);
                writer.Write("crew", ship.Crew);
                writer.Write("shields", ship.Shields);
                writer.Write("hull", ship.Hull);
                writer.Write("energy", ship.Energy);
                writer.Write("fuel", ship.Fuel);
                writer.Write("heat", ship.Heat);
                if (ship.IsOverheated)
                    writer.Write("overheated");

                // Grouped so a ship carrying four of something writes one line.
                foreach (IGrouping<string, Outfit> group in ship.Outfits.GroupBy(o => o.Name))
                    writer.Write("outfit", group.Key, group.Count());

                WriteCargo(writer, ship.Cargo);
                writer.EndChild();
            }

            foreach (string system in player.VisitedSystems.OrderBy(s => s, StringComparer.Ordinal))
                writer.Write("visited", system);

            foreach (string planet in player.VisitedPlanets.OrderBy(p => p, StringComparer.Ordinal))
                writer.Write("visited planet", planet);

            if (missions != null)
            {
                foreach (ActiveMission taken in missions.Active)
                {
                    writer.Write("mission", taken.Mission.Name);
                    writer.BeginChild();
                    writer.Write("accepted", taken.Accepted.Day, taken.Accepted.Month,
                                 taken.Accepted.Year);
                    if (taken.Deadline.HasValue)
                        writer.Write("deadline", taken.Deadline.Value.Day,
                                     taken.Deadline.Value.Month, taken.Deadline.Value.Year);
                    if (taken.CargoLoaded > 0)
                        writer.Write("cargo", taken.CargoLoaded);
                    // The destination was chosen when the job was taken; a job whose
                    // filter matches several worlds would otherwise pick a different
                    // one on reload and send the player somewhere they were never told.
                    if (taken.Destination != null)
                        writer.Write("destination", taken.Destination);
                    writer.EndChild();
                }
            }

            // Events already scheduled but not yet due. Without these a load loses
            // every consequence the player has set in motion but not yet seen.
            foreach ((string name, DateTime when) in player.ScheduledEvents)
                writer.Write("scheduled event", name, when.Day, when.Month, when.Year);

            // When the player bought what they own. Without it a load re-values the
            // whole fleet at the no-record default -- the depreciation floor -- so
            // saving and loading quietly took three quarters off everything.
            var purchases = player.Purchases.Records
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();

            if (purchases.Count > 0)
            {
                writer.Write("purchases");
                writer.BeginChild();
                foreach (KeyValuePair<string, List<DateTime>> entry in purchases)
                    foreach (DateTime day in entry.Value)
                        writer.Write(entry.Key, day.Day, day.Month, day.Year);
                writer.EndChild();
            }

            // Stored conditions only. Provided ones are recomputed on load.
            var stored = player.Conditions.Values
                .Where(entry => !player.Conditions.IsProvided(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();

            if (stored.Count > 0)
            {
                writer.Write("conditions");
                writer.BeginChild();
                foreach (KeyValuePair<string, long> condition in stored)
                    writer.Write(condition.Key, condition.Value);
                writer.EndChild();
            }

            return writer.ToString();
        }

        private static void WriteCargo(DataWriter writer, CargoHold cargo)
        {
            if (cargo.IsEmpty)
                return;
            writer.Write("cargo");
            writer.BeginChild();
            foreach (var entry in cargo.Commodities.OrderBy(e => e.Key, StringComparer.Ordinal))
                writer.Write("commodity", entry.Key, entry.Value);
            writer.EndChild();
        }

        /// <summary>
        /// Restores a player from a save. Ships and places are resolved against the
        /// universe, so a save is meaningless without the data it was made with.
        /// </summary>
        public static PlayerState Read(string text, GameData data, MissionLog? missions = null) =>
            Read(text, data, missions is null ? null : _ => missions);

        /// <summary>
        /// Restores a player, building its mission log from the restored player itself.
        /// </summary>
        /// <param name="buildLog">
        /// Called once with the new player, before any mission is read, and expected to
        /// return the log those missions should be restored into.
        /// </param>
        /// <remarks>
        /// A <see cref="MissionLog"/> is constructed against a player, and the player is
        /// what this method produces — so a caller loading a save had no way to supply
        /// a log that belonged to the right player. That circle is why nothing outside
        /// the tests could load a game with a mission in progress. The factory closes
        /// it: the player exists first, then its log is asked for.
        /// </remarks>
        public static PlayerState Read(string text, GameData data,
                                       Func<PlayerState, MissionLog>? buildLog)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            var player = new PlayerState(data);
            MissionLog? missions = buildLog?.Invoke(player);
            var file = new DataFile(text ?? string.Empty, "save");
            string? system = null, planet = null;

            foreach (DataNode node in file.Nodes)
            {
                switch (node.Token(0))
                {
                    case "date" when node.Size >= 4:
                        player.SetDate(SafeDate(node) ?? player.Date);
                        break;

                    case "start date" when node.Size >= 4:
                        player.SetStartDate(SafeDate(node) ?? player.StartDate);
                        break;

                    case "system" when node.Size >= 2:
                        system = node.Token(1);
                        break;

                    case "planet" when node.Size >= 2:
                        planet = node.Token(1);
                        break;

                    case "account":
                        foreach (DataNode child in node.Children)
                            if (child.Token(0) == "credits" && child.Size >= 2)
                                player.SetCredits((long)child.Value(1));
                        break;

                    case "ship" when node.Size >= 2:
                        ReadShip(node, data, player);
                        break;

                    case "visited" when node.Size >= 2:
                        if (data.Systems.TryGetValue(node.Token(1), out StarSystem? visited))
                            player.MarkVisited(visited);
                        break;

                    case "visited planet" when node.Size >= 2:
                        if (data.Planets.TryGetValue(node.Token(1), out Planet? seen))
                            player.MarkVisited(seen);
                        break;

                    case "cargo":
                        foreach (DataNode child in node.Children)
                            if (child.Token(0) == "commodity" && child.Size >= 3)
                                player.Fleet.LoadCargo(child.Token(1), (int)child.Value(2));
                        break;

                    case "scheduled event" when node.Size >= 5:
                        {
                            DateTime? when = SafeDate(node.Slice(1));
                            if (when.HasValue)
                                player.ScheduleEvent(node.Token(1), when.Value);
                            break;
                        }

                    case "purchases":
                        foreach (DataNode child in node.Children)
                        {
                            if (child.Size < 4)
                                continue;

                            DateTime? bought = SafeDate(child);
                            if (bought.HasValue)
                                player.Purchases.Record(child.Token(0), bought.Value);
                        }
                        break;

                    case "conditions":
                        foreach (DataNode child in node.Children)
                            if (child.Size >= 2)
                                player.Conditions.Set(child.Token(0), (long)child.Value(1));
                        break;

                    case "mission" when node.Size >= 2 && missions != null:
                        ReadMission(node, data, missions);
                        break;
                }
            }

            // Placement last: entering a system clears the landed planet, so the order
            // has to be system then planet however the file was written.
            if (system != null && data.Systems.TryGetValue(system, out StarSystem? current))
                player.EnterSystem(current);

            // Older saves recorded only the player's system. A per-ship location
            // wins when present, particularly for ships parked somewhere else.
            foreach (Ship ship in player.Fleet.Ships)
                ship.CurrentSystem ??= player.CurrentSystem;

            if (planet != null && data.Planets.TryGetValue(planet, out Planet? landed))
                player.Land(landed);

            return player;
        }

        private static void ReadShip(DataNode node, GameData data, PlayerState player)
        {
            if (!data.Ships.ContainsKey(node.Token(1)))
                return;

            // Built as a bare hull, then given exactly the outfits the save records:
            // BuildShip would install the stock loadout on top of them.
            var ship = new Ship(data.Ships[node.Token(1)]);
            ship.BuildMounts();

            // Restore capacity before levels and cargo, irrespective of field order.
            foreach (DataNode child in node.Children)
            {
                if (child.Token(0) != "outfit" || child.Size < 2)
                    continue;
                int count = child.Size >= 3 && child.IsNumber(2) ? (int)child.Value(2) : 1;
                if (count > 0 && data.Outfits.TryGetValue(child.Token(1), out Outfit? outfit))
                    ship.AddOutfit(outfit, count);
            }

            bool isFlagship = false;
            bool parked = false;
            bool overheated = false;
            double? shields = null, hull = null, energy = null, fuel = null, heat = null;

            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "flagship":
                        isFlagship = true;
                        break;

                    case "parked":
                        parked = true;
                        break;

                    case "name" when child.Size >= 2:
                        ship.GivenName = child.Token(1);
                        break;
                    case "system" when child.Size >= 2:
                        if (data.Systems.TryGetValue(child.Token(1), out StarSystem? system))
                            ship.CurrentSystem = system;
                        break;
                    case "position" when child.Size >= 3:
                        ship.Position = new Point(child.Value(1), child.Value(2));
                        break;
                    case "velocity" when child.Size >= 3:
                        ship.Velocity = new Point(child.Value(1), child.Value(2));
                        break;
                    case "facing" when child.Size >= 2:
                        ship.Facing = new Angle(child.Value(1));
                        break;
                    case "crew" when child.Size >= 2:
                        ship.Crew = (int)child.Value(1);
                        break;
                    case "shields" when child.Size >= 2:
                        shields = child.Value(1);
                        break;
                    case "hull" when child.Size >= 2:
                        hull = child.Value(1);
                        break;
                    case "energy" when child.Size >= 2:
                        energy = child.Value(1);
                        break;
                    case "fuel" when child.Size >= 2:
                        fuel = child.Value(1);
                        break;
                    case "heat" when child.Size >= 2:
                        heat = child.Value(1);
                        break;
                    case "overheated":
                        overheated = true;
                        break;
                    case "cargo":
                        foreach (DataNode entry in child.Children)
                            if (entry.Token(0) == "commodity" && entry.Size >= 3)
                                ship.LoadCargo(entry.Token(1), (int)entry.Value(2));
                        break;
                }
            }

            string? government = data.GovernmentOf(ship.Definition.DisplayName);
            if (government != null && data.Governments.TryGetValue(government, out Government? faction))
                ship.Government = faction;

            ship.IsParked = parked;
            ship.SetLevels(shields: shields ?? ship.MaxShields, hull: hull ?? ship.MaxHull,
                           energy: energy ?? ship.MaxEnergy, fuel: fuel ?? ship.MaxFuel,
                           heat: heat ?? 0.0, overheated: overheated);

            player.Fleet.Add(ship);
            if (isFlagship)
                player.Fleet.SetFlagship(ship);
        }

        private static void ReadMission(DataNode node, GameData data, MissionLog missions)
        {
            if (!data.Missions.TryGetValue(node.Token(1), out Mission? mission))
                return;

            missions.Restore(mission, node);
        }

        private static DateTime? SafeDate(DataNode node)
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
    }
}
