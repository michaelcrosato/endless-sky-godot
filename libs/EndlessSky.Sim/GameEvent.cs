using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A scheduled change to the galaxy. Port of upstream <c>GameEvent</c>.
    /// </summary>
    /// <remarks>
    /// Events are how Endless Sky's universe moves. They set conditions, mark systems
    /// and planets visited, and - the part that matters most - carry raw definition
    /// nodes that are re-loaded over the existing universe when the event fires. That
    /// is how a shipyard gains a ship, a world changes government, or a hyperspace
    /// link opens partway through a campaign.
    ///
    /// The dataset defines 416 of them. Without events, content that is authored to
    /// appear later simply never appears: the Kestrel's shipyard, for instance, is
    /// defined EMPTY and is stocked only by three events, so the ship is unobtainable
    /// no matter how far a player progresses.
    ///
    /// Upstream sorts every child into one of three buckets, and the fallthrough is
    /// the important one: anything that is not a date, a visit mark, or a recognised
    /// definition node is a CONDITION ASSIGNMENT. That is why "set", "clear" and bare
    /// arithmetic all work inside an event without being listed anywhere.
    ///
    /// INCOMPLETE, tracked rather than dropped: "save raw changes" is parsed and
    /// ignored, since it only affects how a save file is written, and there is no save
    /// system yet.
    /// </remarks>
    public class GameEvent
    {
        /// <summary>
        /// Nodes an event may re-load over the universe. Upstream builds this from its
        /// DEFINITION_NODES set plus link and unlink; anything here can modify an
        /// existing object but never create a new kind of one.
        /// </summary>
        private static readonly HashSet<string> AllowedChanges =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Upstream's DEFINITION_NODES (GameEvent.cpp:29) ...
                "fleet", "galaxy", "government", "outfitter", "news", "planet",
                "shipyard", "system", "substitutions", "wormhole",
                // ... plus the two that modify without defining.
                "link", "unlink",
            };

        private readonly List<DataNode> _changes = new List<DataNode>();
        private readonly List<string> _systemsToVisit = new List<string>();
        private readonly List<string> _systemsToUnvisit = new List<string>();
        private readonly List<string> _planetsToVisit = new List<string>();
        private readonly List<string> _planetsToUnvisit = new List<string>();

        public GameEvent(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public string Name { get; }

        /// <summary>The date this event fires, or null if it is triggered by content.</summary>
        public DateTime? Date { get; private set; }

        /// <summary>Definition nodes to re-load over the universe when this fires.</summary>
        public IReadOnlyList<DataNode> Changes => _changes;

        public ConditionAssignments Conditions { get; private set; } = new ConditionAssignments();

        public IReadOnlyList<string> SystemsToVisit => _systemsToVisit;

        public IReadOnlyList<string> SystemsToUnvisit => _systemsToUnvisit;

        public IReadOnlyList<string> PlanetsToVisit => _planetsToVisit;

        public IReadOnlyList<string> PlanetsToUnvisit => _planetsToUnvisit;

        public void Load(DataNode node)
        {
            var assignments = new List<DataNode>();

            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                bool hasValue = child.Size >= 2;

                if (key == "date" && child.Size >= 4)
                {
                    Date = SafeDate(child.Value(1), child.Value(2), child.Value(3));
                }
                else if (key == "visit" && hasValue)
                {
                    _systemsToVisit.Add(child.Token(1));
                }
                else if (key == "unvisit" && hasValue)
                {
                    _systemsToUnvisit.Add(child.Token(1));
                }
                else if (key == "visit planet" && hasValue)
                {
                    _planetsToVisit.Add(child.Token(1));
                }
                else if (key == "unvisit planet" && hasValue)
                {
                    _planetsToUnvisit.Add(child.Token(1));
                }
                else if (key == "save raw changes")
                {
                    // Only affects how a save file is written; nothing to do here yet.
                }
                else if (AllowedChanges.Contains(key))
                {
                    _changes.Add(child);
                }
                else
                {
                    // Everything else is a condition assignment. This fallthrough is
                    // upstream's, and it is what makes "set", "clear" and bare
                    // arithmetic work inside an event without being enumerated.
                    assignments.Add(child);
                }
            }

            if (assignments.Count > 0)
                Conditions = ConditionAssignments.Load(Wrap(node, assignments));
        }

        /// <summary>
        /// Fires this event: patches the universe, marks visits, and applies condition
        /// assignments including upstream's "event: &lt;name&gt;" marker.
        /// </summary>
        public void Apply(GameData? data, PlayerState? player)
        {
            if (data is not null)
                foreach (DataNode change in _changes)
                    data.ApplyChange(change);

            if (player is null)
                return;

            foreach (string system in _systemsToVisit)
                if (data is not null && data.Systems.TryGetValue(system, out StarSystem? found))
                    player.MarkVisited(found);

            foreach (string system in _systemsToUnvisit)
                player.ClearVisitedSystem(system);

            foreach (string planet in _planetsToVisit)
                if (data is not null && data.Planets.TryGetValue(planet, out Planet? found))
                    player.MarkVisited(found);

            foreach (string planet in _planetsToUnvisit)
                player.ClearVisitedPlanet(planet);

            Conditions.Apply(player.Conditions);

            // Upstream records that the event happened, so content can gate on it.
            player.Conditions.Set("event: " + Name, 1);
        }

        /// <summary>Whether this event's scheduled date has arrived.</summary>
        public bool IsDue(DateTime date) => Date.HasValue && date >= Date.Value;

        private static DateTime? SafeDate(double day, double month, double year)
        {
            try
            {
                return new DateTime((int)year, (int)month, (int)day);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Content occasionally carries an impossible date; upstream's Date type
                // tolerates it rather than refusing to load the file.
                return null;
            }
        }

        /// <summary>
        /// Rebuilds a node carrying only the assignment children, since
        /// <see cref="ConditionAssignments"/> loads from a parent node.
        /// </summary>
        private static DataNode Wrap(DataNode _, List<DataNode> children)
        {
            var wrapper = new DataNode();
            wrapper.AddToken("on");
            foreach (DataNode child in children)
                wrapper.AddChild(child);

            return wrapper;
        }

        public override string ToString() =>
            Date.HasValue ? $"{Name} ({Date.Value:yyyy-MM-dd})" : Name;
    }
}
