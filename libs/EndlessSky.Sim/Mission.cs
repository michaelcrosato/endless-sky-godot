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

        /// <summary>
        /// The player gave the mission up, as distinct from failing it. Upstream lists
        /// it separately (Mission.cpp:360-371) and only falls back to
        /// <see cref="Fail"/> when a mission defines no abort action — content uses the
        /// difference to refund a change of mind while penalising a botched job.
        /// </summary>
        Abort,
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

        /// <summary>Named conversation to play, or null when there is none or it is inline.</summary>
        public string? Conversation { get; private set; }

        /// <summary>
        /// A conversation defined in place rather than by name, or null.
        /// </summary>
        /// <remarks>
        /// Both forms are common upstream: <c>conversation "assisting merchant"</c>
        /// references a shared one, while a bare <c>conversation</c> with children
        /// defines it inline. Reading only the named form drops most of the game's
        /// mission dialogue, since inline is the more common shape.
        /// </remarks>
        public Conversation? InlineConversation { get; private set; }

        /// <summary>Whether this action plays any dialogue at all.</summary>
        public bool HasConversation => Conversation is not null || InlineConversation is not null;

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

                    case "conversation":
                        // Bare "conversation" with children: defined in place.
                        if (child.Children.Count > 0)
                            action.InlineConversation = Sim.Conversation.Load(child);
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
    /// INCOMPLETE, tracked rather than dropped: waypoints and stopovers, phrases in
    /// text, and the job-board payment formula. A source filter carrying terms this
    /// port does not model yet is treated as no restriction rather than as a rejection
    /// - see IsOfferedAt.
    /// </remarks>
    public class Mission
    {
        private readonly Dictionary<MissionTrigger, MissionAction> _actions =
            new Dictionary<MissionTrigger, MissionAction>();

        private readonly List<MissionNpc> _npcs = new List<MissionNpc>();

        public Mission(string name)
        {
            Name = name;
            DisplayName = name;
        }

        /// <summary>Internal identifier, unique across content.</summary>
        public string Name { get; }

        /// <summary>Ships this mission places, and the objectives attached to them.</summary>
        public IReadOnlyList<MissionNpc> Npcs => _npcs;

        /// <summary>
        /// Whether every NPC objective is met. An NPC with no stated objective counts
        /// as satisfied, so a mission whose ships only need to exist can still be
        /// completed.
        /// </summary>
        public bool NpcObjectivesMet(Func<MissionNpc, ShipEvent> outcome)
        {
            if (outcome is null)
                return _npcs.Count == 0;

            foreach (MissionNpc npc in _npcs)
                if (!npc.IsSatisfied(outcome(npc)))
                    return false;

            return true;
        }

        /// <summary>Name shown to the player.</summary>
        public string DisplayName { get; private set; }

        public string Description { get; private set; } = string.Empty;

        /// <summary>Literal destination planet, when the mission names one.</summary>
        public string? Destination { get; private set; }

        /// <summary>Literal source planet, when the mission names one.</summary>
        public string? Source { get; private set; }

        /// <summary>
        /// Where this mission may be offered, when it states a filter rather than a
        /// planet.
        /// </summary>
        public LocationFilter? SourceFilter { get; private set; }

        /// <summary>Where it may send the player, when stated as a filter.</summary>
        public LocationFilter? DestinationFilter { get; private set; }

        /// <summary>
        /// A concrete destination for this mission: the planet it names, or one drawn
        /// from its destination filter. Port of upstream <c>LocationFilter::PickPlanet</c>
        /// as called from <c>Mission::Instantiate</c>.
        /// </summary>
        /// <remarks>
        /// Most missions do not name where they send you. They describe it - a
        /// Republic world with a spaceport, somewhere in the Dirt Belt - and upstream
        /// picks a real planet when the mission is offered. Without that step the
        /// mission has no destination at all, and its text still reads
        /// "Researchers to &lt;planet&gt;" because there is nothing to substitute in.
        ///
        /// Candidates are ordered by name so the same mission on the same world always
        /// picks the same destination: a job that changed where it was going between
        /// two glances at the board would be unplayable.
        /// </remarks>
        public string? ResolveDestination(GameData? data, string? originSystem = null)
        {
            if (Destination != null)
                return Destination;

            if (data is null || DestinationFilter is null || DestinationFilter.IsEmpty)
                return null;

            // An unmodelled filter would match nothing, which would silently strand the
            // mission; leave it unresolved instead so the gap stays visible.
            if (DestinationFilter.HasUnmodelledTerms)
                return null;

            string? best = null;
            foreach (Planet planet in data.Planets.Values)
            {
                if (!planet.IsInhabited)
                    continue;

                // A planet's own system is what distance terms measure against.
                string? system = SystemOf(data, planet.Name);
                if (!DestinationFilter.Matches(planet, system, data, originSystem))
                    continue;

                if (best is null || string.CompareOrdinal(planet.Name, best) < 0)
                    best = planet.Name;
            }

            return best;
        }

        /// <summary>The system a planet sits in, or null if nothing lists it.</summary>
        private static string? SystemOf(GameData data, string planetName)
        {
            foreach (StarSystem system in data.Systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                    if (obj.PlanetName == planetName)
                        return system.Name;

            return null;
        }

        /// <summary>
        /// Whether this mission can be offered on a particular world.
        /// </summary>
        /// <remarks>
        /// Content targets the galaxy by DESCRIPTION far more often than by name - a
        /// job for Republic farming worlds is written once and offered on all of them.
        /// Ignoring that and treating every mission as offerable anywhere is not a
        /// small inaccuracy: it put 424 jobs on one planet's board, most of which
        /// belong to other governments, other species and other regions entirely.
        /// </remarks>
        public bool IsOfferedAt(Planet? planet, string? systemName = null)
        {
            if (planet is null)
                return false;

            if (Source != null)
                return planet.Name == Source;

            // A filter with terms this port does not model yet would silently reject
            // everything, so an unmodelled filter is treated as "no restriction"
            // rather than as "nowhere". Being too permissive is recoverable; making
            // content unreachable is not.
            if (SourceFilter is null || SourceFilter.IsEmpty || SourceFilter.HasUnmodelledTerms)
                return true;

            return SourceFilter.Matches(planet, systemName, null, systemName);
        }

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
        /// <remarks>
        /// Kept as the flat "days from acceptance" view of
        /// <see cref="DeadlineBase"/>; callers that know how far the job is going
        /// should use <see cref="DeadlineAfter"/> instead.
        /// </remarks>
        public int DeadlineDays => DeadlineBase;

        /// <summary>Days allowed regardless of distance.</summary>
        public int DeadlineBase { get; private set; }

        /// <summary>
        /// Extra days allowed per jump between where the job is taken and where it is
        /// going. Upstream computes the real deadline as
        /// <c>base + multiplier * jumps</c> (<c>Mission.cpp:165-172</c>), and a bare
        /// <c>deadline</c> means two days a jump and nothing else — so reading only a
        /// numeric token left 162 upstream missions with no deadline whatever, and the
        /// clock their content is built around never ran.
        /// </summary>
        public int DeadlineMultiplier { get; private set; }

        /// <summary>A fixed calendar deadline, where the mission names one outright.</summary>
        public DateTime? AbsoluteDeadline { get; private set; }

        /// <summary>
        /// When this mission is due, given where it was taken and how far it is going.
        /// Null when it has no deadline at all.
        /// </summary>
        public DateTime? DeadlineAfter(DateTime accepted, int jumps)
        {
            if (AbsoluteDeadline.HasValue)
                return AbsoluteDeadline;

            int days = DeadlineBase + DeadlineMultiplier * Math.Max(0, jumps);
            return days > 0 ? accepted.AddDays(days) : null;
        }

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

        /// <summary>
        /// Extra gate on ACCEPTING an offered mission. Parsing it away lets the player
        /// take on missions upstream would refuse.
        /// </summary>
        public ConditionSet ToAccept { get; private set; } = new ConditionSet();

        /// <summary>Gate on declining, used by content that forces a choice.</summary>
        public ConditionSet ToDecline { get; private set; } = new ConditionSet();

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

                    case "source" when child.Size >= 2:
                        // "source <planet>": offered on exactly that world.
                        Source = child.Token(1);
                        break;

                    case "source":
                        // "source" with children is a filter over places.
                        SourceFilter = LocationFilter.Load(child);
                        break;

                    case "destination" when child.Size >= 2:
                        Destination = child.Token(1);
                        break;

                    case "destination":
                        DestinationFilter = LocationFilter.Load(child);
                        break;

                    case "npc":
                        // The ships this mission puts in the galaxy, and what the
                        // player must do to them. A mission without these is a text box
                        // and a payment.
                        var npc = new MissionNpc();
                        npc.Load(child);
                        _npcs.Add(npc);
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

                    // Four shapes, all of them in use (Mission.cpp:163-173):
                    //   deadline                -> two days per jump
                    //   deadline <n>            -> n days flat
                    //   deadline <base> <mult>  -> both
                    //   deadline <d> <m> <y>    -> a fixed date
                    case "deadline" when child.Size >= 4 && child.IsNumber(1)
                                         && child.IsNumber(2) && child.IsNumber(3):
                        AbsoluteDeadline = SafeDate(child);
                        break;

                    case "deadline":
                        if (child.Size == 1)
                            DeadlineMultiplier += 2;
                        if (child.Size >= 2 && child.IsNumber(1))
                            DeadlineBase += (int)child.Value(1);
                        if (child.Size >= 3 && child.IsNumber(2))
                            DeadlineMultiplier += (int)child.Value(2);
                        break;

                    case "to" when child.Size >= 2:
                        switch (child.Token(1))
                        {
                            case "offer": ToOffer = ConditionSet.Load(child); break;
                            case "complete": ToComplete = ConditionSet.Load(child); break;
                            case "fail": ToFail = ConditionSet.Load(child); break;
                            case "accept": ToAccept = ConditionSet.Load(child); break;
                            case "decline": ToDecline = ConditionSet.Load(child); break;
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
                case "abort": trigger = MissionTrigger.Abort; return true;
                default: trigger = default; return false;
            }
        }

        /// <summary>
        /// Whether this mission may currently be offered. A mission whose failure
        /// condition is already satisfied is not offered: doing so hands the player
        /// something that is dead on arrival.
        /// </summary>
        public bool CanOffer(Conditions conditions) =>
            ToOffer.Test(conditions) && !HasFailed(conditions);

        /// <summary>Whether the player may accept an offered mission.</summary>
        public bool CanAccept(Conditions conditions) => ToAccept.Test(conditions);

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

        /// <summary>
        /// A date from three tokens, or null when the content names an impossible one.
        /// </summary>
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

        public override string ToString() => Name;
    }
}
