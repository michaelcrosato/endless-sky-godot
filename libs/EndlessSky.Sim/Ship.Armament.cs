using System;
using System.Collections.Generic;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The firing half of a ship: its weapon mounts, and what it costs to shoot.
    /// Port of the parts of upstream <c>Ship::Fire</c> and <c>Ship::CanFire</c> that
    /// govern cadence and resource cost.
    /// </summary>
    /// <remarks>
    /// INCOMPLETE, tracked rather than dropped: turret traverse, blindspots, firing
    /// effects, anti-missile, cluster aiming, and the fighter-bay half of armament.
    /// </remarks>
    public partial class Ship
    {
        private readonly List<WeaponMount> _mounts = new List<WeaponMount>();
        private readonly Dictionary<string, int> _ammo = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Every weapon mount, guns first then turrets, in definition order.</summary>
        public IReadOnlyList<WeaponMount> Mounts => _mounts;

        private static readonly Random SharedRandom = new Random();

        /// <summary>
        /// Source of randomness for firing inaccuracy, returning [0, 1).
        /// Replaceable so tests can make shot spread deterministic.
        /// </summary>
        public Func<double>? RandomSource { get; set; }

        private double RandomUnit() => RandomSource?.Invoke() ?? SharedRandom.NextDouble();

        /// <summary>
        /// Builds the mount list from the ship's definition. Guns fire along the hull,
        /// turrets aim independently.
        /// </summary>
        public void BuildMounts()
        {
            _mounts.Clear();

            // Ship sprite coordinates are stored at twice scale, so upstream halves
            // every hardpoint on construction. Using the raw value puts each mount at
            // twice its true distance from the hull centre.
            foreach (Hardpoint gun in Definition.Guns)
                _mounts.Add(new WeaponMount(gun.Offset * 0.5, default, isTurret: false));

            foreach (Hardpoint turret in Definition.Turrets)
                _mounts.Add(new WeaponMount(turret.Offset * 0.5, default, isTurret: true));

            // Arm them from the weapons this ship already carries. Building the
            // hardpoints and loading them are one step upstream (Ship::FinishLoading
            // hands every weapon outfit to the Armament); splitting them left a ship
            // that had been given its stock loadout holding the guns as inventory with
            // every hardpoint still empty. A stock Sparrow carries two Beam Lasers and
            // could not fire either of them, which made every NPC in the game harmless.
            foreach (Outfit outfit in Outfits)
                if (outfit.Weapon is not null && outfit.Weapon.IsWeapon)
                    ArmMount(outfit);
        }

        /// <summary>
        /// Loads one weapon outfit into a free hardpoint of the matching kind, without
        /// touching the outfit inventory. Returns the mount, or null if none is free.
        /// </summary>
        /// <remarks>
        /// Which kind a weapon needs is what it consumes: a turret spends "turret
        /// mounts" and a gun spends "gun ports", exactly as the outfitter checks.
        /// </remarks>
        private WeaponMount? ArmMount(Outfit outfit, bool? asTurret = null)
        {
            bool turret = asTurret ?? outfit.Attributes.Get("turret mounts") < 0.0;

            foreach (WeaponMount mount in _mounts)
            {
                if (mount.IsTurret == turret && mount.IsEmpty)
                {
                    mount.Install(outfit);
                    return mount;
                }
            }

            return null;
        }

        /// <summary>Ammunition currently carried, by outfit name.</summary>
        public int AmmoCount(string? outfitName) =>
            outfitName is not null && _ammo.TryGetValue(outfitName, out int count) ? count : 0;

        public void AddAmmo(string? outfitName, int count)
        {
            if (string.IsNullOrEmpty(outfitName) || count == 0)
                return;

            _ammo.TryGetValue(outfitName, out int existing);
            int total = existing + count;
            if (total <= 0)
                _ammo.Remove(outfitName);
            else
                _ammo[outfitName] = total;
        }

        /// <summary>
        /// Installs a weapon in the first free mount of the matching kind.
        /// Returns the mount used, or null when every suitable mount is taken.
        /// </summary>
        public WeaponMount? InstallWeapon(Outfit outfit, bool asTurret = false)
        {
            if (outfit is null) throw new ArgumentNullException(nameof(outfit));

            WeaponMount? mount = ArmMount(outfit, asTurret);
            if (mount is not null)
                AddOutfit(outfit, arm: false);

            return mount;
        }

        /// <summary>Advances every mount's reload clocks by one frame.</summary>
        public void StepArmament()
        {
            foreach (WeaponMount mount in _mounts)
                mount.Step();
        }

        /// <summary>
        /// Whether the ship can pay for a shot from this weapon right now.
        /// A disabled ship cannot fire at all.
        /// </summary>
        public bool CanFire(Weapon? weapon)
        {
            if (weapon is null || !weapon.IsWeapon || IsDisabled)
                return false;

            // Upstream gates on the whole firing cost, not energy alone. The human
            // Flamethrower runs on fuel and must stop when the tank is dry; several
            // Korath and Kahet weapons spend hull.
            if (Energy < weapon.FiringEnergy)
                return false;

            if (weapon.FiringFuel > 0.0 && Fuel < weapon.FiringFuel)
                return false;

            if (weapon.FiringHull > 0.0 && Hull < weapon.FiringHull)
                return false;

            // A weapon that names ammunition cannot fire without a round left.
            string? ammo = weapon.AmmoName;
            if (ammo is not null && AmmoCount(ammo) < weapon.AmmoUsage)
                return false;

            return true;
        }

        /// <summary>
        /// Fires one mount if it is loaded and affordable, returning the shot.
        /// Returns null when the mount is empty, still reloading, or unaffordable.
        /// </summary>
        /// <param name="mount">A mount belonging to this ship.</param>
        /// <param name="target">Optional target for homing weapons.</param>
        /// <param name="government">The firing government, carried by the projectile.</param>
        public Projectile? Fire(WeaponMount mount, ITarget? target = null, Government? government = null)
        {
            if (mount is null) throw new ArgumentNullException(nameof(mount));

            if (!mount.IsReady)
                return null;

            Weapon weapon = mount.Weapon!;
            if (!CanFire(weapon))
                return null;

            // Pay for the shot before it exists, so a half-affordable burst stops
            // cleanly rather than firing on credit.
            Energy -= weapon.FiringEnergy;
            Heat += weapon.FiringHeat;
            SpendFiringResources(weapon);
            if (weapon.AmmoName is not null)
                AddAmmo(weapon.AmmoName, -(int)weapon.AmmoUsage);

            mount.RecordShot();

            Angle aim = Facing + mount.BaseAngle;

            // Weapon inaccuracy is a firing cone, applied to the aim at the moment of
            // the shot. Parsing it and never using it makes every weapon perfectly
            // accurate, which removes the reason streams of fire spread at all.
            double inaccuracy = weapon.Inaccuracy;
            if (inaccuracy > 0.0)
                aim += new Angle((RandomUnit() * 2.0 - 1.0) * inaccuracy);

            // Upstream spawns the projectile back by half the ship's velocity so it
            // renders in the right place relative to the moving hull.
            Point muzzle = Position + Facing.Rotate(mount.Point) - Velocity * 0.5;

            return new Projectile(weapon, muzzle, Velocity, aim, target, government);
        }

        /// <summary>
        /// Fires every mount that is ready and affordable. This is the "hold the
        /// trigger" path; mounts that cannot fire are simply skipped.
        /// </summary>
        public List<Projectile> FireAll(ITarget? target = null, Government? government = null)
        {
            var shots = new List<Projectile>();

            foreach (WeaponMount mount in _mounts)
            {
                Projectile? shot = Fire(mount, target, government);
                if (shot is not null)
                    shots.Add(shot);
            }

            return shots;
        }
    }
}
