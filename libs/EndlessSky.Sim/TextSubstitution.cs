using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Fills the angle-bracket placeholders that mission and conversation text is
    /// written with. Port of the substitution map upstream builds in
    /// <c>Mission::Instantiate</c>, applied through <c>Format::Replace</c>.
    /// </summary>
    /// <remarks>
    /// Mission text in Endless Sky is a template. A job is authored as
    /// "Deliver <cargo> to <destination> by <date>" and the engine fills the brackets
    /// when the mission is offered, which is how one authored job serves every planet
    /// in the galaxy. Skipping this does not fail loudly - it just shows the player
    /// the template, and a job board reading "&lt;planet&gt; business convention"
    /// looks like a bug in the content rather than a missing feature.
    ///
    /// Replacement is single-pass and simultaneous: a value that itself contains
    /// angle brackets must not be rescanned, or content could inject placeholders into
    /// its own output.
    ///
    /// INCOMPLETE, tracked rather than dropped: phrase expansion inside substituted
    /// values, stopover and waypoint lists, and the "&lt;conditions&gt;" and
    /// "&lt;capacity&gt;" forms that depend on the player's ship.
    /// </remarks>
    public static class TextSubstitution
    {
        /// <summary>
        /// Builds the standard substitutions for a mission being offered to a player.
        /// </summary>
        public static Dictionary<string, string> For(Mission mission, PlayerState? player,
                                                     GameData? data, DateTime? deadline = null)
        {
            var subs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mission is null)
                return subs;

            // Where the player is standing when it is offered.
            subs["<origin>"] = player?.CurrentPlanet?.Name ?? player?.CurrentSystem?.Name ?? "";

            string destination = mission.Destination ?? "";
            subs["<planet>"] = destination;
            subs["<system>"] = SystemOf(destination, data);
            subs["<destination>"] = destination.Length == 0
                ? ""
                : subs["<system>"].Length > 0
                    ? $"{destination} in the {subs["<system>"]} system"
                    : destination;

            subs["<commodity>"] = mission.CargoType ?? "";
            subs["<tons>"] = Tons(mission.CargoTons);
            subs["<cargo>"] = mission.CargoTons > 0 && mission.CargoType != null
                ? $"{Tons(mission.CargoTons)} of {mission.CargoType}"
                : "";

            subs["<bunks>"] = mission.Passengers.ToString(CultureInfo.InvariantCulture);
            subs["<passengers>"] = mission.Passengers == 1 ? "passenger" : "passengers";
            subs["<fare>"] = mission.Passengers == 1
                ? "a passenger"
                : $"{mission.Passengers} passengers";

            long payment = mission.Action(MissionTrigger.Complete)?.Payment ?? 0;
            subs["<payment>"] = payment != 0 ? Credits(Math.Abs(payment)) : "";

            if (deadline.HasValue)
            {
                subs["<date>"] = deadline.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                subs["<day>"] = deadline.Value.ToString("dddd, d MMMM yyyy",
                                                        CultureInfo.InvariantCulture);
            }
            else
            {
                subs["<date>"] = "";
                subs["<day>"] = "";
            }

            return subs;
        }

        /// <summary>
        /// Replaces every placeholder in one pass.
        /// </summary>
        /// <remarks>
        /// Single-pass on purpose. Replacing one key at a time over the whole string
        /// would rescan text that has already been substituted, so a planet named with
        /// angle brackets - or any value containing them - could inject a further
        /// placeholder into its own output.
        /// </remarks>
        public static string Apply(string? text, IReadOnlyDictionary<string, string>? subs)
        {
            if (string.IsNullOrEmpty(text) || subs is null || subs.Count == 0)
                return text ?? string.Empty;

            var result = new StringBuilder(text!.Length);
            int i = 0;

            while (i < text.Length)
            {
                if (text[i] != '<')
                {
                    result.Append(text[i++]);
                    continue;
                }

                int close = text.IndexOf('>', i + 1);
                if (close < 0)
                {
                    result.Append(text, i, text.Length - i);
                    break;
                }

                string key = text.Substring(i, close - i + 1);
                if (subs.TryGetValue(key, out string? value))
                {
                    result.Append(value);
                }
                else
                {
                    // An unknown placeholder is left alone rather than blanked: content
                    // uses brackets for its own purposes, and silently eating them
                    // loses text nobody meant as a placeholder.
                    result.Append(key);
                }

                i = close + 1;
            }

            return result.ToString();
        }

        /// <summary>Convenience: substitute a mission's own display name.</summary>
        public static string NameOf(Mission mission, PlayerState? player, GameData? data) =>
            Apply(mission?.DisplayName, For(mission!, player, data));

        /// <summary>Convenience: substitute a mission's description.</summary>
        public static string DescriptionOf(Mission mission, PlayerState? player, GameData? data,
                                           DateTime? deadline = null) =>
            Apply(mission?.Description, For(mission!, player, data, deadline));

        private static string SystemOf(string planetName, GameData? data)
        {
            if (data is null || string.IsNullOrEmpty(planetName))
                return "";

            foreach (StarSystem system in data.Systems.Values)
                foreach (StellarObject obj in system.AllObjects())
                    if (obj.PlanetName == planetName)
                        return system.Name;

            return "";
        }

        private static string Tons(int tons) =>
            tons == 1 ? "1 ton" : $"{tons.ToString("n0", CultureInfo.InvariantCulture)} tons";

        private static string Credits(long amount) =>
            amount == 1 ? "1 credit"
                        : $"{amount.ToString("n0", CultureInfo.InvariantCulture)} credits";
    }
}
