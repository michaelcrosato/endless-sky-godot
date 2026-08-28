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
    /// INCOMPLETE, tracked rather than dropped: pilot name, per-ship damage levels and
    /// individual ship names, cargo held by specific ships rather than the fleet as a
    /// whole, purchase records for depreciation, event schedules, and the mission log's
    /// NPC event history. Ships are saved by model and outfit list, which restores a
    /// fleet's capability but not its scars.
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

                // Grouped so a ship carrying four of something writes one line.
                foreach (IGrouping<string, Outfit> group in ship.Outfits.GroupBy(o => o.Name))
                    writer.Write("outfit", group.Key, group.Count());

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
                    writer.EndChild();
                }
            }

            // Cargo in the hold, by commodity.
            var cargo = player.Fleet.Ships
                .SelectMany(s => s.Cargo.Commodities)
                .GroupBy(entry => entry.Key, entry => entry.Value)
                .Select(g => (Commodity: g.Key, Tons: g.Sum()))
                .Where(entry => entry.Tons > 0)
                .OrderBy(entry => entry.Commodity, StringComparer.Ordinal)
                .ToList();

            if (cargo.Count > 0)
            {
                writer.Write("cargo");
                writer.BeginChild();
                foreach ((string commodity, int tons) in cargo)
                    writer.Write("commodity", commodity, tons);
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

        /// <summary>
        /// Restores a player from a save. Ships and places are resolved against the
        /// universe, so a save is meaningless without the data it was made with.
        /// </summary>
        public static PlayerState Read(string text, GameData data, MissionLog? missions = null)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            var player = new PlayerState(data);
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

            bool isFlagship = false;
            bool parked = false;

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

                    case "outfit" when child.Size >= 2:
                        int count = child.Size >= 3 && child.IsNumber(2) ? (int)child.Value(2) : 1;
                        if (data.Outfits.TryGetValue(child.Token(1), out Outfit? outfit))
                            ship.AddOutfit(outfit, count);
                        break;
                }
            }

            string? government = data.GovernmentOf(ship.Definition.DisplayName);
            if (government != null && data.Governments.TryGetValue(government, out Government? faction))
                ship.Government = faction;

            ship.IsParked = parked;
            ship.SetLevels(shields: ship.MaxShields, hull: ship.MaxHull,
                           energy: ship.MaxEnergy, fuel: ship.MaxFuel);

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
