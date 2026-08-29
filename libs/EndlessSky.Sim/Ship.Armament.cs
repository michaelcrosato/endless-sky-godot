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
    /// INCOMPLETE, tracked rather than dropped: turret firing ARCS (every turret is
    /// treated as omnidirectional, so one mounted behind a hull can still bear
    /// forward), blindspots, firing effects, anti-missile, and the fighter-bay half of
    /// armament.
    /// </remarks>
    public partial class Ship
    {
        private readonly List<WeaponMount> _mounts = new List<WeaponMount>();

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

        /// <summary>
        /// Ammunition currently carried, by outfit name.
        /// </summary>
        /// <remarks>
        /// This counts the ship's own outfits, because that is what ammunition is:
        /// upstream's <c>Ship::CanFire</c> looks the round up in the same
        /// <c>outfits</c> map that installing an outfit fills
        /// (<c>Ship.cpp:3657</c>). Keeping a separate ledger meant a hull built with
        /// its stock loadout carried 45 Sidewinders that <c>CanFire</c> could not
        /// see, so every launcher, torpedo tube and missile pod in the dataset was
        /// inert while the tests -- which loaded rounds through a back door no
        /// production code used -- stayed green.
        /// </remarks>
        public int AmmoCount(string? outfitName)
        {
            if (string.IsNullOrEmpty(outfitName))
                return 0;

            int count = 0;
            foreach (Outfit carried in _outfits)
                if (string.Equals(carried.Name, outfitName, StringComparison.Ordinal))
                    count++;

            return count;
        }

        /// <summary>
        /// Spends rounds of the named ammunition, removing the outfits themselves so
        /// their mass and attributes leave the ship with them.
        /// </summary>
        /// <remarks>
        /// Upstream spends a round with <c>AddOutfit(ammo, -AmmoUsage())</c>
        /// (<c>Ship.cpp:3687</c>), so an emptying magazine really does make the hull
        /// lighter. There is deliberately no public "load ammunition" call: rounds are
        /// ordinary outfits, so they arrive through <see cref="AddOutfit(Outfit,int,bool)"/>
        /// like everything else a ship carries. An earlier separate ledger let tests
        /// load rounds by a route the game never took, which is why every launcher in
        /// the dataset was inert under a green suite.
        /// </remarks>
        private void SpendAmmo(string? outfitName, int count)
        {
            if (string.IsNullOrEmpty(outfitName) || count <= 0)
                return;

            Outfit? round = FindCarriedOutfit(outfitName);
            if (round is not null)
                RemoveOutfit(round, count);
        }

        /// <summary>
        /// A deflection from this weapon's firing cone. Port of upstream
        /// <c>Distribution::GenerateInaccuracy</c> (<c>Distribution.cpp:61-79</c>).
        /// </summary>
        /// <remarks>
        /// Triangular by default — <c>(random - random) * value</c> — which peaks at
        /// zero deflection, so most shots land near the aim point and the spread tails
        /// off. A flat draw is upstream's explicitly-opted-in <c>uniform</c> mode, and
        /// using it for everything makes every weapon feel like a shotgun: a shot at
        /// the edge of the cone is exactly as likely as one down the middle.
        ///
        /// INCOMPLETE, tracked rather than dropped: the <c>narrow</c>/<c>medium</c>/
        /// <c>wide</c> normal distributions and the <c>inverted</c> flag, none of which
        /// are parsed off the weapon yet.
        /// </remarks>
        private Angle Inaccuracy(Weapon weapon)
        {
            double spread = weapon.Inaccuracy;
            if (spread <= 0.0)
                return default;

            return new Angle((RandomUnit() - RandomUnit()) * spread);
        }

        /// <summary>The ammunition outfit this ship carries under that name, if any.</summary>
        private Outfit? FindCarriedOutfit(string outfitName)
        {
            foreach (Outfit carried in _outfits)
                if (string.Equals(carried.Name, outfitName, StringComparison.Ordinal))
                    return carried;

            return null;
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

        /// <summary>
        /// Turns every turret toward a point, one frame's traverse at a time.
        /// </summary>
        /// <remarks>
        /// Upstream drives this from the AI's per-hardpoint aim commands
        /// (<c>Armament.cpp:233</c>); this is the same motion with the common case —
        /// every turret onto the same target — expressed directly. Each turret turns at
        /// its own rate and takes the shorter way round, so a mount already nearly on
        /// target eases the last degree rather than overshooting it.
        /// </remarks>
        public void AimTurrets(Point target)
        {
            foreach (WeaponMount mount in _mounts)
            {
                if (!mount.IsTurret || mount.Weapon is null || mount.Weapon.TurretTurn <= 0.0)
                    continue;

                // Where the mount points now, and where it needs to point, both in
                // hull-relative terms so the ship's own heading cancels out.
                Point offset = target - (Position + Facing.Rotate(mount.Point));
                if (offset.LengthSquared <= 0.0)
                    continue;

                // Angle.Degrees already folds to [-180, 180), so subtracting the three
                // angles gives the SHORTER way round without any further wrapping.
                double delta = (Angle.FromPoint(offset) - Facing - mount.BaseAngle).Degrees;

                double rate = mount.Weapon.TurretTurn;
                mount.Aim(Math.Clamp(delta / rate, -1.0, 1.0));
            }
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
                SpendAmmo(weapon.AmmoName, weapon.AmmoUsage);

            mount.RecordShot();

            Angle aim = Facing + mount.BaseAngle;

            // Weapon inaccuracy is a firing cone, applied to the aim at the moment of
            // the shot. Parsing it and never using it makes every weapon perfectly
            // accurate, which removes the reason streams of fire spread at all.
            aim += Inaccuracy(weapon);

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
