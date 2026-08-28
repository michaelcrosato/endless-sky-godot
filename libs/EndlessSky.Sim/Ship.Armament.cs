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

        /// <summary>
        /// Builds the mount list from the ship's definition. Guns fire along the hull,
        /// turrets aim independently.
        /// </summary>
        public void BuildMounts()
        {
            _mounts.Clear();

            foreach (Hardpoint gun in Definition.Guns)
                _mounts.Add(new WeaponMount(gun.Offset, default, isTurret: false));

            foreach (Hardpoint turret in Definition.Turrets)
                _mounts.Add(new WeaponMount(turret.Offset, default, isTurret: true));
        }

        /// <summary>Ammunition currently carried, by outfit name.</summary>
        public int AmmoCount(string outfitName) =>
            outfitName is not null && _ammo.TryGetValue(outfitName, out int count) ? count : 0;

        public void AddAmmo(string outfitName, int count)
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
        public WeaponMount InstallWeapon(Outfit outfit, bool asTurret = false)
        {
            if (outfit is null) throw new ArgumentNullException(nameof(outfit));

            foreach (WeaponMount mount in _mounts)
            {
                if (mount.IsTurret == asTurret && mount.IsEmpty)
                {
                    mount.Install(outfit);
                    AddOutfit(outfit);
                    return mount;
                }
            }

            return null;
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
        public bool CanFire(Weapon weapon)
        {
            if (weapon is null || !weapon.IsWeapon || IsDisabled)
                return false;

            if (Energy < weapon.FiringEnergy)
                return false;

            // A weapon that names ammunition cannot fire without a round left.
            string ammo = weapon.AmmoName;
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
        public Projectile Fire(WeaponMount mount, ITarget target = null, Government government = null)
        {
            if (mount is null) throw new ArgumentNullException(nameof(mount));

            if (!mount.IsReady)
                return null;

            Weapon weapon = mount.Weapon;
            if (!CanFire(weapon))
                return null;

            // Pay for the shot before it exists, so a half-affordable burst stops
            // cleanly rather than firing on credit.
            Energy -= weapon.FiringEnergy;
            Heat += weapon.FiringHeat;
            if (weapon.AmmoName is not null)
                AddAmmo(weapon.AmmoName, -(int)weapon.AmmoUsage);

            mount.RecordShot();

            Angle aim = Facing + mount.BaseAngle;
            Point muzzle = Position + Facing.Rotate(mount.Point);

            return new Projectile(weapon, muzzle, Velocity, aim, target, government);
        }

        /// <summary>
        /// Fires every mount that is ready and affordable. This is the "hold the
        /// trigger" path; mounts that cannot fire are simply skipped.
        /// </summary>
        public List<Projectile> FireAll(ITarget target = null, Government government = null)
        {
            var shots = new List<Projectile>();

            foreach (WeaponMount mount in _mounts)
            {
                Projectile shot = Fire(mount, target, government);
                if (shot is not null)
                    shots.Add(shot);
            }

            return shots;
        }
    }
}
