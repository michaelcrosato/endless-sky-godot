using System;
using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>When a projectile releases its submunitions.</summary>
    [Flags]
    public enum DeathType
    {
        None = 0,
        /// <summary>The projectile reached the end of its lifetime.</summary>
        Natural = 1 << 0,
        /// <summary>It struck a ship.</summary>
        Collision = 1 << 1,
        Explosion = 1 << 2,
        AntiMissile = 1 << 3,
    }

    /// <summary>
    /// A cluster of projectiles spawned when a parent projectile dies.
    /// Upstream syntax: <c>"submunition" "Some Weapon" 11</c>, count defaulting to 1.
    /// </summary>
    public class Submunition
    {
        public Submunition(string weaponName, int count)
        {
            WeaponName = weaponName;
            Count = count;
        }

        /// <summary>Name of the outfit whose weapon block defines the spawned projectile.</summary>
        public string WeaponName { get; }

        public int Count { get; }

        /// <summary>
        /// Which deaths release this cluster. Upstream defaults to natural expiry
        /// ONLY, so a cluster round that hits a ship head-on does not also shower it
        /// with submunitions.
        /// </summary>
        public DeathType SpawnOn { get; internal set; } = DeathType.Natural;

        /// <summary>Resolved by <see cref="Weapon.ResolveSubmunitions"/> once all outfits are loaded.</summary>
        public Weapon? Weapon { get; internal set; }

        public override string ToString() => $"{Count}x {WeaponName}";
    }

    /// <summary>
    /// The <c>weapon</c> block of an outfit (or of a ship hull, which upstream uses
    /// for the explosion a ship produces when it dies).
    /// </summary>
    /// <remarks>
    /// Values live in an <see cref="Attributes"/> bag so unrecognised keys survive:
    /// upstream content and plugins define many attributes this port does not read
    /// yet, and dropping them would force a re-parse later.
    ///
    /// Two upstream behaviours here are not visible in the data and are easy to miss.
    /// Disabled damage DEFAULTS to hull damage rather than zero - no vanilla weapon
    /// declares it, so reading the raw attribute makes every ship indestructible once
    /// disabled. And a weapon's damage INCLUDES its submunitions' damage, which is
    /// what makes the Korath Minelayer's negative carrier damage a net positive
    /// rather than a repair beam.
    ///
    /// INCOMPLETE, tracked rather than dropped: damage-over-time types, damage
    /// dropoff, penetration count, and the piercing/permeability interactions.
    /// </remarks>
    public class Weapon
    {
        private readonly List<Submunition> _submunitions = new List<Submunition>();

        // Upstream tracks whether content set these explicitly, because their default
        // is "same as hull damage" rather than zero.
        private bool _disabledDamageSet;
        private bool _relativeDisabledDamageSet;

        // Totals fold in submunition damage and are computed once, after resolution.
        private Dictionary<string, double>? _totalDamage;

        public Attributes Attributes { get; } = new Attributes();

        /// <summary>True once a <c>weapon</c> node has been read into this instance.</summary>
        public bool IsWeapon { get; private set; }

        /// <summary>Projectiles this one bursts into. Empty for ordinary weapons.</summary>
        public IReadOnlyList<Submunition> Submunitions => _submunitions;

        /// <summary>
        /// True for cluster weapons. Such a carrier round often has no lifetime of its
        /// own, because it exists only to split on its first frame.
        /// </summary>
        public bool HasSubmunitions => _submunitions.Count > 0;

        public void Load(DataNode weaponNode)
        {
            IsWeapon = true;

            foreach (DataNode child in weaponNode.Children)
            {
                string key = child.Token(0);

                if (key == "ammo" && child.Size >= 2)
                {
                    // "ammo <outfit name> [rounds per shot]", count defaulting to 1.
                    AmmoName = child.Token(1);
                    AmmoUsage = child.Size >= 3 && child.IsNumber(2)
                        ? Math.Max(0, (int)child.Value(2))
                        : 1;
                    continue;
                }

                if (key == "submunition" && child.Size >= 2)
                {
                    int count = child.Size >= 3 && child.IsNumber(2) ? (int)child.Value(2) : 1;
                    var submunition = new Submunition(child.Token(1), count);
                    LoadSubmunitionOptions(child, submunition);
                    _submunitions.Add(submunition);
                    continue;
                }

                if (child.Size >= 2 && child.IsNumber(1))
                {
                    Attributes.Add(key, child.Value(1));
                    if (key == "disabled damage") _disabledDamageSet = true;
                    if (key == "relative disabled damage") _relativeDisabledDamageSet = true;
                    continue;
                }

                if (child.Size == 1)
                {
                    // Valueless keys are boolean flags upstream: a bare "homing" or
                    // "stream" line means enabled. Weapon::LoadWeapon sets the flag on
                    // key presence and treats a following number as deprecated legacy
                    // syntax, so presence alone has to be enough here too.
                    Attributes.Set(key, 1.0);
                }

                // Anything else is a string-valued key (sprite, sound, hit effect);
                // not an attribute, ignored for now.
            }

            ApplyDefaults();
        }

        private static void LoadSubmunitionOptions(DataNode node, Submunition submunition)
        {
            foreach (DataNode grand in node.Children)
            {
                if (grand.Token(0) != "spawn on" || grand.Size < 2)
                    continue;

                DeathType spawnOn = DeathType.None;
                for (int i = 1; i < grand.Size; i++)
                {
                    switch (grand.Token(i))
                    {
                        case "natural": spawnOn |= DeathType.Natural; break;
                        case "collision": spawnOn |= DeathType.Collision; break;
                        case "explosion": spawnOn |= DeathType.Explosion; break;
                        case "anti-missile": spawnOn |= DeathType.AntiMissile; break;
                    }
                }

                submunition.SpawnOn = spawnOn;
            }
        }

        /// <summary>
        /// Post-load fixups upstream applies after the parse loop. Disabled and
        /// minable damage default to hull damage, not to zero.
        /// </summary>
        private void ApplyDefaults()
        {
            if (!_disabledDamageSet)
                Attributes.Set("disabled damage", Attributes.Get("hull damage"));

            if (!_relativeDisabledDamageSet)
                Attributes.Set("relative disabled damage", Attributes.Get("relative hull damage"));
        }

        /// <summary>
        /// Links submunition names to their weapons, once every outfit is loaded.
        /// Damage totals cannot be computed before this runs.
        /// </summary>
        public void ResolveSubmunitions(Func<string, Weapon?> lookup)
        {
            if (lookup is null) throw new ArgumentNullException(nameof(lookup));

            foreach (Submunition submunition in _submunitions)
                submunition.Weapon ??= lookup(submunition.WeaponName);

            _totalDamage = null;
        }

        // --- Damage ---------------------------------------------------------------

        /// <summary>
        /// Damage of one type INCLUDING submunitions. Port of upstream
        /// <c>Weapon::TotalDamage</c>.
        /// </summary>
        /// <remarks>
        /// The Korath Minelayer is the case that makes this matter: its carrier shell
        /// declares -3200 shield damage, and only the 11 submunitions it releases turn
        /// that into a net positive. Reading the carrier's own attribute alone makes
        /// the mine repair whatever it hits.
        /// </remarks>
        public double TotalDamage(string key)
        {
            _totalDamage ??= new Dictionary<string, double>(StringComparer.Ordinal);

            if (_totalDamage.TryGetValue(key, out double cached))
                return cached;

            // Seed before recursing so a submunition cycle terminates instead of
            // overflowing the stack.
            _totalDamage[key] = Attributes.Get(key);

            double total = Attributes.Get(key);
            foreach (Submunition submunition in _submunitions)
            {
                if (submunition.Weapon is not null && !ReferenceEquals(submunition.Weapon, this))
                    total += submunition.Weapon.TotalDamage(key) * submunition.Count;
            }

            _totalDamage[key] = total;
            return total;
        }

        /// <summary>This weapon's own declared value, ignoring submunitions.</summary>
        public double OwnDamage(string key) => Attributes.Get(key);

        public double ShieldDamage => TotalDamage("shield damage");
        public double HullDamage => TotalDamage("hull damage");

        /// <summary>
        /// Damage applied only to the portion of a hit that would carry the target
        /// past its disabled threshold. Defaults to hull damage when content does not
        /// declare it, which is every vanilla weapon.
        /// </summary>
        public double DisabledDamage => TotalDamage("disabled damage");

        public double EnergyDamage => TotalDamage("energy damage");
        public double HeatDamage => TotalDamage("heat damage");
        public double FuelDamage => TotalDamage("fuel damage");

        public double RelativeShieldDamage => TotalDamage("relative shield damage");
        public double RelativeHullDamage => TotalDamage("relative hull damage");
        public double RelativeDisabledDamage => TotalDamage("relative disabled damage");
        public double RelativeEnergyDamage => TotalDamage("relative energy damage");
        public double RelativeHeatDamage => TotalDamage("relative heat damage");
        public double RelativeFuelDamage => TotalDamage("relative fuel damage");

        /// <summary>Fraction of shields bypassed entirely, in [0, 1].</summary>
        public double Piercing => Attributes.Get("piercing");

        /// <summary>Momentum imparted to the target on hit.</summary>
        public double HitForce => TotalDamage("hit force");

        public double BlastRadius => Attributes.Get("blast radius");

        // --- Firing behaviour -----------------------------------------------------

        /// <summary>Frames between shots. Upstream treats a reload of 0 as 1.</summary>
        public double Reload => Attributes.Has("reload") ? Attributes.Get("reload") : 1.0;

        /// <summary>
        /// Frames between shots WITHIN a burst. Upstream's default is 1, not the full
        /// reload: defaulting it to Reload collapses every burst into a single shot.
        /// </summary>
        public double BurstReload => Attributes.Has("burst reload") ? Attributes.Get("burst reload") : 1.0;

        public double BurstCount => Attributes.Has("burst count") ? Attributes.Get("burst count") : 1.0;

        public double FiringEnergy => Attributes.Get("firing energy");
        public double FiringHeat => Attributes.Get("firing heat");

        /// <summary>Fuel drawn per shot. The human Flamethrower runs on this.</summary>
        public double FiringFuel => Attributes.Get("firing fuel");

        /// <summary>Hull spent per shot; negative on the weapons that repair as they fire.</summary>
        public double FiringHull => Attributes.Get("firing hull");

        public double FiringShields => Attributes.Get("firing shields");

        /// <summary>Outfit consumed per shot, or null for weapons needing no ammunition.</summary>
        public string? AmmoName { get; private set; }

        /// <summary>Rounds consumed per shot, from the ammo line's optional count.</summary>
        public int AmmoUsage { get; private set; } = 1;

        // --- Projectile behaviour -------------------------------------------------

        /// <summary>Muzzle speed in units per frame, added to the firing ship's velocity.</summary>
        public double Velocity => Attributes.Get("velocity");

        /// <summary>
        /// Replaces <see cref="Velocity"/> when computing range. Upstream uses it for
        /// weapons whose effective reach differs from their muzzle speed.
        /// </summary>
        public double VelocityOverride => Attributes.Get("velocity override");

        /// <summary>Frames the projectile lives before expiring.</summary>
        public double Lifetime => Attributes.Get("lifetime");

        /// <summary>Non-zero for missiles that steer toward their target.</summary>
        public double Homing => Attributes.Get("homing");

        /// <summary>Degrees per frame a homing projectile can turn.</summary>
        public double Turn => Attributes.Get("turn");

        public double Acceleration => Attributes.Get("acceleration");

        /// <summary>Random firing cone in degrees.</summary>
        public double Inaccuracy => Attributes.Get("inaccuracy");

        /// <summary>True when the projectile chases a target rather than flying straight.</summary>
        public bool IsHoming => Homing != 0.0;

        /// <summary>Explicit range cap, for weapons whose reach is not velocity times lifetime.</summary>
        public double RangeOverride => Attributes.Get("range override");

        /// <summary>
        /// Speed used for range, which upstream calls the weighted velocity.
        /// </summary>
        public double EffectiveVelocity => VelocityOverride > 0.0 ? VelocityOverride : Velocity;

        /// <summary>
        /// Total flight time including the longest-lived submunition, so a cluster
        /// weapon's reach counts the distance its children travel.
        /// </summary>
        public double TotalLifetime
        {
            get
            {
                double longestChild = 0.0;
                foreach (Submunition submunition in _submunitions)
                {
                    if (submunition.Weapon is not null && !ReferenceEquals(submunition.Weapon, this))
                        longestChild = Math.Max(longestChild, submunition.Weapon.TotalLifetime);
                }

                return Lifetime + longestChild;
            }
        }

        /// <summary>How far a shot reaches, in simulation units.</summary>
        public double Range
        {
            get
            {
                if (RangeOverride > 0.0)
                    return RangeOverride;

                return EffectiveVelocity * TotalLifetime;
            }
        }
    }
}
