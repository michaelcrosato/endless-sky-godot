using System;

namespace EndlessSky.Sim
{
    /// <summary>The stages of the opening tutorial, in the order a new pilot meets them.</summary>
    public enum TutorialStep
    {
        /// <summary>Find somewhere to put down and land on it.</summary>
        Land,

        /// <summary>Take work off the job board.</summary>
        TakeJob,

        /// <summary>Travel to the system the work is going to.</summary>
        Jump,

        /// <summary>Land at the destination and hand the job in.</summary>
        Deliver,

        /// <summary>Nothing further to teach.</summary>
        Done,
    }

    /// <summary>
    /// What the tutorial can see of the game this frame: plain observed facts, with no
    /// reference to how any of them came about.
    /// </summary>
    /// <remarks>
    /// Deliberately facts rather than objects. The tutorial asking a
    /// <see cref="MissionLog"/> or a <see cref="GameData"/> its questions directly
    /// would tie the sequence of a first-time-user experience to the shape of the
    /// mission system, and every later change to one would drag the other along.
    /// Resolving a job's destination world into a system is the caller's problem,
    /// because the caller is the one that already holds the galaxy.
    /// </remarks>
    public struct TutorialState
    {
        /// <summary>Whether the player is on the ground right now.</summary>
        public bool IsLanded;

        /// <summary>Whether the player is carrying at least one accepted job.</summary>
        public bool HasJob;

        /// <summary>Whether a job has been handed in successfully.</summary>
        public bool DeliveredAJob;

        /// <summary>How much work the counter the player is standing at is offering.</summary>
        public int JobsOnOffer;

        /// <summary>The system the player is in.</summary>
        public string? CurrentSystem;

        /// <summary>The system the carried job is going to, if it named one.</summary>
        public string? DestinationSystem;

        /// <summary>The world the carried job is going to, if it named one.</summary>
        public string? DestinationPlanet;
    }

    /// <summary>
    /// The opening tutorial: land, take a job, travel, deliver — the loop the whole
    /// game is built out of, taught once.
    /// </summary>
    /// <remarks>
    /// Driven by POLLING <see cref="TutorialState"/> rather than by the view calling
    /// "the player just landed". Two reasons, and the second is the one that matters:
    ///
    /// - A poll cannot miss an event. A player who lands, accepts a job and takes off
    ///   between two ticks arrives at the right step anyway, because the step is
    ///   derived from where they ARE rather than from a sequence of notifications that
    ///   all had to fire.
    /// - It keeps the whole sequence testable with no engine. An event-fed design puts
    ///   the interesting half — did the right call site actually fire? — on the view
    ///   side of the architecture boundary, and this project has now lost two separate
    ///   defects in exactly that gap.
    ///
    /// It gets out of the way rather than blocking. Every step that a particular
    /// galaxy, world or job cannot satisfy is skipped, because a tutorial that asks
    /// for something impossible is worse than no tutorial: the player cannot tell an
    /// instruction they are failing from one the game cannot accept.
    /// </remarks>
    public class Tutorial
    {
        /// <summary>Where the player has got to.</summary>
        public TutorialStep Step { get; private set; } = TutorialStep.Land;

        /// <summary>Whether the player waved it away.</summary>
        public bool IsDismissed { get; private set; }

        /// <summary>Whether there is nothing further to show.</summary>
        public bool IsComplete => IsDismissed || Step == TutorialStep.Done;

        /// <summary>Where the current step is sending the player, when it is sending them anywhere.</summary>
        private string _target = string.Empty;

        /// <summary>What to tell the player to do now.</summary>
        public string Prompt => Step switch
        {
            TutorialStep.Land =>
                "Press L to pick somewhere to land — it flies you there and puts you down. " +
                "Press L again to choose a different world. Green labels have a port.",
            TutorialStep.TakeJob =>
                "You are on the ground. Open the JOBS counter and press B to take a job.",
            TutorialStep.Jump =>
                $"Your job is bound for {Naming(_target)}. Press J to line up the jump, " +
                "and hold your course until it goes.",
            TutorialStep.Deliver =>
                $"You have arrived. Land at {Naming(_target)} and hand the job in from the JOBS counter.",
            _ => string.Empty,
        };

        private static string Naming(string place) =>
            string.IsNullOrEmpty(place) ? "your destination" : place;

        /// <summary>
        /// Look at the world and advance if the current step has been satisfied.
        /// Returns a line worth showing when a step was just finished, and null
        /// otherwise — so a caller can flash a confirmation without having to work out
        /// for itself whether anything changed.
        /// </summary>
        /// <remarks>
        /// At most one step per call, deliberately. A player who arrives having already
        /// done three of them walks forward one step per frame and reaches the right
        /// place within a few frames, which costs nothing and keeps each confirmation
        /// visible rather than collapsing the whole sequence into one silent jump.
        /// </remarks>
        public string? Observe(TutorialState state)
        {
            if (IsComplete)
                return null;

            switch (Step)
            {
                case TutorialStep.Land:
                    if (!state.IsLanded)
                        return null;
                    return Finish(TutorialStep.TakeJob, state, "Landed. This is a world's port.");

                case TutorialStep.TakeJob:
                    // Nothing on the board is not the player's failure to find it.
                    if (state.IsLanded && state.JobsOnOffer <= 0 && !state.HasJob)
                        return Finish(TutorialStep.Done, state,
                                      "Nothing is on offer here — try another world's job board.");

                    if (!state.HasJob)
                        return null;

                    return Finish(TutorialStep.Jump, state, "Job accepted. It is in your hold.");

                case TutorialStep.Jump:
                    // A job that names nowhere has no jump to teach.
                    if (state.DestinationSystem is null)
                        return Finish(TutorialStep.Deliver, state, null);

                    if (state.CurrentSystem != state.DestinationSystem)
                        return null;

                    return Finish(TutorialStep.Deliver, state,
                                  $"You are in {state.DestinationSystem}.");

                case TutorialStep.Deliver:
                    if (!state.DeliveredAJob)
                        return null;

                    return Finish(TutorialStep.Done, state,
                                  "Delivered, and paid. That is the whole game: " +
                                  "take work, go there, get paid. Good flying.");
            }

            return null;
        }

        /// <summary>Advance, re-aim the prompt at wherever the new step points, and report.</summary>
        private string? Finish(TutorialStep next, TutorialState state, string? confirmation)
        {
            Step = next;
            _target = next switch
            {
                TutorialStep.Jump => state.DestinationSystem ?? string.Empty,
                TutorialStep.Deliver => state.DestinationPlanet ?? string.Empty,
                _ => string.Empty,
            };

            return confirmation;
        }

        /// <summary>Wave it away. It does not come back.</summary>
        public void Dismiss() => IsDismissed = true;
    }
}
