using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// How the galaxy's governments react to what the player does. Port of upstream
    /// <c>Politics::Offend</c> (<c>Politics.cpp:111-149</c>).
    /// </summary>
    /// <remarks>
    /// Reputation is the directive's "faction reputation", and nothing in the game
    /// moved it: <see cref="Government.Offend"/> existed and had no caller anywhere
    /// outside a unit test, so a player could destroy a navy fleet or clear a system of
    /// pirates and every government in the galaxy would feel exactly the same about
    /// them afterwards.
    ///
    /// The part that has to live above a single government is the propagation. An
    /// offence against one government is felt by EVERY government, weighted by how that
    /// government feels about the victim — which is the only mechanism by which
    /// shooting pirates earns navy goodwill, or shooting a navy turns its allies
    /// hostile. Applying the penalty to the victim alone, as a method on Government
    /// must, loses that entirely.
    ///
    /// INCOMPLETE, tracked rather than dropped: the atrocity special case that clamps
    /// standing to zero rather than merely lowering it, per-government penalty
    /// overrides beyond the shared defaults, penalty scaling by ship cost, and bribes.
    /// </remarks>
    public class Politics
    {
        /// <summary>
        /// Weights below this never move reputation, so two governments can be allies
        /// without the player's dealings with one bleeding into the other.
        /// </summary>
        public const double MinimumWeight = 0.05;

        private readonly GameData _data;

        public Politics(GameData data) =>
            _data = data ?? throw new ArgumentNullException(nameof(data));

        /// <summary>
        /// Commits an offence against a government, which every other government
        /// judges according to how it feels about the victim.
        /// </summary>
        /// <param name="offended">Whose ship it was.</param>
        /// <param name="action">"disable", "board", "capture", "destroy", "assist"…</param>
        /// <param name="count">
        /// Crew aboard, upstream's measure of how much the act cost. Zero for an
        /// unmanned drone, which is why shooting one provokes without costing standing.
        /// </param>
        public void Offend(Government? offended, string action, int count = 1)
        {
            // A government cannot offend itself, and the player's own flag has no
            // standing to lose (Politics.cpp:116-117).
            if (offended is null || offended.IsPlayer || count == 0)
                return;

            foreach (Government other in _data.Governments.Values)
            {
                double weight = other.AttitudeToward(offended);
                if (Math.Abs(weight) < MinimumWeight)
                    continue;

                // A government wholly on the victim's side (weight 1, which is what
                // AttitudeToward returns for itself) takes the full penalty; one that
                // hates the victim gains by the same measure, because the weight is
                // negative and the penalty is subtracted.
                other.Offend(action, count * weight);
            }
        }
    }
}
