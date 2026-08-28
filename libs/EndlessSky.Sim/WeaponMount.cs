using System;

namespace EndlessSky.Sim
{
    /// <summary>
    /// The runtime state of one weapon mount: what is installed and when it may fire
    /// next. Port of the firing-cadence half of upstream <c>Hardpoint</c>.
    ///
    /// Distinct from <see cref="Hardpoint"/>, which is the static mount *definition*
    /// read out of ship data (offset and default outfit).
    /// </summary>
    /// <remarks>
    /// The three counters are what give Endless Sky weapons their distinct rhythms.
    /// A plain gun has burst count 1 and fires every <c>reload</c> frames. A burst
    /// weapon fires <c>burst count</c> shots spaced <c>burst reload</c> frames apart,
    /// then falls silent until the full <c>reload</c> elapses - which is why the Ion
    /// Hail Turret feels like a stutter rather than a stream.
    ///
    /// INCOMPLETE, tracked rather than dropped: turret traverse rate and blindspots,
    /// hardpoint offsets and firing effects, cluster/stream aiming, and the
    /// "special" hardpoint category.
    /// </remarks>
    public class WeaponMount
    {
        private double _reload;
        private double _burstReload;
        private double _burstCount;

        public WeaponMount(Point point = default, Angle baseAngle = default, bool isTurret = false)
        {
            Point = point;
            BaseAngle = baseAngle;
            IsTurret = isTurret;
        }

        /// <summary>Mount position in ship-local coordinates.</summary>
        public Point Point { get; }

        /// <summary>Fixed mounting angle, relative to the ship's facing.</summary>
        public Angle BaseAngle { get; }

        /// <summary>Turrets aim independently; guns fire along the hull.</summary>
        public bool IsTurret { get; }

        public Outfit InstalledOutfit { get; private set; }

        public Weapon Weapon => InstalledOutfit?.Weapon;

        public bool IsEmpty => InstalledOutfit is null;

        /// <summary>Frames until the full reload completes.</summary>
        public double ReloadRemaining => _reload;

        /// <summary>Shots left in the current burst.</summary>
        public double BurstRemaining => _burstCount;

        public void Install(Outfit outfit)
        {
            if (outfit is not null && !outfit.IsWeapon)
                throw new ArgumentException($"{outfit.Name} is not a weapon", nameof(outfit));

            InstalledOutfit = outfit;
            _reload = 0.0;
            _burstReload = 0.0;
            _burstCount = outfit?.Weapon.BurstCount ?? 0.0;
        }

        public void Uninstall() => Install(null);

        /// <summary>
        /// Ticks the reload counters one frame. Port of <c>Hardpoint::Step</c>.
        /// </summary>
        public void Step()
        {
            if (InstalledOutfit is null)
                return;

            if (_reload > 0.0)
                --_reload;

            // A completed full reload refills the burst magazine.
            if (_reload <= 0.0)
                _burstCount = Weapon.BurstCount;

            if (_burstReload > 0.0)
                --_burstReload;
        }

        /// <summary>
        /// Whether the mount could fire this frame, ignoring the ship's resources.
        /// Port of <c>Hardpoint::IsReady</c>.
        /// </summary>
        public bool IsReady => InstalledOutfit is not null && _burstReload <= 0.0 && _burstCount > 0.0;

        /// <summary>
        /// Records a shot: advances both reload clocks and spends one round of the
        /// burst. Callers fire through <see cref="Ship.Fire"/>, which also pays the
        /// energy, heat and ammunition costs.
        /// </summary>
        internal void RecordShot()
        {
            _reload += Weapon.Reload;
            _burstReload += Weapon.BurstReload;
            --_burstCount;
        }

        public override string ToString() =>
            $"{(IsTurret ? "turret" : "gun")} {(IsEmpty ? "(empty)" : InstalledOutfit.Name)}";
    }
}
