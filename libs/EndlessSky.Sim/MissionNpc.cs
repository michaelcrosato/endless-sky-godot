using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Ships a mission places in the galaxy, and what the player has to do to them.
    /// Port of upstream <c>NPC</c>.
    /// </summary>
    /// <remarks>
    /// This is the half of the mission system that has anything to do with the game
    /// being played. A mission without NPCs is a text box and a payment; the 1,186
    /// npc blocks in the dataset are the pirates to fight, the convoys to escort and
    /// the derelicts to board.
    ///
    /// Objectives are a bitmask over <see cref="ShipEvent"/>, which is why that enum
    /// carries upstream's exact bit values. The vocabulary is small and asymmetric:
    /// "kill" means the ship must be destroyed, "save" means it must NOT be, and
    /// evade and accompany are separate booleans because they are about where a ship
    /// ends up rather than what was done to it.
    ///
    /// INCOMPLETE, tracked rather than dropped: conversations and dialogs attached to
    /// an NPC, per-NPC cargo settings, "on" action triggers, uuid identity across a
    /// save, and actually spawning these ships into a running system. Placement is
    /// recorded here; nothing yet reads it to populate a system.
    /// </remarks>
    public class MissionNpc
    {
        private readonly List<string> _shipNames = new List<string>();
        private readonly List<string> _personality = new List<string>();

        /// <summary>Events that satisfy this NPC's objective.</summary>
        public ShipEvent SucceedIf { get; private set; }

        /// <summary>Events that fail the mission outright.</summary>
        public ShipEvent FailIf { get; private set; }

        /// <summary>The ships must leave the system without being caught.</summary>
        public bool MustEvade { get; private set; }

        /// <summary>The ships must arrive with the player.</summary>
        public bool MustAccompany { get; private set; }

        public string? Government { get; private set; }

        /// <summary>System these ships appear in, when the mission names one.</summary>
        public string? System { get; private set; }

        /// <summary>Planet these ships are found at, when the mission names one.</summary>
        public string? Planet { get; private set; }

        /// <summary>Explicitly named ship models.</summary>
        public IReadOnlyList<string> ShipNames => _shipNames;

        /// <summary>An inline fleet definition, when the NPC is described as one.</summary>
        public Fleet? Fleet { get; private set; }

        public IReadOnlyList<string> Personality => _personality;

        /// <summary>Gate on the player's conditions, from the NPC's "to spawn" block.</summary>
        public ConditionSet? ToSpawn { get; private set; }

        public void Load(DataNode node)
        {
            // The objectives ride on the npc line itself: `npc kill save`.
            for (int i = 1; i < node.Size; i++)
                ApplyObjective(node.Token(i));

            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                bool hasValue = child.Size >= 2;

                switch (key)
                {
                    case "government" when hasValue:
                        Government = child.Token(1);
                        break;

                    case "system" when hasValue:
                        System = child.Token(1);
                        break;

                    case "planet" when hasValue:
                        Planet = child.Token(1);
                        break;

                    case "succeed" when hasValue:
                        SucceedIf = (ShipEvent)(int)child.Value(1);
                        break;

                    case "fail" when hasValue:
                        FailIf = (ShipEvent)(int)child.Value(1);
                        break;

                    case "evade":
                        MustEvade = true;
                        break;

                    case "accompany":
                        MustAccompany = true;
                        break;

                    case "personality":
                        // Inline traits and child traits are both used in content.
                        for (int i = 1; i < child.Size; i++)
                            _personality.Add(child.Token(i));

                        foreach (DataNode trait in child.Children)
                            for (int i = 0; i < trait.Size; i++)
                                if (!trait.IsNumber(i))
                                    _personality.Add(trait.Token(i));
                        break;

                    case "ship" when hasValue:
                        _shipNames.Add(child.Token(1));
                        break;

                    case "fleet":
                        if (hasValue)
                        {
                            // A named fleet: recorded, resolved against GameData later.
                            FleetName = child.Token(1);
                        }
                        else
                        {
                            // An inline fleet definition.
                            Fleet ??= new Fleet($"npc fleet");
                            Fleet.Load(child);
                        }
                        break;

                    case "to" when hasValue && child.Token(1) == "spawn":
                        ToSpawn = ConditionSet.Load(child);
                        break;
                }
            }
        }

        /// <summary>A fleet referenced by name rather than defined inline.</summary>
        public string? FleetName { get; private set; }

        /// <summary>
        /// Whether the objective is met, given everything that has happened to these
        /// ships and whether they left or arrived as required.
        /// </summary>
        /// <remarks>
        /// An NPC with no stated objective is satisfied from the outset - most exist
        /// only to be present - so the default has to be true rather than false, or
        /// every escort mission would be uncompletable.
        /// </remarks>
        public bool IsSatisfied(ShipEvent happened, bool evaded = false, bool accompanied = false)
        {
            if ((happened & FailIf) != 0)
                return false;

            if (MustEvade && !evaded)
                return false;

            if (MustAccompany && !accompanied)
                return false;

            if (SucceedIf == ShipEvent.None)
                return true;

            return (happened & SucceedIf) == SucceedIf;
        }

        /// <summary>Whether anything that has happened has failed this NPC outright.</summary>
        public bool HasFailed(ShipEvent happened) => (happened & FailIf) != 0;

        private void ApplyObjective(string token)
        {
            switch (token)
            {
                // "save" is the odd one: it states what must NOT happen.
                case "save": FailIf |= ShipEvent.Destroy; break;
                case "kill": SucceedIf |= ShipEvent.Destroy; break;
                case "board": SucceedIf |= ShipEvent.Board; break;
                case "assist": SucceedIf |= ShipEvent.Assist; break;
                case "disable": SucceedIf |= ShipEvent.Disable; break;
                case "scan cargo": SucceedIf |= ShipEvent.ScanCargo; break;
                case "scan outfits": SucceedIf |= ShipEvent.ScanOutfits; break;
                case "capture": SucceedIf |= ShipEvent.Capture; break;
                case "provoke": SucceedIf |= ShipEvent.Provoke; break;
                case "evade": MustEvade = true; break;
                case "accompany": MustAccompany = true; break;
            }
        }

        public override string ToString()
        {
            string what = SucceedIf == ShipEvent.None ? "present" : SucceedIf.ToString();
            return $"npc {what}{(Government is null ? "" : $" ({Government})")}";
        }
    }
}
