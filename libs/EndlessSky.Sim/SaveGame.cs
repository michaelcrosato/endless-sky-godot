using System;
using System.Collections.Generic;
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
    /// Ship::Save. Energy, heat, velocity, facing and committed jumps also survive
    /// because this port permits saving in flight. A jump retains its phase,
    /// destination, drive kind and latched fuel cost. Old saves retain their defaults.
    ///
    /// Mission UUIDs link accepted jobs to freight aboard ships or pooled ashore,
    /// including zero-ton parcels. A root cargo block preserves the port inventory
    /// even if the remaining ships cannot carry it. Old saves reserve freight from mixed
    /// commodity stock once; new saves never reconstruct cargo lost during a flight.
    /// A pilot with no ships can retain a landed location and cargo ashore. The
    /// runtime accepts that save only when its planet belongs to its saved system.
    ///
    /// The economy block restores supply, displayed quotes and pending sales. A
    /// staged read lets the runtime validate the pilot before replacing shared markets.
    /// Saves predating that block restart from base prices.
    ///
    /// The basis block stores exact remaining commodity purchase costs. Older saves
    /// without a basis have zero recorded cost; historical prices cannot be recovered.
    ///
    /// INCOMPLETE, tracked rather than dropped: pilot name, navigation and fleet
    /// orders, applied universe changes, politics, and weapon mount assignments.
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

            player.Data?.Trade.WriteEconomy(writer);

            if (player.CostBasis.Count > 0)
            {
                writer.Write("basis");
                writer.BeginChild();
                foreach (var entry in player.CostBasis.OrderBy(e => e.Key, StringComparer.Ordinal))
                    writer.Write(entry.Key, entry.Value);
                writer.EndChild();
            }

            foreach (Ship ship in player.Fleet.Ships)
                WriteShip(writer, ship, ReferenceEquals(ship, player.Fleet.Flagship));

            if (player.Fleet.PortCargo != null) WriteCargo(writer, player.Fleet.PortCargo);

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
                    writer.Write("uuid", taken.Id.ToString());
                    writer.Write("accepted", taken.Accepted.Day, taken.Accepted.Month,
                                 taken.Accepted.Year);
                    if (taken.Deadline.HasValue)
                        writer.Write("deadline", taken.Deadline.Value.Day,
                                     taken.Deadline.Value.Month, taken.Deadline.Value.Year);
                    if (taken.CargoType != null)
                        writer.Write("cargo", taken.CargoLoaded, taken.CargoType);
                    writer.Write("passengers", taken.PassengersCarried);
                    // The destination was chosen when the job was taken; a job whose
                    // filter matches several worlds would otherwise pick a different
                    // one on reload and send the player somewhere they were never told.
                    if (taken.Destination != null)
                        writer.Write("destination", taken.Destination);
                    WriteNpcs(writer, taken);
                    writer.EndChild();
                }
            }

            // Events already scheduled but not yet due. Without these a load loses
            // every consequence the player has set in motion but not yet seen.
            foreach ((string name, DateTime when) in player.ScheduledEvents)
                writer.Write("scheduled event", name, when.Day, when.Month, when.Year);

            WritePurchases(writer, "purchases", player.Purchases);
            if (player.CurrentPlanet != null)
                WritePurchases(writer, "outfit stock", player.OutfitStock);

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

        private static void WriteShip(DataWriter writer, Ship ship, bool flagship = false,
                                      ShipEvent actions = ShipEvent.None)
        {
            writer.Write("ship", ship.Definition.DisplayName);
            writer.BeginChild();
            if (flagship)
                writer.Write("flagship");
            if (ship.IsParked)
                writer.Write("parked");
            if (ship.GivenName != null)
                writer.Write("name", ship.GivenName);
            if (ship.CurrentSystem != null)
                writer.Write("system", ship.CurrentSystem.Name);
            // An NPC can fly a different flag from the faction that sells its model.
            writer.Write("government", ship.Government?.Name ?? string.Empty);
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
            if (ship.IsEnteringHyperspace || ship.IsHyperspacing)
                writer.Write("hyperspace", ship.HyperspaceCount, ship.HyperspaceFuelCost,
                    ship.IsUsingJumpDrive ? "jump" : "hyper", ship.HyperspaceSystem?.Name ?? string.Empty);

            // Grouped so a ship carrying four of something writes one line.
            foreach (IGrouping<string, Outfit> group in ship.Outfits.GroupBy(o => o.Name))
                writer.Write("outfit", group.Key, group.Count());
            WriteCargo(writer, ship.Cargo);
            if (actions != ShipEvent.None)
                writer.Write("actions", (int)actions);
            writer.EndChild();
        }

        private static void WriteNpcs(DataWriter writer, ActiveMission taken)
        {
            // Even an empty collection is explicit: only older saves without this
            // block need fresh placement. Objectives still refer to the mission's
            // templates, by index; each instantiated ship retains its own events.
            writer.Write("npcs");
            writer.BeginChild();
            for (int index = 0; index < taken.Mission.Npcs.Count; index++)
            {
                MissionNpc template = taken.Mission.Npcs[index];
                if (taken.NpcEvents.TryGetValue(template, out ShipEvent aggregate))
                    writer.Write("events", index, (int)aggregate);

                foreach (NpcInstance npc in taken.Npcs.Where(n => ReferenceEquals(n.Template, template)))
                {
                    writer.Write("npc", index);
                    writer.BeginChild();
                    if (npc.System != null)
                        writer.Write("system", npc.System.Name);
                    if (npc.Planet != null)
                        writer.Write("planet", npc.Planet);
                    // Dead hulls are retained: a kill must neither resurrect a target
                    // nor get credited to the surviving members of the same group.
                    foreach (Ship ship in npc.Ships)
                        WriteShip(writer, ship, actions: npc.EventsFor(ship));
                    writer.EndChild();
                }
            }
            writer.EndChild();
        }

        private static void WriteCargo(DataWriter writer, CargoHold cargo)
        {
            if (cargo.IsEmpty)
                return;
            writer.Write("cargo");
            writer.BeginChild();
            foreach (var entry in cargo.Commodities.OrderBy(e => e.Key, StringComparer.Ordinal))
                writer.Write("commodity", entry.Key, entry.Value);
            // Upstream only saves at ports and repopulates freight from missions.
            // In-flight saves must retain its actual carrier and any missing cargo.
            foreach (var entry in cargo.MissionCargo.OrderBy(e => e.Key))
                writer.Write("mission", entry.Key.ToString(), entry.Value);
            writer.EndChild();
        }

        /// <summary>
        /// Restores a player from a save. Ships and places are resolved against the
        /// universe, so a save is meaningless without the data it was made with.
        /// </summary>
        public static PlayerState Read(string text, GameData data) => Read(text, data, null);

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
            PlayerState player = Read(text, data, buildLog, out Action restoreEconomy);
            restoreEconomy();
            return player;
        }

        /// <summary>
        /// Stages a load. Call restoreEconomy after accepting the pilot, so a rejected
        /// save cannot change the active game's prices or queued trades.
        /// </summary>
        public static PlayerState Read(string text, GameData data,
                                       Func<PlayerState, MissionLog>? buildLog, out Action restoreEconomy)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            var player = new PlayerState(data);
            MissionLog? missions = buildLog?.Invoke(player);
            var file = new DataFile(text ?? string.Empty, "save");
            DataNode? economy = file.Nodes.LastOrDefault(n => n.Token(0) == "economy");
            restoreEconomy = () => data.Trade.ReadEconomy(economy);
            string? system = null, planet = null;
            var cargoNodes = new List<DataNode>();

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
                                player.SetCredits(child.IntegerValue(1));
                        break;

                    case "basis":
                        foreach (DataNode child in node.Children)
                            if (child.Size >= 2)
                                player.AdjustBasis(child.Token(0), child.IntegerValue(1));
                        break;

                    case "ship" when node.Size >= 2:
                        Ship? ship = ReadShip(node, data);
                        if (ship != null)
                        {
                            player.Fleet.Add(ship);
                            if (node.Children.Any(c => c.Token(0) == "flagship"))
                                player.Fleet.SetFlagship(ship);
                        }
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
                        cargoNodes.Add(node);
                        break;

                    case "scheduled event" when node.Size >= 5:
                        {
                            DateTime? when = SafeDate(node.Slice(1));
                            if (when.HasValue)
                                player.ScheduleEvent(node.Token(1), when.Value);
                            break;
                        }

                    case "purchases":
                        ReadPurchases(node, player.Purchases);
                        break;

                    case "outfit stock":
                        ReadPurchases(node, player.OutfitStock);
                        break;

                    case "conditions":
                        foreach (DataNode child in node.Children)
                            if (child.Size >= 2)
                                player.Conditions.Set(child.Token(0), child.IntegerValue(1));
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
            if (player.CurrentPlanet == null) player.OutfitStock.Clear();

            foreach (DataNode cargo in cargoNodes)
                player.Fleet.RestoreCargo(ReadCargo(cargo));

            // An explicit saved selection retains its condition, including a
            // disabled flagship. With no selection, a port cannot adopt a remote
            // or parked hull merely because it was first in the save.
            if (player.CurrentPlanet != null && !file.Nodes.Any(n => n.Token(0) == "ship"
                && n.Children.Any(c => c.Token(0) == "flagship")))
                player.Fleet.RefreshFlagship();

            // Mission fallback destinations and legacy NPC placement need the loaded
            // location, date and conditions, regardless of the save's field order.
            if (missions != null)
                foreach (DataNode node in file.Nodes)
                    if (node.Token(0) == "mission" && node.Size >= 2)
                        ReadMission(node, data, missions);

            return player;
        }

        private static Ship? ReadShip(DataNode node, GameData data)
        {
            if (!data.Ships.ContainsKey(node.Token(1)))
                return null;

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

            bool parked = false;
            bool overheated = false;
            string? government = data.GovernmentOf(ship.Definition.DisplayName);
            double? shields = null, hull = null, energy = null, fuel = null, heat = null;
            DataNode? hyperspace = null;

            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
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
                    case "government" when child.Size >= 2:
                        government = child.Token(1);
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
                    case "hyperspace" when child.Size >= 5:
                        hyperspace = child;
                        break;
                    case "cargo":
                        ship.LoadFrom(ReadCargo(child));
                        break;
                }
            }

            if (government != null && data.Governments.TryGetValue(government, out Government? faction))
                ship.Government = faction;

            ship.IsParked = parked;
            ship.SetLevels(shields: shields ?? ship.MaxShields, hull: hull ?? ship.MaxHull,
                           energy: energy ?? ship.MaxEnergy, fuel: fuel ?? ship.MaxFuel,
                           heat: heat ?? 0.0, overheated: overheated);

            if (hyperspace != null && hyperspace.IntegerValue(1) is >= 0 and <= Ship.HyperspaceFrames)
            {
                StarSystem? destination = null;
                if (hyperspace.Token(4).Length == 0 || data.Systems.TryGetValue(hyperspace.Token(4), out destination))
                    ship.RestoreHyperspace((int)hyperspace.IntegerValue(1), destination,
                        hyperspace.Value(2), hyperspace.Token(3) == "jump");
            }

            return ship;
        }

        private static CargoHold ReadCargo(DataNode node)
        {
            var cargo = new CargoHold(long.MaxValue);
            foreach (DataNode entry in node.Children)
            {
                if (entry.Token(0) == "commodity" && entry.Size >= 3)
                    cargo.Add(entry.Token(1), entry.IntegerValue(2));
                else if (entry.Token(0) == "mission" && entry.Size >= 3
                    && Guid.TryParse(entry.Token(1), out Guid id))
                    cargo.AddMissionCargo(id, entry.IntegerValue(2));
            }
            return cargo;
        }

        internal static NpcInstance ReadNpc(DataNode node, MissionNpc template, GameData data)
        {
            StarSystem? system = null;
            string? planet = null;
            var ships = new List<(Ship Ship, ShipEvent Actions)>();
            foreach (DataNode child in node.Children)
            {
                if (child.Size < 2)
                    continue;
                switch (child.Token(0))
                {
                    case "system":
                        data.Systems.TryGetValue(child.Token(1), out system);
                        break;
                    case "planet":
                        planet = child.Token(1);
                        break;
                    case "ship":
                        Ship? ship = ReadShip(child, data);
                        if (ship != null)
                        {
                            ShipEvent actions = ShipEvent.None;
                            foreach (DataNode field in child.Children)
                                if (field.Token(0) == "actions" && field.Size >= 2)
                                    actions |= (ShipEvent)(int)field.Value(1);
                            ships.Add((ship, actions));
                        }
                        break;
                }
            }

            var npc = new NpcInstance(template, system, planet, ships.Select(s => s.Ship));
            foreach (var entry in ships)
            {
                entry.Ship.CurrentSystem ??= system;
                npc.Record(entry.Ship, entry.Actions);
            }
            return npc;
        }

        private static void ReadMission(DataNode node, GameData data, MissionLog missions)
        {
            if (!data.Missions.TryGetValue(node.Token(1), out Mission? mission))
                return;

            missions.Restore(mission, node);
        }

        private static void WritePurchases(DataWriter writer, string name, PurchaseLog log)
        {
            if (!log.Records.Any()) return;
            writer.Write(name);
            writer.BeginChild();
            foreach (var entry in log.Records.OrderBy(e => e.Key, StringComparer.Ordinal))
                foreach (int dayNumber in entry.Value)
                {
                    if (dayNumber < 0) writer.Write(entry.Key, "day", dayNumber);
                    else
                    {
                        DateOnly day = DateOnly.FromDayNumber(dayNumber);
                        writer.Write(entry.Key, day.Day, day.Month, day.Year);
                    }
                }
            writer.EndChild();
        }

        private static void ReadPurchases(DataNode node, PurchaseLog log)
        {
            foreach (DataNode child in node.Children)
            {
                if (child.Size >= 3 && child.Token(1) == "day")
                {
                    if (int.TryParse(child.Token(2), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int day))
                        log.RecordDay(child.Token(0), day);
                }
                else if (child.Size >= 4 && SafeDate(child) is DateTime day)
                    log.Record(child.Token(0), day);
            }
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
