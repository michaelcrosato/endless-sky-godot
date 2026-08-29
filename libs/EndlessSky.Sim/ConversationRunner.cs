using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Walks a <see cref="Conversation"/>, gathering narration until it needs the
    /// player to choose or the dialogue ends.
    /// </summary>
    /// <remarks>
    /// Kept in the simulation layer, engine-free, so conversation flow can be tested
    /// without a UI. The presentation layer drives it: read <see cref="PendingText"/>
    /// and <see cref="Choices"/>, call <see cref="Choose"/>, repeat until
    /// <see cref="IsFinished"/>.
    ///
    /// A guard against runaway jumps is deliberate rather than defensive: content can
    /// and does contain label cycles, and upstream's own editor tooling warns about
    /// them. Looping forever inside a game frame would hang the engine.
    /// </remarks>
    public class ConversationRunner
    {
        /// <summary>Upper bound on jumps between player interactions, to survive a label cycle.</summary>
        private const int MaxStepsPerAdvance = 10_000;

        private readonly Conversation _conversation;
        private readonly Conditions _conditions;
        private readonly List<string> _pending = new List<string>();
        private readonly List<string> _choices = new List<string>();

        private int _index;

        public ConversationRunner(Conversation conversation, Conditions conditions)
        {
            _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
            _conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));

            Advance();
        }

        /// <summary>Narration accumulated since the last interaction, in order.</summary>
        public IReadOnlyList<string> PendingText => _pending;

        /// <summary>Options awaiting the player, empty when none are pending.</summary>
        public IReadOnlyList<string> Choices => _choices;

        public bool IsFinished { get; private set; }

        public ConversationOutcome Outcome { get; private set; } = ConversationOutcome.None;

        /// <summary>True when the runner is waiting on <see cref="Choose"/>.</summary>
        public bool IsAwaitingChoice => _choices.Count > 0;

        /// <summary>True when a jump cycle was detected and the runner bailed out.</summary>
        public bool AbortedOnCycle { get; private set; }

        /// <summary>
        /// Takes an option. The conversation resumes from that option's target, or
        /// simply continues after the choice when the option names none.
        /// </summary>
        public void Choose(int optionIndex)
        {
            if (IsFinished || !IsAwaitingChoice)
                return;

            if (optionIndex < 0 || optionIndex >= _choices.Count)
                throw new ArgumentOutOfRangeException(nameof(optionIndex));

            string? target = _pendingOptionTargets[optionIndex];
            ConversationOutcome outcome = _pendingOptionOutcomes[optionIndex];
            _choices.Clear();
            _pendingOptionTargets.Clear();
            _pendingOptionOutcomes.Clear();
            _pending.Clear();

            // An option can end the conversation outright rather than jumping.
            if (outcome != ConversationOutcome.None)
            {
                Outcome = outcome;
                IsFinished = true;
                return;
            }

            if (target is not null && _conversation.TryGetLabel(target, out int index))
                _index = index;
            else
                _index++;

            Advance();
        }

        private readonly List<string?> _pendingOptionTargets = new List<string?>();
        private readonly List<ConversationOutcome> _pendingOptionOutcomes = new List<ConversationOutcome>();

        /// <summary>Runs forward until a choice is required or the conversation ends.</summary>
        private void Advance()
        {
            int steps = 0;

            while (_index >= 0 && _index < _conversation.ElementCount)
            {
                if (++steps > MaxStepsPerAdvance)
                {
                    AbortedOnCycle = true;
                    IsFinished = true;
                    return;
                }

                Conversation.Element element = _conversation.Elements[_index];

                switch (element.Kind)
                {
                    case Conversation.Kind.Label:
                    case Conversation.Kind.NamePrompt:
                        // A name-entry prompt asks the player for a name; it is not
                        // narration and must not be shown as a line of dialogue.
                        _index++;
                        break;

                    case Conversation.Kind.Text:
                        _pending.Add(element.Text);

                        // An ending hangs off the text node itself upstream: the line
                        // is spoken and the conversation then ends.
                        if (element.Outcome != ConversationOutcome.None)
                        {
                            Outcome = element.Outcome;
                            IsFinished = true;
                            return;
                        }

                        if (!Jump(element.Target))
                            _index++;
                        break;

                    case Conversation.Kind.Action:
                        element.Assignments?.Apply(_conditions);
                        _index++;
                        break;

                    case Conversation.Kind.Goto:
                        if (!Jump(element.Target))
                            _index++;
                        break;

                    case Conversation.Kind.Branch:
                        {
                            bool taken = element.Condition?.Test(_conditions) ?? false;
                            string? target = taken ? element.Target : element.ElseTarget;
                            ConversationOutcome outcome = taken ? element.Outcome : element.ElseOutcome;

                            // A branch target may be an endpoint rather than a label.
                            if (outcome != ConversationOutcome.None)
                            {
                                Outcome = outcome;
                                IsFinished = true;
                                return;
                            }

                            if (!Jump(target))
                                _index++;
                            break;
                        }

                    case Conversation.Kind.Choice:
                        for (int option = 0; option < element.Options.Count; option++)
                        {
                            // An option's `to display` gate decides whether the player
                            // is even shown it (Conversation.cpp:507-509). Ignoring the
                            // gate offers choices the content meant to hide, and gives
                            // away that the branch exists at all.
                            ConditionSet? gate = option < element.OptionGates.Count
                                ? element.OptionGates[option]
                                : null;

                            if (gate != null && !gate.Test(_conditions))
                                continue;

                            (string text, string? target, ConversationOutcome outcome) = element.Options[option];
                            _choices.Add(text);
                            _pendingOptionTargets.Add(target);
                            _pendingOptionOutcomes.Add(outcome);
                        }

                        // A choice with no options cannot be answered; treat it as
                        // narration and continue rather than deadlocking.
                        if (_choices.Count == 0)
                        {
                            _index++;
                            break;
                        }
                        return;

                    case Conversation.Kind.End:
                        Outcome = element.Outcome;
                        IsFinished = true;
                        return;
                }
            }

            // Running off the end is a DECLINE upstream, not a neutral ending: any
            // jump landing outside the node list is mapped to Endpoint::DECLINE. A
            // caller treating "no outcome" as acceptance would silently hand out
            // missions the player never agreed to.
            Outcome = ConversationOutcome.Decline;
            IsFinished = true;
        }

        private bool Jump(string? label)
        {
            if (label is null || !_conversation.TryGetLabel(label, out int index))
                return false;

            _index = index;
            return true;
        }
    }
}
