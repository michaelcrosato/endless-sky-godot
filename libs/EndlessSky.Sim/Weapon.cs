using System.Collections.Generic;
using EndlessSky.Data;

namespace EndlessSky.Sim
{
    /// <summary>
    /// A cluster of projectiles spawned when a parent projectile expires or hits.
    /// Upstream syntax: <c>"submunition" "Some Weapon" 11</c>, count defaulting to 1.
    /// </summary>
    public readonly struct Submunition
    {
        public Submunition(string weaponName, int count)
        {
            WeaponName = weaponName;
            Count = count;
        }

        /// <summary>Name of the outfit whose weapon block defines the spawned projectile.</summary>
        public string WeaponName { get; }

        public int Count { get; }

        public override string ToString() => $"{Count}x {WeaponName}";
    }

    /// <summary>
    /// The <c>weapon</c> block of an outfit (or of a ship hull, which upstream uses
    /// for the explosion a ship produces when it dies). Port of the subset of
    /// upstream <c>Weapon</c> that Milestone 2 exercises.
    /// </summary>
    /// <remarks>
    /// Values are kept in an <see cref="Attributes"/> bag rather than fields so that
    /// unrecognised keys survive a round trip: upstream content and plugins define
    /// many weapon attributes this milestone does not read yet, and silently
    /// dropping them would make later milestones re-parse the data.
    /// </remarks>
    public class Weapon
    {
        private readonly List<Submunition> _submunitions = new List<Submunition>();

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

                if (child.Size >= 2 && child.IsNumber(1))
                {
                    Attributes.Add(key, child.Value(1));
                }
                else if (key == "submunition" && child.Size >= 2)
                {
                    // "submunition" <weapon name> [count]; count defaults to 1.
                    int count = child.Size >= 3 && child.IsNumber(2) ? (int)child.Value(2) : 1;
                    _submunitions.Add(new Submunition(child.Token(1), count));
                }
                else if (child.Size == 1)
                {
                    // Valueless keys are boolean flags upstream: a bare "homing" or
                    // "stream" line means enabled. Weapon::LoadWeapon sets the flag on
                    // key presence and treats a following number as deprecated legacy
                    // syntax, so presence alone has to be enough here too.
                    Attributes.Set(key, 1.0);
                }

                // Anything else is a string-valued key (sprite, sound, ammo,
                // submunition, hit effect); not an attribute, ignored for now.
            }
        }

        // --- Instantaneous damage -------------------------------------------------

        public double ShieldDamage => Attributes.Get("shield damage");
        public double HullDamage => Attributes.Get("hull damage");

        /// <summary>
        /// Damage dealt only to the portion of a hit that would take the target below
        /// its disabled threshold. Upstream uses this so "stun" weapons can disable
        /// without destroying.
        /// </summary>
        public double DisabledDamage => Attributes.Get("disabled damage");

        public double EnergyDamage => Attributes.Get("energy damage");
        public double HeatDamage => Attributes.Get("heat damage");
        public double FuelDamage => Attributes.Get("fuel damage");

        // --- Damage proportional to the target's capacity -------------------------

        public double RelativeShieldDamage => Attributes.Get("relative shield damage");
        public double RelativeHullDamage => Attributes.Get("relative hull damage");
        public double RelativeDisabledDamage => Attributes.Get("relative disabled damage");
        public double RelativeEnergyDamage => Attributes.Get("relative energy damage");
        public double RelativeHeatDamage => Attributes.Get("relative heat damage");
        public double RelativeFuelDamage => Attributes.Get("relative fuel damage");

        /// <summary>Fraction of shields bypassed entirely, in [0, 1].</summary>
        public double Piercing => Attributes.Get("piercing");

        /// <summary>Momentum imparted to the target on hit.</summary>
        public double HitForce => Attributes.Get("hit force");

        public double BlastRadius => Attributes.Get("blast radius");

        // --- Firing behaviour -----------------------------------------------------

        /// <summary>Frames between shots. Upstream treats a reload of 0 as 1.</summary>
        public double Reload => Attributes.Has("reload") ? Attributes.Get("reload") : 1.0;

        public double BurstReload => Attributes.Has("burst reload") ? Attributes.Get("burst reload") : Reload;

        public double BurstCount => Attributes.Has("burst count") ? Attributes.Get("burst count") : 1.0;

        /// <summary>Energy drawn per shot.</summary>
        public double FiringEnergy => Attributes.Get("firing energy");

        public double FiringHeat => Attributes.Get("firing heat");

        /// <summary>Ammunition consumed per shot; 0 for weapons that need none.</summary>
        public double AmmoUsage => Attributes.Has("ammo usage") ? Attributes.Get("ammo usage") : 1.0;

        // --- Projectile behaviour -------------------------------------------------

        /// <summary>Muzzle speed in units per frame, added to the firing ship's velocity.</summary>
        public double Velocity => Attributes.Get("velocity");

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
    }
}
