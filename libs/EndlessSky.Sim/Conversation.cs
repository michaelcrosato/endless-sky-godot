using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>How a conversation ended.</summary>
    public enum ConversationOutcome
    {
        /// <summary>Still running, or ran off the end without an explicit ending.</summary>
        None,
        Accept,
        Decline,
        Defer,
        Die,
        Launch,
        Flee,
        Depart,
    }

    /// <summary>
    /// A branching dialogue. Port of upstream <c>Conversation</c>.
    /// </summary>
    /// <remarks>
    /// Conversations are how Endless Sky delivers nearly all of its narrative, and
    /// they are a small program rather than a script: text nodes, labels, gotos,
    /// conditional branches, player choices, and condition side effects, terminating
    /// in an outcome that tells the caller whether the mission was accepted.
    ///
    /// INCOMPLETE, tracked rather than dropped: text substitutions
    /// (<c>&lt;origin&gt;</c>, <c>&lt;payment&gt;</c>, <c>${phrase}</c>), scene
    /// images, per-node display conditions, and the "apply" block.
    /// </remarks>
    public class Conversation
    {
        internal enum Kind { Text, Choice, Label, Goto, Branch, Action, End }

        internal sealed class Element
        {
            public Kind Kind;
            public string Text = string.Empty;

            /// <summary>Label to jump to, for Goto and for a Text node with a goto child.</summary>
            public string? Target;

            /// <summary>Branch only: label taken when the condition fails.</summary>
            public string? ElseTarget;

            public ConditionSet? Condition;
            public ConditionAssignments? Assignments;
            public ConversationOutcome Outcome;

            /// <summary>Choice only: option text, the label it jumps to, and any ending it triggers.</summary>
            public List<(string Text, string? Target, ConversationOutcome Outcome)> Options =
                new List<(string, string?, ConversationOutcome)>();
        }

        private readonly List<Element> _elements = new List<Element>();
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>(StringComparer.Ordinal);

        public string Name { get; private set; } = string.Empty;

        internal IReadOnlyList<Element> Elements => _elements;

        public int ElementCount => _elements.Count;

        public bool HasLabel(string label) => label is not null && _labels.ContainsKey(label);

        internal bool TryGetLabel(string label, out int index) => _labels.TryGetValue(label, out index);

        public static Conversation Load(DataNode node)
        {
            var conversation = new Conversation { Name = node.Size >= 2 ? node.Token(1) : string.Empty };
            conversation.LoadElements(node);
            return conversation;
        }

        private void LoadElements(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                string key = child.Token(0);

                switch (key)
                {
                    case "label" when child.Size >= 2:
                        // Labels are positions, not elements the runner stops on.
                        _labels[child.Token(1)] = _elements.Count;
                        _elements.Add(new Element { Kind = Kind.Label, Text = child.Token(1) });
                        break;

                    case "goto" when child.Size >= 2:
                        _elements.Add(new Element { Kind = Kind.Goto, Target = child.Token(1) });
                        break;

                    case "branch" when child.Size >= 2:
                        // "branch <label> [<else label>]" with the test as children.
                        _elements.Add(new Element
                        {
                            Kind = Kind.Branch,
                            Target = child.Token(1),
                            ElseTarget = child.Size >= 3 ? child.Token(2) : null,
                            Condition = ConditionSet.Load(child),
                        });
                        break;

                    case "choice":
                        _elements.Add(LoadChoice(child));
                        break;

                    case "action":
                        _elements.Add(new Element
                        {
                            Kind = Kind.Action,
                            Assignments = ConditionAssignments.Load(child),
                        });
                        break;

                    case "accept": AddEnd(ConversationOutcome.Accept); break;
                    case "decline": AddEnd(ConversationOutcome.Decline); break;
                    case "defer": AddEnd(ConversationOutcome.Defer); break;
                    case "die": AddEnd(ConversationOutcome.Die); break;
                    case "launch": AddEnd(ConversationOutcome.Launch); break;
                    case "flee": AddEnd(ConversationOutcome.Flee); break;
                    case "depart": AddEnd(ConversationOutcome.Depart); break;

                    // "scene" and "apply" are recognised but not yet modelled.
                    case "scene":
                        break;

                    default:
                        // Anything else is narration. Upstream hangs flow control off
                        // the text node as CHILDREN rather than siblings, so a line
                        // like:
                        //     `They wave you off.`
                        //         decline
                        // both speaks and ends the conversation.
                        _elements.Add(new Element
                        {
                            Kind = Kind.Text,
                            Text = key,
                            Target = FindGoto(child),
                            Outcome = FindOutcome(child),
                        });
                        break;
                }
            }
        }

        private void AddEnd(ConversationOutcome outcome) =>
            _elements.Add(new Element { Kind = Kind.End, Outcome = outcome });

        private static Element LoadChoice(DataNode node)
        {
            var element = new Element { Kind = Kind.Choice };

            foreach (DataNode option in node.Children)
                element.Options.Add((option.Token(0), FindGoto(option), FindOutcome(option)));

            return element;
        }

        /// <summary>An ending declared as a child of a text node or choice option.</summary>
        private static ConversationOutcome FindOutcome(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                switch (child.Token(0))
                {
                    case "accept": return ConversationOutcome.Accept;
                    case "decline": return ConversationOutcome.Decline;
                    case "defer": return ConversationOutcome.Defer;
                    case "die": return ConversationOutcome.Die;
                    case "launch": return ConversationOutcome.Launch;
                    case "flee": return ConversationOutcome.Flee;
                    case "depart": return ConversationOutcome.Depart;
                }
            }

            return ConversationOutcome.None;
        }

        /// <summary>A goto child redirects flow after this node.</summary>
        private static string? FindGoto(DataNode node)
        {
            foreach (DataNode child in node.Children)
            {
                if (child.Token(0) == "goto" && child.Size >= 2)
                    return child.Token(1);
            }

            return null;
        }

        public override string ToString() => $"{Name} ({_elements.Count} elements)";
    }
}
