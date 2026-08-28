using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Where a new pilot begins: the date, the world, the money and the conditions
    /// they start with. Port of upstream <c>StartConditions</c>.
    /// </summary>
    /// <remarks>
    /// The directive's Milestone 7 rule is explicit: do not hard-code content that can
    /// be loaded from the source data. A start date of 16 November 3013, a start
    /// system of Rutilicus, a start planet of New Boston and an opening balance of
    /// 480,000 credits are all in <c>starts.txt</c>, and all four were constants in
    /// the flight scene.
    ///
    /// The starting conditions matter as much as the numbers. "default" sets a pilot's
    /// licence, a species and a "start: default" marker, and content gates on all
    /// three — a campaign that checks for the licence simply never fires for a player
    /// who was placed in the world by hand.
    ///
    /// Note that a start does NOT name a ship. Upstream's opening conversation sells
    /// the player their first hull on credit, which is why the classic start begins in
    /// debt. Until conversations can run and grant ships, a caller still has to choose
    /// one.
    ///
    /// INCOMPLETE, tracked rather than dropped: the mortgage and its interest and
    /// term, the score, the thumbnail, and running the intro conversation.
    /// </remarks>
    public class StartScenario
    {
        private readonly List<string> _description = new List<string>();

        public StartScenario(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DisplayName = name;
        }

        /// <summary>Identifier, e.g. "default".</summary>
        public string Name { get; }

        /// <summary>Title shown when choosing a start, e.g. "Endless Sky".</summary>
        public string DisplayName { get; private set; }

        /// <summary>Paragraphs describing the scenario.</summary>
        public IReadOnlyList<string> Description => _description;

        public DateTime? Date { get; private set; }

        public string? SystemName { get; private set; }

        public string? PlanetName { get; private set; }

        /// <summary>The conversation that opens the game, if any.</summary>
        public string? Conversation { get; private set; }

        public long Credits { get; private set; }

        /// <summary>Debt the player starts under, from the account's mortgage.</summary>
        public long MortgagePrincipal { get; private set; }

        /// <summary>Conditions set before the first frame.</summary>
        public ConditionAssignments Conditions { get; private set; } = new ConditionAssignments();

        public void Load(DataNode node)
        {
            var assignments = new List<DataNode>();

            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);
                bool hasValue = child.Size >= 2;

                switch (key)
                {
                    case "name" when hasValue:
                        DisplayName = child.Token(1);
                        break;

                    case "description" when hasValue:
                        _description.Add(child.Token(1));
                        break;

                    case "date" when child.Size >= 4:
                        Date = SafeDate(child);
                        break;

                    case "system" when hasValue:
                        SystemName = child.Token(1);
                        break;

                    case "planet" when hasValue:
                        PlanetName = child.Token(1);
                        break;

                    case "conversation" when hasValue:
                        Conversation = child.Token(1);
                        break;

                    case "account":
                        LoadAccount(child);
                        break;

                    case "thumbnail":
                        break;

                    default:
                        // Same fallthrough upstream uses for events: anything not
                        // recognised is a condition assignment, which is how "set" works
                        // here without being listed.
                        assignments.Add(child);
                        break;
                }
            }

            if (assignments.Count > 0)
                Conditions = ConditionAssignments.Load(Wrap(assignments));
        }

        /// <summary>
        /// Places a player at this start: date, money, world and opening conditions.
        /// </summary>
        public void ApplyTo(PlayerState? player, GameData? data)
        {
            if (player is null)
                return;

            if (Date.HasValue)
            {
                player.SetDate(Date.Value);
                player.SetStartDate(Date.Value);
            }

            player.SetCredits(Credits);
            Conditions.Apply(player.Conditions);

            if (data is null)
                return;

            if (SystemName != null && data.Systems.TryGetValue(SystemName, out StarSystem? system))
                player.EnterSystem(system);

            if (PlanetName != null && data.Planets.TryGetValue(PlanetName, out Planet? planet))
                player.Land(planet);
        }

        private void LoadAccount(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                if (child.Token(0) == "credits" && child.Size >= 2)
                {
                    Credits = (long)child.Value(1);
                }
                else if (child.Token(0) == "mortgage")
                {
                    foreach (DataNode term in child.Children)
                        if (term.Token(0) == "principal" && term.Size >= 2)
                            MortgagePrincipal = (long)term.Value(1);
                }
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

        private static DataNode Wrap(List<DataNode> children)
        {
            var wrapper = new DataNode();
            wrapper.AddToken("on");
            foreach (DataNode child in children)
                wrapper.AddChild(child);

            return wrapper;
        }

        public override string ToString() =>
            $"{DisplayName} ({PlanetName ?? SystemName ?? "?"}, {Credits:n0} cr)";
    }
}
