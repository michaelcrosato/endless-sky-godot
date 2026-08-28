using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>When a mission's actions run.</summary>
    public enum MissionTrigger
    {
        Offer,
        Accept,
        Decline,
        Complete,
        Fail,
        Visit,
        Defer,
    }

    /// <summary>
    /// What happens at one point in a mission's life. Port of upstream
    /// <c>MissionAction</c>, reduced to the parts that change player state.
    /// </summary>
    public class MissionAction
    {
        /// <summary>Credits paid (negative for a cost).</summary>
        public long Payment { get; private set; }

        /// <summary>Condition changes applied when this fires.</summary>
        public ConditionAssignments Assignments { get; private set; } = new ConditionAssignments();

        /// <summary>Conversation to play, by name, or null.</summary>
        public string? Conversation { get; private set; }

        /// <summary>Plain message shown to the player, or null.</summary>
        public string? Dialog { get; private set; }

        public static MissionAction Load(DataNode node)
        {
            var action = new MissionAction
            {
                Assignments = ConditionAssignments.Load(node),
            };

            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "payment":
                        // "payment" alone means the job board's computed rate; a number
                        // overrides it.
                        if (child.Size >= 2 && child.IsNumber(1))
                            action.Payment += (long)child.Value(1);
                        break;

                    case "conversation" when child.Size >= 2:
                        action.Conversation = child.Token(1);
                        break;

                    case "dialog" when child.Size >= 2:
                        action.Dialog = child.Token(1);
                        break;
                }
            }

            return action;
        }

        /// <summary>Applies the condition changes. Payment is the caller's to bank.</summary>
        public void Apply(Conditions conditions) => Assignments.Apply(conditions);
    }

    /// <summary>
    /// A mission definition. Port of the offer/complete half of upstream
    /// <c>Mission</c>.
    /// </summary>
    /// <remarks>
    /// Missions are the spine of Endless Sky's content: nearly all of the game's
    /// progression is missions gated on conditions, setting conditions that gate the
    /// next ones. The parts modelled here are the ones that decide whether a mission
    /// appears and what it does to the player's state.
    ///
    /// INCOMPLETE, tracked rather than dropped: LocationFilter (source/destination
    /// matching by government, category and attributes - only a literal destination
    /// planet name is read), NPC blocks and their fleets, waypoints and stopovers,
    /// substitutions and phrases in text, deadline arithmetic against the calendar,
    /// and the job-board payment formula.
    /// </remarks>
    public class Mission
    {
        private readonly Dictionary<MissionTrigger, MissionAction> _actions =
            new Dictionary<MissionTrigger, MissionAction>();

        public Mission(string name)
        {
            Name = name;
            DisplayName = name;
        }

        /// <summary>Internal identifier, unique across content.</summary>
        public string Name { get; }

        /// <summary>Name shown to the player.</summary>
        public string DisplayName { get; private set; }

        public string Description { get; private set; } = string.Empty;

        /// <summary>Literal destination planet, when the mission names one.</summary>
        public string? Destination { get; private set; }

        // --- Offer style ----------------------------------------------------------

        /// <summary>Appears on a planet's job board rather than being offered in the spaceport.</summary>
        public bool IsJob { get; private set; }

        /// <summary>Offered when boarding a disabled ship.</summary>
        public bool IsBoarding { get; private set; }

        /// <summary>Offered when assisting a ship in distress.</summary>
        public bool IsAssisting { get; private set; }

        /// <summary>Can be offered again after completing.</summary>
        public bool IsRepeating { get; private set; }

        /// <summary>Low priority: upstream offers minor missions only when nothing else is available.</summary>
        public bool IsMinor { get; private set; }

        /// <summary>Hidden from the mission list.</summary>
        public bool IsInvisible { get; private set; }

        // --- Cargo ----------------------------------------------------------------

        public string? CargoType { get; private set; }
        public int CargoTons { get; private set; }
        public int Passengers { get; private set; }

        /// <summary>Days allowed, or 0 for no deadline.</summary>
        public int DeadlineDays { get; private set; }

        // --- Gates ----------------------------------------------------------------

        /// <summary>Gate on being offered. Empty means "always available".</summary>
        public ConditionSet ToOffer { get; private set; } = new ConditionSet();

        /// <summary>Gate on completion.</summary>
        public ConditionSet ToComplete { get; private set; } = new ConditionSet();

        /// <summary>
        /// Gate on failure. Left empty by most missions, and an empty set PASSES, so
        /// callers must check <see cref="ConditionSet.IsEmpty"/> before treating this
        /// as a test - otherwise every mission without an explicit fail condition
        /// would fail immediately. <see cref="HasFailed"/> does that.
        /// </summary>
        public ConditionSet ToFail { get; private set; } = new ConditionSet();

        public IReadOnlyDictionary<MissionTrigger, MissionAction> Actions => _actions;

        public MissionAction? Action(MissionTrigger trigger) =>
            _actions.TryGetValue(trigger, out MissionAction? action) ? action : null;

        public void Load(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);

                switch (key)
                {
                    case "name" when child.Size >= 2:
                        DisplayName = child.Token(1);
                        break;

                    case "description" when child.Size >= 2:
                        Description = child.Token(1);
                        break;

                    case "destination" when child.Size >= 2:
                        // A destination can also be a filter block; only the literal
                        // planet form is read for now.
                        Destination = child.Token(1);
                        break;

                    case "job": IsJob = true; break;
                    case "boarding": IsBoarding = true; break;
                    case "assisting": IsAssisting = true; break;
                    case "repeat": IsRepeating = true; break;
                    case "minor": IsMinor = true; break;
                    case "invisible": IsInvisible = true; break;

                    case "cargo" when child.Size >= 3:
                        CargoType = child.Token(1);
                        CargoTons = (int)child.Value(2);
                        break;

                    case "passengers" when child.Size >= 2:
                        Passengers = (int)child.Value(1);
                        break;

                    case "deadline":
                        DeadlineDays = child.Size >= 2 && child.IsNumber(1) ? (int)child.Value(1) : 0;
                        break;

                    case "to" when child.Size >= 2:
                        switch (child.Token(1))
                        {
                            case "offer": ToOffer = ConditionSet.Load(child); break;
                            case "complete": ToComplete = ConditionSet.Load(child); break;
                            case "fail": ToFail = ConditionSet.Load(child); break;
                        }
                        break;

                    case "on" when child.Size >= 2:
                        if (TryParseTrigger(child.Token(1), out MissionTrigger trigger))
                            _actions[trigger] = MissionAction.Load(child);
                        break;
                }
            }
        }

        private static bool TryParseTrigger(string token, out MissionTrigger trigger)
        {
            switch (token)
            {
                case "offer": trigger = MissionTrigger.Offer; return true;
                case "accept": trigger = MissionTrigger.Accept; return true;
                case "decline": trigger = MissionTrigger.Decline; return true;
                case "complete": trigger = MissionTrigger.Complete; return true;
                case "fail": trigger = MissionTrigger.Fail; return true;
                case "visit": trigger = MissionTrigger.Visit; return true;
                case "defer": trigger = MissionTrigger.Defer; return true;
                default: trigger = default; return false;
            }
        }

        /// <summary>Whether this mission may currently be offered.</summary>
        public bool CanOffer(Conditions conditions) => ToOffer.Test(conditions);

        /// <summary>Whether its completion requirements are met.</summary>
        public bool CanComplete(Conditions conditions) => ToComplete.Test(conditions);

        /// <summary>Whether it has failed.</summary>
        public bool HasFailed(Conditions conditions) => !ToFail.IsEmpty && ToFail.Test(conditions);

        /// <summary>
        /// Fires a trigger: applies its condition changes and returns the credits
        /// moved, so the caller can bank them against the player's account.
        /// </summary>
        public long Fire(MissionTrigger trigger, Conditions conditions)
        {
            MissionAction? action = Action(trigger);
            if (action is null)
                return 0L;

            action.Apply(conditions);
            return action.Payment;
        }

        public override string ToString() => Name;
    }
}
