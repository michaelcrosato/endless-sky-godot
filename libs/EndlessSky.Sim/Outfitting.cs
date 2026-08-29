using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// Outfit installation rules. Port of upstream <c>Outfit::CanAdd</c>.
    /// </summary>
    /// <remarks>
    /// Endless Sky has no bespoke "does this fit" logic per outfit category. An
    /// outfit simply carries negative attributes for the capacities it consumes -
    /// <c>"outfit space" -5</c>, <c>"gun ports" -1</c> - and installation is legal
    /// exactly when no attribute would fall below its minimum. Every capacity rule in
    /// the game falls out of that one check, which is why unmodified content and
    /// plugins work without the engine knowing what a "gun port" means.
    ///
    /// Most attributes floor at zero. Upstream documents the exceptions in a
    /// MINIMUM_OVERRIDES table: 97 attributes may take any value (heat and
    /// energy costs that can legitimately be negative), and 32 have explicit
    /// floors so that multipliers cannot invert or divide by zero. Both tables below
    /// are transcribed from that source.
    ///
    /// INCOMPLETE, tracked rather than dropped: the "required crew" special case for
    /// automatons, licence requirements, and outfitter stock lists.
    /// </remarks>
    public static class Outfitting
    {
        /// <summary>Attributes upstream allows to hold any value, including negative.</summary>
        private static readonly HashSet<string> Unbounded = new HashSet<string>(StringComparer.Ordinal)
        {
            "shield energy",
            "shield fuel",
            "shield heat",
            "hull energy",
            "hull fuel",
            "hull heat",
            "hull threshold",
            "energy generation",
            "energy consumption",
            "fuel generation",
            "fuel consumption",
            "fuel energy",
            "fuel heat",
            "heat generation",
            "flotsam chance",
            "thrusting shields",
            "thrusting hull",
            "thrusting energy",
            "thrusting fuel",
            "thrusting heat",
            "thrusting discharge",
            "thrusting corrosion",
            "thrusting ion",
            "thrusting leakage",
            "thrusting burn",
            "thrusting disruption",
            "thrusting slowing",
            "turning shields",
            "turning hull",
            "turning energy",
            "turning fuel",
            "turning heat",
            "turning discharge",
            "turning corrosion",
            "turning ion",
            "turning leakage",
            "turning burn",
            "turning disruption",
            "turning slowing",
            "reverse thrusting shields",
            "reverse thrusting hull",
            "reverse thrusting energy",
            "reverse thrusting fuel",
            "reverse thrusting heat",
            "reverse thrusting discharge",
            "reverse thrusting corrosion",
            "reverse thrusting ion",
            "reverse thrusting leakage",
            "reverse thrusting burn",
            "reverse thrusting disruption",
            "reverse thrusting slowing",
            "afterburner shields",
            "afterburner hull",
            "afterburner energy",
            "afterburner fuel",
            "afterburner heat",
            "afterburner discharge",
            "afterburner corrosion",
            "afterburner ion",
            "afterburner leakage",
            "afterburner burn",
            "afterburner disruption",
            "afterburner slowing",
            "cooling energy",
            "discharge resistance energy",
            "discharge resistance fuel",
            "discharge resistance heat",
            "corrosion resistance energy",
            "corrosion resistance fuel",
            "corrosion resistance heat",
            "ion resistance energy",
            "ion resistance fuel",
            "ion resistance heat",
            "scramble resistance energy",
            "scramble resistance fuel",
            "scramble resistance heat",
            "leak resistance energy",
            "leak resistance fuel",
            "leak resistance heat",
            "burn resistance energy",
            "burn resistance fuel",
            "burn resistance heat",
            "disruption resistance energy",
            "disruption resistance fuel",
            "disruption resistance heat",
            "slowing resistance energy",
            "slowing resistance fuel",
            "slowing resistance heat",
            "crew equivalent",
            "cloaking energy",
            "cloaking fuel",
            "cloaking heat",
            "cloaking hull",
            "cloaking repair delay",
            "cloaking shields",
            "cloaking shield delay",
            "cloaked firing",
        };

        /// <summary>Attributes with an explicit floor other than zero.</summary>
        private static readonly Dictionary<string, double> MinimumOverrides =
            new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["shield protection"] = -0.99,
            ["hull protection"] = -0.99,
            ["energy protection"] = -0.99,
            ["fuel protection"] = -0.99,
            ["heat protection"] = -0.99,
            ["piercing protection"] = -0.99,
            ["force protection"] = -0.99,
            ["discharge protection"] = -0.99,
            ["drag reduction"] = -0.99,
            ["corrosion protection"] = -0.99,
            ["inertia reduction"] = -0.99,
            ["ion protection"] = -0.99,
            ["scramble protection"] = -0.99,
            ["leak protection"] = -0.99,
            ["burn protection"] = -0.99,
            ["disruption protection"] = -0.99,
            ["slowing protection"] = -0.99,
            ["hull multiplier"] = -1,
            ["hull repair multiplier"] = -1,
            ["hull energy multiplier"] = -1,
            ["hull fuel multiplier"] = -1,
            ["hull heat multiplier"] = -1,
            ["cloaked repair multiplier"] = -1,
            ["shield multiplier"] = -1,
            ["shield generation multiplier"] = -1,
            ["shield energy multiplier"] = -1,
            ["shield fuel multiplier"] = -1,
            ["shield heat multiplier"] = -1,
            ["cloaked regen multiplier"] = -1,
            ["acceleration multiplier"] = -1,
            ["turn multiplier"] = -1,
            ["turret turn multiplier"] = -1,
        };

        /// <summary>The lowest value an attribute may hold on a ship.</summary>
        public static double? Minimum(string attribute)
        {
            if (attribute is null || Unbounded.Contains(attribute))
                return null;

            return MinimumOverrides.TryGetValue(attribute, out double minimum) ? minimum : 0.0;
        }

        /// <summary>
        /// How many of <paramref name="outfit"/> can be installed on
        /// <paramref name="ship"/>, up to <paramref name="count"/>.
        /// Returns 0 when none fit.
        /// </summary>
        /// <remarks>
        /// Pass a negative <paramref name="count"/> to ask how many may be REMOVED;
        /// upstream uses the same function for both directions, because uninstalling
        /// can also violate a minimum (pulling a reactor out from under a ship whose
        /// other outfits need the energy).
        /// </remarks>
        public static int CanInstall(Ship ship, Outfit outfit, int count = 1)
        {
            if (ship is null) throw new ArgumentNullException(nameof(ship));
            if (outfit is null) throw new ArgumentNullException(nameof(outfit));
            if (count == 0) return 0;

            foreach (KeyValuePair<string, double> attribute in outfit.Attributes.Values)
            {
                double? minimum = Minimum(attribute.Key);
                if (!minimum.HasValue || attribute.Value == 0.0)
                    continue;

                double current = ship.Attributes.Get(attribute.Key);
                if (current + attribute.Value * count < minimum.Value)
                {
                    // Assigned, not clamped, exactly as upstream does
                    // (Outfit.cpp:665-666). The quotient already carries the right sign
                    // for both directions, and truncating toward zero is what keeps a
                    // partial fit from overshooting. Clamping it with Math.Max flipped
                    // the sign of a removal query -- asked how many of three could come
                    // off, it answered +2, which a caller reads as "two fit".
                    count = (int)((current - minimum.Value) / -attribute.Value);
                }
            }

            return count;
        }

        /// <summary>Whether at least one of this outfit fits.</summary>
        public static bool Fits(Ship ship, Outfit outfit) => CanInstall(ship, outfit) >= 1;

        /// <summary>
        /// Installs as many as fit and returns how many went on.
        /// </summary>
        public static int Install(Ship ship, Outfit outfit, int count = 1)
        {
            int installable = CanInstall(ship, outfit, count);
            if (installable > 0)
                ship.AddOutfit(outfit, installable);

            return installable;
        }

        /// <summary>
        /// The attribute that stops another <paramref name="outfit"/> fitting, or null
        /// when one would fit. Intended for outfitter UI messages.
        /// </summary>
        public static string? LimitingAttribute(Ship ship, Outfit outfit)
        {
            if (ship is null || outfit is null || Fits(ship, outfit))
                return null;

            foreach (KeyValuePair<string, double> attribute in outfit.Attributes.Values)
            {
                double? minimum = Minimum(attribute.Key);
                if (!minimum.HasValue || attribute.Value >= 0.0)
                    continue;

                if (ship.Attributes.Get(attribute.Key) + attribute.Value < minimum.Value)
                    return attribute.Key;
            }

            return null;
        }
    }
}
