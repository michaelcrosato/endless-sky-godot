using System;

namespace EndlessSky.Sim
{
    /// <summary>What a single hit did to a ship, as reported by <see cref="Ship.TakeDamage"/>.</summary>
    [Flags]
    public enum ShipEvent
    {
        None = 0,
        Disable = 1 << 0,
        Destroy = 1 << 1,
    }

    /// <summary>
    /// Combat state: the damage levels a ship carries and how weapons change them.
    /// Port of the instantaneous-damage path through upstream <c>DamageProfile</c>
    /// and <c>Ship::TakeDamage</c>.
    /// </summary>
    /// <remarks>
    /// INCOMPLETE, tracked deliberately rather than dropped (directive rule 2). Not
    /// yet modelled: damage-over-time types (ion, burn, corrosion, leak, discharge),
    /// per-type damage protection and resistance attributes, shield permeability,
    /// disruption, cloaking interactions, crew loss, and blast-radius falloff.
    /// The scaling hooks below are written so those slot in without restructuring.
    /// </remarks>
    public partial class Ship : ITarget
    {
        // Position and Velocity are public fields on the flight half, which cannot
        // satisfy an interface directly; explicit implementation bridges them so a
        // ship can be chased by a homing projectile.
        Point ITarget.Position => Position;
        Point ITarget.Velocity => Velocity;

        // Levels start full. They are lazily initialised because attributes are not
        // final until every outfit has been installed, which happens after the ctor.
        private double? _shields;
        private double? _hull;
        private double? _energy;
        private double? _fuel;

        public double Shields
        {
            get => _shields ??= MaxShields;
            private set => _shields = value;
        }

        public double Hull
        {
            get => _hull ??= MaxHull;
            private set => _hull = value;
        }

        public double Energy
        {
            get => _energy ??= MaxEnergy;
            private set => _energy = value;
        }

        public double Fuel
        {
            get => _fuel ??= MaxFuel;
            private set => _fuel = value;
        }

        /// <summary>Heat starts at zero and rises; it is the one level that is not a reserve.</summary>
        public double Heat { get; private set; }

        /// <summary>The faction this ship belongs to. Shots pass through their own side.</summary>
        public Government? Government { get; set; }

        private double? _collisionRadius;

        /// <summary>
        /// Radius used for projectile impacts, in simulation units.
        /// </summary>
        /// <remarks>
        /// INCOMPLETE: upstream collides against the ship's sprite mask, which we do
        /// not have in an engine-free layer. Until sprite dimensions are plumbed
        /// through, this falls back to a mass-derived estimate so that bigger ships
        /// are meaningfully easier to hit. Set it explicitly to override.
        /// </remarks>
        public double CollisionRadius
        {
            get => _collisionRadius ??= EstimateCollisionRadius();
            set => _collisionRadius = value;
        }

        /// <summary>
        /// Rough stand-in for a sprite mask: hull mass scales roughly with area, so
        /// radius scales with its square root.
        /// </summary>
        private double EstimateCollisionRadius()
        {
            double mass = Math.Max(1.0, Attributes.Get("mass"));
            return Math.Max(6.0, 2.2 * Math.Sqrt(mass));
        }

        public double MaxShields => Attributes.Get("shields");
        public double MaxHull => Attributes.Get("hull");
        public double MaxEnergy => Attributes.Get("energy capacity");
        public double MaxFuel => Attributes.Get("fuel capacity");

        /// <summary>Upstream's MAXIMUM_TEMPERATURE constant.</summary>
        public const double MaximumTemperature = 100.0;

        /// <summary>
        /// Heat capacity. Port of upstream <c>Ship::MaxHeat</c>.
        /// </summary>
        /// <remarks>
        /// The base term is the ship's own mass plus its cargo, NOT a multiple of the
        /// "heat capacity" attribute: that attribute is an additive bonus from
        /// heatsink outfits. Reading it as a multiplier gives every ship without such
        /// an outfit a maximum heat of zero, which makes overheating instantaneous
        /// and relative heat damage meaningless.
        /// </remarks>
        public double MaxHeat =>
            MaximumTemperature * (CargoMass + Attributes.Get("mass") + Attributes.Get("heat capacity"));

        /// <summary>
        /// Hull level below which the ship is disabled rather than destroyed.
        /// Port of the minimumHull block in upstream <c>Ship::CacheAttributes</c>.
        /// </summary>
        public double MinimumHull
        {
            get
            {
                // The flag lives on the definition, not in the attribute bag; an
                // outfit may also grant it numerically.
                if (Definition.IsNeverDisabled || Attributes.Get("never disabled") != 0.0)
                    return 0.0;

                double absoluteThreshold = Attributes.Get("absolute threshold");
                if (absoluteThreshold > 0.0)
                    return absoluteThreshold;

                double maxHull = MaxHull;
                double thresholdPercent = Attributes.Get("threshold percentage");

                // Small ships are disabled at a much higher fraction of their hull than
                // large ones; this curve slides from 50% down toward 10% as hull grows.
                double transition = 1.0 / (1.0 + 0.0005 * maxHull);
                double fraction = thresholdPercent > 0.0
                    ? Math.Min(thresholdPercent, 1.0)
                    : 0.1 * (1.0 - transition) + 0.5 * transition;

                double minimum = maxHull * fraction;
                return Math.Max(0.0, Math.Floor(minimum + Attributes.Get("hull threshold")));
            }
        }

        /// <summary>
        /// Hull that can still be lost before the ship becomes disabled.
        /// Port of upstream <c>Entity::HullLevelUntilDisabled</c>.
        /// </summary>
        /// <remarks>
        /// The 0.25 is upstream's, and it is load-bearing rather than a fudge. A ship
        /// is disabled at <c>hull &lt; minimumHull</c>, strictly below, while incoming
        /// hull damage is clamped to this value. Without the epsilon a weapon could
        /// only ever bring hull down to exactly the threshold, so nothing lacking an
        /// explicit "disabled damage" attribute could disable anything at all.
        /// </remarks>
        public double HullUntilDisabled => Math.Max(0.0, Hull + 0.25 - MinimumHull);

        /// <summary>Upstream destroys a ship only once hull goes strictly below zero.</summary>
        public bool IsDestroyed => Hull < 0.0;

        /// <summary>Recomputes the disabled flag from current levels, as upstream does after every hit.</summary>
        private bool ComputeDisabled() => Hull < MinimumHull;

        /// <summary>
        /// Pays a weapon's non-energy firing costs. Fuel, hull and shields can all be
        /// spent per shot, and the hull/shield costs are negative on the weapons that
        /// repair the firing ship as they fire.
        /// </summary>
        internal void SpendFiringResources(Weapon weapon)
        {
            if (weapon.FiringFuel != 0.0)
                Fuel = Math.Min(Math.Max(Fuel - weapon.FiringFuel, 0.0), MaxFuel);

            if (weapon.FiringHull != 0.0)
                Hull = Math.Min(Math.Max(Hull - weapon.FiringHull, 0.0), MaxHull);

            if (weapon.FiringShields != 0.0)
                Shields = Math.Min(Math.Max(Shields - weapon.FiringShields, 0.0), MaxShields);
        }

        /// <summary>Sets levels directly. For tests and for restoring a saved game.</summary>
        public void SetLevels(double? shields = null, double? hull = null,
                              double? energy = null, double? heat = null, double? fuel = null)
        {
            if (shields.HasValue) _shields = Math.Min(shields.Value, MaxShields);
            if (hull.HasValue) _hull = Math.Min(hull.Value, MaxHull);
            if (energy.HasValue) _energy = Math.Min(energy.Value, MaxEnergy);
            if (fuel.HasValue) _fuel = Math.Min(fuel.Value, MaxFuel);
            if (heat.HasValue) Heat = heat.Value;

            IsDisabled = ComputeDisabled();
        }

        /// <summary>
        /// Applies one weapon hit. Returns the state transitions it caused.
        /// </summary>
        /// <remarks>
        /// The shape of this calculation is why Endless Sky combat feels the way it
        /// does: while any shields remain, hull damage is scaled by
        /// <c>1 - shieldFraction</c>, which is zero. Shields block hull damage
        /// *entirely* rather than absorbing it proportionally. Bleed-through happens
        /// only in the frame where a shot exceeds the remaining shields, and then only
        /// for the excess. Weapons with piercing are the exception.
        /// </remarks>
        public ShipEvent TakeDamage(Weapon weapon)
        {
            if (weapon is null) throw new ArgumentNullException(nameof(weapon));

            bool wasDisabled = IsDisabled;
            bool wasDestroyed = IsDestroyed;

            double shieldFraction = 0.0;
            double shieldDamage = 0.0;

            if (Shields > 0.0)
            {
                double piercing = Math.Max(0.0, Math.Min(1.0, weapon.Piercing));
                shieldFraction = 1.0 - piercing;

                shieldDamage = weapon.ShieldDamage + weapon.RelativeShieldDamage * MaxShields;

                // If the shot would overrun the shields, only the part of it the shields
                // can actually pay for is blocked; the rest bleeds through this frame.
                if (shieldDamage > Shields)
                    shieldFraction = Math.Min(shieldFraction, Shields / shieldDamage);
            }

            // Hull is blocked 100% by shields, so it scales by the un-shielded share.
            double hullScale = 1.0 - shieldFraction;
            // Energy, heat and fuel are blocked only 50% by shields.
            double halfBlockedScale = 0.5 * shieldFraction + (1.0 - shieldFraction);

            double hullDamage = (weapon.HullDamage + weapon.RelativeHullDamage * MaxHull) * hullScale;

            // A hit that would push the ship past its disabled threshold converts the
            // overshoot into "disabled damage", which is how upstream lets a weapon
            // reliably disable a target without destroying it.
            double hullUntilDisabled = HullUntilDisabled;
            if (hullDamage > hullUntilDisabled && hullDamage > 0.0)
            {
                double hullFraction = hullUntilDisabled / hullDamage;
                hullDamage = hullDamage * hullFraction
                    + (weapon.DisabledDamage + weapon.RelativeDisabledDamage * MaxHull)
                      * hullScale * (1.0 - hullFraction);
            }

            Shields = Math.Min(Math.Max(Shields - shieldDamage * shieldFraction, 0.0), MaxShields);
            Hull = Math.Min(Hull - hullDamage, MaxHull);
            Energy = Math.Max(Energy - (weapon.EnergyDamage + weapon.RelativeEnergyDamage * MaxEnergy) * halfBlockedScale, 0.0);
            Fuel = Math.Max(Fuel - (weapon.FuelDamage + weapon.RelativeFuelDamage * MaxFuel) * halfBlockedScale, 0.0);
            // Heat floors at zero; cooling effects reduce it but cannot go negative.
            Heat = Math.Max(0.0, Heat + (weapon.HeatDamage + weapon.RelativeHeatDamage * MaxHeat) * halfBlockedScale);

            IsDisabled = ComputeDisabled();

            ShipEvent events = ShipEvent.None;
            if (!wasDisabled && IsDisabled) events |= ShipEvent.Disable;
            if (!wasDestroyed && IsDestroyed) events |= ShipEvent.Destroy;
            return events;
        }
    }
}
