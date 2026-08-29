using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// What can happen to a ship, as a bitmask; also what
    /// <see cref="Ship.TakeDamage"/> reports about a single hit. Bit values are upstream's
    /// (<c>ShipEvent.h</c>) because mission NPC objectives are stored as a mask over
    /// exactly these flags, and a mission that says "kill" has to mean the same bit
    /// the combat layer sets.
    /// </summary>
    [Flags]
    public enum ShipEvent
    {
        None = 0,
        Assist = 1 << 0,
        ScanCargo = 1 << 1,
        ScanOutfits = 1 << 2,
        Provoke = 1 << 3,
        Disable = 1 << 4,
        Board = 1 << 5,
        Capture = 1 << 6,
        Destroy = 1 << 7,
        Atrocity = 1 << 8,
        Jump = 1 << 9,
        Encounter = 1 << 10,
    }

    /// <summary>
    /// What a port will put back for a ship that lands at it. Values are upstream's
    /// (<c>Port.h:36-45</c>).
    /// </summary>
    [Flags]
    public enum RechargeType
    {
        None = 0,
        Shields = 1 << 0,
        Hull = 1 << 1,
        Energy = 1 << 2,
        Fuel = 1 << 3,
        All = Shields | Hull | Energy | Fuel,
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
        /// Fraction of accumulated heat shed each frame, upstream's
        /// <c>Ship::HeatDissipation</c>: the hull attribute scaled by .001.
        /// </summary>
        public double HeatDissipation => Attributes.Get("heat dissipation") * 0.001;

        /// <summary>
        /// Runs one frame of resource generation: power, cooling, and shield and hull
        /// repair. Partial port of upstream <c>Ship::DoGeneration</c>.
        /// </summary>
        /// <remarks>
        /// Nothing regenerated anything before this existed. Energy, shields and hull
        /// only ever went down, which is survivable while nothing spends energy but
        /// becomes total once manoeuvring costs power: every ship in the game would
        /// brown out permanently within about a minute of flying, and no ship would
        /// ever recover its shields between fights.
        ///
        /// Order matters and is upstream's: repair spends what is left of LAST frame's
        /// energy, so it cannot steal power from movement or weapons, and only then is
        /// this frame's generation added. Repair is also capped by what the ship can
        /// pay for, which is why a battered ship with a weak reactor mends slowly
        /// rather than instantly.
        ///
        /// INCOMPLETE, tracked rather than dropped: repair delays after taking damage,
        /// carried fighters drawing on their parent, ramscoop and solar collection,
        /// active cooling, heat-driven shield disruption, and the fuel-burning
        /// generators. Depleted-shield and disabled-hull delays in particular make
        /// upstream's combat pacing slower than this.
        /// </remarks>
        public void StepResources()
        {
            // A disabled ship generates and repairs nothing. Upstream wraps this whole
            // block in `if(!isDisabled)` (Ship.cpp:4331), and that is what makes a
            // crippled hull STAY crippled until it is boarded or repaired. Running it
            // unguarded let a disabled ship rebuild its shields while the fight went on
            // around it, so a crippled raider could come back without anyone touching it.
            if (!IsDisabled)
            {
                // 1. Hull repair, then shields, out of energy already in the bank.
                double hullRate = Attributes.Get("hull repair rate");
                if (hullRate > 0.0 && Hull < MaxHull)
                    Repair(hullRate, Attributes.Get("hull energy"), Attributes.Get("hull heat"),
                           Hull, MaxHull, repaired => SetLevels(hull: repaired));

                double shieldRate = Attributes.Get("shield generation");
                if (shieldRate > 0.0 && Shields < MaxShields)
                    Repair(shieldRate, Attributes.Get("shield energy"), Attributes.Get("shield heat"),
                           Shields, MaxShields, regenerated => SetLevels(shields: regenerated));

                // 2. This frame's power, minus what the ship's systems draw idling.
                double generated = Attributes.Get("energy generation") - Attributes.Get("energy consumption");
                if (generated != 0.0)
                    SetLevels(energy: Math.Clamp(Energy + generated, 0.0, MaxEnergy));
            }

            // 3. Heat: what the hull makes, less what it sheds. Dissipation is a
            //    fraction of current heat, so a hot ship cools faster than a cool one.
            //    Heat keeps moving on a disabled ship, which is how one cools back down.
            Heat += Attributes.Get("heat generation") - Attributes.Get("cooling");
            Heat -= Heat * HeatDissipation;
            if (Heat < 0.0)
                Heat = 0.0;

            ApplyOverheating();
            IsDisabled = ComputeDisabled();
        }

        /// <summary>
        /// Updates the overheated flag and applies overheat hull burn.
        /// Port of upstream <c>Ship::DoGeneration</c>'s tail (<c>Ship.cpp:4449-4457</c>).
        /// </summary>
        /// <remarks>
        /// The hysteresis is the point: heat above <see cref="MaxHeat"/> shuts a ship
        /// down, and only falling below nine tenths of it brings the ship back, so a
        /// hull sitting on the threshold does not flicker in and out of commission.
        /// The burn itself is opt-in -- <c>overheat damage rate</c> defaults to zero
        /// (<c>ShipAttributeCache.h:81</c>), so vanilla ships shut down without
        /// catching fire.
        /// </remarks>
        private void ApplyOverheating()
        {
            double max = MaxHeat;
            if (max <= 0.0)
                return;

            if (Heat > max)
            {
                IsOverheated = true;

                double threshold = 1.0 + Attributes.Get("overheat damage threshold");
                double heatRatio = Heat / max / threshold;
                double rate = Attributes.Get("overheat damage rate");
                if (rate > 0.0 && heatRatio > 1.0)
                    SetLevels(hull: Hull - rate * heatRatio);
            }
            else if (Heat < 0.9 * max)
            {
                IsOverheated = false;
            }
        }

        /// <summary>
        /// Restores up to <paramref name="rate"/> points, limited by the energy the
        /// ship can actually spend on it.
        /// </summary>
        private void Repair(double rate, double energyPerPoint, double heatPerPoint,
                            double current, double maximum, Action<double> apply)
        {
            double wanted = Math.Min(rate, maximum - current);
            if (wanted <= 0.0)
                return;

            // Costs are stated per point repaired, so a ship short of power repairs
            // proportionally less rather than repairing free.
            if (energyPerPoint > 0.0)
                wanted = Math.Min(wanted, Energy / energyPerPoint);

            if (wanted <= 0.0)
                return;

            if (energyPerPoint > 0.0)
                SetLevels(energy: Math.Max(0.0, Energy - energyPerPoint * wanted));

            if (heatPerPoint > 0.0)
                Heat += heatPerPoint * wanted;

            apply(current + wanted);
        }

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

        /// <summary>
        /// Puts a ship back in order after landing. Port of upstream
        /// <c>Ship::Recharge</c> (<c>Ship.cpp:2644-2668</c>).
        /// </summary>
        /// <param name="port">What the world this ship landed at provides.</param>
        /// <remarks>
        /// Each stat is restored if the PORT offers it OR the ship makes it itself,
        /// which is upstream's `||`. That distinction is the whole point: a hull with a
        /// shield generator comes back to full anywhere, while a bare hull at a world
        /// with no port comes back to nothing.
        ///
        /// This is the only repair path most ships have. Per-frame regeneration only
        /// runs for hulls carrying a "hull repair rate" or "shield generation" outfit,
        /// which most do not — so with nothing calling this, battle damage was
        /// permanent for the rest of the game.
        ///
        /// INCOMPLETE, tracked rather than dropped: upstream returns heat to the ship's
        /// computed IdleHeat rather than to zero, re-hires crew up to the bunk count,
        /// and clears the status effects (ionisation, disruption, slowing) that are not
        /// modelled here yet.
        /// </remarks>
        public void Recharge(RechargeType port)
        {
            if (IsDestroyed)
                return;

            if (port.HasFlag(RechargeType.Shields) || Attributes.Get("shield generation") > 0.0)
                Shields = MaxShields;

            if (port.HasFlag(RechargeType.Hull) || Attributes.Get("hull repair rate") > 0.0)
                Hull = MaxHull;

            if (port.HasFlag(RechargeType.Energy) || Attributes.Get("energy generation") > 0.0)
                Energy = MaxEnergy;

            if (port.HasFlag(RechargeType.Fuel) || Attributes.Get("fuel generation") > 0.0)
                Fuel = MaxFuel;

            Heat = 0.0;
            IsOverheated = false;
            IsDisabled = ComputeDisabled();
        }

        /// <summary>
        /// Cripples this ship: brings its hull just under the disabling threshold, so
        /// it is disabled by its own state rather than by a flag.
        /// </summary>
        /// <remarks>
        /// The disabled flag is recomputed from levels every frame, exactly as
        /// upstream recomputes it (<c>Ship.cpp:4469</c>), so setting it directly does
        /// not survive the next step. A derelict has to actually BE a wreck. Kept
        /// above zero because a hull below zero is destroyed, not boardable.
        /// </remarks>
        public void Disable()
        {
            double crippled = Math.Max(0.0, MinimumHull - 0.5);
            SetLevels(hull: Math.Min(Hull, crippled));
        }

        /// <summary>
        /// Whether heat has shut this ship down. Cleared only below nine tenths of
        /// <see cref="MaxHeat"/>; see <see cref="ApplyOverheating"/>.
        /// </summary>
        public bool IsOverheated { get; private set; }

        /// <summary>Recomputes the disabled flag from current levels, as upstream does after every hit.</summary>
        /// <remarks>
        /// <c>Ship.cpp:4469</c>: <c>isDisabled = isOverheated || hull &lt; minimumHull
        /// || (!crew &amp;&amp; RequiredCrew())</c>. The crew clause is not modelled yet;
        /// the heat clause is what makes a cooked reactor take a ship out of a fight.
        /// </remarks>
        private bool ComputeDisabled() => IsOverheated || Hull < MinimumHull;

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
        /// <param name="attacker">
        /// The government that fired, when it is known. Upstream returns PROVOKE
        /// whenever the shooter is not already an enemy of the target's government
        /// (<c>Ship.cpp:3275-3285</c>) — that is what turns a stray shot into a fight.
        /// MissionNpc accepts a `provoke` objective and sets the bit as a completion
        /// requirement, so without this the objective could be written, parsed, and
        /// never satisfied by anything.
        ///
        /// INCOMPLETE, tracked rather than dropped: upstream also gates provocation on
        /// the pacifist and forbearing personalities and on how badly the hit landed;
        /// neither is modelled here, so any non-enemy hit provokes.
        /// </param>
        public ShipEvent TakeDamage(Weapon weapon, Government? attacker = null)
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

            // Shot at by somebody who was not already an enemy: that is a provocation,
            // and it is how a stray round starts a fight.
            if (attacker != null && !attacker.IsEnemy(Government))
                events |= ShipEvent.Provoke;

            return events;
        }
    }
}
